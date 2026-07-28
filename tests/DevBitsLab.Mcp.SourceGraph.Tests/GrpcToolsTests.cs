using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;
using Core = DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

[Collection("LeafFormatterState")]
public sealed class GrpcToolsTests : IAsyncLifetime
{
    private const string Package = "fixture.v1";
    private const string Service = Package + ".Api";
    private const string RpcKey = "proto:R:" + Service + ".Run";
    private const string FieldKey =
        "proto:F:" + Package + ".Request.value";
    private const string ClientKey =
        "csharp:M:Fixture.Client.Send(System.Int32)";
    private const string ServerKey =
        "csharp:M:Fixture.Server.Run(Fixture.Request,Grpc.Core.ServerCallContext)";
    private const string GeneratedClientKey =
        "csharp:M:Fixture.Generated.Api.ApiClient.RunAsync(Fixture.Generated.Request,Grpc.Core.CallOptions)";

    private readonly List<ScopeHost> _hosts = [];
    private string _root = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-grpc-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(
        nameof(GrpcTools.TraceRpcAsync),
        "trace_rpc",
        typeof(TraceRpcResult))]
    [InlineData(
        nameof(GrpcTools.CheckProtoContractAsync),
        "check_proto_contract",
        typeof(CheckProtoContractResult))]
    public void Tools_publish_named_object_root_structured_schemas(
        string methodName,
        string expectedName,
        Type expectedOutput)
    {
        var method = typeof(GrpcTools).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        var attribute =
            method!.GetCustomAttribute<McpServerToolAttribute>();
        attribute.Should().NotBeNull();
        attribute!.UseStructuredContent.Should().BeTrue();
        attribute.OutputSchemaType.Should().Be(expectedOutput);
        method.GetCustomAttribute<ToolAnnotationAttribute>()
            .Should().Match<ToolAnnotationAttribute>(annotation =>
                annotation.ReadOnlyHint == true
                && annotation.IdempotentHint == true);
        McpServerTool.Create(
                method,
                target: null,
                new McpServerToolCreateOptions())
            .ProtocolTool.Name.Should().Be(expectedName);
        ToolOutputJsonContext.Default.GetTypeInfo(expectedOutput)
            .Should().NotBeNull();
        if (expectedOutput == typeof(CheckProtoContractResult))
        {
            var schema = JsonSchemaExporter.GetJsonSchemaAsNode(
                JsonSerializerOptions.Web,
                expectedOutput);
            schema["properties"]?["scopes"]?["items"]?["properties"]?
                    ["findings"]?["items"]?["properties"]?.AsObject()
                .ContainsKey("relation").Should().BeTrue();
        }
    }

    [Fact]
    public async Task Trace_rpc_reverses_stored_managed_to_proto_edges_with_evidence()
    {
        var fixture = await CreateHostAsync(
            "alpha",
            includeServer: true);
        var router = new ScopeRouter();
        router.Register(fixture.Host);
        router.SetDefaultScope("alpha");

        var fromProto = ReadTrace(await GrpcTools.TraceRpcAsync(
            router,
            RpcKey));
        var fromManaged = ReadTrace(await GrpcTools.TraceRpcAsync(
            router,
            ClientKey));

        fromProto.Scopes.Should().ContainSingle();
        var rpc = fromProto.Scopes[0].Rpcs.Should()
            .ContainSingle().Which;
        rpc.CanonicalKey.Should().Be(RpcKey);
        rpc.StoredOrientation.Should()
            .Be("managed-source-to-proto-rpc-target");
        rpc.Clients.Should().ContainSingle();
        rpc.Servers.Should().ContainSingle();
        rpc.Clients[0].StoredSource.Should().Be(ClientKey);
        rpc.Clients[0].StoredTarget.Should().Be(RpcKey);
        rpc.Clients[0].TraversalDirection.Should()
            .Be("reverse-inbound-from-proto-rpc");
        rpc.Clients[0].Evidence.Should().NotBeEmpty();
        rpc.Clients[0].Evidence.Should().OnlyContain(evidence =>
            evidence.FilePath == fixture.ClientPath);
        fromManaged.Scopes[0].SelectionStatus.Should()
            .Be("selected_managed_symbol");
        fromManaged.Scopes[0].Rpcs.Should().ContainSingle(item =>
            item.CanonicalKey == RpcKey);
    }

    [Fact]
    public async Task Scope_comma_and_wildcard_forms_are_sorted_and_bounded()
    {
        var alpha = await CreateHostAsync("alpha", includeServer: true);
        var beta = await CreateHostAsync("beta", includeServer: true);
        var router = new ScopeRouter();
        router.Register(beta.Host);
        router.Register(alpha.Host);

        var comma = ReadTrace(await GrpcTools.TraceRpcAsync(
            router,
            RpcKey,
            scope: "beta,alpha"));
        var wildcard = ReadTrace(await GrpcTools.TraceRpcAsync(
            router,
            RpcKey,
            scope: "*"));

        comma.Scopes.Select(scope => scope.ScopeId)
            .Should().Equal("alpha", "beta");
        wildcard.Scopes.Select(scope => scope.ScopeId)
            .Should().Equal("alpha", "beta");
        comma.Scopes.Should().OnlyContain(scope =>
            scope.Rpcs.Count == 1
            && scope.TotalClientCount == 1
            && scope.TotalServerCount == 1);
    }

    [Fact]
    public async Task Caught_query_failures_set_the_MCP_error_flag()
    {
        var fixture = await CreateHostAsync(
            "default",
            includeServer: true);
        var router = new ScopeRouter();
        router.Register(fixture.Host);
        router.SetDefaultScope("default");
        await fixture.Host.Store.ReplaceAnnotationsForFileByFlavorAsync(
            fixture.ProtoPath,
            ProtoContractAnnotations.Flavor,
            [AnnotationFact(RpcFact(serverStreaming: false), "{}")]);

        var trace = await GrpcTools.TraceRpcAsync(router, RpcKey);
        var check = await GrpcTools.CheckProtoContractAsync(router);

        trace.IsError.Should().BeTrue(
            "caught query failure payload was {0}",
            trace.StructuredContent?.GetRawText());
        check.IsError.Should().BeTrue(
            "caught query failure payload was {0}",
            check.StructuredContent?.GetRawText());
        ReadTrace(trace).Scopes.Should().ContainSingle()
            .Which.Failures.Should().ContainSingle(failure =>
                failure.Phase == "query");
        ReadCheck(check).Scopes.Should().ContainSingle()
            .Which.Failures.Should().ContainSingle(failure =>
                failure.Phase == "query");
    }

    [Fact]
    public async Task Partial_runtime_accounts_for_every_retained_failure()
    {
        var fixture = await CreateHostAsync(
            "default",
            includeServer: true);
        var failures = Enumerable.Range(0, 20)
            .Select(index => new GrpcLinkFailure(
                $"failure-{index:D2}",
                $"failure {index}",
                RpcKey))
            .ToArray();
        var state = new GrpcLinkRuntimeState(
            GrpcLinkRuntimeStatus.Partial,
            ProtoContracts: 1,
            ClientLinks: 1,
            ServerLinks: 1,
            RetainedLastGood: true,
            FailureCount: failures.Length,
            OmittedFailures: 0,
            failures);

        var scope = await new GrpcContractQueryService().TraceAsync(
            "default",
            "ok",
            fixture.Host.Store,
            state,
            RpcKey,
            GrpcContractQueryService.MaximumRelationsPerRpc);

        scope.Failures.Should().HaveCount(failures.Length);
        scope.TotalFailureCount.Should().Be(failures.Length);
        scope.Status.Should().Be("partial");
        scope.Partial.Should().BeTrue();
        scope.RetainedLastGood.Should().BeTrue();
        scope.Rpcs.Should().ContainSingle(rpc =>
            rpc.CanonicalKey == RpcKey);
        scope.TotalRpcCount.Should().Be(1);
        scope.TotalClientCount.Should().Be(1);
        scope.TotalServerCount.Should().Be(1);
        scope.Truncated.Should().BeFalse();
        scope.OmittedCount.Should().Be(0);
    }

    [Fact]
    public async Task First_observation_is_baseline_then_real_changes_have_both_evidence_sets()
    {
        var fixture = await CreateHostAsync(
            "default",
            includeServer: true);
        var router = new ScopeRouter();
        router.Register(fixture.Host);
        router.SetDefaultScope("default");

        var first = ReadCheck(
            await GrpcTools.CheckProtoContractAsync(router));
        first.TotalFindingCount.Should().Be(0);

        await ReplaceCurrentContractsAsync(
            fixture,
            fieldNumber: 9,
            serverStreaming: true);
        fixture.Host.GrpcLinkState = CompleteState(
        [
            new GrpcLinkFailure(
                "grpc-signature-mismatch",
                "The generated client signature does not match the current streaming contract.",
                GeneratedClientKey,
                RpcKey,
                "client"),
        ]);

        var changedCall =
            await GrpcTools.CheckProtoContractAsync(router);
        var changed = ReadCheck(changedCall);
        changed.Scopes[0].Findings.Select(finding => finding.RuleId)
            .Should().Contain(["Grpc002", "Grpc003", "Grpc004"]);
        changed.Scopes[0].Findings.Should().OnlyContain(finding =>
            finding.Relation == "diagnoses-contract");
        CallToolResultHelpers.ProseText(changedCall).Should().Contain(
            "relation=`diagnoses-contract`");
        var field = changed.Scopes[0].Findings.Single(finding =>
            finding.RuleId == "Grpc002");
        field.Confidence.Should().Be("semantic");
        field.BaselineProvenance.Should().Be(
            "first-complete-successful-observation-per-exact-canonical-key");
        field.Details.Should().Contain("baseline_number", "1")
            .And.Contain("current_number", "9");
        field.CurrentEvidence.Should().ContainSingle();
        field.BaselineEvidence.Should().ContainSingle();
        field.BaselineEvidence[0].Producer.Should()
            .Be("grpc-contract-baseline-v1");
        var streaming = changed.Scopes[0].Findings.Single(finding =>
            finding.RuleId == "Grpc003");
        streaming.Details.Should()
            .Contain("baseline_server_streaming", "false")
            .And.Contain("current_server_streaming", "true");
        changed.Scopes[0].Findings.Single(finding =>
                finding.RuleId == "Grpc004")
            .GeneratedRole.Should().Be("client");
    }

    [Fact]
    public async Task Complete_scope_without_server_edge_reports_rpc_no_implementation()
    {
        var fixture = await CreateHostAsync(
            "default",
            includeServer: false);
        var router = new ScopeRouter();
        router.Register(fixture.Host);
        router.SetDefaultScope("default");

        var result = ReadCheck(await GrpcTools.CheckProtoContractAsync(
            router,
            RpcKey));

        var finding = result.Scopes[0].Findings
            .Should().ContainSingle(item =>
                item.RuleId == "Grpc001")
            .Which;
        finding.Relation.Should().Be("diagnoses-contract");
        finding.Confidence.Should().Be("semantic");
        finding.CurrentEvidence.Should().ContainSingle();
        finding.BaselineEvidence.Should().BeEmpty();
    }

    [Fact]
    public async Task Partial_or_malformed_refresh_retains_baseline_and_emits_no_speculation()
    {
        var fixture = await CreateHostAsync(
            "default",
            includeServer: true);
        var baselinesBefore = await fixture.Host.Store
            .ListGrpcContractBaselinesAsync(100);
        await fixture.Host.Store.ReplaceAnnotationsForFileByFlavorAsync(
            fixture.ProtoPath,
            ProtoContractAnnotations.Flavor,
            [
                AnnotationFact(
                    RpcFact(serverStreaming: true),
                    payloadOverride: """{"version":999}"""),
            ]);
        fixture.Host.GrpcLinkState = new GrpcLinkRuntimeState(
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
                    "grpc-payload-malformed",
                    "The current proto payload is malformed.",
                    RpcKey),
            ]);
        var router = new ScopeRouter();
        router.Register(fixture.Host);
        router.SetDefaultScope("default");

        var check = ReadCheck(
            await GrpcTools.CheckProtoContractAsync(router));
        var trace = ReadTrace(
            await GrpcTools.TraceRpcAsync(router, RpcKey));

        check.TotalFindingCount.Should().Be(0);
        check.Scopes[0].Partial.Should().BeTrue();
        check.Scopes[0].RetainedLastGood.Should().BeTrue();
        trace.Scopes[0].Partial.Should().BeTrue();
        trace.Scopes[0].RetainedLastGood.Should().BeTrue();
        (await fixture.Host.Store
                .ListGrpcContractBaselinesAsync(100))
            .Should().Equal(baselinesBefore);
    }

    [Fact]
    public async Task Deleted_current_proto_is_absent_while_history_remains_dormant()
    {
        var fixture = await CreateHostAsync(
            "default",
            includeServer: true);
        (await fixture.Host.Store.DeleteFileAsync(fixture.ProtoPath))
            .Should().BeTrue();
        fixture.Host.GrpcLinkState = new GrpcLinkRuntimeState(
            GrpcLinkRuntimeStatus.Complete,
            ProtoContracts: 0,
            ClientLinks: 0,
            ServerLinks: 0,
            RetainedLastGood: false,
            FailureCount: 0,
            OmittedFailures: 0,
            Failures: []);
        var router = new ScopeRouter();
        router.Register(fixture.Host);
        router.SetDefaultScope("default");

        var check = ReadCheck(
            await GrpcTools.CheckProtoContractAsync(router));
        var trace = ReadTrace(
            await GrpcTools.TraceRpcAsync(router, RpcKey));

        check.TotalContractCount.Should().Be(0);
        check.TotalFindingCount.Should().Be(0);
        trace.TotalRpcCount.Should().Be(0);
        trace.Scopes[0].SelectionStatus.Should().Be("not_found");
        (await fixture.Host.Store
                .ListGrpcContractBaselinesAsync(100))
            .Should().NotBeEmpty(
                "the insert-only baseline is history, not a current declaration");
    }

    [Fact]
    public void Oversized_findings_are_reduced_deterministically_below_50k()
    {
        var evidence = new GrpcToolEvidenceRow(
            ProducingFileId: 1,
            FilePath: new string('p', 4000),
            StartLine: 1,
            StartColumn: 1,
            EndLine: 1,
            EndColumn: 2,
            Confidence: "semantic",
            Producer: "test",
            Metadata: Enumerable.Range(0, 40)
                .ToDictionary(
                    index => $"key-{index}",
                    _ => new string('v', 1000),
                    StringComparer.Ordinal),
            MetadataOmittedCount: 0,
            ObservedAtUnixMs: null);
        var findings = Enumerable.Range(0, 1000)
            .Select(index => new GrpcContractFindingRow(
                "Grpc003",
                "diagnoses-contract",
                "streaming_changed",
                "error",
                "semantic",
                new string('m', 4000),
                $"proto:R:fixture.v1.Api.Run{index}",
                null,
                null,
                "first-complete-successful-observation-per-exact-canonical-key",
                new Dictionary<string, string>
                {
                    ["detail"] = new string('d', 4000),
                },
                [evidence],
                [evidence],
                EvidenceOmittedCount: 0))
            .ToArray();
        var scope = new GrpcContractCheckScopeResult(
            "alpha",
            "ok",
            "ok",
            Partial: false,
            RetainedLastGood: false,
            "first-complete-successful-observation-per-exact-canonical-key",
            TotalContractCount: findings.Length,
            findings,
            TotalFindingCount: findings.Length,
            Failures: [],
            TotalFailureCount: 0,
            Truncated: false,
            OmittedCount: 0,
            OmittedEvidenceCount: 0);

        var first = GrpcTools.BuildBoundedCheckForTests(null, [scope]);
        var second = GrpcTools.BuildBoundedCheckForTests(null, [scope]);
        var firstJson = JsonSerializer.Serialize(
            first,
            McpJsonUtilities.DefaultOptions);
        var secondJson = JsonSerializer.Serialize(
            second,
            McpJsonUtilities.DefaultOptions);

        firstJson.Length.Should().BeLessThanOrEqualTo(
            OutputBudget.DefaultBudgetChars);
        firstJson.Should().Be(secondJson);
        var result = ReadCheck(first);
        result.Truncated.Should().BeTrue();
        result.OmittedCount.Should().BeGreaterThan(0);
        result.TotalFindingCount.Should().Be(findings.Length);
        result.Scopes.SelectMany(scope => scope.Findings)
            .Should().OnlyContain(finding =>
                finding.Relation == "diagnoses-contract");
    }

    private async Task<FixtureHost> CreateHostAsync(
        string id,
        bool includeServer)
    {
        var root = Path.Join(_root, id);
        Directory.CreateDirectory(root);
        var store = new SqliteGraphStore(Path.Join(root, "graph.db"));
        await store.EnsureSchemaAsync();
        var protoPath = Path.Join(root, "contracts.proto");
        var clientPath = Path.Join(root, "Client.cs");
        var serverPath = Path.Join(root, "Server.cs");
        var generatedPath = Path.Join(root, "ApiGrpc.cs");
        var protoFileId = await SeedFileAsync(store, protoPath);
        var clientFileId = await SeedFileAsync(store, clientPath);
        var serverFileId = await SeedFileAsync(store, serverPath);
        var generatedFileId = await SeedFileAsync(store, generatedPath);

        var facts = CurrentFacts(
            fieldNumber: 1,
            serverStreaming: false);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (fact, line) in facts.Select(
            (fact, index) => (fact, index + 1)))
        {
            ids[fact.SymbolCanonicalKey] =
                await store.UpsertSymbolAsync(
                    fact.SymbolCanonicalKey,
                    ProtoSymbol(protoFileId, fact, line));
        }
        await store.BulkInsertAnnotationsAsync(
            facts.Select(fact => Annotation(
                ids[fact.SymbolCanonicalKey],
                fact)));

        var clientId = await store.UpsertSymbolAsync(
            ClientKey,
            ManagedSymbol(
                clientFileId,
                "Send",
                "Fixture.Client.Send",
                clientPath,
                "public Task Send(int value)"));
        var serverId = await store.UpsertSymbolAsync(
            ServerKey,
            ManagedSymbol(
                serverFileId,
                "Run",
                "Fixture.Server.Run",
                serverPath,
                "public override Task<Reply> Run(Request request, ServerCallContext context)",
                "override"));
        await store.UpsertSymbolAsync(
            GeneratedClientKey,
            ManagedSymbol(
                generatedFileId,
                "RunAsync",
                "Fixture.Generated.Api.ApiClient.RunAsync",
                generatedPath,
                "public virtual AsyncUnaryCall<Reply> RunAsync(Request request, CallOptions options)",
                "virtual"));

        var rpcId = ids[RpcKey];
        var edges = new List<Edge>
        {
            EvidenceEdge(
                clientId,
                rpcId,
                EdgeKinds.GrpcCalls,
                clientFileId,
                clientPath),
        };
        if (includeServer)
        {
            edges.Add(EvidenceEdge(
                serverId,
                rpcId,
                EdgeKinds.ImplementsRpc,
                serverFileId,
                serverPath));
        }
        await store.BulkInsertEdgesAsync(edges);
        await store.EnsureGrpcContractBaselinesAsync(
            facts.Select((fact, index) =>
                new GrpcContractBaselineFact(
                    fact.SymbolCanonicalKey,
                    ProtoContractPayloadCodec.Encode(fact),
                    protoPath,
                    index + 1,
                    1,
                    index + 1,
                    20))
                .ToArray());

        var solutionPath = Path.Join(root, "fixture.sln");
        var scope = new Scope(
            id,
            id,
            root,
            new ScopeProjectSet.Solutions(
                [solutionPath],
                Exclude: []),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solutionPath)
        {
            Status = "ok",
            GrpcLinkState = CompleteState([]),
        };
        host.MarkReady();
        _hosts.Add(host);
        return new FixtureHost(
            host,
            protoPath,
            clientPath,
            serverPath);
    }

    private static async Task ReplaceCurrentContractsAsync(
        FixtureHost fixture,
        int fieldNumber,
        bool serverStreaming)
    {
        var facts = CurrentFacts(fieldNumber, serverStreaming);
        await fixture.Host.Store.ReplaceAnnotationsForFileByFlavorAsync(
            fixture.ProtoPath,
            ProtoContractAnnotations.Flavor,
            facts.Select(fact => AnnotationFact(fact)).ToArray());
    }

    private static IReadOnlyList<ProtoContractFact> CurrentFacts(
        int fieldNumber,
        bool serverStreaming) =>
    [
        MessageFact(Package + ".Request"),
        FieldFact(fieldNumber),
        MessageFact(Package + ".Reply"),
        RpcFact(serverStreaming),
    ];

    private static ProtoContractFact MessageFact(string fullName) =>
        new(
            ProtoContractKind.Message,
            ProtoCanonicalKeys.ForMessage(fullName),
            Package,
            fullName,
            ProtoContractStatus.Complete,
            [],
            0,
            new ProtoMessageContract(null, 0),
            null,
            null);

    private static ProtoContractFact FieldFact(int number) =>
        new(
            ProtoContractKind.Field,
            FieldKey,
            Package,
            Package + ".Request.value",
            ProtoContractStatus.Complete,
            [],
            0,
            null,
            new ProtoFieldContract(
                Package + ".Request",
                "int32",
                number,
                ProtoFieldCardinality.Singular,
                null),
            null);

    private static ProtoContractFact RpcFact(bool serverStreaming) =>
        new(
            ProtoContractKind.Rpc,
            RpcKey,
            Package,
            Service + ".Run",
            ProtoContractStatus.Complete,
            [],
            0,
            null,
            null,
            new ProtoRpcContract(
                Service,
                Package + ".Request",
                Package + ".Reply",
                ClientStreaming: false,
                serverStreaming));

    private static AnnotationRecord Annotation(
        long symbolId,
        ProtoContractFact fact) =>
        new(
            symbolId,
            AnnotationName(fact.Kind),
            AnnotationFullName(fact.Kind),
            ProtoContractAnnotations.Flavor,
            ProtoContractPayloadCodec.Encode(fact),
            null);

    private static FileAnnotationFact AnnotationFact(
        ProtoContractFact fact,
        string? payloadOverride = null) =>
        new(
            fact.SymbolCanonicalKey,
            AnnotationName(fact.Kind),
            AnnotationFullName(fact.Kind),
            ProtoContractAnnotations.Flavor,
            payloadOverride
                ?? ProtoContractPayloadCodec.Encode(fact),
            null);

    private static string AnnotationName(ProtoContractKind kind) =>
        kind switch
        {
            ProtoContractKind.Message => "ProtoMessageContract",
            ProtoContractKind.Field => "ProtoFieldContract",
            ProtoContractKind.Rpc => "ProtoRpcContract",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string AnnotationFullName(ProtoContractKind kind) =>
        kind switch
        {
            ProtoContractKind.Message =>
                "protobuf.contract.v1.message",
            ProtoContractKind.Field =>
                "protobuf.contract.v1.field",
            ProtoContractKind.Rpc =>
                "protobuf.contract.v1.rpc",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static Symbol ProtoSymbol(
        long fileId,
        ProtoContractFact fact,
        int line) =>
        new(
            0,
            fact.FullName[(fact.FullName.LastIndexOf('.') + 1)..],
            fact.FullName,
            fact.Kind switch
            {
                ProtoContractKind.Message => SymbolKinds.Message,
                ProtoContractKind.Field => SymbolKinds.ProtoField,
                ProtoContractKind.Rpc => SymbolKinds.Rpc,
                _ => throw new ArgumentOutOfRangeException(nameof(fact)),
            },
            fileId,
            line,
            1,
            line,
            20,
            fact.Kind.ToString(),
            null);

    private static Symbol ManagedSymbol(
        long fileId,
        string name,
        string fqn,
        string path,
        string signature,
        string? modifiers = null)
    {
        _ = path;
        return new Symbol(
            0,
            name,
            fqn,
            SymbolKinds.Method,
            fileId,
            1,
            1,
            1,
            20,
            signature,
            null,
            Modifiers: modifiers);
    }

    private static Edge EvidenceEdge(
        long source,
        long target,
        string kind,
        long fileId,
        string path) =>
        new(source, target, kind)
        {
            Evidence = new Evidence(
                fileId,
                new Core.SourceLocation(path, 2, 1, 2, 10),
                Core.EvidenceConfidence.Semantic,
                GrpcContractLinker.Producer,
                new Dictionary<string, string>
                {
                    ["evidence_role"] =
                        kind == EdgeKinds.GrpcCalls
                            ? "managed-call"
                            : "managed-override",
                }),
        };

    private static async Task<long> SeedFileAsync(
        SqliteGraphStore store,
        string path) =>
        await store.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);

    private static GrpcLinkRuntimeState CompleteState(
        IReadOnlyList<GrpcLinkFailure> failures) =>
        new(
            GrpcLinkRuntimeStatus.Complete,
            ProtoContracts: 1,
            ClientLinks: 1,
            ServerLinks: 1,
            RetainedLastGood: false,
            FailureCount: failures.Count,
            OmittedFailures: 0,
            failures);

    private static TraceRpcResult ReadTrace(CallToolResult result) =>
        result.StructuredContent?.Deserialize(
            ToolOutputJsonContext.Default.TraceRpcResult)
        ?? throw new Xunit.Sdk.XunitException(
            "trace_rpc returned no typed payload.");

    private static CheckProtoContractResult ReadCheck(
        CallToolResult result) =>
        result.StructuredContent?.Deserialize(
            ToolOutputJsonContext.Default.CheckProtoContractResult)
        ?? throw new Xunit.Sdk.XunitException(
            "check_proto_contract returned no typed payload.");

    private sealed record FixtureHost(
        ScopeHost Host,
        string ProtoPath,
        string ClientPath,
        string ServerPath);
}
