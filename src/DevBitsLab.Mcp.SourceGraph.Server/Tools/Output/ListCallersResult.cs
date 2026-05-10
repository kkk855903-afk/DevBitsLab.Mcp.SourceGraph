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
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    IReadOnlyList<ListCallersRow> Callers);

/// <summary>
/// One inbound-edge row — the calling/consuming symbol on the source side of the edge.
/// <see cref="PayloadJson"/> carries the originating <c>edges.payload</c> column when the edge
/// has per-instance metadata (e.g. XAML binding paths); null otherwise.
/// </summary>
public sealed record ListCallersRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("payload_json")] string? PayloadJson);
