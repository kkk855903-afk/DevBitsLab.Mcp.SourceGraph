using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropQueryServiceTests : IAsyncLifetime
{
    private string _tempDirectory = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-interop-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _store = new SqliteGraphStore(Path.Join(_tempDirectory, "graph.db"));
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
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
    public async Task Exact_managed_key_returns_typed_persisted_match_and_phase2_findings()
    {
        var managed = await SeedManagedAsync(
            "managed/NativeMethods.cs",
            "csharp:M:Fixture.NativeMethods.Run",
            "Run");
        var native = await SeedNativeAsync(
            "native/export.h",
            "c:E:native/export.h::run",
            "run");
        await SeedMatchAsync(managed, native, InteropMatchStatus.Matched);
        await SeedFindingAsync(
            managed,
            native,
            InteropRuleIds.CallingConvention,
            "Calling convention mismatch.");
        await SeedFindingAsync(
            managed,
            native,
            InteropRuleIds.StructLayout,
            "Struct detail belongs to Phase 3.");

        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            CompleteState(),
            managed.Key,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: true);

        query.SerializedJson.Length.Should()
            .BeLessThanOrEqualTo(InteropQueryBudget.MaximumSerializedCharacters);
        query.Result.Status.Should().Be("ok");
        query.Result.SelectionStatus.Should().Be("selected");
        query.Result.Matches.Should().ContainSingle();
        var match = query.Result.Matches[0];
        match.ManagedSymbol.Should().Be(managed.Key);
        match.NativeSymbol.Should().Be(native.Key);
        match.Status.Should().Be("matched");
        match.CandidateCount.Should().Be(1);
        match.Reasons.Should().NotBeEmpty();
        match.Target.RuntimeIdentifier.Should().Be(Target.RuntimeIdentifier);
        match.Evidence.Should().NotBeEmpty();
        query.Result.Findings.Should().ContainSingle()
            .Which.RuleId.Should().Be(InteropRuleIds.CallingConvention);
        query.Result.Findings.Should().NotContain(
            finding => finding.RuleId == InteropRuleIds.StructLayout);

        using var json = JsonDocument.Parse(query.SerializedJson);
        json.RootElement.GetProperty("scope_id").GetString()
            .Should().Be("scope-a");
        json.RootElement.GetProperty("matches")[0]
            .GetProperty("candidate_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Name_query_with_multiple_managed_imports_is_explicitly_ambiguous()
    {
        var second = await SeedManagedAsync(
            "managed/B.cs",
            "csharp:M:Fixture.B.Run",
            "Run");
        var first = await SeedManagedAsync(
            "managed/A.cs",
            "csharp:M:Fixture.A.Run",
            "Run");

        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            CompleteState(),
            "Run",
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: false);

        query.Result.Status.Should().Be("ambiguous_selection");
        query.Result.SelectionStatus.Should().Be("ambiguous");
        query.Result.Matches.Should().BeEmpty();
        query.Result.SelectionCandidates.Select(item => item.CanonicalKey)
            .Should().Equal(first.Key, second.Key);
        query.Result.TotalSelectionCandidateCount.Should().Be(2);
    }

    [Fact]
    public async Task Complete_scope_with_zero_candidates_reports_not_found()
    {
        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            CompleteState(),
            "MissingBoundary",
            InteropQuerySelectionMode.ManagedOrNativeBoundary,
            includeFindings: true);

        query.Result.Status.Should().Be("not_found");
        query.Result.SelectionStatus.Should().Be("not_found");
        query.Result.Partial.Should().BeFalse();
        query.Result.Matches.Should().BeEmpty();
        query.Result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task Partial_scope_never_renders_retained_last_good_as_current()
    {
        var managed = await SeedManagedAsync(
            "managed/NativeMethods.cs",
            "csharp:M:Fixture.NativeMethods.Run",
            "Run");
        var native = await SeedNativeAsync(
            "native/export.h",
            "c:E:native/export.h::run",
            "run");
        await SeedMatchAsync(managed, native, InteropMatchStatus.Matched);
        await SeedFindingAsync(
            managed,
            native,
            InteropRuleIds.NativeException,
            "A native exception can escape.");
        var state = CompleteState() with
        {
            Status = NativeInteropRuntimeStatus.Partial,
            RetainedLastGood = true,
            IsExportUniverseComplete = false,
            Failures =
            [
                new NativeInteropRuntimeFailure(
                    "worker",
                    "timeout",
                    "Native extraction timed out.",
                    TranslationUnitIndex: 2,
                    ConfiguredPath: "native/export.cpp"),
            ],
        };

        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            state,
            managed.Key,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: true);

        query.Result.Status.Should().Be("partial");
        query.Result.Partial.Should().BeTrue();
        query.Result.RetainedLastGood.Should().BeTrue();
        query.Result.Matches.Should().ContainSingle();
        query.Result.Matches[0].Status.Should().Be("unknown");
        query.Result.Matches[0].NativeSymbol.Should().BeNull();
        query.Result.Matches[0].CandidateCount.Should().Be(0);
        query.Result.Matches[0].Reasons.Should().Contain(
            reason => reason.Contains("retained", StringComparison.OrdinalIgnoreCase));
        query.Result.Findings.Should().BeEmpty();
        query.SerializedJson.Should().NotContain(native.Key);
        query.Result.Failures.Should().Contain(failure =>
            failure.Code == "timeout"
            && failure.TranslationUnitIndex == 2);
    }

    [Fact]
    public async Task Unique_native_export_returns_all_related_boundaries_in_stable_order()
    {
        var native = await SeedNativeAsync(
            "native/export.h",
            "c:E:native/export.h::run",
            "run");
        var second = await SeedManagedAsync(
            "managed/B.cs",
            "csharp:M:Fixture.B.Run",
            "RunB");
        var first = await SeedManagedAsync(
            "managed/A.cs",
            "csharp:M:Fixture.A.Run",
            "RunA");
        await SeedMatchAsync(second, native, InteropMatchStatus.Matched);
        await SeedMatchAsync(first, native, InteropMatchStatus.Matched);
        await SeedFindingAsync(
            second,
            native,
            InteropRuleIds.ParameterTypeRisk,
            "Parameter risk B.");
        await SeedFindingAsync(
            first,
            native,
            InteropRuleIds.ParameterTypeRisk,
            "Parameter risk A.");

        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            CompleteState(),
            native.Key,
            InteropQuerySelectionMode.ManagedOrNativeBoundary,
            includeFindings: true);

        query.Result.Status.Should().Be("ok");
        query.Result.SelectionCandidates.Should().ContainSingle()
            .Which.SymbolType.Should().Be("native_export");
        query.Result.Matches.Select(item => item.ManagedSymbol)
            .Should().Equal(first.Key, second.Key);
        query.Result.TotalMatchCount.Should().Be(2);
        query.Result.Findings.Select(item => item.ManagedSymbol)
            .Should().Equal(first.Key, second.Key);
    }

    [Fact]
    public async Task Name_query_with_multiple_native_exports_does_not_choose_by_store_order()
    {
        var second = await SeedNativeAsync(
            "native/z-export.h",
            "c:E:native/z-export.h::run",
            "run");
        var first = await SeedNativeAsync(
            "native/a-export.h",
            "c:E:native/a-export.h::run",
            "run");

        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            CompleteState(),
            "run",
            InteropQuerySelectionMode.ManagedOrNativeBoundary,
            includeFindings: true);

        query.Result.Status.Should().Be("ambiguous_selection");
        query.Result.SelectionStatus.Should().Be("ambiguous");
        query.Result.SelectionCandidates.Select(item => item.CanonicalKey)
            .Should().Equal(first.Key, second.Key);
        query.Result.Matches.Should().BeEmpty();
        query.Result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task Partial_scope_does_not_treat_retained_native_export_as_selected()
    {
        var native = await SeedNativeAsync(
            "native/export.h",
            "c:E:native/export.h::run",
            "run");
        var state = CompleteState() with
        {
            Status = NativeInteropRuntimeStatus.Indexing,
            RetainedLastGood = true,
            IsExportUniverseComplete = false,
        };

        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            state,
            native.Key,
            InteropQuerySelectionMode.ManagedOrNativeBoundary,
            includeFindings: true);

        query.Result.Status.Should().Be("partial");
        query.Result.SelectionStatus.Should().Be("unknown");
        query.Result.SelectionCandidates.Should().BeEmpty();
        query.Result.Matches.Should().BeEmpty();
        query.Result.Findings.Should().BeEmpty();
        query.Result.Failures.Should().Contain(
            failure => failure.Code == "native-selection-not-current");
    }

    [Fact]
    public async Task Source_match_keeps_reasons_but_returns_no_findings()
    {
        var managed = await SeedManagedAsync(
            "managed/NativeMethods.cs",
            "csharp:M:Fixture.NativeMethods.Run",
            "Run");
        var native = await SeedNativeAsync(
            "native/export.h",
            "c:E:native/export.h::run",
            "run");
        await SeedMatchAsync(
            managed,
            native,
            InteropMatchStatus.SourceMatched);
        await SeedFindingAsync(
            managed,
            native,
            InteropRuleIds.CallingConvention,
            "A stale finding must not be exposed.");

        var query = await Service().QueryAsync(
            "scope-a",
            _store!,
            CompleteState(),
            managed.Key,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: true);

        query.Result.Partial.Should().BeFalse();
        query.Result.Matches.Should().ContainSingle();
        query.Result.Matches[0].Status.Should().Be("source_matched");
        query.Result.Matches[0].Reasons.Should().NotBeEmpty();
        query.Result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_persisted_projection_forces_unknown_instead_of_stale_answer()
    {
        var managed = await SeedManagedAsync(
            "managed/NativeMethods.cs",
            "csharp:M:Fixture.NativeMethods.Run",
            "Run");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managed.SymbolId,
                InteropAnnotationFlavors.Match,
                "{}"),
        ]);

        var query = await Service().QueryAsync(
            "scope-a",
            _store,
            CompleteState(),
            managed.Key,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: false);

        query.Result.Status.Should().Be("partial");
        query.Result.Matches.Should().ContainSingle()
            .Which.Status.Should().Be("unknown");
        query.Result.Matches[0].NativeSymbol.Should().BeNull();
        query.Result.Failures.Should().Contain(
            failure => failure.Stage == "fact-read");
    }

    [Fact]
    public async Task Fully_serialized_result_is_deterministically_trimmed_below_50k()
    {
        var managed = await SeedManagedAsync(
            "managed/NativeMethods.cs",
            "csharp:M:Fixture.NativeMethods.Run",
            "Run");
        var native = await SeedNativeAsync(
            "native/export.h",
            "c:E:native/export.h::run",
            "run");
        await SeedMatchAsync(managed, native, InteropMatchStatus.Matched);
        var annotations = Enumerable.Range(0, 120)
            .Select(index =>
            {
                var finding = Finding(
                    managed,
                    native,
                    InteropRuleIds.ParameterTypeRisk,
                    $"Risk {index:D3}: {new string('x', 2000)}");
                return Annotation(
                    managed.SymbolId,
                    InteropAnnotationFlavors.Finding,
                    InteropFactPayloadCodec.EncodeFinding(finding));
            })
            .ToArray();
        await _store!.BulkInsertAnnotationsAsync(annotations);

        var first = await Service().QueryAsync(
            "scope-a",
            _store,
            CompleteState(),
            managed.Key,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: true);
        var second = await Service().QueryAsync(
            "scope-a",
            _store,
            CompleteState(),
            managed.Key,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: true);

        first.SerializedJson.Length.Should()
            .BeLessThanOrEqualTo(InteropQueryBudget.MaximumSerializedCharacters);
        var parse = () => JsonDocument.Parse(first.SerializedJson);
        parse.Should().NotThrow();
        first.SerializedJson.Should().Be(second.SerializedJson);
        first.Result.Truncated.Should().BeTrue();
        first.Result.TotalFindingCount.Should().Be(120);
        first.Result.Findings.Count.Should().BeLessThan(120);
        first.Result.OmittedCount.Should().BeGreaterThan(0);
    }

    private InteropQueryService Service() => new();

    private async Task<Owner> SeedManagedAsync(
        string relativePath,
        string key,
        string name)
    {
        var owner = await SeedOwnerAsync(
            relativePath,
            key,
            name,
            $"Fixture.{name}",
            "method");
        var fact = new ManagedImport(
            key,
            ManagedImportKind.DllImport,
            "native.dll",
            "run",
            InteropCallingConvention.Cdecl,
            VoidType,
            [],
            CharacterSet: null,
            SetLastError: false,
            Target,
            EvidenceAt(owner, "roslyn-managed-interop"))
        {
            ExactSpelling = true,
        };
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(fact)),
        ]);
        return owner;
    }

    private async Task<Owner> SeedNativeAsync(
        string relativePath,
        string key,
        string name)
    {
        var owner = await SeedOwnerAsync(
            relativePath,
            key,
            name,
            name,
            "native-export");
        var fact = new NativeExport(
            key,
            name,
            InteropCallingConvention.Cdecl,
            VoidType,
            [],
            HasCLinkage: true,
            IsBinaryVerified: true,
            Target,
            EvidenceAt(owner, "clang-native-interop"))
        {
            LibraryName = "native.dll",
            ModuleIdentitySource = NativeModuleIdentitySource.Binary,
        };
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(fact)),
        ]);
        return owner;
    }

    private async Task SeedMatchAsync(
        Owner managed,
        Owner native,
        InteropMatchStatus status)
    {
        var projection = new InteropMatchProjection(
            managed.Key,
            native.Key,
            status,
            EvidenceConfidence.Exact,
            status == InteropMatchStatus.SourceMatched
                ? ["Source declaration matched; binary verification is unavailable."]
                : ["One binary-verified runtime-legal export matched."],
            Target,
            CandidateCount: 1,
            SnapshotComplete: true,
            [
                EvidenceProjection(managed, "roslyn-managed-interop"),
                EvidenceProjection(native, "clang-native-interop"),
            ]);
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managed.SymbolId,
                InteropAnnotationFlavors.Match,
                InteropFactPayloadCodec.EncodeMatch(projection)),
        ]);
    }

    private async Task SeedFindingAsync(
        Owner managed,
        Owner native,
        string ruleId,
        string message)
    {
        var finding = Finding(managed, native, ruleId, message);
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managed.SymbolId,
                InteropAnnotationFlavors.Finding,
                InteropFactPayloadCodec.EncodeFinding(finding)),
        ]);
    }

    private static InteropFindingProjection Finding(
        Owner managed,
        Owner native,
        string ruleId,
        string message) =>
        new(
            ruleId,
            InteropFindingSeverity.Warning,
            message,
            managed.Key,
            native.Key,
            Target,
            EvidenceConfidence.Exact,
            [
                EvidenceProjection(managed, "interop-analysis"),
                EvidenceProjection(native, "interop-analysis"),
            ]);

    private async Task<Owner> SeedOwnerAsync(
        string relativePath,
        string key,
        string name,
        string fqn,
        string kind)
    {
        var path = Path.GetFullPath(Path.Join(_tempDirectory, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "// fixture");
        var fileId = await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
        var symbolId = await _store.UpsertSymbolAsync(
            key,
            new Symbol(
                0,
                name,
                fqn,
                kind,
                fileId,
                1,
                1,
                2,
                1,
                $"void {name}()",
                null));
        return new Owner(fileId, symbolId, key, path);
    }

    private static AnnotationRecord Annotation(
        long symbolId,
        string flavor,
        string payload) =>
        new(
            symbolId,
            "InteropFact",
            "MedInterop.InteropFact",
            flavor,
            payload,
            AttributeSymbolId: null);

    private static Evidence EvidenceAt(Owner owner, string producer) =>
        new(
            owner.FileId,
            new SourceLocation(owner.Path, 1, 1, 1, 5),
            EvidenceConfidence.Exact,
            producer);

    private static InteropEvidenceProjection EvidenceProjection(
        Owner owner,
        string producer) =>
        new(
            new SourceLocation(owner.Path, 1, 1, 1, 5),
            EvidenceConfidence.Exact,
            producer);

    private static NativeInteropRuntimeState CompleteState() =>
        new(
            NativeInteropRuntimeStatus.Complete,
            Target,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            RetainedLastGood: false,
            IsExportUniverseComplete: true,
            TranslationUnits: 1,
            IncludedFiles: 2,
            NativeSymbols: 1,
            ManagedMatches: 1,
            Findings: 1,
            BoundaryEdges: 1,
            PendingStaleSymbols: 0,
            Failures: []);

    private static AbiTypeRef VoidType { get; } =
        new("void", AbiTypeCategory.Void);

    private static InteropTarget Target { get; } =
        InteropTarget.WindowsX86Msvc;

    private sealed record Owner(
        long FileId,
        long SymbolId,
        string Key,
        string Path);
}
