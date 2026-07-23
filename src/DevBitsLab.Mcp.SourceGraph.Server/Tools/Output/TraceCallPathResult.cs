using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Evidence-first result for <c>trace_call_path</c>. A single response keeps per-scope results
/// structured even when the caller fans out with <c>scope="*"</c>.
/// </summary>
public sealed record TraceCallPathResult(
    [property: JsonPropertyName("from_query")] string FromQuery,
    [property: JsonPropertyName("to_query")] string ToQuery,
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    [property: JsonPropertyName("max_paths")] int MaxPaths,
    [property: JsonPropertyName("max_nodes")] int MaxNodes,
    IReadOnlyList<TraceCallPathScopeResult> Scopes);

public sealed record TraceCallPathScopeResult(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    IReadOnlyList<TraceCallPath> Paths,
    bool Truncated,
    [property: JsonPropertyName("expanded_nodes")] int ExpandedNodes,
    string? Note);

public sealed record TraceCallPath(
    TraceCallPathSymbol From,
    TraceCallPathSymbol To,
    string Confidence,
    IReadOnlyList<TraceCallPathHop> Hops);

public sealed record TraceCallPathHop(
    TraceCallPathSymbol From,
    TraceCallPathSymbol To,
    string Relation,
    string Confidence,
    IReadOnlyList<TraceCallPathEvidence> Evidence,
    [property: JsonPropertyName("evidence_truncated")] bool EvidenceTruncated);

public sealed record TraceCallPathSymbol(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column);

public sealed record TraceCallPathEvidence(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("start_column")] int StartColumn,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata);
