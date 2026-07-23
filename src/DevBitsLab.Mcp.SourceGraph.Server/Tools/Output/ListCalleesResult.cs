using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>list_callees</c> MCP tool. Mirror of
/// <see cref="ListCallersResult"/> but for outbound edges from the target.
/// </summary>
public sealed record ListCalleesResult(
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    IReadOnlyList<ListCalleesRow> Callees);

/// <summary>
/// One outbound-edge row — the called/consumed symbol on the destination side of the edge.
/// <see cref="PayloadJson"/> carries the originating <c>edges.payload</c> column when the edge
/// has per-instance metadata; null otherwise.
/// </summary>
public sealed record ListCalleesRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("payload_json")] string? PayloadJson);
