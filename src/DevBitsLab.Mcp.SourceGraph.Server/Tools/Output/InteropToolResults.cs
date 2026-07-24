using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>Typed object-root output for <c>match_pinvoke</c>.</summary>
public sealed record MatchPInvokeResult(
    string Symbol,
    string Status,
    IReadOnlyList<InteropScopeQueryResult> Scopes,
    [property: JsonPropertyName("total_selection_candidate_count")]
        int TotalSelectionCandidateCount,
    [property: JsonPropertyName("total_match_count")] int TotalMatchCount,
    [property: JsonPropertyName("total_failure_count")] int TotalFailureCount,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_evidence_count")]
        int OmittedEvidenceCount,
    [property: JsonPropertyName("prose_omitted_count")]
        int ProseOmittedCount);

/// <summary>Typed object-root output for <c>analyze_native_boundary</c>.</summary>
public sealed record AnalyzeNativeBoundaryResult(
    string Symbol,
    string Status,
    IReadOnlyList<InteropScopeQueryResult> Scopes,
    [property: JsonPropertyName("total_selection_candidate_count")]
        int TotalSelectionCandidateCount,
    [property: JsonPropertyName("total_match_count")] int TotalMatchCount,
    [property: JsonPropertyName("total_finding_count")] int TotalFindingCount,
    [property: JsonPropertyName("total_failure_count")] int TotalFailureCount,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_evidence_count")]
        int OmittedEvidenceCount,
    [property: JsonPropertyName("prose_omitted_count")]
        int ProseOmittedCount);
