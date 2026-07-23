using System.Reflection;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;
using CoreEvidence = DevBitsLab.Mcp.SourceGraph.Core.Evidence;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation = DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class TraceCallPathToolsTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private ScopeHost? _host;
    private ScopeRouter? _router;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-trace-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var store = new SqliteGraphStore(Path.Join(_tempDir, "graph.db"));
        await store.EnsureSchemaAsync();

        var a = await SeedSymbolAsync(store, "A");
        var b = await SeedSymbolAsync(store, "B");
        var c = await SeedSymbolAsync(store, "C");
        var d = await SeedSymbolAsync(store, "D");

        await store.BulkInsertEdgesAsync(new[]
        {
            Edge(a, b, 5, CoreEvidenceConfidence.Exact, "a-to-b"),
            Edge(b, c, 8, CoreEvidenceConfidence.Semantic, "b-to-c"),
            Edge(a, d, 6, CoreEvidenceConfidence.Exact, "a-to-d"),
            Edge(d, c, 9, CoreEvidenceConfidence.Exact, "d-to-c"),
            Edge(c, a, 12, CoreEvidenceConfidence.Exact, "cycle"),
        });

        var scope = new Scope(
            "default",
            "default",
            _tempDir,
            new ScopeProjectSet.Paths(["**/*.cs"], Array.Empty<string>()),
            Isolated: false,
            DateTimeOffset.UtcNow);
        var indexer = new RoslynIndexer(store);
        _host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            indexer,
            solutionPath: "");
        _host.MarkReady();
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");

        Edge Edge(
            SeededSymbol source,
            SeededSymbol target,
            int line,
            CoreEvidenceConfidence confidence,
            string marker) =>
            new(source.SymbolId, target.SymbolId, EdgeKinds.Calls)
            {
                Evidence = new CoreEvidence(
                    source.FileId,
                    new CoreSourceLocation(source.FilePath, line, 5, line, 12),
                    confidence,
                    "fixture",
                    new Dictionary<string, string> { ["marker"] = marker }),
            };
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Tool_registersExactPhase1Name()
    {
        var method = typeof(TraceCallPathTools).GetMethod(
            nameof(TraceCallPathTools.TraceCallPathAsync),
            BindingFlags.Public | BindingFlags.Static);
        var tool = McpServerTool.Create(
            method!,
            target: null,
            new McpServerToolCreateOptions());

        tool.ProtocolTool.Name.Should().Be("trace_call_path");
    }

    [Fact]
    public async Task Trace_returnsEveryHopWithEvidence_andDetectsCycles()
    {
        var result = await TraceCallPathTools.TraceCallPathAsync(
            _router!,
            from: "Graph.A",
            to: "Graph.C",
            maxDepth: 4,
            maxPaths: 10,
            maxNodes: 100);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!;
        var scope = dto.Scopes.Should().ContainSingle().Which;
        scope.Truncated.Should().BeFalse();
        scope.Paths.Should().HaveCount(2, "A reaches C through B and through D");
        var throughB = new[] { "Graph.B", "Graph.C" };
        var throughD = new[] { "Graph.D", "Graph.C" };
        scope.Paths.Select(path => path.Hops.Select(hop => hop.To.Fqn))
            .Should().Contain(sequence => sequence.SequenceEqual(throughB))
            .And.Contain(sequence => sequence.SequenceEqual(throughD));

        var semanticPath = scope.Paths.Single(path =>
            path.Hops[0].To.Fqn == "Graph.B");
        semanticPath.Confidence.Should().Be("semantic",
            "path confidence is the weakest hop");
        semanticPath.Hops.Should().OnlyContain(hop => hop.Relation == EdgeKinds.Calls);
        semanticPath.Hops.Should().AllSatisfy(hop =>
        {
            hop.Evidence.Should().ContainSingle();
            hop.Evidence[0].FilePath.Should().EndWith(
                $"{hop.From.Fqn.Split('.')[1]}.cs");
            hop.Evidence[0].Producer.Should().Be("fixture");
            hop.Evidence[0].Metadata.Should().ContainKey("marker");
        });

        var exactPath = scope.Paths.Single(path =>
            path.Hops[0].To.Fqn == "Graph.D");
        exactPath.Confidence.Should().Be("exact");
        CallToolResultHelpers.ProseText(result).Should().Contain("2 paths");
        result.Content.OfType<ResourceLinkBlock>().Should().HaveCount(4)
            .And.OnlyHaveUniqueItems(link => link.Uri);
    }

    [Fact]
    public async Task Trace_enforcesDepthAndResourceCaps()
    {
        var shallow = await TraceCallPathTools.TraceCallPathAsync(
            _router!,
            from: "Graph.A",
            to: "Graph.C",
            maxDepth: 1);
        var shallowDto = shallow.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!;
        shallowDto.Scopes.Single().Paths.Should().BeEmpty();
        shallowDto.Scopes.Single().Truncated.Should().BeTrue(
            "the configured depth cap prevented deeper traversal");

        var invalid = await TraceCallPathTools.TraceCallPathAsync(
            _router!,
            from: "Graph.A",
            to: "Graph.C",
            maxNodes: 5001);
        invalid.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(invalid).Should().Contain("between 1 and 5000");
    }

    private async Task<SeededSymbol> SeedSymbolAsync(
        SqliteGraphStore store,
        string name)
    {
        var path = Path.Join(_tempDir, $"{name}.cs");
        var fileId = await store.UpsertFileAsync(
            path,
            new byte[] { 1, 2, 3, 4 },
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
                20,
                1,
                $"void {name}()",
                null));
        return new SeededSymbol(symbolId, fileId, path);
    }

    private sealed record SeededSymbol(
        long SymbolId,
        long FileId,
        string FilePath);
}
