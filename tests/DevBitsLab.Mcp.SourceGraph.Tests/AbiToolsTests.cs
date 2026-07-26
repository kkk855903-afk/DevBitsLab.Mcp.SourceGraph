using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

[Collection("LeafFormatterState")]
public sealed class AbiToolsTests : IAsyncLifetime
{
    private string _tempDirectory = string.Empty;
    private readonly List<ScopeHost> _hosts = [];

    public Task InitializeAsync()
    {
        _tempDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-abi-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Compare_struct_publishes_typed_output_and_exact_mapping_schema()
    {
        var method = typeof(AbiTools).GetMethod(
            nameof(AbiTools.CompareStructAsync),
            BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        var attribute =
            method!.GetCustomAttribute<McpServerToolAttribute>();
        attribute.Should().NotBeNull();
        attribute!.UseStructuredContent.Should().BeTrue();
        attribute.OutputSchemaType.Should().Be(typeof(CompareStructResult));

        var mappingParameter = method.GetParameters()
            .Single(parameter =>
                parameter.Name == "nested_mappings");
        mappingParameter.ParameterType.Should().Be(
            typeof(IReadOnlyList<AbiNestedRecordMappingInput>));
        var names = typeof(AbiNestedRecordMappingInput)
            .GetProperties()
            .ToDictionary(
                property => property.Name,
                property => property
                    .GetCustomAttribute<JsonPropertyNameAttribute>()?.Name);
        names.Should().Contain(
            "ManagedTypeCanonicalName",
            "managed_type_canonical_name");
        names.Should().Contain(
            "NativeTypeCanonicalName",
            "native_type_canonical_name");
        names.Should().Contain(
            "ManagedRecord",
            "managed_record");
        names.Should().Contain(
            "NativeRecord",
            "native_record");

        ToolOutputJsonContext.Default.CompareStructResult
            .Should().NotBeNull();

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(
            JsonSerializerOptions.Web,
            typeof(CompareStructResult));
        var rootProperties = schema["properties"]?.AsObject()
            ?? throw new Xunit.Sdk.XunitException(
                "CompareStructResult output schema is missing properties.");
        rootProperties.ContainsKey("nested_mapping_count").Should().BeTrue();
        rootProperties.ContainsKey("total_scope_count").Should().BeTrue();
        var scopeProperties = rootProperties["scopes"]?["items"]?["properties"]
            ?.AsObject()
            ?? throw new Xunit.Sdk.XunitException(
                "CompareStructResult scope schema is missing properties.");
        scopeProperties.ContainsKey("scope_id").Should().BeTrue();
        scopeProperties.ContainsKey("relation").Should().BeTrue();
        scopeProperties.ContainsKey("managed_selection").Should().BeTrue();
        scopeProperties.ContainsKey("total_check_count").Should().BeTrue();
        scopeProperties["checks"]?["items"]?["properties"]?.AsObject()
            .ContainsKey("relation").Should().BeTrue();
        scopeProperties["finding"]?["properties"]?.AsObject()
            .ContainsKey("relation").Should().BeTrue();
    }

    [Fact]
    public async Task Scope_id_wildcard_and_comma_forms_return_one_typed_block_per_scope()
    {
        var router = new ScopeRouter();
        router.Register(await CreateHostAsync("alpha"));
        router.Register(await CreateHostAsync("beta"));
        router.SetDefaultScope("alpha");
        var before = ToolMetrics.Snapshot()
            .TryGetValue("compare_struct", out var snapshot)
            ? snapshot.Count
            : 0;

        var single = await AbiTools.CompareStructAsync(
            router,
            "csharp:T:Fixture.Packet",
            "cpp:T:native/packet.h::Packet",
            scope: "alpha");
        var comma = await AbiTools.CompareStructAsync(
            router,
            "csharp:T:Fixture.Packet",
            "cpp:T:native/packet.h::Packet",
            scope: "beta,alpha");
        var wildcard = await AbiTools.CompareStructAsync(
            router,
            "csharp:T:Fixture.Packet",
            "cpp:T:native/packet.h::Packet",
            scope: "*");

        Read(single).Scopes.Should().ContainSingle()
            .Which.ScopeId.Should().Be("alpha");
        Read(comma).Scopes.Select(scope => scope.ScopeId)
            .Should().Equal("alpha", "beta");
        var wildcardDto = Read(wildcard);
        wildcardDto.Scopes.Select(scope => scope.ScopeId)
            .Should().Equal("alpha", "beta");
        wildcardDto.Scopes.Should().OnlyContain(scope =>
            scope.Status == "partial"
            && scope.Compatibility == "unknown"
            && scope.Relation == "struct-maps-to"
            && scope.Partial);
        CallToolResultHelpers.ProseText(wildcard).Should().Contain(
            "relation=`struct-maps-to`");
        wildcardDto.Scopes.Should().OnlyContain(scope =>
            scope.Failures.Any(failure =>
                failure.Code == "native-runtime-unavailable"));
        ToolMetrics.Snapshot()["compare_struct"].Count
            .Should().BeGreaterThanOrEqualTo(before + 3);
    }

    [Fact]
    public void Full_call_result_is_deterministically_reduced_below_50k()
    {
        var evidence = new AbiQueryEvidenceRow(
            ProducingFileId: 1,
            FilePath: Path.Join(_tempDirectory, "Managed.cs"),
            StartLine: 10,
            StartColumn: 2,
            EndLine: 10,
            EndColumn: 20,
            Confidence: "exact",
            Producer: "test",
            Metadata: Enumerable.Range(0, 16)
                .ToDictionary(
                    index => $"key-{index:D2}",
                    index => new string(
                        (char)('a' + index % 20),
                        512),
                    StringComparer.Ordinal),
            MetadataOmittedCount: 0);
        var checks = Enumerable.Range(0, 2000)
            .Select(index => new AbiCompatibilityCheckRow(
                $"$.field[{index}]",
                "field_offset",
                "struct-maps-to",
                index == 0 ? "error" : "compatible",
                $"check-{index:D4}: {new string('x', 500)}",
                "exact",
                [evidence, evidence with { ProducingFileId = 2 }],
                EvidenceOmittedCount: 0))
            .ToArray();
        var reasons = Enumerable.Range(0, 2000)
            .Select(index =>
                $"reason-{index:D4}: {new string('r', 500)}")
            .ToArray();
        var target = new AbiQueryTarget(
            "win-x64",
            "x64",
            "msvc",
            PointerSizeBytes: 8,
            DefaultPack: 8);
        var managedSelection = Selection(
            "csharp:T:Fixture.Packet",
            "sequential");
        var nativeSelection = Selection(
            "cpp:T:native/packet.h::Packet",
            "native");
        var finding = new AbiFindingRow(
            "Interop002",
            "error",
            "Struct layout mismatch.",
            "csharp:T:Fixture.Packet",
            "cpp:T:native/packet.h::Packet",
            "struct-maps-to",
            "exact",
            [evidence],
            EvidenceOmittedCount: 0);
        var scope = new AbiScopeComparisonResult(
            "alpha",
            "ok",
            "struct-maps-to",
            "ok",
            "error",
            Partial: false,
            RetainedLastGood: false,
            target,
            managedSelection,
            nativeSelection,
            Record(
                "csharp:T:Fixture.Packet",
                "sequential",
                target,
                evidence),
            Record(
                "cpp:T:native/packet.h::Packet",
                "native",
                target,
                evidence),
            checks,
            checks.Length,
            reasons,
            reasons.Length,
            finding,
            TotalFindingCount: 1,
            Failures: [],
            TotalFailureCount: 0,
            Truncated: false,
            OmittedCount: 0,
            OmittedCheckCount: 0,
            OmittedReasonCount: 0,
            OmittedEvidenceCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);

        var first = AbiTools.BuildBoundedResultForTests(
            "csharp:T:Fixture.Packet",
            "cpp:T:native/packet.h::Packet",
            mappingCount: 0,
            [scope]);
        var second = AbiTools.BuildBoundedResultForTests(
            "csharp:T:Fixture.Packet",
            "cpp:T:native/packet.h::Packet",
            mappingCount: 0,
            [scope]);
        var firstJson = JsonSerializer.Serialize(
            first,
            McpJsonUtilities.DefaultOptions);
        var secondJson = JsonSerializer.Serialize(
            second,
            McpJsonUtilities.DefaultOptions);

        firstJson.Length.Should().BeLessThanOrEqualTo(
            OutputBudget.DefaultBudgetChars);
        firstJson.Should().Be(secondJson);
        var dto = Read(first);
        dto.Truncated.Should().BeTrue();
        dto.Partial.Should().BeFalse(
            "response compaction must not make a completed compatibility analysis partial");
        dto.OmittedCount.Should().BeGreaterThan(0);
        dto.OmittedCheckCount.Should().BeGreaterThan(0);
        dto.TotalCheckCount.Should().Be(2000);
        dto.TotalFindingCount.Should().Be(1);
        dto.Scopes.Should().ContainSingle()
            .Which.Compatibility.Should().Be("error");
        dto.Scopes[0].Relation.Should().Be("struct-maps-to");
        dto.Scopes[0].Checks.Should().OnlyContain(check =>
            check.Relation == "struct-maps-to");
        dto.Scopes[0].Finding?.Relation.Should().Be("struct-maps-to");
    }

    [Fact]
    public void Warning_checks_and_omission_breakdown_are_visible_in_prose()
    {
        var target = new AbiQueryTarget(
            "win-x64",
            "x64",
            "msvc",
            PointerSizeBytes: 8,
            DefaultPack: 8);
        var scope = new AbiScopeComparisonResult(
            "default",
            "ok",
            "struct-maps-to",
            "ok",
            "warning",
            Partial: false,
            RetainedLastGood: false,
            target,
            Selection("csharp:T:Fixture.CameraFormat", "sequential"),
            Selection("cpp:T:native/camera.h::PgCameraFormat", "native"),
            ManagedRecord: null,
            NativeRecord: null,
            Checks:
            [
                new AbiCompatibilityCheckRow(
                    "$.pixel_format",
                    "field_name",
                    "struct-maps-to",
                    "warning",
                    "Managed and native field names differ.",
                    "exact",
                    Evidence: [],
                    EvidenceOmittedCount: 0),
            ],
            TotalCheckCount: 9,
            Reasons:
            [
                "Managed and native field names differ.",
            ],
            TotalReasonCount: 1,
            Finding: null,
            TotalFindingCount: 0,
            Failures: [],
            TotalFailureCount: 0,
            Truncated: true,
            OmittedCount: 8,
            OmittedCheckCount: 8,
            OmittedReasonCount: 0,
            OmittedEvidenceCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);

        var result = AbiTools.BuildBoundedResultForTests(
            "csharp:T:Fixture.CameraFormat",
            "cpp:T:native/camera.h::PgCameraFormat",
            mappingCount: 0,
            [scope]);
        var prose = CallToolResultHelpers.ProseText(result);

        prose.Should().Contain("$.pixel_format")
            .And.Contain("Managed and native field names differ.")
            .And.Contain("omitted_checks=8")
            .And.Contain("checks=1/9");
    }

    private async Task<ScopeHost> CreateHostAsync(string id)
    {
        var root = Path.Join(_tempDirectory, id);
        Directory.CreateDirectory(root);
        var store = new SqliteGraphStore(Path.Join(root, "graph.db"));
        await store.EnsureSchemaAsync();
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
            solutionPath);
        host.Status = "ok";
        host.MarkReady();
        _hosts.Add(host);
        return host;
    }

    private static CompareStructResult Read(
        ModelContextProtocol.Protocol.CallToolResult result)
    {
        result.StructuredContent.Should().NotBeNull();
        return result.StructuredContent!.Value.Deserialize(
                   ToolOutputJsonContext.Default.CompareStructResult)
               ?? throw new Xunit.Sdk.XunitException(
                   "compare_struct returned an empty structured payload.");
    }

    private static AbiRecordSelectionResult Selection(
        string canonicalKey,
        string kind) =>
        new(
            "selected",
            [
                new AbiRecordSelectionCandidate(
                    SymbolId: 1,
                    canonicalKey,
                    kind,
                    "record.h",
                    StartLine: 1,
                    StartColumn: 1,
                    EndLine: 1,
                    EndColumn: 10),
            ],
            TotalCandidateCount: 1,
            CandidateOmittedCount: 0);

    private static AbiRecordSummary Record(
        string canonicalKey,
        string kind,
        AbiQueryTarget target,
        AbiQueryEvidenceRow evidence) =>
        new(
            canonicalKey,
            kind,
            SizeBytes: 4,
            AlignmentBytes: 4,
            Pack: 8,
            FieldCount: 1,
            target,
            [evidence],
            EvidenceOmittedCount: 0);
}
