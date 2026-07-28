using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>impact_of_change</c> MCP tool. Pairs the resolved root
/// symbol with the transitive set of upstream callers reached by walking <see cref="EdgeKind"/>
/// edges backwards up to <see cref="MaxDepth"/>.
/// </summary>
public sealed record ImpactOfChangeResult(
    string Result,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string Completeness,
    [property: JsonPropertyName("absence_authoritative")] bool AbsenceAuthoritative,
    string? Reason,
    [property: JsonPropertyName("selection_mode")] string SelectionMode,
    [property: JsonPropertyName("fallback_used")] bool FallbackUsed,
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("selection_ambiguous")] bool SelectionAmbiguous,
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    [property: JsonPropertyName("target_canonical_key")] string? TargetCanonicalKey,
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    bool Truncated,
    [property: JsonPropertyName("expanded_nodes")] int ExpandedNodes,
    IReadOnlyList<ImpactOfChangeRow> Upstream);

/// <summary>
/// One upstream caller — <see cref="Depth"/> is the BFS distance from the root (1 = direct
/// caller, 2 = caller-of-caller, etc.). <see cref="Predecessor"/> is the next symbol toward the
/// changed target in the breadth-first predecessor tree. <see cref="Path"/> is ordered from this
/// upstream symbol to the changed target and every hop contains real occurrence evidence.
/// </summary>
public sealed record ImpactOfChangeRow(
    int Depth,
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    TraceCallPathSymbol Predecessor,
    string Confidence,
    IReadOnlyList<TraceCallPathHop> Path);
