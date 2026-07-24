using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

/// <summary>
/// One caller-supplied, exact nested-record identity mapping. Record values are persisted
/// canonical keys; type values are the exact canonical names carried by the containing fields.
/// </summary>
internal sealed record AbiRecordMappingQuery(
    string ManagedTypeCanonicalName,
    string NativeTypeCanonicalName,
    string ManagedRecord,
    string NativeRecord);

/// <summary>One scope block returned by the persisted ABI compatibility query.</summary>
public sealed record AbiScopeComparisonResult(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string Status,
    string Compatibility,
    bool Partial,
    [property: JsonPropertyName("retained_last_good")] bool RetainedLastGood,
    AbiQueryTarget? Target,
    [property: JsonPropertyName("managed_selection")]
        AbiRecordSelectionResult ManagedSelection,
    [property: JsonPropertyName("native_selection")]
        AbiRecordSelectionResult NativeSelection,
    [property: JsonPropertyName("managed_record")] AbiRecordSummary? ManagedRecord,
    [property: JsonPropertyName("native_record")] AbiRecordSummary? NativeRecord,
    IReadOnlyList<AbiCompatibilityCheckRow> Checks,
    [property: JsonPropertyName("total_check_count")] int TotalCheckCount,
    IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("total_reason_count")] int TotalReasonCount,
    AbiFindingRow? Finding,
    [property: JsonPropertyName("total_finding_count")] int TotalFindingCount,
    IReadOnlyList<AbiQueryFailureRow> Failures,
    [property: JsonPropertyName("total_failure_count")] int TotalFailureCount,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_check_count")] int OmittedCheckCount,
    [property: JsonPropertyName("omitted_reason_count")] int OmittedReasonCount,
    [property: JsonPropertyName("omitted_evidence_count")] int OmittedEvidenceCount,
    [property: JsonPropertyName("omitted_metadata_count")] int OmittedMetadataCount,
    [property: JsonPropertyName("omitted_character_count")] int OmittedCharacterCount);

public sealed record AbiRecordSelectionResult(
    string Status,
    IReadOnlyList<AbiRecordSelectionCandidate> Candidates,
    [property: JsonPropertyName("total_candidate_count")] int TotalCandidateCount,
    [property: JsonPropertyName("candidate_omitted_count")] int CandidateOmittedCount);

public sealed record AbiRecordSelectionCandidate(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string CanonicalKey,
    [property: JsonPropertyName("record_kind")] string RecordKind,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("start_column")] int StartColumn,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);

public sealed record AbiRecordSummary(
    [property: JsonPropertyName("canonical_key")] string CanonicalKey,
    [property: JsonPropertyName("record_kind")] string RecordKind,
    [property: JsonPropertyName("size_bytes")] int? SizeBytes,
    [property: JsonPropertyName("alignment_bytes")] int? AlignmentBytes,
    int? Pack,
    [property: JsonPropertyName("field_count")] int FieldCount,
    AbiQueryTarget Target,
    IReadOnlyList<AbiQueryEvidenceRow> Evidence,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount);

public sealed record AbiQueryTarget(
    [property: JsonPropertyName("runtime_identifier")] string RuntimeIdentifier,
    string Architecture,
    [property: JsonPropertyName("compiler_abi")] string CompilerAbi,
    [property: JsonPropertyName("pointer_size_bytes")] int PointerSizeBytes,
    [property: JsonPropertyName("default_pack")] int DefaultPack);

public sealed record AbiCompatibilityCheckRow(
    string Path,
    string Aspect,
    string Compatibility,
    string Reason,
    string Confidence,
    IReadOnlyList<AbiQueryEvidenceRow> Evidence,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount);

public sealed record AbiQueryEvidenceRow(
    [property: JsonPropertyName("producing_file_id")] long ProducingFileId,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("start_column")] int StartColumn,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("metadata_omitted_count")]
        int MetadataOmittedCount);

public sealed record AbiFindingRow(
    [property: JsonPropertyName("rule_id")] string RuleId,
    string Severity,
    string Message,
    [property: JsonPropertyName("managed_symbol")] string? ManagedSymbol,
    [property: JsonPropertyName("native_symbol")] string? NativeSymbol,
    string Confidence,
    IReadOnlyList<AbiQueryEvidenceRow> Evidence,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount);

public sealed record AbiQueryFailureRow(
    string Stage,
    string Code,
    string Message,
    [property: JsonPropertyName("annotation_id")] long? AnnotationId = null,
    [property: JsonPropertyName("file_path")] string? FilePath = null,
    [property: JsonPropertyName("translation_unit_index")]
        int? TranslationUnitIndex = null,
    [property: JsonPropertyName("configured_path")] string? ConfiguredPath = null);

/// <summary>
/// Reads the strict persisted ABI-record universe for one scope, selects one managed and one
/// native record without guessing, resolves explicit nested mappings, and invokes the pure ABI
/// engine only when every selected fact is current for the runtime target.
/// </summary>
internal sealed class AbiCompatibilityQueryService
{
    internal const int MaximumSearchHits = 10_000;
    internal const int MaximumNestedMappings = 64;
    internal const int MaximumQueryCharacters = 4096;
    private const int MaximumSelectionCandidates = 64;
    private const int MaximumFailureMessageCharacters = 512;

    public async Task<AbiScopeComparisonResult> QueryAsync(
        string scopeId,
        string scopeStatus,
        IGraphStore store,
        NativeInteropRuntimeState? runtimeState,
        string managedQuery,
        string nativeQuery,
        bool managedInputComplete,
        IReadOnlyList<AbiRecordMappingQuery>? nestedMappings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeStatus);
        ArgumentNullException.ThrowIfNull(store);
        var managedProbe = ValidateQuery(managedQuery, nameof(managedQuery));
        var nativeProbe = ValidateQuery(nativeQuery, nameof(nativeQuery));
        nestedMappings ??= [];

        var snapshot = await InteropFactStoreReader.ReadAbiRecordsAsync(
                store,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var failures = snapshot.Failures
            .Select(FactFailure)
            .ToList();
        var managedFacts = new List<StoredInteropFact<AbiRecordLayout>>();
        var nativeFacts = new List<StoredInteropFact<AbiRecordLayout>>();
        foreach (var stored in snapshot.Facts)
        {
            var fact = stored.Fact;
            if (IsManagedCanonicalKey(fact.SymbolCanonicalKey))
            {
                if (fact.Kind is not (
                        AbiRecordKind.Sequential or AbiRecordKind.Explicit))
                {
                    failures.Add(new AbiQueryFailureRow(
                        "fact-validation",
                        "managed-record-kind-invalid",
                        "A csharp: ABI record is not Sequential or Explicit.",
                        stored.Row.AnnotationId,
                        stored.Row.FilePath));
                    continue;
                }
                managedFacts.Add(stored);
                continue;
            }

            if (IsNativeCanonicalKey(fact.SymbolCanonicalKey))
            {
                if (fact.Kind != AbiRecordKind.Native)
                {
                    failures.Add(new AbiQueryFailureRow(
                        "fact-validation",
                        "native-record-kind-invalid",
                        "A c:/cpp: ABI record does not have native record kind.",
                        stored.Row.AnnotationId,
                        stored.Row.FilePath));
                    continue;
                }
                nativeFacts.Add(stored);
                continue;
            }

            failures.Add(new AbiQueryFailureRow(
                "fact-validation",
                "record-owner-scheme-invalid",
                "ABI record owners must use lowercase csharp:, c:, or cpp: canonical keys.",
                stored.Row.AnnotationId,
                stored.Row.FilePath));
        }

        if (runtimeState is null)
        {
            failures.Add(new AbiQueryFailureRow(
                "runtime",
                "native-runtime-unavailable",
                "The scope has no current native interop runtime state."));
        }
        else
        {
            failures.AddRange(runtimeState.Failures.Select(RuntimeFailure));
            if (runtimeState.Status != NativeInteropRuntimeStatus.Complete)
            {
                failures.Add(new AbiQueryFailureRow(
                    "runtime",
                    "native-state-not-current",
                    $"Native interop state is {RuntimeStatusToken(runtimeState.Status)}."));
            }
            if (!runtimeState.IsExportUniverseComplete)
            {
                failures.Add(new AbiQueryFailureRow(
                    "runtime",
                    "export-universe-incomplete",
                    "The native export universe is incomplete."));
            }
            if (runtimeState.RetainedLastGood)
            {
                failures.Add(new AbiQueryFailureRow(
                    "runtime",
                    "retained-last-good",
                    "Persisted native ABI records are retained from the last-good snapshot."));
            }
        }
        if (!managedInputComplete)
        {
            failures.Add(new AbiQueryFailureRow(
                "runtime",
                "managed-input-incomplete",
                "The managed ABI-record input universe is incomplete."));
        }
        if (!string.Equals(scopeStatus, "ok", StringComparison.Ordinal))
        {
            failures.Add(new AbiQueryFailureRow(
                "runtime",
                "scope-not-current",
                $"Scope status is {scopeStatus}; no current complete ABI comparison is available."));
        }

        var runtimeComplete = runtimeState is
        {
            Status: NativeInteropRuntimeStatus.Complete,
            IsExportUniverseComplete: true,
            RetainedLastGood: false,
        };
        var currentUniverseAvailable = snapshot.IsComplete
            && failures.All(failure =>
                failure.Stage != "fact-validation")
            && runtimeComplete
            && managedInputComplete
            && string.Equals(scopeStatus, "ok", StringComparison.Ordinal);
        if (!currentUniverseAvailable)
        {
            return Unknown(
                scopeId,
                scopeStatus,
                runtimeState,
                managedSelection: EmptySelection("unknown"),
                nativeSelection: EmptySelection("unknown"),
                failures);
        }

        var managedByKey = managedFacts.ToDictionary(
            item => item.Fact.SymbolCanonicalKey,
            StringComparer.Ordinal);
        var nativeByKey = nativeFacts.ToDictionary(
            item => item.Fact.SymbolCanonicalKey,
            StringComparer.Ordinal);
        var managedSelection = await SelectAsync(
                store,
                managedProbe,
                managedByKey,
                managed: true,
                cancellationToken)
            .ConfigureAwait(false);
        var nativeSelection = await SelectAsync(
                store,
                nativeProbe,
                nativeByKey,
                managed: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (managedSelection.Failure is not null)
        {
            failures.Add(managedSelection.Failure);
        }
        if (nativeSelection.Failure is not null)
        {
            failures.Add(nativeSelection.Failure);
        }
        if (managedSelection.Selected is null
            || nativeSelection.Selected is null)
        {
            return Unknown(
                scopeId,
                scopeStatus,
                runtimeState,
                managedSelection.Output,
                nativeSelection.Output,
                failures);
        }

        var target = runtimeState!.Target;
        var managedLayout = managedSelection.Selected.Fact;
        var nativeLayout = nativeSelection.Selected.Fact;
        if (!managedLayout.Target.IsAbiEquivalentTo(target))
        {
            failures.Add(new AbiQueryFailureRow(
                "target-validation",
                "managed-target-mismatch",
                "The selected managed record target does not equal the current runtime target.",
                managedSelection.Selected.Row.AnnotationId,
                managedSelection.Selected.Row.FilePath));
        }
        if (!nativeLayout.Target.IsAbiEquivalentTo(target))
        {
            failures.Add(new AbiQueryFailureRow(
                "target-validation",
                "native-target-mismatch",
                "The selected native record target does not equal the current runtime target.",
                nativeSelection.Selected.Row.AnnotationId,
                nativeSelection.Selected.Row.FilePath));
        }

        var resolvedMappings = ResolveMappings(
            nestedMappings,
            managedByKey,
            nativeByKey,
            target,
            failures);
        if (failures.Any(failure =>
                failure.Stage is "target-validation" or "mapping"))
        {
            return Unknown(
                scopeId,
                scopeStatus,
                runtimeState,
                managedSelection.Output,
                nativeSelection.Output,
                failures,
                managedLayout,
                nativeLayout);
        }

        var engineResult = new AbiStructCompatibilityEngine().Compare(
            managedLayout,
            nativeLayout,
            resolvedMappings);
        var finding = new Interop002FindingAdapter().CreateFinding(engineResult);
        var checks = engineResult.Checks
            .Select(ProjectCheck)
            .ToArray();
        var reasons = engineResult.Differences.ToArray();
        return new AbiScopeComparisonResult(
            scopeId,
            scopeStatus,
            "ok",
            CompatibilityToken(engineResult.Compatibility),
            Partial: false,
            RetainedLastGood: false,
            ProjectTarget(target),
            managedSelection.Output,
            nativeSelection.Output,
            ProjectRecord(managedLayout),
            ProjectRecord(nativeLayout),
            checks,
            checks.Length,
            reasons,
            reasons.Length,
            finding is null ? null : ProjectFinding(finding),
            finding is null ? 0 : 1,
            OrderFailures(failures),
            failures.Count,
            Truncated: false,
            OmittedCount: 0,
            OmittedCheckCount: 0,
            OmittedReasonCount: 0,
            OmittedEvidenceCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);
    }

    private static IReadOnlyList<AbiRecordIdentityMapping> ResolveMappings(
        IReadOnlyList<AbiRecordMappingQuery> mappings,
        IReadOnlyDictionary<string, StoredInteropFact<AbiRecordLayout>>
            managedByKey,
        IReadOnlyDictionary<string, StoredInteropFact<AbiRecordLayout>>
            nativeByKey,
        InteropTarget target,
        ICollection<AbiQueryFailureRow> failures)
    {
        if (mappings.Count > MaximumNestedMappings)
        {
            failures.Add(new AbiQueryFailureRow(
                "mapping",
                "mapping-limit-exceeded",
                $"Nested record mappings exceed the {MaximumNestedMappings}-item input limit."));
            return [];
        }

        var resolved = new List<AbiRecordIdentityMapping>(mappings.Count);
        var exactPairs = new HashSet<(string Managed, string Native)>();
        var managedNames = new HashSet<string>(StringComparer.Ordinal);
        var nativeNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            if (mapping is null)
            {
                failures.Add(MappingFailure(
                    "mapping-null",
                    $"Nested record mapping {index} is null."));
                continue;
            }

            var managedType = ValidateMappingValue(
                mapping.ManagedTypeCanonicalName,
                index,
                "managed_type_canonical_name",
                failures);
            var nativeType = ValidateMappingValue(
                mapping.NativeTypeCanonicalName,
                index,
                "native_type_canonical_name",
                failures);
            var managedRecord = ValidateMappingValue(
                mapping.ManagedRecord,
                index,
                "managed_record",
                failures);
            var nativeRecord = ValidateMappingValue(
                mapping.NativeRecord,
                index,
                "native_record",
                failures);
            if (managedType is null
                || nativeType is null
                || managedRecord is null
                || nativeRecord is null)
            {
                continue;
            }
            if (!IsManagedCanonicalKey(managedRecord)
                || !IsNativeCanonicalKey(nativeRecord))
            {
                failures.Add(MappingFailure(
                    "mapping-record-scheme-invalid",
                    $"Nested record mapping {index} requires an exact lowercase csharp: "
                    + "managed record and c:/cpp: native record."));
                continue;
            }
            if (!managedByKey.TryGetValue(managedRecord, out var managed)
                || !nativeByKey.TryGetValue(nativeRecord, out var native))
            {
                failures.Add(MappingFailure(
                    "mapping-record-not-found",
                    $"Nested record mapping {index} could not resolve both exact record keys."));
                continue;
            }
            if (!managed.Fact.Target.IsAbiEquivalentTo(target)
                || !native.Fact.Target.IsAbiEquivalentTo(target))
            {
                failures.Add(MappingFailure(
                    "mapping-target-mismatch",
                    $"Nested record mapping {index} does not target the current runtime ABI."));
                continue;
            }

            var pair = (managedType, nativeType);
            if (!exactPairs.Add(pair)
                || !managedNames.Add(managedType)
                || !nativeNames.Add(nativeType))
            {
                failures.Add(MappingFailure(
                    "mapping-not-one-to-one",
                    $"Nested record mapping {index} is duplicate or one-to-many."));
                continue;
            }
            resolved.Add(new AbiRecordIdentityMapping(
                managedType,
                nativeType,
                managed.Fact,
                native.Fact));
        }

        return failures.Any(failure => failure.Stage == "mapping")
            ? []
            : resolved
                .OrderBy(
                    mapping => mapping.ManagedTypeCanonicalName,
                    StringComparer.Ordinal)
                .ThenBy(
                    mapping => mapping.NativeTypeCanonicalName,
                    StringComparer.Ordinal)
                .ToArray();
    }

    private static string? ValidateMappingValue(
        string? value,
        int index,
        string field,
        ICollection<AbiQueryFailureRow> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(MappingFailure(
                "mapping-value-invalid",
                $"Nested record mapping {index} field `{field}` must be non-empty."));
            return null;
        }
        if (value.Length > MaximumQueryCharacters)
        {
            failures.Add(MappingFailure(
                "mapping-value-too-long",
                $"Nested record mapping {index} field `{field}` exceeds "
                + $"{MaximumQueryCharacters} characters."));
            return null;
        }
        return value;
    }

    private static async Task<SelectionOutcome> SelectAsync(
        IGraphStore store,
        string query,
        IReadOnlyDictionary<string, StoredInteropFact<AbiRecordLayout>> facts,
        bool managed,
        CancellationToken cancellationToken)
    {
        var isExpectedExact = managed
            ? IsManagedCanonicalKey(query)
            : IsNativeCanonicalKey(query);
        var isOppositeExact = managed
            ? IsNativeCanonicalKey(query)
            : IsManagedCanonicalKey(query);
        if (isOppositeExact)
        {
            return SelectionOutcome.Invalid(
                managed ? "managed" : "native",
                "selection-role-scheme-invalid",
                managed
                    ? "Managed record selection cannot use a c:/cpp: canonical key."
                    : "Native record selection cannot use a csharp: canonical key.");
        }
        if (isExpectedExact)
        {
            return facts.TryGetValue(query, out var exact)
                ? SelectionOutcome.From([exact])
                : SelectionOutcome.NotFound(managed ? "managed" : "native");
        }

        var hits = await store.FindSymbolsAsync(
                query,
                filePathHint: null,
                MaximumSearchHits,
                cancellationToken)
            .ConfigureAwait(false);
        var candidates = hits
            .Where(hit => hit.CanonicalKey is not null)
            .Select(hit => hit.CanonicalKey!)
            .Distinct(StringComparer.Ordinal)
            .Where(facts.ContainsKey)
            .Select(key => facts[key])
            .OrderBy(
                item => item.Fact.SymbolCanonicalKey,
                StringComparer.Ordinal)
            .ToArray();
        if (hits.Count >= MaximumSearchHits)
        {
            return SelectionOutcome.Bounded(
                managed ? "managed" : "native",
                candidates);
        }
        return SelectionOutcome.From(
            candidates,
            managed ? "managed" : "native");
    }

    private static AbiScopeComparisonResult Unknown(
        string scopeId,
        string scopeStatus,
        NativeInteropRuntimeState? runtimeState,
        AbiRecordSelectionResult managedSelection,
        AbiRecordSelectionResult nativeSelection,
        IReadOnlyCollection<AbiQueryFailureRow> failures,
        AbiRecordLayout? managed = null,
        AbiRecordLayout? native = null)
    {
        const string reason =
            "Comparison was not performed because current, uniquely selected ABI facts were unavailable.";
        var orderedFailures = OrderFailures(failures);
        return new AbiScopeComparisonResult(
            scopeId,
            scopeStatus,
            "partial",
            "unknown",
            Partial: true,
            RetainedLastGood: runtimeState?.RetainedLastGood == true,
            runtimeState is null ? null : ProjectTarget(runtimeState.Target),
            managedSelection,
            nativeSelection,
            managed is null ? null : ProjectRecord(managed),
            native is null ? null : ProjectRecord(native),
            Checks: [],
            TotalCheckCount: 0,
            Reasons: [reason],
            TotalReasonCount: 1,
            Finding: null,
            TotalFindingCount: 0,
            orderedFailures,
            orderedFailures.Count,
            Truncated: false,
            OmittedCount: 0,
            OmittedCheckCount: 0,
            OmittedReasonCount: 0,
            OmittedEvidenceCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);
    }

    private static AbiRecordSelectionResult EmptySelection(string status) =>
        new(status, [], 0, 0);

    private static AbiRecordSummary ProjectRecord(AbiRecordLayout layout) =>
        new(
            layout.SymbolCanonicalKey,
            RecordKindToken(layout.Kind),
            layout.SizeBytes,
            layout.AlignmentBytes,
            layout.Pack,
            layout.Fields.Count,
            ProjectTarget(layout.Target),
            [ProjectEvidence(layout.Evidence)],
            EvidenceOmittedCount: 0);

    private static AbiCompatibilityCheckRow ProjectCheck(
        AbiCompatibilityCheck check) =>
        new(
            check.Path,
            AspectToken(check.Aspect),
            CompatibilityToken(check.Compatibility),
            check.Reason,
            ConfidenceToken(check.Confidence),
            ProjectEvidence(check.Evidence),
            EvidenceOmittedCount: 0);

    private static AbiFindingRow ProjectFinding(InteropFinding finding) =>
        new(
            finding.RuleId,
            SeverityToken(finding.Severity),
            finding.Message,
            finding.ManagedSymbolCanonicalKey,
            finding.NativeSymbolCanonicalKey,
            ConfidenceToken(finding.Confidence),
            ProjectEvidence(finding.Evidence),
            EvidenceOmittedCount: 0);

    private static IReadOnlyList<AbiQueryEvidenceRow> ProjectEvidence(
        IEnumerable<Evidence> evidence) =>
        evidence
            .OrderBy(item => item.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Location.StartLine)
            .ThenBy(item => item.Location.StartColumn)
            .ThenBy(item => item.Location.EndLine)
            .ThenBy(item => item.Location.EndColumn)
            .ThenBy(item => item.ProducingFileId)
            .ThenBy(item => item.Producer, StringComparer.Ordinal)
            .Select(ProjectEvidence)
            .ToArray();

    private static AbiQueryEvidenceRow ProjectEvidence(Evidence evidence) =>
        new(
            evidence.ProducingFileId,
            evidence.Location.FilePath,
            evidence.Location.StartLine,
            evidence.Location.StartColumn,
            evidence.Location.EndLine,
            evidence.Location.EndColumn,
            ConfidenceToken(evidence.Confidence),
            evidence.Producer,
            evidence.Metadata is null
                ? null
                : new SortedDictionary<string, string>(
                    evidence.Metadata.ToDictionary(
                        item => item.Key,
                        item => item.Value,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
            MetadataOmittedCount: 0);

    private static AbiQueryTarget ProjectTarget(InteropTarget target) =>
        new(
            target.RuntimeIdentifier,
            ArchitectureToken(target.Architecture),
            CompilerAbiToken(target.CompilerAbi),
            target.PointerSizeBytes,
            target.DefaultPack);

    private static AbiQueryFailureRow FactFailure(
        InteropFactLoadFailure failure) =>
        new(
            "fact-read",
            "abi-record-invalid",
            BoundMessage(failure.Reason),
            failure.AnnotationId,
            string.IsNullOrEmpty(failure.FilePath)
                ? null
                : failure.FilePath);

    private static AbiQueryFailureRow RuntimeFailure(
        NativeInteropRuntimeFailure failure) =>
        new(
            failure.Stage,
            failure.Code,
            BoundMessage(failure.Message),
            TranslationUnitIndex: failure.TranslationUnitIndex,
            ConfiguredPath: failure.ConfiguredPath);

    private static AbiQueryFailureRow MappingFailure(
        string code,
        string message) =>
        new("mapping", code, message);

    private static IReadOnlyList<AbiQueryFailureRow> OrderFailures(
        IEnumerable<AbiQueryFailureRow> failures) =>
        failures
            .OrderBy(failure => failure.Stage, StringComparer.Ordinal)
            .ThenBy(failure => failure.Code, StringComparer.Ordinal)
            .ThenBy(failure => failure.AnnotationId ?? long.MaxValue)
            .ThenBy(failure => failure.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(failure => failure.Message, StringComparer.Ordinal)
            .ToArray();

    private static string ValidateQuery(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumQueryCharacters)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                trimmed.Length,
                $"ABI record queries are limited to {MaximumQueryCharacters} characters.");
        }
        return trimmed;
    }

    private static string BoundMessage(string message) =>
        message.Length <= MaximumFailureMessageCharacters
            ? message
            : message[..MaximumFailureMessageCharacters];

    private static bool IsManagedCanonicalKey(string value) =>
        value.StartsWith("csharp:", StringComparison.Ordinal);

    private static bool IsNativeCanonicalKey(string value) =>
        value.StartsWith("c:", StringComparison.Ordinal)
        || value.StartsWith("cpp:", StringComparison.Ordinal);

    private static string RuntimeStatusToken(NativeInteropRuntimeStatus value) =>
        value switch
        {
            NativeInteropRuntimeStatus.NotStarted => "not_started",
            NativeInteropRuntimeStatus.Indexing => "indexing",
            NativeInteropRuntimeStatus.Complete => "complete",
            NativeInteropRuntimeStatus.Partial => "partial",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string CompatibilityToken(InteropCompatibility value) =>
        value switch
        {
            InteropCompatibility.Unknown => "unknown",
            InteropCompatibility.Compatible => "compatible",
            InteropCompatibility.Warning => "warning",
            InteropCompatibility.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string RecordKindToken(AbiRecordKind value) =>
        value switch
        {
            AbiRecordKind.Sequential => "sequential",
            AbiRecordKind.Explicit => "explicit",
            AbiRecordKind.Native => "native",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string ConfidenceToken(EvidenceConfidence value) =>
        value switch
        {
            EvidenceConfidence.Inferred => "inferred",
            EvidenceConfidence.Semantic => "semantic",
            EvidenceConfidence.Exact => "exact",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string SeverityToken(InteropFindingSeverity value) =>
        value switch
        {
            InteropFindingSeverity.Info => "info",
            InteropFindingSeverity.Warning => "warning",
            InteropFindingSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string ArchitectureToken(InteropArchitecture value) =>
        value switch
        {
            InteropArchitecture.X86 => "x86",
            InteropArchitecture.X64 => "x64",
            InteropArchitecture.Arm64 => "arm64",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string CompilerAbiToken(InteropCompilerAbi value) =>
        value switch
        {
            InteropCompilerAbi.Msvc => "msvc",
            InteropCompilerAbi.Itanium => "itanium",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string AspectToken(AbiCompatibilityAspect value) =>
        value switch
        {
            AbiCompatibilityAspect.Target => "target",
            AbiCompatibilityAspect.RecordKind => "record_kind",
            AbiCompatibilityAspect.RecordSize => "record_size",
            AbiCompatibilityAspect.RecordAlignment => "record_alignment",
            AbiCompatibilityAspect.Pack => "pack",
            AbiCompatibilityAspect.FieldCount => "field_count",
            AbiCompatibilityAspect.FieldOrder => "field_order",
            AbiCompatibilityAspect.FieldCategory => "field_category",
            AbiCompatibilityAspect.FieldOffset => "field_offset",
            AbiCompatibilityAspect.FieldSize => "field_size",
            AbiCompatibilityAspect.FixedArrayLength => "fixed_array_length",
            AbiCompatibilityAspect.BooleanSize => "boolean_size",
            AbiCompatibilityAspect.PointerDepth => "pointer_depth",
            AbiCompatibilityAspect.PointerSize => "pointer_size",
            AbiCompatibilityAspect.NestedRecordIdentity =>
                "nested_record_identity",
            AbiCompatibilityAspect.NestedRecordLayout =>
                "nested_record_layout",
            AbiCompatibilityAspect.CollectionLimit => "collection_limit",
            AbiCompatibilityAspect.RecursionLimit => "recursion_limit",
            AbiCompatibilityAspect.Cycle => "cycle",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private sealed record SelectionOutcome(
        AbiRecordSelectionResult Output,
        StoredInteropFact<AbiRecordLayout>? Selected,
        AbiQueryFailureRow? Failure)
    {
        public static SelectionOutcome From(
            IReadOnlyList<StoredInteropFact<AbiRecordLayout>> candidates,
            string role = "record")
        {
            var output = ProjectSelection(
                candidates.Count switch
                {
                    0 => "not_found",
                    1 => "selected",
                    _ => "ambiguous",
                },
                candidates);
            return candidates.Count switch
            {
                0 => new(
                    output,
                    Selected: null,
                    new AbiQueryFailureRow(
                        "selection",
                        $"{role}-record-not-found",
                        $"No persisted {role} ABI record uniquely matched the query.")),
                1 => new(output, candidates[0], Failure: null),
                _ => new(
                    output,
                    Selected: null,
                    new AbiQueryFailureRow(
                        "selection",
                        $"{role}-record-ambiguous",
                        $"Multiple persisted {role} ABI records matched the query.")),
            };
        }

        public static SelectionOutcome NotFound(string role) =>
            From([], role);

        public static SelectionOutcome Invalid(
            string role,
            string code,
            string message) =>
            new(
                EmptySelection("invalid"),
                Selected: null,
                new AbiQueryFailureRow(
                    "selection",
                    $"{role}-{code}",
                    message));

        public static SelectionOutcome Bounded(
            string role,
            IReadOnlyList<StoredInteropFact<AbiRecordLayout>> candidates) =>
            new(
                ProjectSelection("unknown", candidates),
                Selected: null,
                new AbiQueryFailureRow(
                    "selection",
                    $"{role}-search-bound-reached",
                    $"Symbol selection reached the {MaximumSearchHits}-hit bound; "
                    + "uniqueness cannot be proven."));

        private static AbiRecordSelectionResult ProjectSelection(
            string status,
            IReadOnlyList<StoredInteropFact<AbiRecordLayout>> candidates)
        {
            var rows = candidates
                .OrderBy(
                    item => item.Fact.SymbolCanonicalKey,
                    StringComparer.Ordinal)
                .Take(MaximumSelectionCandidates)
                .Select(item =>
                {
                    var location = item.Fact.Evidence.Location;
                    return new AbiRecordSelectionCandidate(
                        item.Row.SymbolId,
                        item.Fact.SymbolCanonicalKey,
                        RecordKindToken(item.Fact.Kind),
                        location.FilePath,
                        location.StartLine,
                        location.StartColumn,
                        location.EndLine,
                        location.EndColumn);
                })
                .ToArray();
            return new AbiRecordSelectionResult(
                status,
                rows,
                candidates.Count,
                Math.Max(0, candidates.Count - rows.Length));
        }
    }
}
