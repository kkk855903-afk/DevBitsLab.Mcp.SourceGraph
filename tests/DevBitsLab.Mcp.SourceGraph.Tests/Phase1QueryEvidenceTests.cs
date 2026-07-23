using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using CoreEvidence = DevBitsLab.Mcp.SourceGraph.Core.Evidence;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation = DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class Phase1QueryEvidenceTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _databasePath = string.Empty;
    private ScopeHost? _host;
    private ScopeRouter? _router;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-phase1-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _databasePath = Path.Join(_tempDir, "graph.db");
        var store = new SqliteGraphStore(_databasePath);
        await store.EnsureSchemaAsync();

        var a = await SeedSymbolAsync(store, "A");
        var b = await SeedSymbolAsync(store, "B");
        var c = await SeedSymbolAsync(store, "C");
        var d = await SeedSymbolAsync(store, "D");

        await store.BulkInsertEdgesAsync(new[]
        {
            Edge(a, b, EdgeKinds.Calls, 10, CoreEvidenceConfidence.Exact, "a-calls-b"),
            Edge(b, c, EdgeKinds.Calls, 20, CoreEvidenceConfidence.Semantic, "b-calls-c"),
            Edge(b, c, EdgeKinds.UsesType, 21, CoreEvidenceConfidence.Exact, "b-uses-c"),
            Edge(c, a, EdgeKinds.Calls, 30, CoreEvidenceConfidence.Exact, "cycle"),
            Edge(a, c, EdgeKinds.UsesType, 40, CoreEvidenceConfidence.Exact, "a-uses-c"),
        });

        // Deliberately malformed legacy row. Evidence-first tools must skip it instead of
        // presenting D's declaration as a fabricated D -> C call site.
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO edges(src, dst, kind_name) VALUES ($src, $dst, $kind);";
            command.Parameters.AddWithValue("$src", d.SymbolId);
            command.Parameters.AddWithValue("$dst", c.SymbolId);
            command.Parameters.AddWithValue("$kind", EdgeKinds.Calls);
            await command.ExecuteNonQueryAsync();
        }

        var scope = new Scope(
            "default",
            "default",
            _tempDir,
            new ScopeProjectSet.Paths(["**/*.cs"], Array.Empty<string>()),
            Isolated: false,
            DateTimeOffset.UtcNow);
        _host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solutionPath: "");
        _host.MarkReady();
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task CallerAndCalleeAliases_returnCanonicalAuditableRelations()
    {
        var aliasCallers = await Phase1CompatibilityTools.FindCallersAsync(
            _router!,
            "Graph.C",
            kind: "all");
        var listCallers = await GraphTools.ListCallersAsync(
            _router!,
            "Graph.C",
            kind: "all");
        var callerDto = Deserialize<ListCallersResult>(
            aliasCallers,
            ToolOutputJsonContext.Default.ListCallersResult);
        var listCallerDto = Deserialize<ListCallersResult>(
            listCallers,
            ToolOutputJsonContext.Default.ListCallersResult);

        callerDto.Should().BeEquivalentTo(listCallerDto);
        callerDto.TargetCanonicalKey.Should().Be("csharp:M:Graph.C");
        callerDto.Callers.Should().HaveCount(3);
        callerDto.Callers.Should().NotContain(row => row.Fqn == "Graph.D");
        callerDto.Callers.Select(row => row.Relation)
            .Should().BeEquivalentTo(
                EdgeKinds.Calls,
                EdgeKinds.UsesType,
                EdgeKinds.UsesType);
        listCallers.Content
            .OfType<ModelContextProtocol.Protocol.ResourceLinkBlock>()
            .Should().HaveCount(3, "each evidence-backed relation row gets its own resource link");
        AssertEveryRelationIsAuditable(callerDto.Callers.Select(row =>
            (row.Source, row.Target, row.Relation, row.Confidence, row.Evidence)));

        var aliasCallees = await Phase1CompatibilityTools.FindCalleesAsync(
            _router!,
            "Graph.A",
            kind: "all");
        var calleeDto = Deserialize<ListCalleesResult>(
            aliasCallees,
            ToolOutputJsonContext.Default.ListCalleesResult);
        calleeDto.Callees.Should().HaveCount(2);
        calleeDto.Callees.Select(row => row.Relation)
            .Should().BeEquivalentTo(EdgeKinds.Calls, EdgeKinds.UsesType);
        AssertEveryRelationIsAuditable(calleeDto.Callees.Select(row =>
            (row.Source, row.Target, row.Relation, row.Confidence, row.Evidence)));

        var duplicateTargetResult = await GraphTools.ListCalleesAsync(
            _router!,
            "Graph.B",
            kind: "all");
        var duplicateTargetDto = Deserialize<ListCalleesResult>(
            duplicateTargetResult,
            ToolOutputJsonContext.Default.ListCalleesResult);
        duplicateTargetDto.Callees.Should().HaveCount(2)
            .And.OnlyContain(row => row.Fqn == "Graph.C");
        duplicateTargetResult.Content
            .OfType<ModelContextProtocol.Protocol.ResourceLinkBlock>()
            .Should().HaveCount(2, "resource links are per relation row, not per target id");
    }

    [Fact]
    public async Task ImpactAlias_returnsCycleSafePredecessorPaths_withEvidence()
    {
        var result = await Phase1CompatibilityTools.ImpactAnalysisAsync(
            _router!,
            "Graph.C",
            maxDepth: 4,
            limit: 10,
            kind: EdgeKinds.Calls);
        var dto = Deserialize<ImpactOfChangeResult>(
            result,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);

        dto.Truncated.Should().BeFalse();
        dto.TargetCanonicalKey.Should().Be("csharp:M:Graph.C");
        dto.Upstream.Select(row => row.Fqn)
            .Should().Equal("Graph.B", "Graph.A");
        dto.Upstream.Should().OnlyHaveUniqueItems(row => row.SymbolId);
        dto.Upstream.Should().NotContain(row =>
            row.Fqn == "Graph.C" || row.Fqn == "Graph.D");

        var b = dto.Upstream.Single(row => row.Fqn == "Graph.B");
        b.Depth.Should().Be(1);
        b.Predecessor.CanonicalKey.Should().Be("csharp:M:Graph.C");
        b.Path.Should().ContainSingle();
        b.Confidence.Should().Be("semantic");

        var a = dto.Upstream.Single(row => row.Fqn == "Graph.A");
        a.Depth.Should().Be(2);
        a.Predecessor.CanonicalKey.Should().Be("csharp:M:Graph.B");
        a.Path.Should().HaveCount(2);
        a.Path.Select(hop => hop.From.Fqn)
            .Should().Equal("Graph.A", "Graph.B");
        a.Path.Select(hop => hop.To.Fqn)
            .Should().Equal("Graph.B", "Graph.C");
        a.Confidence.Should().Be("semantic", "the path uses its weakest hop confidence");
        a.Path.Should().AllSatisfy(AssertHopIsAuditable);
    }

    [Fact]
    public async Task Impact_reportsDepthAndResultBoundaries()
    {
        var shallow = await Phase1CompatibilityTools.ImpactAnalysisAsync(
            _router!,
            "Graph.C",
            maxDepth: 1,
            limit: 10);
        var shallowDto = Deserialize<ImpactOfChangeResult>(
            shallow,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);
        shallowDto.Upstream.Select(row => row.Fqn).Should().Equal("Graph.B");
        shallowDto.Truncated.Should().BeTrue();

        var limited = await GraphTools.ImpactOfChangeAsync(
            _router!,
            "Graph.C",
            maxDepth: 4,
            limit: 1);
        var limitedDto = Deserialize<ImpactOfChangeResult>(
            limited,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);
        limitedDto.Upstream.Should().ContainSingle();
        limitedDto.Truncated.Should().BeTrue();

        var invalidDepth = await GraphTools.ImpactOfChangeAsync(
            _router!,
            "Graph.C",
            maxDepth: 13);
        invalidDepth.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(invalidDepth).Should().Contain("between 1 and 12");

        var invalidLimit = await GraphTools.ListCallersAsync(
            _router!,
            "Graph.C",
            limit: 0);
        invalidLimit.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(invalidLimit).Should().Contain("between 1 and 1000");
    }

    private async Task<SeededSymbol> SeedSymbolAsync(
        SqliteGraphStore store,
        string name)
    {
        var path = Path.Join(_tempDir, $"{name}.cs");
        var fileId = await store.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
        var symbolId = await store.UpsertSymbolAsync(
            $"csharp:M:Graph.{name}",
            new Symbol(
                0,
                name,
                $"Graph.{name}",
                SymbolKinds.Method,
                fileId,
                1,
                1,
                50,
                1,
                $"void {name}()",
                null));
        return new SeededSymbol(symbolId, fileId, path);
    }

    private static Edge Edge(
        SeededSymbol source,
        SeededSymbol target,
        string relation,
        int line,
        CoreEvidenceConfidence confidence,
        string marker) =>
        new(source.SymbolId, target.SymbolId, relation)
        {
            Evidence = new CoreEvidence(
                source.FileId,
                new CoreSourceLocation(source.FilePath, line, 3, line, 11),
                confidence,
                "phase1-fixture",
                new Dictionary<string, string> { ["marker"] = marker }),
        };

    private static T Deserialize<T>(
        ModelContextProtocol.Protocol.CallToolResult result,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        result.StructuredContent!.Value.Deserialize(typeInfo)!;

    private static void AssertEveryRelationIsAuditable(
        IEnumerable<(
            TraceCallPathSymbol Source,
            TraceCallPathSymbol Target,
            string Relation,
            string Confidence,
            IReadOnlyList<TraceCallPathEvidence> Evidence)> relations)
    {
        relations.Should().AllSatisfy(relation =>
        {
            relation.Source.CanonicalKey.Should().NotBeNullOrWhiteSpace();
            relation.Target.CanonicalKey.Should().NotBeNullOrWhiteSpace();
            relation.Relation.Should().NotBeNullOrWhiteSpace();
            relation.Confidence.Should().BeOneOf("exact", "semantic", "inferred");
            relation.Evidence.Should().NotBeEmpty();
            relation.Evidence.Should().AllSatisfy(evidence =>
            {
                evidence.FilePath.Should().EndWith(".cs");
                evidence.StartLine.Should().BePositive();
                evidence.EndLine.Should().BeGreaterThanOrEqualTo(evidence.StartLine);
                evidence.EndColumn.Should().BeGreaterThan(evidence.StartColumn);
            });
        });
    }

    private static void AssertHopIsAuditable(TraceCallPathHop hop) =>
        AssertEveryRelationIsAuditable(
        [
            (hop.From, hop.To, hop.Relation, hop.Confidence, hop.Evidence),
        ]);

    private sealed record SeededSymbol(
        long SymbolId,
        long FileId,
        string FilePath);
}
