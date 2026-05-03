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
/// Cold-indexes <c>tests/fixtures/Sample.sln</c> into a temporary SQLite store, then asserts
/// that the v5 enrichment columns (accessibility, modifiers, xml_summary) carry the expected
/// values for canonical fixture symbols, and that the FTS5 index resolves a doc-summary query.
/// </summary>
public sealed class IndexFixtureTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        var tmp = Path.Combine(Path.GetTempPath(), "sourcegraph-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "graph.db");

        _store = new SqliteGraphStore(_dbPath);
        await RoslynIndexer.IndexSolutionOnceAsync(slnPath, _store);
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
        // Walk upward from AppContext.BaseDirectory to find the repo root that holds tests/fixtures.
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    [Fact]
    public async Task IGreeter_Greet_isPublic()
    {
        var hits = await _store!.FindSymbolsAsync("IGreeter.Greet");
        var greet = hits.Should().Contain(h => h.Fqn.Contains("IGreeter.Greet"))
            .Which;
        greet.Accessibility.Should().Be((int)Microsoft.CodeAnalysis.Accessibility.Public);
    }

    [Fact]
    public async Task Greeter_prefix_isPrivateReadonly()
    {
        var hits = await _store!.FindSymbolsAsync("_prefix");
        var prefix = hits.Should().ContainSingle(h => h.Name == "_prefix").Which;
        prefix.Accessibility.Should().Be((int)Microsoft.CodeAnalysis.Accessibility.Private);
        prefix.Modifiers.Should().Be("readonly");
    }

    [Fact]
    public async Task Calculator_Add_hasNoModifiers()
    {
        var hits = await _store!.FindSymbolsAsync("Calculator.Add");
        var add = hits.Should().Contain(h => h.Fqn.Contains("Calculator.Add"))
            .Which;
        add.Modifiers.Should().BeNullOrEmpty();
        add.Accessibility.Should().Be((int)Microsoft.CodeAnalysis.Accessibility.Public);
    }

    [Fact]
    public async Task SearchSymbols_retry_findsMethodViaXmlSummary()
    {
        var hits = await _store!.SearchSymbolsAsync("retry");
        hits.Should().Contain(h => h.Name == "Multiply",
            "the Multiply XML summary contains 'retry on transient overflow' and the FTS5 trigger surfaces xml_summary");
    }

    [Fact]
    public async Task Calculator_classRow_hasSummaryAndNoModifiers()
    {
        var hits = await _store!.FindSymbolsAsync("Sample.Domain.Calculator");
        var calc = hits.Should().Contain(h => h.Kind == DevBitsLab.Mcp.SourceGraph.Core.SymbolKind.Class).Which;
        calc.Modifiers.Should().BeNullOrEmpty();
        calc.XmlSummary.Should().NotBeNullOrEmpty();
        calc.XmlSummary!.Should().Contain("Simple integer arithmetic");
    }

    [Fact]
    public async Task ListMembers_Calculator_returnsThreeMethods()
    {
        var hits = await _store!.FindSymbolsAsync("Sample.Domain.Calculator");
        var calc = hits.First(h => h.Kind == DevBitsLab.Mcp.SourceGraph.Core.SymbolKind.Class);
        var members = await _store.ListMembersAsync(calc.Id);
        members.Select(m => m.Name).Should().BeEquivalentTo(new[] { "Add", "Subtract", "Multiply" });
        members.Should().AllSatisfy(m => m.Accessibility.Should().Be((int)Microsoft.CodeAnalysis.Accessibility.Public));
    }
}
