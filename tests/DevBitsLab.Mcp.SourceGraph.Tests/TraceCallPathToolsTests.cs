using System.Reflection;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
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
        var starveSource = await SeedSymbolAsync(store, "StarveSource");
        var malformedFirst = await SeedSymbolAsync(store, "AardvarkMalformed");
        var directTarget = await SeedSymbolAsync(store, "ZDirectTarget");
        var terminalSource = await SeedSymbolAsync(store, "TerminalSource");
        var terminalLeaf = await SeedSymbolAsync(store, "TerminalLeaf");
        var ui = await SeedSymbolAsync(store, "Ui");
        var commandProperty = await SeedSymbolAsync(store, "Command");
        var commandHandler = await SeedSymbolAsync(store, "CommandHandler");
        var service = await SeedSymbolAsync(store, "Service");
        var rpc = await SeedSymbolAsync(store, "Rpc");
        var server = await SeedSymbolAsync(store, "Server");
        var import = await SeedSymbolAsync(store, "Import");
        var export = await SeedSymbolAsync(store, "Export");
        var native = await SeedSymbolAsync(store, "Native");

        await store.BulkInsertEdgesAsync(new[]
        {
            Edge(a, b, 5, CoreEvidenceConfidence.Exact, "a-to-b"),
            Edge(b, c, 8, CoreEvidenceConfidence.Semantic, "b-to-c"),
            Edge(a, d, 6, CoreEvidenceConfidence.Exact, "a-to-d"),
            Edge(d, c, 9, CoreEvidenceConfidence.Exact, "d-to-c"),
            Edge(c, a, 12, CoreEvidenceConfidence.Exact, "cycle"),
            Edge(starveSource, directTarget, 15, CoreEvidenceConfidence.Exact, "auditable-direct"),
            Edge(terminalSource, terminalLeaf, 16, CoreEvidenceConfidence.Exact, "terminal-leaf"),
            Edge(ui, commandProperty, 20, CoreEvidenceConfidence.Semantic, "binding", "binds-path"),
            Edge(commandProperty, commandHandler, 21, CoreEvidenceConfidence.Semantic, "command", EdgeKinds.CommandExecutes),
            Edge(commandHandler, service, 22, CoreEvidenceConfidence.Exact, "managed-call"),
            Edge(service, rpc, 23, CoreEvidenceConfidence.Semantic, "grpc-call", EdgeKinds.GrpcCalls),
            Edge(rpc, server, 24, CoreEvidenceConfidence.Semantic, "rpc-dispatch", EdgeKinds.RpcDispatchesTo),
            Edge(server, import, 25, CoreEvidenceConfidence.Exact, "server-call"),
            Edge(import, export, 26, CoreEvidenceConfidence.Exact, "pinvoke", EdgeKinds.PInvokeMapsTo),
            Edge(export, native, 27, CoreEvidenceConfidence.Exact, "native-call"),
            Edge(ui, native, 28, CoreEvidenceConfidence.Exact, "excluded-shortcut", EdgeKinds.Tests),
        });

        // This raw legacy edge sorts before ZDirectTarget but has no occurrence evidence. An
        // evidence-first branch query must remove it before applying its one-row branch cap.
        await using (var connection = new SqliteConnection(
                         $"Data Source={Path.Join(_tempDir, "graph.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO edges(src, dst, kind_name) VALUES ($src, $dst, $kind);";
            command.Parameters.AddWithValue("$src", starveSource.SymbolId);
            command.Parameters.AddWithValue("$dst", malformedFirst.SymbolId);
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
        var indexer = new RoslynIndexer(store);
        _host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            indexer,
            solutionPath: "");
        _host.GrpcLinkState = new GrpcLinkRuntimeState(
            GrpcLinkRuntimeStatus.Complete,
            ProtoContracts: 1,
            ClientLinks: 1,
            ServerLinks: 1,
            RetainedLastGood: false,
            FailureCount: 0,
            OmittedFailures: 0,
            Failures: []);
        _host.MarkReady();
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");

        Edge Edge(
            SeededSymbol source,
            SeededSymbol target,
            int line,
            CoreEvidenceConfidence confidence,
            string marker,
            string kind = EdgeKinds.Calls) =>
            new(source.SymbolId, target.SymbolId, kind)
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
            nameof(TraceCallPathTools.TraceCallPathWithProfileAsync),
            BindingFlags.Public | BindingFlags.Static);
        var tool = McpServerTool.Create(
            method!,
            target: null,
            new McpServerToolCreateOptions());

        tool.ProtocolTool.Name.Should().Be("trace_call_path");
        var annotation = method!.GetCustomAttribute<ToolAnnotationAttribute>();
        annotation.Should().NotBeNull();
        annotation!.ReadOnlyHint.Should().BeTrue();
        annotation.IdempotentHint.Should().BeTrue();
    }

    [Fact]
    public async Task Trace_returnsEveryHopWithEvidence_andDetectsCycles()
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
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
        var shallow = await TraceCallPathTools.TraceCallPathWithProfileAsync(
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

    [Fact]
    public async Task Trace_filtersMalformedEdgesBeforeExactBranchAndPathCaps()
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: "Graph.StarveSource",
            to: "Graph.ZDirectTarget",
            maxDepth: 1,
            maxPaths: 1,
            maxNodes: 1);

        var scope = result.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!
            .Scopes.Should().ContainSingle().Which;
        scope.Paths.Should().ContainSingle();
        scope.Paths[0].Hops.Should().ContainSingle()
            .Which.To.Fqn.Should().Be("Graph.ZDirectTarget");
        scope.Truncated.Should().BeFalse(
            "the only evidence-backed branch and path exactly fit every configured cap");
    }

    [Fact]
    public async Task Trace_depthBoundaryIsComplete_whenFrontierHasNoAuditableChildren()
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: "Graph.TerminalSource",
            to: "Graph.C",
            maxDepth: 1);

        var scope = result.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!
            .Scopes.Should().ContainSingle().Which;
        scope.Paths.Should().BeEmpty();
        scope.Truncated.Should().BeFalse(
            "reaching maxDepth at a terminal evidence-backed frontier leaves no work unexplored");
    }

    [Fact]
    public async Task ExecutionProfile_tracesOnlyWhitelistedRelations_fromExactCanonicalKeys()
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: "csharp:M:Graph.Ui",
            to: "csharp:M:Graph.Native",
            profile: "execution",
            maxDepth: 8,
            maxPaths: 10,
            maxNodes: 100);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!;
        dto.Profile.Should().Be("execution");
        dto.EdgeKind.Should().BeNull();
        dto.Relations.Should().Equal(
            "binds-path",
            EdgeKinds.CommandExecutes,
            EdgeKinds.Calls,
            EdgeKinds.GrpcCalls,
            EdgeKinds.RpcDispatchesTo,
            EdgeKinds.PInvokeMapsTo);
        var scope = dto.Scopes.Should().ContainSingle().Which;
        scope.ExecutionState.Should().NotBeNull();
        scope.ExecutionState!.Status.Should().Be("partial");
        scope.ExecutionState.AbsenceAuthoritative.Should().BeFalse(
            "the fixture deliberately has no native projection configuration");
        scope.ExecutionState.Projections.Should().ContainSingle(projection =>
            projection.Name == "native-interop"
            && projection.Status == "not-configured"
            && !projection.Authoritative);
        var path = scope.Paths.Should().ContainSingle().Which;
        path.Hops.Should().HaveCount(8);
        path.Hops.Select(hop => hop.Relation).Should().Equal(
            "binds-path",
            EdgeKinds.CommandExecutes,
            EdgeKinds.Calls,
            EdgeKinds.GrpcCalls,
            EdgeKinds.RpcDispatchesTo,
            EdgeKinds.Calls,
            EdgeKinds.PInvokeMapsTo,
            EdgeKinds.Calls);

        var shallow = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: "csharp:M:Graph.Ui",
            to: "csharp:M:Graph.Native",
            profile: "execution",
            maxDepth: 7);
        var shallowScope = shallow.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!
            .Scopes.Should().ContainSingle().Which;
        shallowScope.Paths.Should().BeEmpty();
        shallowScope.Truncated.Should().BeTrue(
            "one proven execution hop remains beyond the requested boundary");
    }

    [Fact]
    public async Task ExactCanonicalSelection_neverFallsBackToFuzzyMatching()
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: "csharp:M:Graph.Ui.Missing",
            to: "csharp:M:Graph.Native",
            profile: "execution");

        var scope = result.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!
            .Scopes.Should().ContainSingle().Which;
        scope.Paths.Should().BeEmpty();
        scope.Note.Should().Contain(
            "No source symbol matches 'csharp:M:Graph.Ui.Missing'");
    }

    [Theory]
    [InlineData("csharp:")]
    [InlineData(@"csharp:M:Graph\Ui")]
    public async Task MalformedCanonicalIntent_isRejectedInsteadOfFuzzyMatched(
        string from)
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from,
            to: "csharp:M:Graph.Native",
            profile: "execution");

        result.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(result).Should().Contain(
            "`from` canonical key is invalid");
    }

    [Fact]
    public async Task Trace_rejectsUnboundedSymbolQueries()
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: new string('x', 4097),
            to: "Graph.Native");

        result.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(result).Should().Contain(
            "must not exceed 4096 characters");
    }

    [Fact]
    public async Task ExecutionProfile_disclosesPartialProjection_withoutHidingStoredPaths()
    {
        _host!.GrpcLinkState = new GrpcLinkRuntimeState(
            GrpcLinkRuntimeStatus.Partial,
            ProtoContracts: 1,
            ClientLinks: 1,
            ServerLinks: 1,
            RetainedLastGood: true,
            FailureCount: 1,
            OmittedFailures: 0,
            Failures:
            [
                new GrpcLinkFailure(
                    "fixture-partial",
                    "The current projection is incomplete.",
                    null),
            ]);

        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: "csharp:M:Graph.Ui",
            to: "csharp:M:Graph.Native",
            profile: "execution");
        var scope = result.StructuredContent!.Value.Deserialize(
            ToolOutputJsonContext.Default.TraceCallPathResult)!
            .Scopes.Should().ContainSingle().Which;

        scope.Paths.Should().ContainSingle(
            "persisted evidence remains queryable: {0}",
            CallToolResultHelpers.ProseText(result));
        scope.ExecutionState!.Status.Should().Be("partial");
        scope.ExecutionState.AbsenceAuthoritative.Should().BeFalse();
        scope.ExecutionState.RetainedLastGood.Should().BeTrue();
        scope.ExecutionState.Failures.Should().ContainSingle(
            failure => failure.Contains("fixture-partial", StringComparison.Ordinal));
        scope.Note.Should().Contain("partial projections");
    }

    [Fact]
    public async Task ExecutionProfile_rejectsAnExplicitRelation()
    {
        var result = await TraceCallPathTools.TraceCallPathWithProfileAsync(
            _router!,
            from: "Graph.Ui",
            to: "Graph.Native",
            kind: EdgeKinds.Calls,
            profile: "execution");

        result.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(result).Should().Contain(
            "does not accept `kind` together");
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
