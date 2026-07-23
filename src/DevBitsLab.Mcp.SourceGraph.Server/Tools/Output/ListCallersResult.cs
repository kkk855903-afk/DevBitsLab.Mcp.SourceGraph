using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>list_callers</c> MCP tool. Pairs the resolved target
/// descriptor with the list of inbound-edge rows so consumers can chain queries against
/// <see cref="TargetSymbolId"/> without re-resolving the symbol.
/// </summary>
public sealed record ListCallersResult(
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    [property: JsonPropertyName("target_canonical_key")] string? TargetCanonicalKey,
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    bool Truncated,
    IReadOnlyList<ListCallersRow> Callers);

/// <summary>
/// One auditable inbound edge. The legacy flattened symbol fields describe
/// <see cref="Source"/> and remain available for existing consumers; <see cref="Source"/>,
/// <see cref="Target"/>, <see cref="Relation"/>, and <see cref="Evidence"/> make the actual
/// logical edge and its occurrence locations explicit.
/// <see cref="PayloadJson"/> carries the originating <c>edges.payload</c> column when the edge
/// has per-instance metadata (e.g. XAML binding paths); null otherwise.
/// </summary>
public sealed record ListCallersRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    TraceCallPathSymbol Source,
    TraceCallPathSymbol Target,
    string Relation,
    string Confidence,
    IReadOnlyList<TraceCallPathEvidence> Evidence,
    [property: JsonPropertyName("evidence_truncated")] bool EvidenceTruncated,
    [property: JsonPropertyName("payload_json")] string? PayloadJson);
