using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;

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
        var calc = hits.Should().ContainSingle(h => h.Kind == SymbolKinds.Class).Which;
        hits.Should().ContainSingle(
            "an exact FQN must not include members whose FQN merely contains the type name");
        calc.Modifiers.Should().BeNullOrEmpty();
        calc.XmlSummary.Should().NotBeNullOrEmpty();
        calc.XmlSummary!.Should().Contain("Simple integer arithmetic");
    }

    [Fact]
    public async Task ListMembers_Calculator_returnsAllPublicMethods()
    {
        var hits = await _store!.FindSymbolsAsync("Sample.Domain.Calculator");
        var calc = hits.First(h => h.Kind == SymbolKinds.Class);
        var members = await _store.ListMembersAsync(calc.Id);
        // Calculator gained MakeGreeter (Instantiates demo) and Divide (Throws demo) from
        // expand-edge-types; assert the foundational three are present and every member is public.
        members.Select(m => m.Name).Should().Contain(new[] { "Add", "Subtract", "Multiply" });
        members.Should().AllSatisfy(m => m.Accessibility.Should().Be((int)Microsoft.CodeAnalysis.Accessibility.Public));
    }

    [Fact]
    public async Task InterfaceMemberDispatchesToImplementingMember()
    {
        var interfaceMethod = (await _store!.FindSymbolsAsync("IGreeter.Greet"))
            .Should().ContainSingle(hit =>
                hit.Fqn.Contains("IGreeter.Greet", StringComparison.Ordinal))
            .Which;
        var implementation = (await _store.ListCalleesAsync(
                interfaceMethod.Id,
                edgeKind: EdgeKinds.InterfaceDispatchesTo))
            .Should().ContainSingle(hit =>
                hit.Fqn.Contains("Greeter.Greet", StringComparison.Ordinal)
                && !hit.Fqn.Contains("IGreeter.Greet", StringComparison.Ordinal))
            .Which;

        (await _store.ListEdgeEvidenceAsync(
                interfaceMethod.Id,
                implementation.Id,
                EdgeKinds.InterfaceDispatchesTo))
            .Should().ContainSingle(evidence =>
                evidence.Confidence == CoreEvidenceConfidence.Semantic
                && evidence.Producer == "roslyn");
    }

    [Fact]
    public async Task RepeatedRoslynCalls_preserveExactCallSiteEvidence()
    {
        var caller = (await _store!.FindSymbolsAsync("AddManyNumbers"))
            .Should().ContainSingle(hit =>
                hit.Fqn.Contains("CalculatorTests.AddManyNumbers_isCommutative", StringComparison.Ordinal))
            .Which;
        var target = (await _store.FindSymbolsAsync("Calculator.Add"))
            .Should().ContainSingle(hit =>
                hit.Fqn.Contains("Sample.Domain.Calculator.Add", StringComparison.Ordinal))
            .Which;

        var evidence = await _store.ListEdgeEvidenceAsync(
            caller.Id,
            target.Id,
            EdgeKinds.Calls);

        evidence.Should().HaveCount(2, "the fixture calls Calculator.Add twice from one method");
        evidence.Select(item => item.Location.StartLine).Should().Equal(28, 29);
        evidence.Select(item => item.Location.FilePath)
            .Should().OnlyContain(path =>
                path.EndsWith("CalculatorTests.cs", StringComparison.OrdinalIgnoreCase));
        evidence.Select(item => item.Confidence)
            .Should().OnlyContain(confidence => confidence == CoreEvidenceConfidence.Exact);
        evidence.Select(item => item.Producer)
            .Should().OnlyContain(producer => producer == "roslyn");
        evidence.Select(item => (
                item.Location.StartLine,
                item.Location.StartColumn,
                item.Location.EndLine,
                item.Location.EndColumn))
            .Should().OnlyHaveUniqueItems("separate call sites must not collapse");
    }

    [Fact]
    public async Task CallInsideLambda_isAttributedToContainingMethod()
    {
        var caller = (await _store!.FindSymbolsAsync(
                "CallsInsideLambda_areAttributedToTheContainingMethod"))
            .Should().ContainSingle()
            .Which;
        var target = (await _store.FindSymbolsAsync("Calculator.Add"))
            .Should().ContainSingle(hit =>
                hit.Fqn.Contains(
                    "Sample.Domain.Calculator.Add",
                    StringComparison.Ordinal))
            .Which;

        (await _store.ListEdgeEvidenceAsync(
                caller.Id,
                target.Id,
                EdgeKinds.Calls))
            .Should().ContainSingle(evidence =>
                evidence.Confidence == CoreEvidenceConfidence.Exact
                && evidence.Producer == "roslyn");
    }

    [Fact]
    public async Task UniqueCandidateCallInsideNestedAsyncLambda_isAttributedToContainingMethod()
    {
        var caller = (await _store!.FindSymbolsAsync(
                "CandidateCallInsideNestedAsyncLambda_isAttributedToContainingMethod"))
            .Should().ContainSingle()
            .Which;
        var target = (await _store.FindSymbolsAsync(
                "CandidateOnlyTargetAsync"))
            .Should().ContainSingle()
            .Which;

        (await _store.ListEdgeEvidenceAsync(
                caller.Id,
                target.Id,
                EdgeKinds.Calls))
            .Should().ContainSingle(evidence =>
                evidence.Producer == "roslyn");
    }

    [Fact]
    public async Task RoslynTypeRelations_preserveExactBaseTypeEvidence()
    {
        await AssertTypeRelationEvidenceAsync(
            sourceCanonicalKey: CanonicalKeys.ForType("Sample.Domain.Greeter"),
            targetCanonicalKey: CanonicalKeys.ForType("Sample.Domain.GreeterBase"),
            edgeKind: EdgeKinds.Inherits,
            expectedFileName: "Greeter.cs",
            expectedLine: 3,
            expectedText: "GreeterBase");
        await AssertTypeRelationEvidenceAsync(
            sourceCanonicalKey: CanonicalKeys.ForType("Sample.Domain.Greeter"),
            targetCanonicalKey: CanonicalKeys.ForType("Sample.Domain.IGreeter"),
            edgeKind: EdgeKinds.Implements,
            expectedFileName: "Greeter.cs",
            expectedLine: 3,
            expectedText: "IGreeter");
    }

    private async Task AssertTypeRelationEvidenceAsync(
        string sourceCanonicalKey,
        string targetCanonicalKey,
        string edgeKind,
        string expectedFileName,
        int expectedLine,
        string expectedText)
    {
        const string typeKeyPrefix = "csharp:T:";
        sourceCanonicalKey.Should().StartWith(typeKeyPrefix);
        var source = (await _store!.FindSymbolsAsync(
                sourceCanonicalKey.Substring(typeKeyPrefix.Length)))
            .Should().ContainSingle(hit => hit.CanonicalKey == sourceCanonicalKey)
            .Which;
        var target = (await _store.ListCalleesAsync(
                source.Id,
                limit: 50,
                edgeKind: edgeKind))
            .Should().ContainSingle(hit => hit.CanonicalKey == targetCanonicalKey)
            .Which;

        var evidence = await _store.ListEdgeEvidenceAsync(
            source.Id,
            target.Id,
            edgeKind);
        evidence.Should().ContainSingle();
        evidence[0].Confidence.Should().Be(CoreEvidenceConfidence.Exact);
        evidence[0].Producer.Should().Be("roslyn");
        evidence[0].Location.FilePath.Should()
            .EndWith(expectedFileName);
        evidence[0].Location.StartLine.Should().Be(expectedLine);

        var line = File.ReadLines(evidence[0].Location.FilePath)
            .ElementAt(evidence[0].Location.StartLine - 1);
        line.Substring(
                evidence[0].Location.StartColumn - 1,
                evidence[0].Location.EndColumn - evidence[0].Location.StartColumn)
            .Should().Be(expectedText);
    }
}
