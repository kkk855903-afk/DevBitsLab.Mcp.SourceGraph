using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

[Collection("LeafFormatterState")]
public sealed class InteropToolsTests : IAsyncLifetime, IDisposable
{
    private const string ManagedKey =
        "csharp:M:Fixture.NativeMethods.Run";
    private const string NativeKey =
        "c:E:native/api.cpp::run";

    private readonly ScopeRouter _router = new();
    private readonly List<ScopeHost> _hosts = [];
    private string _root = string.Empty;

    public InteropToolsTests() => LeafFormatter.Suppressed = false;

    public Task InitializeAsync()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-interop-tools-" + Guid.NewGuid().ToString("N"));
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => LeafFormatter.Suppressed = false;

    [Theory]
    [InlineData(
        nameof(InteropTools.MatchPInvokeAsync),
        "match_pinvoke",
        typeof(MatchPInvokeResult))]
    [InlineData(
        nameof(InteropTools.AnalyzeNativeBoundaryAsync),
        "analyze_native_boundary",
        typeof(AnalyzeNativeBoundaryResult))]
    public void Metadata_uses_exact_names_and_object_root_schemas(
        string methodName,
        string expectedToolName,
        Type expectedSchema)
    {
        typeof(InteropTools)
            .GetCustomAttribute<McpServerToolTypeAttribute>()
            .Should().NotBeNull();
        var method = typeof(InteropTools).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);

        method.Should().NotBeNull();
        var attribute =
            method!.GetCustomAttribute<McpServerToolAttribute>();
        attribute.Should().NotBeNull();
        attribute!.UseStructuredContent.Should().BeTrue();
        attribute.OutputSchemaType.Should().Be(expectedSchema);
        expectedSchema.IsClass.Should().BeTrue();
        typeof(System.Collections.IEnumerable)
            .IsAssignableFrom(expectedSchema)
            .Should().BeFalse();
        method.GetCustomAttribute<ToolAnnotationAttribute>()
            .Should().Match<ToolAnnotationAttribute>(
                annotation =>
                    annotation.ReadOnlyHint == true
                    && annotation.IdempotentHint == true);

        var protocolTool = McpServerTool.Create(
            method,
            target: null,
            new McpServerToolCreateOptions());
        protocolTool.ProtocolTool.Name.Should().Be(expectedToolName);
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(
            JsonSerializerOptions.Web,
            expectedSchema);
        var scopeProperties = schema["properties"]?["scopes"]?["items"]?
            ["properties"];
        scopeProperties?["matches"]?["items"]?["properties"]?.AsObject()
            .ContainsKey("relation").Should().BeTrue();
        scopeProperties?["findings"]?["items"]?["properties"]?.AsObject()
            .ContainsKey("relation").Should().BeTrue();
    }

    [Fact]
    public async Task Single_unconfigured_scope_is_typed_and_tracked()
    {
        await RegisterEmptyScopeAsync("default");
        _router.SetDefaultScope("default");
        var before = MetricCount("match_pinvoke");

        var call = await InteropTools.MatchPInvokeAsync(
            _router,
            "Run");

        call.IsError.Should().NotBe(true);
        SerializedLength(call).Should().BeLessThanOrEqualTo(
            OutputBudget.DefaultBudgetChars);
        var result = Deserialize<MatchPInvokeResult>(
            call,
            InteropToolJsonContext.Default.MatchPInvokeResult);
        result.Scopes.Should().ContainSingle();
        var scope = result.Scopes[0];
        scope.ScopeId.Should().Be("default");
        scope.Status.Should().Be("not_configured");
        scope.Partial.Should().BeTrue();
        scope.Failures.Should().ContainSingle(failure =>
            failure.Code == "interop-not-configured");
        result.TotalFailureCount.Should().Be(1);
        CallToolResultHelpers.ProseText(call)
            .Should().Contain("### Scope `default`")
            .And.Contain("interop-not-configured");
        MetricCount("match_pinvoke").Should().Be(before + 1);
    }

    [Fact]
    public async Task Multi_scope_keeps_degraded_and_unconfigured_blocks()
    {
        var healthy = await RegisterEmptyScopeAsync("healthy");
        var degraded = await RegisterEmptyScopeAsync("degraded");
        degraded.Status = "degraded";
        degraded.StatusMessage = "native index unavailable";

        var call = await InteropTools.AnalyzeNativeBoundaryAsync(
            _router,
            "Run",
            scope: "degraded, healthy");

        call.IsError.Should().BeTrue();
        var result = Deserialize<AnalyzeNativeBoundaryResult>(
            call,
            InteropToolJsonContext.Default.AnalyzeNativeBoundaryResult);
        result.Scopes.Select(item => item.ScopeId)
            .Should().Equal("degraded", "healthy");
        result.Scopes[0].ScopeStatus.Should().Be("degraded");
        result.Scopes[0].Status.Should().Be("error");
        result.Scopes[0].Partial.Should().BeTrue();
        result.Scopes[0].Failures.Should().ContainSingle(failure =>
            failure.Code == "scope-unavailable");
        result.Scopes[1].Status.Should().Be("not_configured");
        result.Scopes[1].Partial.Should().BeTrue();
        result.Scopes.Should().OnlyContain(item => item.Partial);
        var prose = CallToolResultHelpers.ProseText(call);
        prose.Should().Contain("### Scope `degraded`")
            .And.Contain("### Scope `healthy`");
        healthy.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Configured_scope_exposes_match_only_or_phase2_findings_by_tool()
    {
        await RegisterConfiguredScopeAsync("interop");

        var matchCall = await InteropTools.MatchPInvokeAsync(
            _router,
            ManagedKey,
            scope: "interop");
        var match = Deserialize<MatchPInvokeResult>(
            matchCall,
            InteropToolJsonContext.Default.MatchPInvokeResult);

        match.Scopes.Should().ContainSingle();
        match.Scopes[0].Matches.Should().ContainSingle();
        match.Scopes[0].Matches[0].ManagedSymbol.Should().Be(ManagedKey);
        match.Scopes[0].Matches[0].NativeSymbol.Should().Be(NativeKey);
        match.Scopes[0].Matches[0].Relation.Should().Be(
            "pinvoke-maps-to");
        match.Scopes[0].Matches[0].Status.Should().Be("matched");
        CallToolResultHelpers.ProseText(matchCall).Should().Contain(
            "relation=pinvoke-maps-to");
        match.Scopes[0].Findings.Should().BeEmpty(
            "match_pinvoke is a managed-import match query, not an analysis query");

        var nativeOnlyCall = await InteropTools.MatchPInvokeAsync(
            _router,
            NativeKey,
            scope: "interop");
        var nativeOnly = Deserialize<MatchPInvokeResult>(
            nativeOnlyCall,
            InteropToolJsonContext.Default.MatchPInvokeResult);
        nativeOnly.Scopes[0].Status.Should().Be("not_found");
        nativeOnly.Scopes[0].Matches.Should().BeEmpty(
            "match_pinvoke selects managed imports only");

        var analyzeBefore = MetricCount("analyze_native_boundary");
        var analysisCall = await InteropTools.AnalyzeNativeBoundaryAsync(
            _router,
            NativeKey,
            scope: "interop");
        var analysis = Deserialize<AnalyzeNativeBoundaryResult>(
            analysisCall,
            InteropToolJsonContext.Default.AnalyzeNativeBoundaryResult);

        analysis.Scopes.Should().ContainSingle();
        analysis.Scopes[0].Matches.Should().ContainSingle();
        analysis.Scopes[0].Findings.Should().NotBeEmpty();
        analysis.Scopes[0].Findings.Should().OnlyContain(finding =>
            finding.Relation == "diagnoses-boundary");
        CallToolResultHelpers.ProseText(analysisCall).Should().Contain(
            "diagnoses-boundary");
        analysis.Scopes[0].Findings.Select(item => item.RuleId)
            .Should().OnlyContain(ruleId =>
                ruleId == InteropRuleIds.CallingConvention
                || ruleId == InteropRuleIds.ParameterTypeRisk
                || ruleId == InteropRuleIds.CallbackGcRisk
                || ruleId == InteropRuleIds.NativeException
                || ruleId == InteropRuleIds.AllocatorMismatch);
        analysis.Scopes[0].Findings.Should().Contain(finding =>
            finding.RuleId == InteropRuleIds.CallingConvention);
        analysis.Scopes[0].Findings.Should().NotContain(finding =>
            finding.RuleId == InteropRuleIds.StructLayout);
        MetricCount("analyze_native_boundary")
            .Should().Be(analyzeBefore + 1);
    }

    [Fact]
    public async Task Partial_run_returns_current_exact_positive_match()
    {
        var host = await RegisterConfiguredScopeAsync("interop");
        var partial = await host.NativeInteropCoordinator!.RunAsync(
            isManagedUniverseComplete: false);
        partial.State.Status.Should().Be(NativeInteropRuntimeStatus.Partial);
        partial.State.HasCurrentProjection.Should().BeTrue();
        partial.State.RetainedLastGood.Should().BeFalse();

        var call = await InteropTools.MatchPInvokeAsync(
            _router,
            ManagedKey,
            scope: "interop");
        var result = Deserialize<MatchPInvokeResult>(
            call,
            InteropToolJsonContext.Default.MatchPInvokeResult);

        result.Scopes.Should().ContainSingle();
        var scope = result.Scopes[0];
        scope.Status.Should().Be("ok");
        scope.Partial.Should().BeTrue();
        scope.RetainedLastGood.Should().BeFalse();
        scope.Matches.Should().ContainSingle();
        scope.Matches[0].Status.Should().Be("matched");
        scope.Matches[0].NativeSymbol.Should().Be(NativeKey);
        scope.Findings.Should().BeEmpty();
        call.StructuredContent!.Value.GetRawText()
            .Should().Contain(NativeKey);
    }

    [Fact]
    public async Task Query_failure_retains_a_typed_scope_block()
    {
        var host = await RegisterConfiguredScopeAsync("broken");
        await using (var sabotage = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = Path.Join(
                                 host.Scope.Root,
                                 "graph.db"),
                         }.ToString()))
        {
            await sabotage.OpenAsync();
            await using var command = sabotage.CreateCommand();
            command.CommandText = "DROP TABLE annotations;";
            await command.ExecuteNonQueryAsync();
        }

        var call = await InteropTools.MatchPInvokeAsync(
            _router,
            ManagedKey,
            scope: "broken");
        var result = Deserialize<MatchPInvokeResult>(
            call,
            InteropToolJsonContext.Default.MatchPInvokeResult);

        call.IsError.Should().BeTrue();
        result.Scopes.Should().ContainSingle();
        result.Scopes[0].ScopeId.Should().Be("broken");
        result.Scopes[0].Status.Should().Be("error");
        result.Scopes[0].Partial.Should().BeTrue();
        result.Scopes[0].Failures.Should().ContainSingle(failure =>
            failure.Code == "interop-query-failed");
    }

    [Fact]
    public async Task Aggregate_over_50k_is_deterministic_and_keeps_every_scope_core()
    {
        var scopes = Enumerable.Range(0, 16)
            .Select(index => LargeScope($"scope-{index:D2}"))
            .ToArray();

        Task<CallToolResult> Build() => ToolMetrics.TrackAsync(
            "interop-budget-fixture",
            args: null,
            () => Task.FromResult(InteropTools.BuildBoundedAggregate(
                scopes,
                new string('q', 4_096),
                includeFindings: true,
                "analyze_native_boundary",
                elapsedMilliseconds: 17,
                isError: false)));
        var first = await Build();
        var second = await Build();
        var firstJson = JsonSerializer.Serialize(
            first,
            McpJsonUtilities.DefaultOptions);
        var secondJson = JsonSerializer.Serialize(
            second,
            McpJsonUtilities.DefaultOptions);

        firstJson.Length.Should().BeLessThanOrEqualTo(
            OutputBudget.DefaultBudgetChars);
        firstJson.Should().Be(secondJson);
        var result = Deserialize<AnalyzeNativeBoundaryResult>(
            first,
            InteropToolJsonContext.Default.AnalyzeNativeBoundaryResult);
        result.Scopes.Should().HaveCount(16);
        result.Scopes.Select(item => item.ScopeId)
            .Should().Equal(scopes.Select(item => item.ScopeId));
        result.Scopes.Should().OnlyContain(item =>
            item.Status == "ok"
            && !item.Partial
            && item.TotalMatchCount == 1
            && item.TotalFindingCount == 32);
        result.Truncated.Should().BeTrue();
        result.OmittedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task More_than_16_selected_scopes_is_rejected_before_fanout()
    {
        for (var index = 0; index < 17; index++)
        {
            await RegisterEmptyScopeAsync($"scope-{index:D2}");
        }

        var call = await InteropTools.MatchPInvokeAsync(
            _router,
            "Run",
            scope: "*");

        call.IsError.Should().BeTrue();
        call.StructuredContent.Should().BeNull();
        CallToolResultHelpers.ProseText(call)
            .Should().Contain("maximum fan-out of 16");
        SerializedLength(call).Should().BeLessThanOrEqualTo(
            OutputBudget.DefaultBudgetChars);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_symbol_is_rejected(string symbol)
    {
        var call = await InteropTools.MatchPInvokeAsync(_router, symbol);

        call.IsError.Should().BeTrue();
        call.StructuredContent.Should().BeNull();
        CallToolResultHelpers.ProseText(call)
            .Should().Contain("non-empty `symbol`");
    }

    [Fact]
    public async Task Symbol_over_4096_characters_is_rejected()
    {
        var call = await InteropTools.AnalyzeNativeBoundaryAsync(
            _router,
            new string('x', 4_097));

        call.IsError.Should().BeTrue();
        call.StructuredContent.Should().BeNull();
        CallToolResultHelpers.ProseText(call)
            .Should().Contain("4096");
    }

    private async Task<ScopeHost> RegisterEmptyScopeAsync(string id)
    {
        var scopeRoot = Path.Join(_root, id);
        Directory.CreateDirectory(scopeRoot);
        var store = new SqliteGraphStore(Path.Join(scopeRoot, "graph.db"));
        await store.EnsureSchemaAsync();
        var solutionPath = Path.Join(scopeRoot, "stub.sln");
        var scope = new Scope(
            id,
            id,
            scopeRoot,
            new ScopeProjectSet.Solutions(
                [solutionPath],
                []),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solutionPath);
        host.MarkReady();
        _router.Register(host);
        _hosts.Add(host);
        return host;
    }

    private async Task<ScopeHost> RegisterConfiguredScopeAsync(string id)
    {
        var scopeRoot = Path.Join(_root, id);
        Directory.CreateDirectory(Path.Join(scopeRoot, "managed"));
        Directory.CreateDirectory(Path.Join(scopeRoot, "native"));
        var managedPath = Path.Join(
            scopeRoot,
            "managed",
            "NativeMethods.cs");
        var nativePath = Path.Join(scopeRoot, "native", "api.cpp");
        var binaryPath = Path.Join(scopeRoot, "native", "native.dll");
        await File.WriteAllTextAsync(managedPath, "// managed fixture");
        await File.WriteAllTextAsync(nativePath, "// native fixture");
        await File.WriteAllBytesAsync(binaryPath, [1, 2, 3, 4]);

        var store = new SqliteGraphStore(Path.Join(scopeRoot, "graph.db"));
        await store.EnsureSchemaAsync();
        await SeedManagedImportAsync(store, managedPath);

        var configuration = new ScopeInteropConfig(
            Target,
            [
                new InteropTranslationUnitConfig(
                    "native/api.cpp",
                    "native.dll",
                    ["-x", "c++"],
                    "native/native.dll"),
            ]);
        var solutionPath = Path.Join(scopeRoot, "stub.sln");
        var scope = new Scope(
            id,
            id,
            scopeRoot,
            new ScopeProjectSet.Solutions([solutionPath], []),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow)
        {
            Interop = configuration,
        };
        var host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solutionPath);
        var coordinator = new NativeInteropCoordinator(
            scopeRoot,
            configuration,
            new ScopePathPolicy(scopeRoot),
            store,
            new FixedTrustPolicy(),
            (request, _) => Task.FromResult(Extraction(
                request,
                NativeExport(request))),
            (_, _, _) => Task.FromResult(
                BinaryVerification()));
        host.NativeInteropCoordinator = coordinator;
        var run = await coordinator.RunAsync();
        run.State.Status.Should().Be(NativeInteropRuntimeStatus.Complete);
        host.MarkReady();
        _router.Register(host);
        _hosts.Add(host);
        return host;
    }

    private static async Task SeedManagedImportAsync(
        SqliteGraphStore store,
        string managedPath)
    {
        var fileId = await store.UpsertFileAsync(
            managedPath,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
        var symbolId = await store.UpsertSymbolAsync(
            ManagedKey,
            new Symbol(
                0,
                "Run",
                "Fixture.NativeMethods.Run",
                "method",
                fileId,
                1,
                1,
                2,
                1,
                "void Run()",
                null));
        var managed = new ManagedImport(
            ManagedKey,
            ManagedImportKind.DllImport,
            "native.dll",
            "run",
            InteropCallingConvention.Cdecl,
            VoidType,
            [],
            CharacterSet: null,
            SetLastError: false,
            Target,
            new Evidence(
                fileId,
                new SourceLocation(
                    managedPath,
                    1,
                    1,
                    1,
                    8),
                EvidenceConfidence.Exact,
                "interop-tool-test"))
        {
            ExactSpelling = true,
        };
        await store.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                symbolId,
                "InteropFact",
                "MedInterop.InteropFact",
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(managed),
                AttributeSymbolId: null),
        ]);
    }

    private static ClangNativeExtractionResult Extraction(
        ClangNativeExtractionRequest request,
        NativeExport export) =>
        new(
            Functions: [],
            Types: [],
            Exports: [export],
            RecordLayouts: [],
            Diagnostics: [])
        {
            IncludedFiles = [request.SourceFilePath],
        };

    private static NativeExport NativeExport(
        ClangNativeExtractionRequest request) =>
        new(
            NativeKey,
            "run",
            InteropCallingConvention.StdCall,
            VoidType,
            [],
            HasCLinkage: true,
            IsBinaryVerified: false,
            request.Target,
            new Evidence(
                request.ProducingFileId,
                new SourceLocation(
                    request.SourceFilePath,
                    1,
                    1,
                    1,
                    8),
                EvidenceConfidence.Exact,
                "interop-tool-test"))
        {
            LibraryName = request.LibraryName,
            ModuleIdentitySource =
                NativeModuleIdentitySource.Configuration,
        };

    private static BinaryExportVerificationResult BinaryVerification() =>
        new(
            BinaryExportVerificationStatus.Complete,
            InteropArchitecture.X86,
            0x014c,
            "native.dll",
            [
                new BinaryExportEntry(
                    Ordinal: 1,
                    AddressRva: 0x1000,
                    Names: ["run"],
                    IsForwarder: false,
                    Forwarder: null),
            ],
            "complete");

    private static InteropScopeQueryResult LargeScope(string id)
    {
        var evidence = Enumerable.Range(0, 12)
            .Select(index => new InteropQueryEvidenceRow(
                $"/repo/{id}/{index:D2}/{new string('p', 900)}.cpp",
                index + 1,
                1,
                index + 1,
                20,
                "exact",
                new string('r', 300),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["large-proof"] = new string(
                        (char)('a' + index),
                        1_000),
                },
                MetadataOmittedCount: 0))
            .ToArray();
        var target = new InteropQueryTarget(
            "win-x86",
            "x86",
            "msvc",
            4,
            8);
        var match = new InteropQueryMatchRow(
            $"{ManagedKey}:{id}:{new string('m', 800)}",
            $"{NativeKey}:{id}:{new string('n', 800)}",
            "pinvoke-maps-to",
            "matched",
            "exact",
            Enumerable.Range(0, 12)
                .Select(index =>
                    $"reason-{index:D2}-{new string('z', 900)}")
                .ToArray(),
            CandidateCount: 1,
            target,
            evidence,
            EvidenceOmittedCount: 0,
            ReasonOmittedCount: 0);
        var findings = Enumerable.Range(0, 32)
            .Select(index => new InteropQueryFindingRow(
                index % 2 == 0
                    ? InteropRuleIds.CallingConvention
                    : InteropRuleIds.ParameterTypeRisk,
                "warning",
                $"risk-{index:D2}-{new string('f', 1_200)}",
                match.ManagedSymbol,
                match.NativeSymbol!,
                "diagnoses-boundary",
                "exact",
                target,
                evidence,
                EvidenceOmittedCount: 0))
            .ToArray();
        return new InteropScopeQueryResult(
            id,
            new string('q', 4_096),
            "ok",
            "ok",
            Partial: false,
            RetainedLastGood: false,
            SelectionStatus: "selected",
            SelectionCandidates:
            [
                new InteropQuerySelectionCandidate(
                    1,
                    match.ManagedSymbol,
                    "managed_import",
                    new string('d', 800),
                    evidence[0].FilePath,
                    1,
                    1),
            ],
            TotalSelectionCandidateCount: 1,
            Matches: [match],
            TotalMatchCount: 1,
            Findings: findings,
            TotalFindingCount: findings.Length,
            Failures: [],
            TotalFailureCount: 0,
            Truncated: false,
            OmittedCount: 0,
            OmittedEvidenceCount: 0,
            OmittedReasonCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);
    }

    private static T Deserialize<T>(
        CallToolResult call,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        call.StructuredContent.Should().NotBeNull();
        return call.StructuredContent!.Value.Deserialize(typeInfo)
            ?? throw new InvalidOperationException(
                $"Could not deserialize {typeof(T).Name}.");
    }

    private static int SerializedLength(CallToolResult call) =>
        JsonSerializer.Serialize(
            call,
            McpJsonUtilities.DefaultOptions).Length;

    private static long MetricCount(string toolName) =>
        ToolMetrics.Snapshot().TryGetValue(toolName, out var stats)
            ? stats.Count
            : 0;

    private static InteropTarget Target { get; } =
        InteropTarget.WindowsX86Msvc;

    private static AbiTypeRef VoidType { get; } =
        new("void", AbiTypeCategory.Void);

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
