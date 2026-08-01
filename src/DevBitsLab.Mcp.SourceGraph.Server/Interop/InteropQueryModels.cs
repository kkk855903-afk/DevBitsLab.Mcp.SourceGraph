using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

/// <summary>
/// Controls which persisted interop declarations may satisfy a symbol query. The two modes map
/// to the query contract: callers may either restrict selection to managed imports or accept
/// one uniquely selected managed import or native export.
/// </summary>
public enum InteropQuerySelectionMode
{
    ManagedImportOnly,
    ManagedOrNativeBoundary,
}

/// <summary>
/// One fully bounded, single-scope interop query result. A later MCP adapter may compose several
/// of these blocks, but it must retain one block per selected scope.
/// </summary>
public sealed record InteropScopeQueryResult(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    string Query,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string Status,
    bool Partial,
    [property: JsonPropertyName("retained_last_good")] bool RetainedLastGood,
    [property: JsonPropertyName("selection_status")] string SelectionStatus,
    [property: JsonPropertyName("selection_candidates")]
        IReadOnlyList<InteropQuerySelectionCandidate> SelectionCandidates,
    [property: JsonPropertyName("total_selection_candidate_count")]
        int TotalSelectionCandidateCount,
    IReadOnlyList<InteropQueryMatchRow> Matches,
    [property: JsonPropertyName("total_match_count")] int TotalMatchCount,
    IReadOnlyList<InteropQueryFindingRow> Findings,
    [property: JsonPropertyName("total_finding_count")] int TotalFindingCount,
    IReadOnlyList<InteropQueryFailureRow> Failures,
    [property: JsonPropertyName("total_failure_count")] int TotalFailureCount,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_evidence_count")]
        int OmittedEvidenceCount,
    [property: JsonPropertyName("omitted_reason_count")] int OmittedReasonCount,
    [property: JsonPropertyName("omitted_metadata_count")]
        int OmittedMetadataCount,
    [property: JsonPropertyName("omitted_character_count")]
        int OmittedCharacterCount);

/// <summary>A managed import or native export retained by fail-closed symbol selection.</summary>
public sealed record InteropQuerySelectionCandidate(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string CanonicalKey,
    [property: JsonPropertyName("symbol_type")] string SymbolType,
    string Display,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column);

/// <summary>One persisted managed/native match rendered without adapter-specific objects.</summary>
public sealed record InteropQueryMatchRow(
    [property: JsonPropertyName("managed_symbol")] string ManagedSymbol,
    [property: JsonPropertyName("native_symbol")] string? NativeSymbol,
    string Relation,
    string Status,
    string Confidence,
    IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    InteropQueryTarget Target,
    IReadOnlyList<InteropQueryEvidenceRow> Evidence,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount,
    [property: JsonPropertyName("reason_omitted_count")]
        int ReasonOmittedCount);

/// <summary>One persisted Phase 2 boundary finding. Interop002 is deliberately not representable.</summary>
public sealed record InteropQueryFindingRow(
    [property: JsonPropertyName("rule_id")] string RuleId,
    string Severity,
    string Message,
    [property: JsonPropertyName("managed_symbol")] string ManagedSymbol,
    [property: JsonPropertyName("native_symbol")] string NativeSymbol,
    string Relation,
    string Confidence,
    InteropQueryTarget Target,
    IReadOnlyList<InteropQueryEvidenceRow> Evidence,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount);

public sealed record InteropQueryTarget(
    [property: JsonPropertyName("runtime_identifier")] string RuntimeIdentifier,
    string Architecture,
    [property: JsonPropertyName("compiler_abi")] string CompilerAbi,
    [property: JsonPropertyName("pointer_size_bytes")] int PointerSizeBytes,
    [property: JsonPropertyName("default_pack")] int DefaultPack);

public sealed record InteropQueryEvidenceRow(
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

/// <summary>A bounded runtime, fact-loading, selection, or projection consistency failure.</summary>
public sealed record InteropQueryFailureRow(
    string Stage,
    string Code,
    string Message,
    [property: JsonPropertyName("translation_unit_index")]
        int? TranslationUnitIndex = null,
    [property: JsonPropertyName("configured_path")] string? ConfiguredPath = null);

/// <summary>
/// Result plus the exact JSON that passed the final character-budget check. The MCP adapter may
/// use <see cref="Result"/> to compose a multi-scope DTO; tests and single-scope callers can use
/// <see cref="SerializedJson"/> to verify that no host-side invalid-JSON truncation is required.
/// </summary>
internal sealed record BoundedInteropScopeQuery(
    InteropScopeQueryResult Result,
    string SerializedJson);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(InteropScopeQueryResult))]
internal partial class InteropQueryJsonContext : JsonSerializerContext;
