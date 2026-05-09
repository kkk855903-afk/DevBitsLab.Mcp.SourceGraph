using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Regression tests for the empty-result materialization bug: when a query that selects an
/// undeclared aggregate expression (e.g. <c>COALESCE((SELECT COUNT(*) ...), 0)</c>) returns
/// zero rows, Microsoft.Data.Sqlite's column-type inference falls back to BLOB and Dapper
/// rejects the row record with a misleading
/// <c>(System.Int64, System.String, System.Byte[]) is required for ... materialization</c>
/// error. The fix is to <c>CAST(... AS INTEGER)</c> at the SQL level so the column has a
/// stable type regardless of row count.
///
/// These tests run against a fresh empty DB (no indexer) so they exercise exactly the
/// empty-result path that <see cref="DiagnosticsAndGeneratorTests"/> can't because the
/// fixture solution always emits at least one generated file and at least one matching symbol.
/// </summary>
public sealed class EmptyResultMaterializationTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sourcegraph-emptyresult-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
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

    [Fact]
    public async Task ListGeneratedFiles_returnsEmpty_onSolutionWithNoGeneratedFiles()
    {
        // Seed a non-generated file so the files table isn't empty — what we're testing is the
        // WHERE is_generated = 1 filter excluding everything, not a totally empty schema.
        await _store!.UpsertFileAsync(
            path: "/virtual/Hand.cs",
            contentSha256: new byte[32],
            indexedAt: DateTimeOffset.UtcNow,
            isGenerated: false);

        var rows = await _store.ListGeneratedFilesAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ListGeneratedFiles_returnsEmpty_onTotallyEmptyDb()
    {
        var rows = await _store!.ListGeneratedFilesAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ModuleSummary_returnsEmpty_whenPrefixMatchesNothing()
    {
        var rows = await _store!.ModuleSummaryAsync("Definitely.Not.A.Real.Namespace.That.Exists");
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ModuleSummary_returnsHit_whenPrefixMatches()
    {
        // Populated case: ensures the dynamic-row → SymbolHit projection (long→int conversions,
        // nullable strings, IsGenerated flag) is exercised end-to-end with at least one match.
        var fileId = await _store!.UpsertFileAsync(
            path: "/virtual/Foo.cs",
            contentSha256: new byte[32],
            indexedAt: DateTimeOffset.UtcNow,
            isGenerated: false);

        await _store.UpsertSymbolAsync("csharp:M:Sample.Foo.Bar", new Symbol(
            Id: 0,
            Name: "Bar",
            Fqn: "Sample.Foo.Bar",
            Kind: SymbolKinds.Method,
            FileId: fileId,
            StartLine: 1, StartCol: 1, EndLine: 5, EndCol: 1,
            Signature: "void Bar()",
            ContainerId: null,
            Modifiers: "public",
            Accessibility: 1,
            XmlSummary: "summary",
            TestFramework: null));

        var rows = await _store.ModuleSummaryAsync("Sample.Foo");
        rows.Should().HaveCount(1);
        var hit = rows[0];
        hit.Symbol.Fqn.Should().Be("Sample.Foo.Bar");
        hit.Symbol.Name.Should().Be("Bar");
        hit.Symbol.Kind.Should().Be(SymbolKinds.Method);
        hit.Symbol.StartLine.Should().Be(1);
        hit.Symbol.IsGenerated.Should().BeFalse();
        hit.InDegree.Should().Be(0); // no edges populated
    }
}
