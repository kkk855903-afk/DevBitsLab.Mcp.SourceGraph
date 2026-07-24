using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Integration tests for the integrate-source-and-diagnostics change: cold-indexes
/// <c>tests/fixtures/Sample.sln</c>, then asserts that source-generated documents and
/// Roslyn diagnostics are persisted as expected.
/// </summary>
public sealed class DiagnosticsAndGeneratorTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private string _slnPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _slnPath = LocateSolution();
        var tmp = Path.Combine(Path.GetTempPath(), "sourcegraph-diag-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "graph.db");

        _store = new SqliteGraphStore(_dbPath);
        await RoslynIndexer.IndexSolutionOnceAsync(_slnPath, _store);
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch { /* best-effort */ }
    }

    private static string LocateSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    [Fact]
    public async Task ListGeneratedFiles_returnsTheGeneratedHelloFile()
    {
        var rows = await _store!.ListGeneratedFilesAsync();
        rows.Should().NotBeEmpty("Sample.Generators.HelloGenerator emits GeneratedHello.g.cs into Sample.App");
        rows.Should().Contain(r => r.FilePath.EndsWith("GeneratedHello.g.cs"));
        rows.Where(r => r.FilePath.EndsWith("GeneratedHello.g.cs"))
            .Single().SymbolCount.Should().BeGreaterThan(0,
                "the generated file declares Sample.Generated namespace + GeneratedHello class + Greet method");
    }

    [Fact]
    public async Task GeneratedSymbols_areIndexedAndMarked()
    {
        var hits = await _store!.FindSymbolsAsync("GeneratedHello");
        var generatedClass = hits.Should().Contain(h => h.Name == "GeneratedHello").Which;
        generatedClass.IsGenerated.Should().BeTrue("GeneratedHello.g.cs is a source-generated document");
    }

    [Fact]
    public async Task FindDiagnostics_CS0618_returnsObsoleteUsage()
    {
        var rows = await _store!.FindDiagnosticsAsync(severity: null, code: "CS0618", symbolId: null);
        rows.Should().NotBeEmpty("Program.cs calls Calculator.LegacyAdd which is [Obsolete] => CS0618");
        rows.Should().AllSatisfy(d => d.Code.Should().Be("CS0618"));
        rows.Should().AllSatisfy(d => d.Severity.Should().Be(2));
        // The diagnostic should land in Program.cs
        rows.Should().Contain(d => d.FilePath.EndsWith("Program.cs"));
    }

    [Fact]
    public async Task FindDiagnostics_warningSeverity_includesObsoleteUsage()
    {
        // severity = 2 (Warning) filters via >= 2, which catches both warnings and errors.
        var rows = await _store!.FindDiagnosticsAsync(severity: 2, code: null, symbolId: null);
        rows.Should().Contain(d => d.Code == "CS0618");
        rows.Should().AllSatisfy(d => d.Severity.Should().BeGreaterThanOrEqualTo(2));
    }

    [Fact]
    public async Task FindReferences_excludesGeneratedByDefault()
    {
        // Look for refs to Sample.Domain.Calculator. The generated file does not reference
        // anything in Sample.Domain, but at least exercise the parameter on a known symbol.
        var hits = await _store!.FindSymbolsAsync("Calculator");
        var calc = hits.Should().Contain(h => h.Fqn == "Sample.Domain.Calculator").Which;
        var refs = await _store.FindReferencesAsync(calc.Id, includeGenerated: false, limit: 200);
        refs.Should().AllSatisfy(r => r.IsGenerated.Should().BeFalse());
    }

    [Fact]
    public async Task UpsertDiagnosticsForFile_replacesPreviousRows()
    {
        // Inject and then replace diagnostics for an arbitrary file id.
        var files = await _store!.GetAllFilesAsync();
        var anyFile = files.First();
        var fid = anyFile.Id;

        var first = new[]
        {
            new Core.DiagnosticRecord(SymbolId: null, FileId: fid, Severity: 2, Code: "TST001", Message: "first", Line: 1, Col: 1),
            new Core.DiagnosticRecord(SymbolId: null, FileId: fid, Severity: 2, Code: "TST001", Message: "first b", Line: 2, Col: 1),
        };
        await _store.UpsertDiagnosticsForFileAsync(fid, first);
        var afterFirst = await _store.FindDiagnosticsAsync(severity: null, code: "TST001", symbolId: null);
        afterFirst.Count.Should().Be(2);

        // Replace with one row; the previous two should be gone.
        var second = new[]
        {
            new Core.DiagnosticRecord(SymbolId: null, FileId: fid, Severity: 2, Code: "TST001", Message: "second", Line: 5, Col: 1),
        };
        await _store.UpsertDiagnosticsForFileAsync(fid, second);
        var afterSecond = await _store.FindDiagnosticsAsync(severity: null, code: "TST001", symbolId: null);
        afterSecond.Count.Should().Be(1);
        afterSecond[0].Message.Should().Be("second");

        // Empty list should clear them all.
        await _store.UpsertDiagnosticsForFileAsync(fid, Array.Empty<Core.DiagnosticRecord>());
        var afterEmpty = await _store.FindDiagnosticsAsync(severity: null, code: "TST001", symbolId: null);
        afterEmpty.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDiagnosticFileIdsByCodes_returnsDistinctExactOwners()
    {
        var files = (await _store!.GetAllFilesAsync()).Take(2).ToArray();
        files.Should().HaveCount(2);
        await _store.UpsertDiagnosticsForFileAsync(
            files[0].Id,
            [
                new Core.DiagnosticRecord(
                    null,
                    files[0].Id,
                    2,
                    "WPFTEST001",
                    "first",
                    1,
                    1),
                new Core.DiagnosticRecord(
                    null,
                    files[0].Id,
                    2,
                    "WPFTEST002",
                    "second",
                    2,
                    1),
            ]);
        await _store.UpsertDiagnosticsForFileAsync(
            files[1].Id,
            [
                new Core.DiagnosticRecord(
                    null,
                    files[1].Id,
                    2,
                    "OTHER001",
                    "other",
                    1,
                    1),
            ]);

        var owners = await _store.ListDiagnosticFileIdsByCodesAsync(
            ["WPFTEST001", "WPFTEST002", "WPFTEST001"]);

        owners.Should().Equal(files[0].Id);
        (await _store.ListDiagnosticFileIdsByCodesAsync([]))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task IsGeneratedFile_returnsTrueForGeneratedFile_falseOtherwise()
    {
        var files = await _store!.GetAllFilesAsync();
        var generated = files.Single(f => f.Path.EndsWith("GeneratedHello.g.cs"));
        var regular = files.First(f => f.Path.EndsWith("Calculator.cs"));

        (await _store.IsGeneratedFileAsync(generated.Id)).Should().BeTrue();
        (await _store.IsGeneratedFileAsync(regular.Id)).Should().BeFalse();
    }
}
