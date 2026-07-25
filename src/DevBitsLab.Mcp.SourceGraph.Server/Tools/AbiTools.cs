using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// One explicit nested-record mapping accepted by <c>compare_struct</c>. The record fields must
/// be exact persisted canonical keys; the type-name fields must exactly match the corresponding
/// <c>AbiTypeRef.CanonicalName</c> values. No simple-name inference is performed.
/// </summary>
public sealed record AbiNestedRecordMappingInput(
    [property: JsonPropertyName("managed_type_canonical_name")]
    string ManagedTypeCanonicalName,
    [property: JsonPropertyName("native_type_canonical_name")]
    string NativeTypeCanonicalName,
    [property: JsonPropertyName("managed_record")]
    string ManagedRecord,
    [property: JsonPropertyName("native_record")]
    string NativeRecord);

/// <summary>Read-only tools for persisted managed/native ABI record comparison.</summary>
[McpServerToolType]
public static class AbiTools
{
    private const int MaximumScopeFanout = 16;
    private const int OutputBudgetSafetyMargin = 256;
    private const int MaximumProseChecksPerScope = 8;
    private const int MaximumProseReasonsPerScope = 4;
    private const int MaximumProseDetailCharacters = 256;
    private static readonly AbiCompatibilityQueryService QueryService = new();

    [McpServerTool(
        UseStructuredContent = true,
        OutputSchemaType = typeof(CompareStructResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger(
        "\"compare these managed and native structs\", "
        + "\"check struct ABI compatibility\", "
        + "\"compare_struct\"")]
    [Description(
        "Compare one persisted managed ABI record with one persisted native ABI record for the "
        + "current scope target. Exact canonical keys are preferred; non-canonical probes must "
        + "select exactly one record on each side. Nested records are compared only through the "
        + "explicit `nested_mappings` input and are never guessed by name. Returns typed checks, "
        + "reasons, exact source evidence, and an Interop002 finding for non-compatible results. "
        + "`partial` describes analysis completeness; `truncated` only describes response "
        + "presentation omissions.")]
    public static Task<CallToolResult> CompareStructAsync(
        ScopeRouter router,
        [Description(
            "Managed ABI record canonical key (`csharp:`) or a query that uniquely selects one.")]
        string managed,
        [Description(
            "Native ABI record canonical key (`c:`/`cpp:`) or a query that uniquely selects one.")]
        string native,
        [Description(
            "Optional explicit nested-record mappings (maximum 64). Every record field is an "
            + "exact canonical key; every type field is an exact field type canonical name.")]
        IReadOnlyList<AbiNestedRecordMappingInput>? nested_mappings = null,
        [Description("Optional scope id, '*', or comma-separated scope ids.")]
        string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "compare_struct",
            new
            {
                managed,
                native,
                nested_mapping_count = nested_mappings?.Count ?? 0,
                scope,
            },
            () => CompareStructImplAsync(
                router,
                managed,
                native,
                nested_mappings,
                scope,
                ct));

    private static Task<CallToolResult> CompareStructImplAsync(
        ScopeRouter router,
        string managed,
        string native,
        IReadOnlyList<AbiNestedRecordMappingInput>? nestedMappings,
        object? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (string.IsNullOrWhiteSpace(managed)
            || string.IsNullOrWhiteSpace(native))
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    "compare_struct requires non-empty `managed` and `native` queries."));
        }
        if (managed.Trim().Length
                > AbiCompatibilityQueryService.MaximumQueryCharacters
            || native.Trim().Length
                > AbiCompatibilityQueryService.MaximumQueryCharacters)
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    "compare_struct queries must not exceed "
                    + $"{AbiCompatibilityQueryService.MaximumQueryCharacters} characters."));
        }
        if (nestedMappings?.Count
            > AbiCompatibilityQueryService.MaximumNestedMappings)
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    "compare_struct accepts at most "
                    + $"{AbiCompatibilityQueryService.MaximumNestedMappings} "
                    + "nested mappings."));
        }
        var mappings = (nestedMappings ?? [])
            .Select(mapping => mapping is null
                ? null!
                : new AbiRecordMappingQuery(
                    mapping.ManagedTypeCanonicalName,
                    mapping.NativeTypeCanonicalName,
                    mapping.ManagedRecord,
                    mapping.NativeRecord))
            .ToArray();
        return ScopedExecution.RunAsync(
            router,
            scope,
            async (host, _, _) =>
            {
                var result = await QueryService.QueryAsync(
                        host.Scope.Id,
                        host.Status,
                        host.Store,
                        host.NativeInteropState,
                        managed,
                        native,
                        host.ManagedInteropInputComplete,
                        mappings,
                        cancellationToken)
                    .ConfigureAwait(false);
                return BuildBoundedResult(
                    managed,
                    native,
                    mappings.Length,
                    [result]);
            },
            scoped => MergeResults(
                managed,
                native,
                mappings.Length,
                scoped),
            cancellationToken,
            maxHosts: MaximumScopeFanout);
    }

    private static CallToolResult MergeResults(
        string managed,
        string native,
        int mappingCount,
        IReadOnlyList<ScopedCallToolResult> perScope)
    {
        var scopes = new List<AbiScopeComparisonResult>(perScope.Count);
        foreach (var scoped in perScope)
        {
            if (scoped.Result.StructuredContent is { } structured)
            {
                var result = structured.Deserialize(
                    ToolOutputJsonContext.Default.CompareStructResult);
                if (result is not null && result.Scopes.Count > 0)
                {
                    scopes.AddRange(result.Scopes);
                    continue;
                }
            }
            scopes.Add(DiagnosticScope(scoped));
        }

        return BuildBoundedResult(
            managed,
            native,
            mappingCount,
            scopes
                .OrderBy(item => item.ScopeId, StringComparer.Ordinal)
                .ToArray());
    }

    private static AbiScopeComparisonResult DiagnosticScope(
        ScopedCallToolResult scoped)
    {
        var message = scoped.Result.Content?
            .OfType<TextContentBlock>()
            .Select(block => block.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?? "Scope query failed without a diagnostic message.";
        if (message.Length > 512)
        {
            message = message[..512];
        }
        var failure = new AbiQueryFailureRow(
            "scope",
            "scope-query-failed",
            message);
        var selection = new AbiRecordSelectionResult(
            "unknown",
            [],
            TotalCandidateCount: 0,
            CandidateOmittedCount: 0);
        return new AbiScopeComparisonResult(
            scoped.ScopeId,
            scoped.ScopeStatus,
            "struct-maps-to",
            "partial",
            "unknown",
            Partial: true,
            RetainedLastGood: false,
            Target: null,
            selection,
            selection,
            ManagedRecord: null,
            NativeRecord: null,
            Checks: [],
            TotalCheckCount: 0,
            Reasons:
            [
                "Comparison was not performed because the scope query failed.",
            ],
            TotalReasonCount: 1,
            Finding: null,
            TotalFindingCount: 0,
            Failures: [failure],
            TotalFailureCount: 1,
            Truncated: false,
            OmittedCount: 0,
            OmittedCheckCount: 0,
            OmittedReasonCount: 0,
            OmittedEvidenceCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);
    }

    private static CallToolResult BuildBoundedResult(
        string managed,
        string native,
        int mappingCount,
        IReadOnlyList<AbiScopeComparisonResult> rawScopes)
    {
        foreach (var limits in ReductionLimits.Stages)
        {
            var scopes = rawScopes
                .OrderBy(scope => scope.ScopeId, StringComparer.Ordinal)
                .Select(scope => LimitScope(scope, limits))
                .ToArray();
            var dto = CreateDto(managed, native, mappingCount, scopes);
            var result = CreateCallToolResult(dto);
            if (SerializedLength(result) <= EffectiveOutputBudget)
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            "The required compare_struct per-scope compatibility core exceeds "
            + "the 50K-character output budget.");
    }

    internal static CallToolResult BuildBoundedResultForTests(
        string managed,
        string native,
        int mappingCount,
        IReadOnlyList<AbiScopeComparisonResult> scopes) =>
        BuildBoundedResult(managed, native, mappingCount, scopes);

    private static CompareStructResult CreateDto(
        string managed,
        string native,
        int mappingCount,
        IReadOnlyList<AbiScopeComparisonResult> scopes)
    {
        var status = AggregateStatus(scopes.Select(scope => scope.Status));
        var compatibility = AggregateCompatibility(
            scopes.Select(scope => scope.Compatibility));
        return new CompareStructResult(
            managed,
            native,
            status,
            compatibility,
            mappingCount,
            scopes,
            scopes.Count,
            SaturatingSum(scopes.Select(scope => scope.TotalCheckCount)),
            SaturatingSum(scopes.Select(scope => scope.TotalFindingCount)),
            scopes.Any(scope => scope.Partial),
            scopes.Any(scope => scope.Truncated),
            SaturatingSum(scopes.Select(scope => scope.OmittedCount)),
            SaturatingSum(scopes.Select(scope => scope.OmittedCheckCount)),
            SaturatingSum(scopes.Select(scope => scope.OmittedReasonCount)),
            SaturatingSum(scopes.Select(scope => scope.OmittedEvidenceCount)),
            SaturatingSum(scopes.Select(scope => scope.OmittedMetadataCount)),
            SaturatingSum(scopes.Select(scope => scope.OmittedCharacterCount)));
    }

    private static CallToolResult CreateCallToolResult(CompareStructResult dto)
    {
        var prose = new StringBuilder()
            .Append("compare_struct: status=`")
            .Append(dto.Status)
            .Append("`, compatibility=`")
            .Append(dto.Compatibility)
            .Append("`, relation=`struct-maps-to")
            .Append("`, scopes=")
            .Append(dto.Scopes.Count)
            .Append(", analysis_partial=")
            .Append(dto.Partial ? "true" : "false")
            .Append(", response_truncated=")
            .Append(dto.Truncated ? "true" : "false")
            .Append(", omitted=")
            .Append(dto.OmittedCount);
        if (dto.OmittedCount > 0)
        {
            prose.Append(" (omitted_checks=")
                .Append(dto.OmittedCheckCount)
                .Append(", omitted_reasons=")
                .Append(dto.OmittedReasonCount)
                .Append(", omitted_evidence=")
                .Append(dto.OmittedEvidenceCount)
                .Append(", omitted_metadata=")
                .Append(dto.OmittedMetadataCount)
                .Append(", omitted_characters=")
                .Append(dto.OmittedCharacterCount)
                .Append(')');
        }
        foreach (var scope in dto.Scopes)
        {
            prose.AppendLine()
                .Append("- scope `")
                .Append(scope.ScopeId)
                .Append("`: compatibility=`")
                .Append(scope.Compatibility)
                .Append("`, checks=")
                .Append(scope.Checks.Count)
                .Append('/')
                .Append(scope.TotalCheckCount)
                .Append(", reasons=")
                .Append(scope.Reasons.Count)
                .Append('/')
                .Append(scope.TotalReasonCount);
            foreach (var check in scope.Checks
                         .Where(check => !string.Equals(
                             check.Compatibility,
                             "compatible",
                             StringComparison.Ordinal))
                         .Take(MaximumProseChecksPerScope))
            {
                prose.AppendLine()
                    .Append("  - [")
                    .Append(check.Compatibility)
                    .Append("] `")
                    .Append(ProseDetail(check.Path))
                    .Append("` ")
                    .Append(check.Aspect)
                    .Append(": ")
                    .Append(ProseDetail(check.Reason));
            }
            foreach (var reason in scope.Reasons
                         .Take(MaximumProseReasonsPerScope))
            {
                prose.AppendLine()
                    .Append("  - reason: ")
                    .Append(ProseDetail(reason));
            }
        }
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = prose.ToString() },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.CompareStructResult),
        };
    }

    private static AbiScopeComparisonResult LimitScope(
        AbiScopeComparisonResult scope,
        ReductionLimits limits)
    {
        var originalEvidenceCount = CountEvidence(scope);
        var originalMetadataCount = CountMetadata(scope);

        var managedSelection = LimitSelection(
            scope.ManagedSelection,
            limits.SelectionCandidates);
        var nativeSelection = LimitSelection(
            scope.NativeSelection,
            limits.SelectionCandidates);
        var managedRecord = limits.IncludeRecords && scope.ManagedRecord is not null
            ? LimitRecord(scope.ManagedRecord, limits)
            : null;
        var nativeRecord = limits.IncludeRecords && scope.NativeRecord is not null
            ? LimitRecord(scope.NativeRecord, limits)
            : null;
        var checks = scope.Checks
            .Select((check, index) => new
            {
                Check = check,
                Index = index,
            })
            .OrderBy(item => string.Equals(
                item.Check.Compatibility,
                "compatible",
                StringComparison.Ordinal)
                    ? 1
                    : 0)
            .ThenBy(item => item.Index)
            .Take(limits.Checks)
            .Select(item => LimitCheck(item.Check, limits))
            .ToArray();
        var reasons = scope.Reasons
            .Take(limits.Reasons)
            .ToArray();
        var finding = limits.IncludeFinding && scope.Finding is not null
            ? LimitFinding(scope.Finding, limits)
            : null;
        var minimumFailures =
            scope.Partial && scope.TotalFailureCount > 0 ? 1 : 0;
        var failures = scope.Failures
            .Take(Math.Max(minimumFailures, limits.Failures))
            .ToArray();

        var keptEvidenceCount = CountEvidence(
            managedRecord,
            nativeRecord,
            checks,
            finding);
        var keptMetadataCount = CountMetadata(
            managedRecord,
            nativeRecord,
            checks,
            finding);
        var omittedChecks = Math.Max(
            scope.OmittedCheckCount,
            scope.TotalCheckCount - checks.Length);
        var omittedReasons = Math.Max(
            scope.OmittedReasonCount,
            scope.TotalReasonCount - reasons.Length);
        var omittedEvidence = Math.Max(
            scope.OmittedEvidenceCount,
            originalEvidenceCount - keptEvidenceCount);
        var omittedMetadata = Math.Max(
            scope.OmittedMetadataCount,
            originalMetadataCount - keptMetadataCount);
        var omittedCandidates = SaturatingAdd(
            managedSelection.CandidateOmittedCount,
            nativeSelection.CandidateOmittedCount);
        var omittedFailures = Math.Max(
            0,
            scope.TotalFailureCount - failures.Length);
        var omittedFinding = Math.Max(
            0,
            scope.TotalFindingCount - (finding is null ? 0 : 1));
        var omittedRecords =
            (scope.ManagedRecord is not null && managedRecord is null ? 1 : 0)
            + (scope.NativeRecord is not null && nativeRecord is null ? 1 : 0);
        var omittedTarget =
            scope.Target is not null && !limits.IncludeTarget ? 1 : 0;
        var omitted = omittedCandidates;
        omitted = SaturatingAdd(omitted, omittedChecks);
        omitted = SaturatingAdd(omitted, omittedReasons);
        omitted = SaturatingAdd(omitted, omittedEvidence);
        omitted = SaturatingAdd(omitted, omittedMetadata);
        omitted = SaturatingAdd(omitted, omittedFailures);
        omitted = SaturatingAdd(omitted, omittedFinding);
        omitted = SaturatingAdd(omitted, omittedRecords);
        omitted = SaturatingAdd(omitted, omittedTarget);
        omitted = SaturatingAdd(omitted, scope.OmittedCharacterCount);
        omitted = Math.Max(scope.OmittedCount, omitted);

        return scope with
        {
            Partial = scope.Partial,
            Target = limits.IncludeTarget ? scope.Target : null,
            ManagedSelection = managedSelection,
            NativeSelection = nativeSelection,
            ManagedRecord = managedRecord,
            NativeRecord = nativeRecord,
            Checks = checks,
            Reasons = reasons,
            Finding = finding,
            Failures = failures,
            Truncated = scope.Truncated || omitted > 0,
            OmittedCount = omitted,
            OmittedCheckCount = omittedChecks,
            OmittedReasonCount = omittedReasons,
            OmittedEvidenceCount = omittedEvidence,
            OmittedMetadataCount = omittedMetadata,
        };
    }

    private static AbiRecordSelectionResult LimitSelection(
        AbiRecordSelectionResult selection,
        int requested)
    {
        var minimum = selection.Status switch
        {
            "selected" => 1,
            "ambiguous" => 2,
            _ => 0,
        };
        var candidates = selection.Candidates
            .Take(Math.Max(minimum, requested))
            .ToArray();
        return selection with
        {
            Candidates = candidates,
            CandidateOmittedCount = Math.Max(
                selection.CandidateOmittedCount,
                selection.TotalCandidateCount - candidates.Length),
        };
    }

    private static AbiRecordSummary LimitRecord(
        AbiRecordSummary record,
        ReductionLimits limits)
    {
        var evidence = LimitEvidence(
            record.Evidence,
            record.EvidenceOmittedCount,
            limits);
        return record with
        {
            Evidence = evidence.Rows,
            EvidenceOmittedCount = evidence.Omitted,
        };
    }

    private static AbiCompatibilityCheckRow LimitCheck(
        AbiCompatibilityCheckRow check,
        ReductionLimits limits)
    {
        var evidence = LimitEvidence(
            check.Evidence,
            check.EvidenceOmittedCount,
            limits);
        return check with
        {
            Evidence = evidence.Rows,
            EvidenceOmittedCount = evidence.Omitted,
        };
    }

    private static AbiFindingRow LimitFinding(
        AbiFindingRow finding,
        ReductionLimits limits)
    {
        var evidence = LimitEvidence(
            finding.Evidence,
            finding.EvidenceOmittedCount,
            limits);
        return finding with
        {
            Evidence = evidence.Rows,
            EvidenceOmittedCount = evidence.Omitted,
        };
    }

    private static (
        IReadOnlyList<AbiQueryEvidenceRow> Rows,
        int Omitted) LimitEvidence(
        IReadOnlyList<AbiQueryEvidenceRow> evidence,
        int alreadyOmitted,
        ReductionLimits limits)
    {
        var total = SaturatingAdd(evidence.Count, alreadyOmitted);
        var rows = evidence
            .Take(limits.EvidencePerItem)
            .Select(item =>
            {
                if (limits.IncludeMetadata)
                {
                    return item;
                }
                return item with
                {
                    Metadata = null,
                    MetadataOmittedCount = SaturatingAdd(
                        item.Metadata?.Count ?? 0,
                        item.MetadataOmittedCount),
                };
            })
            .ToArray();
        return (rows, Math.Max(0, total - rows.Length));
    }

    private static int CountEvidence(AbiScopeComparisonResult scope) =>
        SaturatingAdd(
            CountEvidence(
                scope.ManagedRecord,
                scope.NativeRecord,
                scope.Checks,
                scope.Finding),
            scope.OmittedEvidenceCount);

    private static int CountEvidence(
        AbiRecordSummary? managed,
        AbiRecordSummary? native,
        IReadOnlyList<AbiCompatibilityCheckRow> checks,
        AbiFindingRow? finding)
    {
        var total = managed?.Evidence.Count ?? 0;
        total = SaturatingAdd(total, native?.Evidence.Count ?? 0);
        total = SaturatingAdd(
            total,
            SaturatingSum(checks.Select(check => check.Evidence.Count)));
        total = SaturatingAdd(total, finding?.Evidence.Count ?? 0);
        return total;
    }

    private static int CountMetadata(AbiScopeComparisonResult scope) =>
        SaturatingAdd(
            CountMetadata(
                scope.ManagedRecord,
                scope.NativeRecord,
                scope.Checks,
                scope.Finding),
            scope.OmittedMetadataCount);

    private static int CountMetadata(
        AbiRecordSummary? managed,
        AbiRecordSummary? native,
        IReadOnlyList<AbiCompatibilityCheckRow> checks,
        AbiFindingRow? finding)
    {
        static int EvidenceMetadata(
            IEnumerable<AbiQueryEvidenceRow> evidence) =>
            SaturatingSum(evidence.Select(item => item.Metadata?.Count ?? 0));

        var total = managed is null ? 0 : EvidenceMetadata(managed.Evidence);
        total = SaturatingAdd(
            total,
            native is null ? 0 : EvidenceMetadata(native.Evidence));
        total = SaturatingAdd(
            total,
            SaturatingSum(checks.Select(check =>
                EvidenceMetadata(check.Evidence))));
        total = SaturatingAdd(
            total,
            finding is null ? 0 : EvidenceMetadata(finding.Evidence));
        return total;
    }

    private static int SerializedLength(CallToolResult result) =>
        JsonSerializer.Serialize(
            result,
            McpJsonUtilities.DefaultOptions).Length;

    private static string ProseDetail(string value)
    {
        var oneLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return oneLine.Length <= MaximumProseDetailCharacters
            ? oneLine
            : oneLine[..MaximumProseDetailCharacters] + "…";
    }

    private static int EffectiveOutputBudget =>
        OutputBudget.DefaultBudgetChars - OutputBudgetSafetyMargin;

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        var distinct = statuses
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return distinct.Length switch
        {
            0 => "unknown",
            1 => distinct[0],
            _ => "mixed",
        };
    }

    private static string AggregateCompatibility(
        IEnumerable<string> compatibilities)
    {
        var values = compatibilities.ToArray();
        if (values.Contains("error", StringComparer.Ordinal)) return "error";
        if (values.Contains("warning", StringComparer.Ordinal)) return "warning";
        if (values.Contains("unknown", StringComparer.Ordinal)) return "unknown";
        return values.Length > 0
            && values.All(value =>
                string.Equals(
                    value,
                    "compatible",
                    StringComparison.Ordinal))
            ? "compatible"
            : "unknown";
    }

    private static int SaturatingSum(IEnumerable<int> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total = SaturatingAdd(total, value);
        }
        return total;
    }

    private static int SaturatingAdd(int left, int right)
    {
        var sum = (long)left + right;
        return sum >= int.MaxValue
            ? int.MaxValue
            : (int)sum;
    }

    private sealed record ReductionLimits(
        int Checks,
        int Reasons,
        int SelectionCandidates,
        int EvidencePerItem,
        int Failures,
        bool IncludeMetadata,
        bool IncludeRecords,
        bool IncludeFinding,
        bool IncludeTarget)
    {
        public static IReadOnlyList<ReductionLimits> Stages { get; } =
        [
            new(
                Checks: 1024,
                Reasons: 1024,
                SelectionCandidates: 64,
                EvidencePerItem: 8,
                Failures: 64,
                IncludeMetadata: true,
                IncludeRecords: true,
                IncludeFinding: true,
                IncludeTarget: true),
            new(
                Checks: 256,
                Reasons: 256,
                SelectionCandidates: 16,
                EvidencePerItem: 4,
                Failures: 32,
                IncludeMetadata: false,
                IncludeRecords: true,
                IncludeFinding: true,
                IncludeTarget: true),
            new(
                Checks: 64,
                Reasons: 64,
                SelectionCandidates: 8,
                EvidencePerItem: 2,
                Failures: 8,
                IncludeMetadata: false,
                IncludeRecords: true,
                IncludeFinding: true,
                IncludeTarget: true),
            new(
                Checks: 16,
                Reasons: 16,
                SelectionCandidates: 2,
                EvidencePerItem: 1,
                Failures: 2,
                IncludeMetadata: false,
                IncludeRecords: true,
                IncludeFinding: true,
                IncludeTarget: true),
            new(
                Checks: 1,
                Reasons: 1,
                SelectionCandidates: 0,
                EvidencePerItem: 1,
                Failures: 1,
                IncludeMetadata: false,
                IncludeRecords: false,
                IncludeFinding: true,
                IncludeTarget: true),
            new(
                Checks: 0,
                Reasons: 0,
                SelectionCandidates: 0,
                EvidencePerItem: 0,
                Failures: 0,
                IncludeMetadata: false,
                IncludeRecords: false,
                IncludeFinding: false,
                IncludeTarget: false),
        ];
    }
}
