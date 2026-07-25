using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Evidence-first result for <c>trace_call_path</c>. A single response keeps per-scope results
/// structured even when the caller fans out with <c>scope="*"</c>.
/// </summary>
public sealed record TraceCallPathResult(
    [property: JsonPropertyName("from_query")] string FromQuery,
    [property: JsonPropertyName("to_query")] string? ToQuery,
    string Profile,
    string Detail,
    [property: JsonPropertyName("destination_mode")] string DestinationMode,
    [property: JsonPropertyName("terminal_definition")] string? TerminalDefinition,
    [property: JsonPropertyName("edge_kind")] string? EdgeKind,
    IReadOnlyList<string> Relations,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    [property: JsonPropertyName("max_paths")] int MaxPaths,
    [property: JsonPropertyName("max_nodes")] int MaxNodes,
    IReadOnlyList<TraceCallPathScopeResult> Scopes);

public sealed record TraceCallPathScopeResult(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    IReadOnlyList<TraceCallPath> Paths,
    bool Truncated,
    [property: JsonPropertyName("expanded_nodes")] int ExpandedNodes,
    string? Note,
    [property: JsonPropertyName("execution_state")]
    TraceCallPathExecutionState? ExecutionState)
{
    public string Status { get; init; } = "ok";

    [JsonPropertyName("path_search_executed")]
    public bool PathSearchExecuted { get; init; } = true;

    [JsonPropertyName("ambiguous_role")]
    public string? AmbiguousRole { get; init; }

    public IReadOnlyList<TraceCallPathSymbol> Candidates { get; init; } = [];

    public TraceCallPathTruncation? Truncation { get; init; }
}

public sealed record TraceCallPathTruncation(
    [property: JsonPropertyName("truncated_by")]
    IReadOnlyList<string> TruncatedBy,
    [property: JsonPropertyName("expanded_nodes")] int ExpandedNodes,
    [property: JsonPropertyName("max_nodes")] int MaxNodes,
    [property: JsonPropertyName("depth_reached")] int DepthReached,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    [property: JsonPropertyName("returned_paths")] int ReturnedPaths,
    [property: JsonPropertyName("max_paths")] int MaxPaths,
    [property: JsonPropertyName("returned_evidence_rows")]
    int ReturnedEvidenceRows,
    [property: JsonPropertyName("max_evidence_rows")] int MaxEvidenceRows,
    [property: JsonPropertyName("branch_limit")] int BranchLimit);

/// <summary>
/// Completeness disclosure for the cross-domain execution profile. Persisted paths remain
/// evidence-backed when a projection is partial, but an empty result is authoritative only when
/// every applicable projection is current and complete.
/// </summary>
public sealed record TraceCallPathExecutionState(
    string Status,
    bool Partial,
    [property: JsonPropertyName("absence_authoritative")]
    bool AbsenceAuthoritative,
    [property: JsonPropertyName("retained_last_good")]
    bool RetainedLastGood,
    IReadOnlyList<TraceCallPathProjectionState> Projections,
    IReadOnlyList<string> Failures);

public sealed record TraceCallPathProjectionState(
    string Name,
    string Status,
    bool Applicable,
    bool Authoritative,
    [property: JsonPropertyName("retained_last_good")]
    bool RetainedLastGood,
    [property: JsonPropertyName("failure_count")]
    int FailureCount);

public sealed record TraceCallPath(
    TraceCallPathSymbol From,
    TraceCallPathSymbol To,
    string Confidence,
    IReadOnlyList<TraceCallPathHop> Hops)
{
    [JsonPropertyName("hop_count")]
    public int HopCount { get; init; } = Hops.Count;
}

public sealed record TraceCallPathHop(
    TraceCallPathSymbol From,
    TraceCallPathSymbol To,
    string Relation,
    string Confidence,
    IReadOnlyList<TraceCallPathEvidence> Evidence,
    [property: JsonPropertyName("evidence_truncated")] bool EvidenceTruncated);

public sealed record TraceCallPathSymbol(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);

public sealed record TraceCallPathEvidence(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("start_column")] int StartColumn,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata);
