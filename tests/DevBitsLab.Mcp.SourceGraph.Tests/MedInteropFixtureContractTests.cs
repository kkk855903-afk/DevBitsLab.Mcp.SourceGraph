using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using CoreEvidenceConfidence =
    DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation =
    DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class MedInteropFixtureContractTests
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private static readonly InteropTarget Target =
        InteropTarget.WindowsX64Msvc;

    [Fact]
    public async Task PositiveGraphContract_isProducedByCompleteProductionPipeline()
    {
        var expectedEdges = ReadExpectedEdges("required_edges");
        var expectedAuditEdges = ReadExpectedEdges(
            "required_audit_edges");
        expectedEdges.Should().HaveCount(8);
        expectedAuditEdges.Should().ContainSingle()
            .Which.Relation.Should().Be(EdgeKinds.ImplementsRpc);
        expectedEdges.Select(edge => edge.Relation).Should().Equal(
            "binds-path",
            EdgeKinds.CommandExecutes,
            EdgeKinds.Calls,
            EdgeKinds.GrpcCalls,
            EdgeKinds.RpcDispatchesTo,
            EdgeKinds.Calls,
            EdgeKinds.PInvokeMapsTo,
            EdgeKinds.Calls);
        expectedEdges.Zip(expectedEdges.Skip(1))
            .Should().OnlyContain(pair =>
                pair.First.To == pair.Second.From,
                "the golden contract must describe one contiguous execution path");

        var tempRoot = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-medinterop-chain-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        ScopeHost? host = null;
        try
        {
            var dbPath = Path.Join(tempRoot, "graph.db");
            var store = new SqliteGraphStore(dbPath);
            await store.EnsureSchemaAsync();
            var interop = CreateInteropConfig();
            var solutionPath = Path.Join(
                FixtureRoot,
                "MedInteropChain.slnx");
            var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: FixtureRoot,
                excludePatterns: [],
                interopTarget: Target);
            var scope = new Scope(
                "medinterop-chain",
                "MedInteropChain",
                FixtureRoot,
                new ScopeProjectSet.Solutions(
                    [solutionPath],
                    []),
                Isolated: false,
                DateTimeOffset.UtcNow)
            {
                Interop = interop,
            };
            host = new ScopeHost(
                scope,
                store,
                store.CreateEmbeddingsStore(384),
                indexer,
                solutionPath);

            await indexer.OpenAsync(solutionPath);
            var roslyn = await indexer.IndexAllAsync();
            roslyn.FailedProjects.Should().BeEmpty();
            roslyn.FailedFiles.Should().BeEmpty();
            indexer.IsProjectSemanticInputComplete(
                    Path.Join(
                        FixtureRoot,
                        "ManagedApp",
                        "ManagedApp.csproj"))
                .Should().BeTrue(
                    "the full fixture is inside the configured privacy root");

            var languages = new LanguageIndexerRegistry();
            languages.Register(new XamlLanguageIndexer());
            languages.Register(new ProtobufLanguageIndexer());
            var factories = new LanguageProjectFactoryRegistry();
            factories.Register(new XamlLanguageProjectFactory(
                () => indexer.SanitizedSolution,
                indexer.IsProjectSemanticInputComplete));
            var projectMap = await DiscoverLanguageProjectsAsync(
                factories);
            var dispatcher = new LanguageIndexerDispatcher(
                languages,
                factories);
            var language = await dispatcher.DispatchAllForTestAsync(
                store,
                scope.Id,
                FixtureRoot,
                projectMap);
            language.FailedProjects.Should().BeEmpty();
            language.FailedFiles.Should().BeEmpty();

            var grpc = await new GrpcContractLinker(store).RunAsync(
                sourceUniverseComplete: true);
            grpc.State.Status.Should().Be(
                GrpcLinkRuntimeStatus.Complete,
                GrpcFailureSummary(grpc.State));
            grpc.State.RetainedLastGood.Should().BeFalse();
            grpc.State.Failures.Should().BeEmpty();
            host.GrpcLinkState = grpc.State;

            var coordinator = new NativeInteropCoordinator(
                FixtureRoot,
                interop,
                new ScopePathPolicy(FixtureRoot),
                store,
                new FixedTrustPolicy(),
                ExtractNativeFixtureAsync,
                VerifyBinaryFixtureAsync);
            host.NativeInteropCoordinator = coordinator;
            var native = await coordinator.RunAsync(
                isManagedUniverseComplete: true);
            native.State.Status.Should().Be(
                NativeInteropRuntimeStatus.Complete,
                NativeFailureSummary(native.State));
            native.State.RetainedLastGood.Should().BeFalse();
            native.State.IsExportUniverseComplete.Should().BeTrue();
            native.State.ManagedMatches.Should().BeGreaterThanOrEqualTo(
                1,
                "the exact required P/Invoke match is asserted from the golden edge below");
            native.State.BoundaryEdges.Should().BeGreaterThanOrEqualTo(1);
            native.State.Failures.Should().BeEmpty();
            var nativeCall = native.Snapshot!.Calls.Should()
                .ContainSingle()
                .Which;
            nativeCall.CalleeSymbolCanonicalKey.Should().Be(
                expectedEdges[^1].To);
            native.Snapshot.Functions.Where(function =>
                    function.IsDefinition
                    && function.DeclarationUsr
                        == nativeCall.ReferencedDeclarationUsr)
                .Should().ContainSingle(
                    "the direct call's Clang USR must resolve to one exact definition")
                .Which.GraphCanonicalKey.Should().Be(
                    expectedEdges[^1].To);

            await AssertStoredGoldenContractAsync(
                store,
                expectedEdges);
            await AssertStoredGoldenContractAsync(
                store,
                expectedAuditEdges);
            await AssertGrpcAuditEvidenceAsync(
                store,
                expectedAuditEdges.Single());

            host.MarkReady();
            var router = new ScopeRouter();
            router.Register(host);
            router.SetDefaultScope(scope.Id);
            var result =
                await TraceCallPathTools.TraceCallPathWithProfileAsync(
                    router,
                    expectedEdges[0].From,
                    expectedEdges[^1].To,
                    profile: "execution",
                    maxDepth: 8,
                    maxPaths: 10,
                    maxNodes: 1000,
                    scope: scope.Id);

            result.IsError.Should().NotBe(true);
            var dto = result.StructuredContent!.Value.Deserialize(
                ToolOutputJsonContext.Default.TraceCallPathResult)!;
            dto.Profile.Should().Be("execution");
            var tracedScope = dto.Scopes.Should()
                .ContainSingle()
                .Which;
            tracedScope.ExecutionState.Should().NotBeNull();
            tracedScope.ExecutionState!.Status.Should().Be(
                "complete",
                "execution state was {0}",
                JsonSerializer.Serialize(tracedScope.ExecutionState));
            tracedScope.ExecutionState.Partial.Should().BeFalse();
            tracedScope.ExecutionState.AbsenceAuthoritative
                .Should().BeTrue();
            tracedScope.ExecutionState.Projections
                .Should().OnlyContain(projection =>
                    projection.Authoritative
                    && projection.Status == "complete"
                        || projection.Name == "scope"
                        && projection.Authoritative
                        && projection.Status == "ok");
            var path = tracedScope.Paths.Should()
                .ContainSingle("the fixture has one proven execution chain")
                .Which;
            path.Hops.Should().HaveCount(8);
            path.Hops.Select(hop => hop.Relation)
                .Should().Equal(expectedEdges.Select(edge => edge.Relation));
            path.Hops.Select(hop => hop.From.CanonicalKey)
                .Append(path.To.CanonicalKey)
                .Should().Equal(ExpectedNodeKeys(expectedEdges));
            path.Hops.Should().AllSatisfy(hop =>
            {
                hop.Evidence.Should().NotBeEmpty(
                    $"{hop.Relation} must carry occurrence evidence");
                hop.Evidence.Should().OnlyContain(evidence =>
                    !string.IsNullOrWhiteSpace(evidence.FilePath)
                    && evidence.StartLine > 0
                    && evidence.StartColumn > 0
                    && !string.IsNullOrWhiteSpace(evidence.Producer));
            });
            tracedScope.Truncated.Should().BeFalse();

            var discovered =
                await TraceCallPathTools.TraceCallPathWithProfileAsync(
                    router,
                    from: expectedEdges[0].From,
                    profile: "execution",
                    maxDepth: 8,
                    maxPaths: 10,
                    maxNodes: 1000,
                    scope: scope.Id);
            var discoveredDto = discovered.StructuredContent!.Value.Deserialize(
                ToolOutputJsonContext.Default.TraceCallPathResult)!;
            discoveredDto.ToQuery.Should().BeNull();
            discoveredDto.DestinationMode.Should().Be(
                "execution-terminal");
            var discoveredPath = discoveredDto.Scopes.Should()
                .ContainSingle()
                .Which.Paths.Should()
                .ContainSingle(
                    "the exact UI source has one proven leaf algorithm")
                .Which;
            discoveredPath.To.CanonicalKey.Should().Be(
                expectedEdges[^1].To);
            discoveredPath.Hops.Select(hop => hop.Relation)
                .Should().Equal(expectedEdges.Select(edge => edge.Relation));
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void FindingContract_hasOneIsolatedCaseForEveryInitialInteropRule()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(FixtureRoot, "Expected", "interop-findings.json")));
        var findings = document.RootElement.GetProperty("findings")
            .EnumerateArray()
            .ToList();

        findings.Select(finding => finding.GetProperty("rule").GetString())
            .Should().BeEquivalentTo(
                "Interop001",
                "Interop002",
                "Interop003",
                "Interop004",
                "Interop005",
                "Interop006");
        findings.Select(finding => finding.GetProperty("native_symbol").GetString())
            .Should().OnlyHaveUniqueItems();
        foreach (var finding in findings)
        {
            File.Exists(Path.Join(
                FixtureRoot,
                finding.GetProperty("managed_file").GetString()!)).Should().BeTrue();
            File.Exists(Path.Join(
                FixtureRoot,
                finding.GetProperty("native_file").GetString()!)).Should().BeTrue();
        }
    }

    [Fact]
    public void Fixture_containsNoMedicalImagesOrPatientDataDirectories()
    {
        var files = Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToList();

        files.Should().NotContain(path =>
            path.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        files.Should().NotContain(path =>
            path.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment.Equals("PatientData", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Logs", StringComparison.OrdinalIgnoreCase)));
    }

    private static string FindFixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(
                directory.FullName,
                "tests",
                "fixtures",
                "MedInteropChain");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate tests/fixtures/MedInteropChain.");
    }

    private static IReadOnlyList<ExpectedEdge> ReadExpectedEdges(
        string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(
                FixtureRoot,
                "Expected",
                "graph-contract.json")));
        document.RootElement.GetProperty("version").GetInt32()
            .Should().Be(1);
        return document.RootElement.GetProperty(propertyName)
            .EnumerateArray()
            .Select(edge => new ExpectedEdge(
                edge.GetProperty("from").GetString()!,
                edge.GetProperty("relation").GetString()!,
                edge.GetProperty("to").GetString()!))
            .ToArray();
    }

    private static IReadOnlyList<string?> ExpectedNodeKeys(
        IReadOnlyList<ExpectedEdge> edges) =>
        edges.Select(edge => (string?)edge.From)
            .Append(edges[^1].To)
            .ToArray();

    private static async Task<Dictionary<string, ILanguageProject>>
        DiscoverLanguageProjectsAsync(
            LanguageProjectFactoryRegistry factories)
    {
        var projectMap = new Dictionary<string, ILanguageProject>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var factory in factories.All())
        {
            var projects = await factory.DiscoverAsync(
                FixtureRoot,
                default);
            foreach (var project in projects)
            {
                foreach (var path in project.FilePaths)
                {
                    projectMap.TryAdd(path, project);
                }
            }
        }
        return projectMap;
    }

    private static async Task AssertStoredGoldenContractAsync(
        SqliteGraphStore store,
        IReadOnlyList<ExpectedEdge> expectedEdges)
    {
        foreach (var edge in expectedEdges)
        {
            var source = await store.GetSymbolByCanonicalKeyAsync(
                edge.From);
            var target = await store.GetSymbolByCanonicalKeyAsync(
                edge.To);
            source.Should().NotBeNull(
                $"the production pipeline must publish {edge.From}");
            target.Should().NotBeNull(
                $"the production pipeline must publish {edge.To}");
            var evidence = await store.ListEdgeEvidenceAsync(
                source!.Id,
                target!.Id,
                edge.Relation);
            var outgoing = await store.ListCalleesAsync(
                source.Id,
                limit: 50,
                edgeKind: edge.Relation);
            var incoming = await store.ListCallersAsync(
                target.Id,
                limit: 50,
                edgeKind: edge.Relation);
            var annotations = await store.GetAnnotationsForSymbolAsync(
                source.Id);
            evidence.Should().NotBeEmpty(
                $"{edge.From} --{edge.Relation}--> {edge.To} is a required evidence-backed bridge; "
                + "stored outgoing targets: "
                + string.Join(
                    ", ",
                    outgoing.Select(item => item.CanonicalKey))
                + "; stored incoming sources: "
                + string.Join(
                    ", ",
                    incoming.Select(item => item.CanonicalKey))
                + "; source annotations: "
                + string.Join(
                    ", ",
                    annotations.Select(item =>
                        $"{item.Flavor}/{item.FullName}/{item.ArgsJson}")));
            evidence.Should().OnlyContain(item =>
                item.Location.StartLine > 0
                && item.Location.StartColumn > 0
                && !string.IsNullOrWhiteSpace(item.Producer));
        }
    }

    private static async Task AssertGrpcAuditEvidenceAsync(
        SqliteGraphStore store,
        ExpectedEdge edge)
    {
        var source = await store.GetSymbolByCanonicalKeyAsync(
            edge.From);
        var target = await store.GetSymbolByCanonicalKeyAsync(
            edge.To);
        var evidence = await store.ListEdgeEvidenceAsync(
            source!.Id,
            target!.Id,
            edge.Relation);

        evidence.Should().HaveCount(2);
        evidence.Select(item =>
                item.Metadata!["evidence_role"])
            .Should().BeEquivalentTo(
                "managed-override",
                "proto-contract");
    }

    private static ScopeInteropConfig CreateInteropConfig() =>
        new(
            Target,
            [
                new InteropTranslationUnitConfig(
                    "NativeLibrary/src/exports.cpp",
                    "medalgo.dll",
                    ["-x", "c++"],
                    "NativeLibrary/medalgo.fixture"),
                new InteropTranslationUnitConfig(
                    "NativeLibrary/src/algorithm.cpp",
                    "medalgo.dll",
                    ["-x", "c++"],
                    BinaryPath: null),
            ]);

    private static Task<ClangNativeExtractionResult>
        ExtractNativeFixtureAsync(
            ClangNativeExtractionRequest request,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = Path.GetFileName(request.SourceFilePath);
        return fileName switch
        {
            "exports.cpp" => Task.FromResult(
                ExtractExports(request)),
            "algorithm.cpp" => Task.FromResult(
                ExtractAlgorithm(request)),
            _ => throw new InvalidOperationException(
                $"Unexpected fixture translation unit: {fileName}"),
        };
    }

    private static Task<BinaryExportVerificationResult>
        VerifyBinaryFixtureAsync(
            string binaryPath,
            InteropTarget target,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Path.GetFileName(binaryPath).Should().Be(
            "medalgo.fixture");
        target.IsAbiEquivalentTo(Target).Should().BeTrue();
        return Task.FromResult(
            new BinaryExportVerificationResult(
                BinaryExportVerificationStatus.Complete,
                InteropArchitecture.X64,
                0x8664,
                "medalgo.dll",
                [
                    new BinaryExportEntry(
                        1,
                        0x1000,
                        ["medalgo_calculate"],
                        IsForwarder: false,
                        Forwarder: null),
                ],
                "deterministic fixture export table"));
    }

    private static ClangNativeExtractionResult ExtractExports(
        ClangNativeExtractionRequest request)
    {
        const string exportKey =
            "c:E:NativeLibrary/src/exports.cpp::medalgo_calculate";
        const string exportFunctionKey =
            "c:F:NativeLibrary/src/exports.cpp::medalgo_calculate(const NativeInput *,NativeOutput *)";
        const string exportUsr = "c:@F@medalgo_calculate";
        const string algorithmUsr =
            "c:@S@Algorithm@F@Calculate#&1$@S@NativeInput#S";
        var inputPointer = PointerType(
            "const NativeInput *",
            "NativeInput",
            isConst: true);
        var outputPointer = PointerType(
            "NativeOutput *",
            "NativeOutput",
            isConst: false);
        var location = Location(request.SourceFilePath, line: 3);
        var parameters = new[]
        {
            new AbiParameter(
                0,
                "input",
                inputPointer,
                AbiParameterDirection.In,
                location),
            new AbiParameter(
                1,
                "output",
                outputPointer,
                AbiParameterDirection.Out,
                location),
        };
        var function = NativeFunction(
            request,
            exportFunctionKey,
            exportUsr,
            "medalgo_calculate",
            "medalgo_calculate",
            parameters,
            graphKey: exportKey,
            hasCLinkage: true,
            isExported: true,
            isMethod: false,
            line: 3);
        var export = new NativeExport(
            exportKey,
            "medalgo_calculate",
            InteropCallingConvention.Cdecl,
            IntType(),
            parameters,
            HasCLinkage: true,
            IsBinaryVerified: false,
            request.Target,
            NativeEvidence(request, line: 3))
        {
            LibraryName = request.LibraryName,
            ModuleIdentitySource =
                NativeModuleIdentitySource.Configuration,
        };
        var call = new NativeCallFact(
            exportKey,
            algorithmUsr,
            CalleeSymbolCanonicalKey: null,
            request.Target,
            new Evidence(
                request.ProducingFileId,
                Location(request.SourceFilePath, line: 9),
                CoreEvidenceConfidence.Exact,
                "clang-native-call",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["callKind"] = "direct",
                    ["target"] =
                        request.Target.RuntimeIdentifier,
                }));
        return Extraction(
            request,
            functions: [function],
            exports: [export],
            calls: [call]);
    }

    private static ClangNativeExtractionResult ExtractAlgorithm(
        ClangNativeExtractionRequest request)
    {
        const string algorithmKey =
            "cpp:F:NativeLibrary/src/algorithm.cpp::Algorithm::Calculate(const NativeInput &)";
        const string algorithmUsr =
            "c:@S@Algorithm@F@Calculate#&1$@S@NativeInput#S";
        var parameter = new AbiParameter(
            0,
            "input",
            new AbiTypeRef(
                "const NativeInput &",
                AbiTypeCategory.Pointer,
                pointerDepth: 1,
                sizeBytes: Target.PointerSizeBytes,
                alignmentBytes: Target.PointerSizeBytes,
                pointeeType: new AbiTypeRef(
                    "NativeInput",
                    AbiTypeCategory.Record),
                isPointeeConst: true),
            AbiParameterDirection.In,
            Location(request.SourceFilePath, line: 3));
        var function = NativeFunction(
            request,
            algorithmKey,
            algorithmUsr,
            "Calculate",
            "Algorithm::Calculate",
            [parameter],
            graphKey: algorithmKey,
            hasCLinkage: false,
            isExported: false,
            isMethod: true,
            line: 3);
        return Extraction(
            request,
            functions: [function]);
    }

    private static ClangNativeExtractionResult Extraction(
        ClangNativeExtractionRequest request,
        IReadOnlyList<NativeFunctionFact>? functions = null,
        IReadOnlyList<NativeExport>? exports = null,
        IReadOnlyList<NativeCallFact>? calls = null) =>
        new(
            functions ?? [],
            Types: [],
            exports ?? [],
            RecordLayouts: [],
            Diagnostics: [])
        {
            Calls = calls ?? [],
            IncludedFiles = [request.SourceFilePath],
            IsCallGraphComplete = true,
        };

    private static NativeFunctionFact NativeFunction(
        ClangNativeExtractionRequest request,
        string key,
        string usr,
        string name,
        string qualifiedName,
        IReadOnlyList<AbiParameter> parameters,
        string graphKey,
        bool hasCLinkage,
        bool isExported,
        bool isMethod,
        int line) =>
        new(
            key,
            name,
            qualifiedName,
            InteropCallingConvention.Cdecl,
            isMethod
                ? new AbiTypeRef(
                    "NativeOutput",
                    AbiTypeCategory.Record)
                : IntType(),
            parameters,
            hasCLinkage,
            isExported,
            IsDefinition: true,
            NativeEvidence(request, line))
        {
            DeclarationUsr = usr,
            GraphCanonicalKey = graphKey,
            IsMethod = isMethod,
            Target = request.Target,
        };

    private static Evidence NativeEvidence(
        ClangNativeExtractionRequest request,
        int line) =>
        new(
            request.ProducingFileId,
            Location(request.SourceFilePath, line),
            CoreEvidenceConfidence.Exact,
            "clang-native",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["declarationKind"] = "function",
                ["isDefinition"] = "true",
                ["target"] = request.Target.RuntimeIdentifier,
            });

    private static AbiTypeRef PointerType(
        string name,
        string pointeeName,
        bool isConst) =>
        new(
            name,
            AbiTypeCategory.Pointer,
            pointerDepth: 1,
            sizeBytes: Target.PointerSizeBytes,
            alignmentBytes: Target.PointerSizeBytes,
            pointeeType: new AbiTypeRef(
                pointeeName,
                AbiTypeCategory.Record),
            isPointeeConst: isConst);

    private static AbiTypeRef IntType() =>
        new(
            "int",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static CoreSourceLocation Location(
        string path,
        int line) =>
        new(path, line, 1, line, 8);

    private static string GrpcFailureSummary(
        GrpcLinkRuntimeState state) =>
        string.Join(
            "; ",
            state.Failures.Select(failure =>
                $"{failure.Code}:{failure.SymbolCanonicalKey}"));

    private static string NativeFailureSummary(
        NativeInteropRuntimeState state) =>
        string.Join(
            "; ",
            state.Failures.Select(failure =>
                $"{failure.Stage}/{failure.Code}:{failure.Message}"));

    private sealed record ExpectedEdge(
        string From,
        string Relation,
        string To);

    private sealed class FixedTrustPolicy : IExecutionTrustPolicy
    {
        public ExecutionTrustDecision EvaluateRepositoryCapability(
            string repositoryRoot,
            ExecutionCapability capability) =>
            new(true, ExecutionTrustReason.Allowed);

        public ExecutionTrustDecision EvaluatePathPluginCapability(
            string repositoryRoot,
            string entryAssemblyPath,
            ExecutionCapability capability,
            string? bundleRoot = null) =>
            throw new NotSupportedException();

        public ExecutionTrustDecision EvaluateNuGetPluginCapability(
            string repositoryRoot,
            string packageId,
            string exactVersion,
            ExecutionCapability capability) =>
            throw new NotSupportedException();
    }
}
