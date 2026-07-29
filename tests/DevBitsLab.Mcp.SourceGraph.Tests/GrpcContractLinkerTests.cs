using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using Core = DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class GrpcContractLinkerTests : IAsyncLifetime
{
    private const string Package = "medinterop.algorithm.v1";
    private const string Service = Package + ".AlgorithmApi";
    private const string RpcFullName = Service + ".Calculate";
    private const string RequestType = Package + ".CalculateRequest";
    private const string ResponseType = Package + ".CalculateReply";

    private string _root = string.Empty;
    private string _dbPath = string.Empty;
    private string _protoPath = string.Empty;
    private string _generatedPath = string.Empty;
    private string _clientPath = string.Empty;
    private string _serverPath = string.Empty;
    private SqliteGraphStore? _store;
    private SeededGraph? _graph;

    public async Task InitializeAsync()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-grpc-linker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "graph.db");
        _protoPath = Path.Join(_root, "algorithm.proto");
        _generatedPath = Path.Join(_root, "AlgorithmApi.g.cs");
        _clientPath = Path.Join(_root, "AlgorithmService.cs");
        _serverPath = Path.Join(_root, "AlgorithmGrpcService.cs");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
        _graph = await SeedCompleteUnaryGraphAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Complete_unary_contract_links_business_caller_and_server_override()
    {
        var result = await new GrpcContractLinker(_store!).RunAsync(
            sourceUniverseComplete: true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Complete);
        result.State.ClientLinks.Should().Be(1);
        result.State.ServerLinks.Should().Be(1);
        result.State.RetainedLastGood.Should().BeFalse();
        result.State.Coverage.Should().BeEquivalentTo(
            new GrpcLinkCoverage(
                CompleteRpcContracts: 1,
                IncompleteRpcContracts: 0,
                MissingGeneratedClients: 0,
                MissingGeneratedServers: 0,
                UnlinkedManagedMembers: 0,
                AffectedProtoFiles: []));
        var baselines = await _store!
            .ListGrpcContractBaselinesAsync(100);
        baselines.Should().HaveCount(3);
        ProtoContractPayloadCodec.Decode(
                baselines.Single(row =>
                    row.SymbolCanonicalKey
                    == ProtoCanonicalKeys.ForRpc(
                        Service,
                        "Calculate"))
                    .ContractJson)
            .Rpc!.ServerStreaming.Should().BeFalse();
        var clientEvidence = await _store!.ListEdgeEvidenceAsync(
            _graph!.ClientCallerId,
            _graph.RpcId,
            EdgeKinds.GrpcCalls);
        clientEvidence.Should().HaveCount(2);
        clientEvidence.Should().OnlyContain(evidence =>
            evidence.Producer == GrpcContractLinker.Producer
            && evidence.Confidence == Core.EvidenceConfidence.Semantic);
        var managedCall = clientEvidence.Single(evidence =>
            evidence.Metadata!["evidence_role"] == "managed-call");
        managedCall.Location.FilePath.Should().Be(_clientPath);
        managedCall.Location.StartLine.Should().Be(3);
        managedCall.Metadata.Should().Contain(
            "generated_member",
            _graph.ClientMemberKey);
        managedCall.Metadata.Should().Contain(
            "match",
            "roslyn-call-to-generated-client");
        managedCall.Metadata.Should().Contain(
            "descriptor_proof",
            "structural-signature-only");
        managedCall.Metadata.Should().Contain(
            "upstream_producer",
            "roslyn");
        clientEvidence.Single(evidence =>
                evidence.Metadata!["evidence_role"] == "proto-contract")
            .Location.FilePath.Should().Be(_protoPath);

        var serverEvidence = await _store.ListEdgeEvidenceAsync(
            _graph.ServerOverrideId,
            _graph.RpcId,
            EdgeKinds.ImplementsRpc);
        serverEvidence.Should().HaveCount(2);
        var managedOverride = serverEvidence.Single(evidence =>
            evidence.Metadata!["evidence_role"] == "managed-override");
        managedOverride.Location.FilePath.Should().Be(_serverPath);
        managedOverride.Metadata.Should().Contain(
            "match",
            "roslyn-override-to-generated-base");
        var dispatchEvidence = await _store.ListEdgeEvidenceAsync(
            _graph.RpcId,
            _graph.ServerOverrideId,
            EdgeKinds.RpcDispatchesTo);
        dispatchEvidence.Should().HaveCount(2);
        dispatchEvidence.Single(evidence =>
                evidence.Metadata!["evidence_role"] == "managed-override")
            .Metadata.Should().Contain(
                "match",
                "proto-dispatch-to-managed-override");
        dispatchEvidence.Single(evidence =>
                evidence.Metadata!["evidence_role"] == "proto-contract")
            .Location.FilePath.Should().Be(_protoPath);

        await new GrpcContractLinker(_store).RunAsync(
            sourceUniverseComplete: true);
        (await _store.ListEdgeEvidenceAsync(
                _graph.ClientCallerId,
                _graph.RpcId,
                EdgeKinds.GrpcCalls))
            .Should().HaveCount(
                2,
                "deterministic replacement must not duplicate evidence");
    }

    [Fact]
    public async Task Multiple_roslyn_call_occurrences_are_preserved_with_separate_proto_evidence()
    {
        var clientFile = (await _store!.GetAllFilesAsync())
            .Single(file => file.Path == _clientPath);
        await _store.BulkInsertEdgesAsync(
        [
            new Edge(
                _graph!.ClientCallerId,
                _graph.ClientMemberId,
                EdgeKinds.Calls)
            {
                Evidence = new Evidence(
                    clientFile.Id,
                    new Core.SourceLocation(
                        _clientPath,
                        9,
                        5,
                        9,
                        19),
                    Core.EvidenceConfidence.Exact,
                    "roslyn"),
            },
        ]);

        var result = await new GrpcContractLinker(_store).RunAsync(true);

        result.State.ClientLinks.Should().Be(1);
        var evidence = await _store.ListEdgeEvidenceAsync(
            _graph.ClientCallerId,
            _graph.RpcId,
            EdgeKinds.GrpcCalls);
        evidence.Should().HaveCount(3);
        evidence.Where(item =>
                item.Metadata!["evidence_role"] == "managed-call")
            .Select(item => item.Location.StartLine)
            .Should().Equal(3, 9);
        evidence.Should().ContainSingle(item =>
            item.Metadata!["evidence_role"] == "proto-contract");
    }

    [Theory]
    [InlineData("wrong-type")]
    [InlineData("wrong-streaming")]
    public async Task Proven_signature_mismatch_publishes_no_links(
        string mismatch)
    {
        if (mismatch == "wrong-type")
        {
            await UpdateSignatureAsync(
                _graph!.ClientMemberId,
                "public virtual AsyncUnaryCall<WrongReply> CalculateAsync(CalculateRequest request, Metadata headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default(CancellationToken))");
            await UpdateSignatureAsync(
                _graph.BaseMemberId,
                "public virtual Task<WrongReply> Calculate(CalculateRequest request, ServerCallContext context)");
        }
        else
        {
            await ReplaceRpcFactAsync(
                CreateRpcFact(serverStreaming: true));
        }

        var result = await new GrpcContractLinker(_store!).RunAsync(true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Complete);
        result.State.ClientLinks.Should().Be(0);
        result.State.ServerLinks.Should().Be(0);
        result.State.Failures.Should().Contain(failure =>
            failure.Code == "grpc-signature-mismatch");
        (await _store!.ListEdgeEvidenceAsync(
                _graph!.ClientCallerId,
                _graph.RpcId,
                EdgeKinds.GrpcCalls))
            .Should().BeEmpty();
        (await _store.ListEdgeEvidenceAsync(
                _graph.ServerOverrideId,
                _graph.RpcId,
                EdgeKinds.ImplementsRpc))
            .Should().BeEmpty();
        (await _store.ListEdgeEvidenceAsync(
                _graph.RpcId,
                _graph.ServerOverrideId,
                EdgeKinds.RpcDispatchesTo))
            .Should().BeEmpty(
                "a complete replacement must clean the prior execution-direction dispatch");
    }

    [Fact]
    public async Task Structurally_identical_contracts_are_ambiguous_and_not_guessed()
    {
        await SeedSecondContractAsync();

        var result = await new GrpcContractLinker(_store!).RunAsync(true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Complete);
        result.State.ClientLinks.Should().Be(0);
        result.State.ServerLinks.Should().Be(0);
        result.State.Failures.Should().Contain(failure =>
            failure.Code == "grpc-contract-ambiguous");
    }

    [Fact]
    public async Task First_incomplete_pass_publishes_positive_links_without_authoritative_absence()
    {
        var result = await new GrpcContractLinker(_store!).RunAsync(
            sourceUniverseComplete: false);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Partial);
        result.State.ProtoContracts.Should().Be(1);
        result.State.ClientLinks.Should().Be(1);
        result.State.ServerLinks.Should().Be(1);
        result.State.RetainedLastGood.Should().BeTrue();
        result.State.Failures.Should().ContainSingle(failure =>
            failure.Code == "grpc-input-incomplete");
        result.State.Coverage.Should().NotBeNull();
        result.State.Coverage!.IncompleteRpcs.Should().BeEmpty(
            "the concrete RPC is fully linked even though unrelated source input is incomplete");
        (await _store!.HasEdgeEvidenceByProducerAsync(
                GrpcContractLinker.Producer))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Coverage_lists_each_incomplete_rpc_and_which_generated_role_is_missing()
    {
        await UpdateSignatureAsync(
            _graph!.ClientMemberId,
            "public virtual AsyncUnaryCall<WrongReply> CalculateAsync(CalculateRequest request, CallOptions options)");

        var result = await new GrpcContractLinker(_store!).RunAsync(
            sourceUniverseComplete: true);

        result.State.Coverage.Should().NotBeNull();
        result.State.Coverage!.IncompleteRpcs.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new GrpcIncompleteRpcDetail(
                    ProtoCanonicalKeys.ForRpc(Service, "Calculate"),
                    _protoPath,
                    MissingGeneratedClient: true,
                    MissingGeneratedServer: false));
        result.State.Coverage.OmittedIncompleteRpcDetails.Should().Be(0);
    }

    [Fact]
    public async Task First_malformed_contract_does_not_claim_a_last_good_projection()
    {
        await UpdateRpcPayloadAsync("""{"version":1,"kind":""");

        var result = await new GrpcContractLinker(_store!).RunAsync(
            sourceUniverseComplete: true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Partial);
        result.State.RetainedLastGood.Should().BeFalse();
        result.State.Failures.Should().Contain(failure =>
            failure.Code == "grpc-payload-malformed");
        (await _store!.HasEdgeEvidenceByProducerAsync(
                GrpcContractLinker.Producer))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("partial")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task Invalid_contract_universe_retains_last_good_projection(
        string invalidity)
    {
        var linker = new GrpcContractLinker(_store!);
        (await linker.RunAsync(true)).State.Status.Should()
            .Be(GrpcLinkRuntimeStatus.Complete);
        var baselineBefore =
            (await _store!.ListGrpcContractBaselinesAsync(100))
            .Single(row =>
                row.SymbolCanonicalKey
                == ProtoCanonicalKeys.ForRpc(Service, "Calculate"));

        if (invalidity == "malformed")
        {
            await UpdateRpcPayloadAsync("""{"version":1,"kind":""");
        }
        else if (invalidity == "partial")
        {
            await ReplaceRpcFactAsync(
                CreateRpcFact(
                    status: ProtoContractStatus.Partial,
                    incompleteReasons: ["descriptor-incomplete"]));
        }
        else if (invalidity == "missing")
        {
            await DeleteRpcAnnotationAsync();
        }
        else
        {
            await _store!.BulkInsertAnnotationsAsync(
            [
                Annotation(
                    _graph!.RpcId,
                    "ProtoRpcContract",
                    "protobuf.contract.v1.rpc",
                    CreateRpcFact()),
            ]);
        }

        var result = await new GrpcContractLinker(_store!).RunAsync(true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Partial);
        result.State.RetainedLastGood.Should().BeTrue();
        result.State.Failures.Should().Contain(failure =>
            failure.Code == "grpc-payload-malformed"
            || failure.Code == "grpc-contract-partial"
            || failure.Code == "grpc-contract-fact-missing"
            || failure.Code == "grpc-contract-duplicate");
        (await _store.ListGrpcContractBaselinesAsync(100))
            .Single(row =>
                row.SymbolCanonicalKey
                == ProtoCanonicalKeys.ForRpc(Service, "Calculate"))
            .Should().Be(baselineBefore);
        (await _store!.ListEdgeEvidenceAsync(
                _graph!.ClientCallerId,
                _graph.RpcId,
                EdgeKinds.GrpcCalls))
            .Should().HaveCount(2);
        (await _store.ListEdgeEvidenceAsync(
                _graph.ServerOverrideId,
                _graph.RpcId,
                EdgeKinds.ImplementsRpc))
            .Should().HaveCount(2);
        (await _store.ListEdgeEvidenceAsync(
                _graph.RpcId,
                _graph.ServerOverrideId,
                EdgeKinds.RpcDispatchesTo))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task Incomplete_index_pass_does_not_read_or_replace_last_good()
    {
        var linker = new GrpcContractLinker(_store!);
        await linker.RunAsync(true);

        var result = await new GrpcContractLinker(_store!).RunAsync(
            sourceUniverseComplete: false);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Partial);
        result.State.RetainedLastGood.Should().BeTrue(
            "a restarted linker must discover the persisted prior projection");
        result.State.Failures.Should().ContainSingle(failure =>
            failure.Code == "grpc-input-incomplete");
        (await _store!.ListEdgeEvidenceAsync(
                _graph!.ClientCallerId,
                _graph.RpcId,
                EdgeKinds.GrpcCalls))
            .Should().HaveCount(2);
        (await _store.ListEdgeEvidenceAsync(
                _graph.RpcId,
                _graph.ServerOverrideId,
                EdgeKinds.RpcDispatchesTo))
            .Should().HaveCount(
                2,
                "an incomplete pass must retain execution-direction dispatch evidence");
    }

    [Fact]
    public async Task Complete_mismatch_cleans_only_linker_owned_evidence()
    {
        var linker = new GrpcContractLinker(_store!);
        await linker.RunAsync(true);
        await _store!.BulkInsertEdgesAsync(
        [
            new Edge(
                _graph!.ClientCallerId,
                _graph.RpcId,
                EdgeKinds.GrpcCalls)
            {
                Evidence = new Evidence(
                    _graph.ProtoFileId,
                    new Core.SourceLocation(
                        _protoPath,
                        7,
                        1,
                        7,
                        10),
                    Core.EvidenceConfidence.Exact,
                    "independent-grpc-analyzer",
                    new Dictionary<string, string>
                    {
                        ["proof"] = "independent",
                    }),
            },
        ]);
        await UpdateSignatureAsync(
            _graph.ClientMemberId,
            "public virtual AsyncUnaryCall<WrongReply> CalculateAsync(CalculateRequest request, CallOptions options)");

        var result = await linker.RunAsync(true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Complete);
        var evidence = await _store.ListEdgeEvidenceAsync(
            _graph.ClientCallerId,
            _graph.RpcId,
            EdgeKinds.GrpcCalls);
        evidence.Should().ContainSingle();
        evidence[0].Producer.Should().Be("independent-grpc-analyzer");
        (await _store.ListEdgeEvidenceAsync(
                _graph.RpcId,
                _graph.ServerOverrideId,
                EdgeKinds.RpcDispatchesTo))
            .Should().HaveCount(
                2,
                "the independently valid server dispatch is republished by the complete rebuild");
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = () => new GrpcContractLinker(_store!).RunAsync(
            true,
            cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false, "service.cs", true)]
    [InlineData(false, "contracts.proto", true)]
    [InlineData(false, "native.cpp", false)]
    [InlineData(true, "native.cpp", true)]
    public void Live_refresh_policy_covers_managed_proto_and_project_control(
        bool projectControlChanged,
        string path,
        bool expected)
    {
        LiveIndexService.ShouldRefreshGrpcProjection(
                projectControlChanged,
                [Path.Join(_root, path)])
            .Should().Be(expected);
    }

    [Fact]
    public async Task MedInterop_fixture_one_shot_shape_links_real_roslyn_members()
    {
        var fixtureRoot = LocateMedInteropFixture();
        var dbPath = Path.Join(_root, "fixture.db");
        await using var store = new SqliteGraphStore(dbPath);
        await store.EnsureSchemaAsync();
        var roslyn = await RoslynIndexer.IndexSolutionOnceAsync(
            Path.Join(fixtureRoot, "MedInteropChain.slnx"),
            store);
        roslyn.FailedProjects.Should().BeEmpty();
        roslyn.FailedFiles.Should().BeEmpty();

        var registry = new LanguageIndexerRegistry();
        registry.Register(new ProtobufLanguageIndexer());
        var dispatcher = new LanguageIndexerDispatcher(
            registry,
            new LanguageProjectFactoryRegistry());
        var proto = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            fixtureRoot,
            new Dictionary<string, ILanguageProject>());
        proto.FailedProjects.Should().BeEmpty();
        proto.FailedFiles.Should().BeEmpty();

        var result = await new GrpcContractLinker(store).RunAsync(true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Complete);
        var failureSummary = string.Join(
            "; ",
            result.State.Failures.Select(failure =>
                $"{failure.Code}:{failure.SymbolCanonicalKey}"));
        result.State.ClientLinks.Should().Be(
            1,
            failureSummary);
        result.State.ServerLinks.Should().Be(1);
        var keys = (await store.GetAllSymbolKeysAsync())
            .ToDictionary(row => row.CanonicalKey, StringComparer.Ordinal);
        var rpc = keys[
            "proto:R:medinterop.algorithm.v1.AlgorithmApi.Calculate"];
        var caller = keys[
            "csharp:M:MedInteropChain.GrpcService.AlgorithmService.CalculateAsync(System.Int32,System.Threading.CancellationToken)"];
        var server = keys[
            "csharp:M:MedInteropChain.GrpcService.AlgorithmGrpcService.Calculate(MedInteropChain.GrpcService.Generated.CalculateRequest,Grpc.Core.ServerCallContext)"];
        (await store.ListEdgeEvidenceAsync(
                caller.Id,
                rpc.Id,
                EdgeKinds.GrpcCalls))
            .Should().HaveCount(2);
        (await store.ListEdgeEvidenceAsync(
                server.Id,
                rpc.Id,
                EdgeKinds.ImplementsRpc))
            .Should().HaveCount(2);
        (await store.ListEdgeEvidenceAsync(
                rpc.Id,
                server.Id,
                EdgeKinds.RpcDispatchesTo))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task GrpcTools_generated_fixture_links_actual_generated_client_and_base()
    {
        var fixtureRoot = LocateFixture("GrpcToolsGenerated");
        File.Exists(Path.Join(
                fixtureRoot,
                "Generated",
                "Protos",
                "EchoGrpc.cs"))
            .Should().BeTrue(
                "the fixture build must run the pinned Grpc.Tools generator");
        var dbPath = Path.Join(_root, "grpc-tools-fixture.db");
        await using var store = new SqliteGraphStore(dbPath);
        await store.EnsureSchemaAsync();
        var roslyn = await RoslynIndexer.IndexSolutionOnceAsync(
            Path.Join(fixtureRoot, "GrpcToolsGenerated.slnx"),
            store);
        roslyn.FailedProjects.Should().BeEmpty();
        roslyn.FailedFiles.Should().BeEmpty();

        var registry = new LanguageIndexerRegistry();
        registry.Register(new ProtobufLanguageIndexer());
        var dispatcher = new LanguageIndexerDispatcher(
            registry,
            new LanguageProjectFactoryRegistry());
        var proto = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            fixtureRoot,
            new Dictionary<string, ILanguageProject>());
        proto.FailedProjects.Should().BeEmpty();
        proto.FailedFiles.Should().BeEmpty();

        var result = await new GrpcContractLinker(store).RunAsync(true);

        result.State.Status.Should().Be(GrpcLinkRuntimeStatus.Complete);
        var failureSummary = string.Join(
            "; ",
            result.State.Failures.Select(failure =>
                $"{failure.Code}:{failure.SymbolCanonicalKey}"));
        result.State.ClientLinks.Should().Be(
            1,
            failureSummary);
        result.State.ServerLinks.Should().Be(1);
        var keys = (await store.GetAllSymbolKeysAsync())
            .ToDictionary(row => row.CanonicalKey, StringComparer.Ordinal);
        var rpc = keys[
            "proto:R:grpc.tools.fixture.v1.EchoApi.Echo"];
        var caller = keys[
            "csharp:M:GrpcToolsGenerated.ClientFacade.SendAsync(System.String,System.Threading.CancellationToken)"];
        var server = keys[
            "csharp:M:GrpcToolsGenerated.EchoService.Echo(GrpcToolsGenerated.Generated.EchoRequest,Grpc.Core.ServerCallContext)"];
        var clientEvidence = await store.ListEdgeEvidenceAsync(
            caller.Id,
            rpc.Id,
            EdgeKinds.GrpcCalls);
        clientEvidence.Should().HaveCount(2);
        clientEvidence.Single(evidence =>
                evidence.Metadata!["evidence_role"] == "managed-call")
            .Location.FilePath.Should().Be(
                Path.Join(fixtureRoot, "ClientFacade.cs"));
        clientEvidence.Single(evidence =>
                evidence.Metadata!["evidence_role"] == "proto-contract")
            .Location.FilePath.Should().Be(
                Path.Join(fixtureRoot, "Protos", "echo.proto"));

        var serverEvidence = await store.ListEdgeEvidenceAsync(
            server.Id,
            rpc.Id,
            EdgeKinds.ImplementsRpc);
        serverEvidence.Should().HaveCount(2);
        serverEvidence.Single(evidence =>
                evidence.Metadata!["evidence_role"] == "managed-override")
            .Location.FilePath.Should().Be(
                Path.Join(fixtureRoot, "EchoService.cs"));

        var trace = await new GrpcContractQueryService().TraceAsync(
            "test",
            "ok",
            store,
            result.State,
            rpc.CanonicalKey,
            GrpcContractQueryService.MaximumRelationsPerRpc);
        trace.Status.Should().Be("ok");
        trace.Rpcs.Should().ContainSingle();
        trace.Rpcs[0].Clients.Should().ContainSingle(relation =>
            relation.ManagedSymbol == caller.CanonicalKey);
        trace.Rpcs[0].Servers.Should().ContainSingle(relation =>
            relation.ManagedSymbol == server.CanonicalKey);
    }

    private async Task<SeededGraph> SeedCompleteUnaryGraphAsync()
    {
        var protoFileId = await SeedFileAsync(_protoPath);
        var generatedFileId = await SeedFileAsync(_generatedPath);
        var clientFileId = await SeedFileAsync(_clientPath);
        var serverFileId = await SeedFileAsync(_serverPath);

        var requestId = await SeedSymbolAsync(
            protoFileId,
            ProtoCanonicalKeys.ForMessage(RequestType),
            "CalculateRequest",
            RequestType,
            SymbolKinds.Message,
            "message CalculateRequest");
        var responseId = await SeedSymbolAsync(
            protoFileId,
            ProtoCanonicalKeys.ForMessage(ResponseType),
            "CalculateReply",
            ResponseType,
            SymbolKinds.Message,
            "message CalculateReply");
        var rpcId = await SeedSymbolAsync(
            protoFileId,
            ProtoCanonicalKeys.ForRpc(Service, "Calculate"),
            "Calculate",
            RpcFullName,
            SymbolKinds.Rpc,
            "rpc Calculate(CalculateRequest) returns (CalculateReply)",
            line: 7);
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                requestId,
                "ProtoMessageContract",
                "protobuf.contract.v1.message",
                CreateMessageFact(RequestType)),
            Annotation(
                responseId,
                "ProtoMessageContract",
                "protobuf.contract.v1.message",
                CreateMessageFact(ResponseType)),
            Annotation(
                rpcId,
                "ProtoRpcContract",
                "protobuf.contract.v1.rpc",
                CreateRpcFact()),
        ]);

        const string generatedNamespace =
            "MedInteropChain.GrpcService.Generated";
        var outerName = generatedNamespace + ".AlgorithmApi";
        var clientName = outerName + ".AlgorithmApiClient";
        var baseName = outerName + ".AlgorithmApiBase";
        await SeedSymbolAsync(
            generatedFileId,
            "csharp:T:" + outerName,
            "AlgorithmApi",
            outerName,
            SymbolKinds.Class,
            "public static class AlgorithmApi",
            modifiers: "static");
        await SeedSymbolAsync(
            generatedFileId,
            "csharp:T:" + clientName,
            "AlgorithmApiClient",
            clientName,
            SymbolKinds.Class,
            "public class AlgorithmApiClient");
        await SeedSymbolAsync(
            generatedFileId,
            "csharp:T:" + baseName,
            "AlgorithmApiBase",
            baseName,
            SymbolKinds.Class,
            "public abstract class AlgorithmApiBase",
            modifiers: "abstract");
        await SeedSymbolAsync(
            generatedFileId,
            "csharp:F:" + outerName + ".__ServiceName",
            "__ServiceName",
            outerName + ".__ServiceName",
            SymbolKinds.Field,
            "private static readonly string __ServiceName",
            modifiers: "static,readonly");
        await SeedSymbolAsync(
            generatedFileId,
            "csharp:F:" + outerName + ".__Method_Calculate",
            "__Method_Calculate",
            outerName + ".__Method_Calculate",
            SymbolKinds.Field,
            "private static readonly Method<CalculateRequest, CalculateReply> __Method_Calculate",
            modifiers: "static,readonly");

        var requestClr =
            generatedNamespace + ".CalculateRequest";
        var clientMemberKey =
            "csharp:M:" + clientName
            + ".CalculateAsync("
            + requestClr
            + ",Grpc.Core.Metadata,System.Nullable{System.DateTime},System.Threading.CancellationToken)";
        var clientMemberId = await SeedSymbolAsync(
            generatedFileId,
            clientMemberKey,
            "CalculateAsync",
            clientName + ".CalculateAsync",
            SymbolKinds.Method,
            "public virtual AsyncUnaryCall<CalculateReply> CalculateAsync(CalculateRequest request, Metadata headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default(CancellationToken))",
            modifiers: "virtual");
        var baseMemberKey =
            "csharp:M:" + baseName
            + ".Calculate("
            + requestClr
            + ",Grpc.Core.ServerCallContext)";
        var baseMemberId = await SeedSymbolAsync(
            generatedFileId,
            baseMemberKey,
            "Calculate",
            baseName + ".Calculate",
            SymbolKinds.Method,
            "public virtual Task<CalculateReply> Calculate(CalculateRequest request, ServerCallContext context)",
            modifiers: "virtual");

        var callerKey =
            "csharp:M:MedInteropChain.GrpcService.AlgorithmService.CalculateAsync(System.Int32,System.Threading.CancellationToken)";
        var clientCallerId = await SeedSymbolAsync(
            clientFileId,
            callerKey,
            "CalculateAsync",
            "MedInteropChain.GrpcService.AlgorithmService.CalculateAsync",
            SymbolKinds.Method,
            "public async Task<int> CalculateAsync(int patientAge, CancellationToken cancellationToken = default(CancellationToken))",
            modifiers: "async");
        var overrideKey =
            "csharp:M:MedInteropChain.GrpcService.AlgorithmGrpcService.Calculate("
            + requestClr
            + ",Grpc.Core.ServerCallContext)";
        var serverOverrideId = await SeedSymbolAsync(
            serverFileId,
            overrideKey,
            "Calculate",
            "MedInteropChain.GrpcService.AlgorithmGrpcService.Calculate",
            SymbolKinds.Method,
            "public override Task<CalculateReply> Calculate(CalculateRequest request, ServerCallContext context)",
            modifiers: "override");
        await _store.BulkInsertEdgesAsync(
        [
            EvidenceEdge(
                clientCallerId,
                clientMemberId,
                EdgeKinds.Calls,
                clientFileId,
                _clientPath,
                "roslyn"),
            EvidenceEdge(
                serverOverrideId,
                baseMemberId,
                EdgeKinds.OverridesMember,
                serverFileId,
                _serverPath,
                "roslyn"),
        ]);

        return new SeededGraph(
            protoFileId,
            rpcId,
            clientCallerId,
            serverOverrideId,
            clientMemberId,
            baseMemberId,
            clientMemberKey);
    }

    private async Task SeedSecondContractAsync()
    {
        const string secondPackage = "shadow.algorithm.v1";
        const string secondService =
            secondPackage + ".AlgorithmApi";
        var request = secondPackage + ".CalculateRequest";
        var response = secondPackage + ".CalculateReply";
        var requestId = await SeedSymbolAsync(
            _graph!.ProtoFileId,
            ProtoCanonicalKeys.ForMessage(request),
            "CalculateRequest",
            request,
            SymbolKinds.Message,
            "message CalculateRequest",
            line: 20);
        var responseId = await SeedSymbolAsync(
            _graph.ProtoFileId,
            ProtoCanonicalKeys.ForMessage(response),
            "CalculateReply",
            response,
            SymbolKinds.Message,
            "message CalculateReply",
            line: 24);
        var rpcFullName = secondService + ".Calculate";
        var rpcId = await SeedSymbolAsync(
            _graph.ProtoFileId,
            ProtoCanonicalKeys.ForRpc(secondService, "Calculate"),
            "Calculate",
            rpcFullName,
            SymbolKinds.Rpc,
            "rpc Calculate(CalculateRequest) returns (CalculateReply)",
            line: 18);
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                requestId,
                "ProtoMessageContract",
                "protobuf.contract.v1.message",
                CreateMessageFact(request, secondPackage)),
            Annotation(
                responseId,
                "ProtoMessageContract",
                "protobuf.contract.v1.message",
                CreateMessageFact(response, secondPackage)),
            Annotation(
                rpcId,
                "ProtoRpcContract",
                "protobuf.contract.v1.rpc",
                new ProtoContractFact(
                    ProtoContractKind.Rpc,
                    ProtoCanonicalKeys.ForRpc(
                        secondService,
                        "Calculate"),
                    secondPackage,
                    rpcFullName,
                    ProtoContractStatus.Complete,
                    [],
                    0,
                    null,
                    null,
                    new ProtoRpcContract(
                        secondService,
                        request,
                        response,
                        false,
                        false))),
        ]);
    }

    private static ProtoContractFact CreateMessageFact(
        string fullName,
        string package = Package) =>
        new(
            ProtoContractKind.Message,
            ProtoCanonicalKeys.ForMessage(fullName),
            package,
            fullName,
            ProtoContractStatus.Complete,
            [],
            0,
            new ProtoMessageContract(null, 0),
            null,
            null);

    private static ProtoContractFact CreateRpcFact(
        bool serverStreaming = false,
        ProtoContractStatus status = ProtoContractStatus.Complete,
        IReadOnlyList<string>? incompleteReasons = null) =>
        new(
            ProtoContractKind.Rpc,
            ProtoCanonicalKeys.ForRpc(Service, "Calculate"),
            Package,
            RpcFullName,
            status,
            incompleteReasons ?? [],
            0,
            null,
            null,
            new ProtoRpcContract(
                Service,
                RequestType,
                ResponseType,
                false,
                serverStreaming));

    private static AnnotationRecord Annotation(
        long symbolId,
        string name,
        string fullName,
        ProtoContractFact fact) =>
        new(
            symbolId,
            name,
            fullName,
            ProtoContractAnnotations.Flavor,
            ProtoContractPayloadCodec.Encode(fact),
            null);

    private async Task ReplaceRpcFactAsync(ProtoContractFact fact) =>
        await UpdateRpcPayloadAsync(
            ProtoContractPayloadCodec.Encode(fact));

    private async Task UpdateRpcPayloadAsync(string payload)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={_dbPath}");
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE annotations
            SET args_json = @payload
            WHERE symbol_id = @symbolId
              AND flavor = @flavor;
            """,
            new
            {
                payload,
                symbolId = _graph!.RpcId,
                flavor = ProtoContractAnnotations.Flavor,
            });
    }

    private async Task DeleteRpcAnnotationAsync()
    {
        await using var connection = new SqliteConnection(
            $"Data Source={_dbPath}");
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            DELETE FROM annotations
            WHERE symbol_id = @symbolId
              AND flavor = @flavor;
            """,
            new
            {
                symbolId = _graph!.RpcId,
                flavor = ProtoContractAnnotations.Flavor,
            });
    }

    private async Task UpdateSignatureAsync(
        long symbolId,
        string signature)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={_dbPath}");
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE symbols SET signature = @signature WHERE id = @symbolId;",
            new { signature, symbolId });
    }

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);

    private async Task<long> SeedSymbolAsync(
        long fileId,
        string key,
        string name,
        string fqn,
        string kind,
        string signature,
        int line = 1,
        string? modifiers = null) =>
        await _store!.UpsertSymbolAsync(
            key,
            new Symbol(
                0,
                name,
                fqn,
                kind,
                fileId,
                line,
                1,
                line,
                Math.Max(2, name.Length + 1),
                signature,
                null,
                Modifiers: modifiers));

    private static Edge EvidenceEdge(
        long sourceId,
        long targetId,
        string kind,
        long producingFileId,
        string path,
        string producer) =>
        new(sourceId, targetId, kind)
        {
            Evidence = new Evidence(
                producingFileId,
                new Core.SourceLocation(path, 3, 1, 3, 12),
                Core.EvidenceConfidence.Exact,
                producer,
                null),
        };

    private static string LocateMedInteropFixture()
        => LocateFixture("MedInteropChain");

    private static string LocateFixture(string fixtureName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(
                directory.FullName,
                "tests",
                "fixtures",
                fixtureName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate tests/fixtures/{fixtureName}.");
    }

    private sealed record SeededGraph(
        long ProtoFileId,
        long RpcId,
        long ClientCallerId,
        long ServerOverrideId,
        long ClientMemberId,
        long BaseMemberId,
        string ClientMemberKey);
}
