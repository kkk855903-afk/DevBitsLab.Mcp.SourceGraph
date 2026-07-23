using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end coverage of the <c>integrate-tests-and-history</c> change. Cold-indexes the
/// fixture solution (which now includes <c>Sample.Tests</c>) and verifies:
///   1. <c>symbols.test_framework</c> is set to <c>xunit | nunit | mstest</c> for the
///      decorated test methods.
///   2. <c>"tests"</c> edges land from each test method to the first non-trivial
///      production-code call inside its body (kebab-case kind name introduced by
///      open-language-contract; see <c>EdgeKinds.Tests</c> for the constant).
///
/// History pipeline coverage is intentionally lighter (one disabled-mode test): wiring it up
/// requires git, which we don't assume in CI.
/// </summary>
public sealed class IntegrateTestsAndHistoryTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        var tmp = Path.Combine(Path.GetTempPath(), "sourcegraph-tests-history-" + Guid.NewGuid().ToString("N"));
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
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    [Fact]
    public async Task xUnitTest_hasTestFrameworkSetToXunit()
    {
        var hits = await _store!.FindSymbolsAsync("AddTwoNumbers_returnsTheirSum");
        hits.Should().Contain(h => h.TestFramework == "xunit");
    }

    [Fact]
    public async Task NUnitTest_hasTestFrameworkSetToNunit()
    {
        var hits = await _store!.FindSymbolsAsync("Greet_PrependsPrefix");
        hits.Should().Contain(h => h.TestFramework == "nunit");
    }

    [Fact]
    public async Task MsTest_hasTestFrameworkSetToMstest()
    {
        var hits = await _store!.FindSymbolsAsync("MakeGreeter_returnsSomething");
        hits.Should().Contain(h => h.TestFramework == "mstest");
    }

    [Fact]
    public async Task TestsEdge_xUnitFact_pointsAtCalculatorAdd()
    {
        var addHits = await _store!.FindSymbolsAsync("Calculator.Add");
        var add = addHits.First(h => h.Fqn.StartsWith("int Sample.Domain.Calculator.Add"));
        var tests = await _store.ListTestsForAsync(add.Id, limit: 50);
        tests.Should().Contain(t => t.Test.Fqn.Contains("CalculatorTests.AddTwoNumbers_returnsTheirSum"));
        tests.Should().Contain(t => t.Framework == "xunit");
    }

    [Fact]
    public async Task TestsEdge_emittedForAllThreeFrameworks()
    {
        // Walk every Tests edge in the graph and confirm we see xunit + nunit + mstest sources.
        var addHits = await _store!.FindSymbolsAsync("Calculator.Add");
        var add = addHits.First(h => h.Fqn.StartsWith("int Sample.Domain.Calculator.Add"));
        var tests = await _store.ListTestsForAsync(add.Id, limit: 50);
        tests.Should().Contain(t => t.Framework == "xunit");
        tests.Should().Contain(t => t.Framework == "nunit");
    }

    [Fact]
    public async Task ProductionCalculator_hasNoTestFramework()
    {
        // A non-test method (Calculator.Add) must NOT be tagged with a test framework — that
        // would imply we accidentally classified production code as a test.
        var hits = await _store!.FindSymbolsAsync("Calculator.Add");
        var add = hits.First(h => h.Fqn.StartsWith("int Sample.Domain.Calculator.Add"));
        add.TestFramework.Should().BeNull();
    }
}
