using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>neighborhood</c> MCP tool. Pairs the resolved center
/// symbol with its immediate inbound and outbound neighbors. <see cref="EdgeKind"/> is the
/// kebab-case kind that was walked (or "all" for cross-kind walks).
/// </summary>
public sealed record NeighborhoodResult(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    IReadOnlyList<NeighborhoodRow> Inbound,
    IReadOnlyList<NeighborhoodRow> Outbound);

/// <summary>
/// One neighbor of the centre symbol. <see cref="PayloadJson"/> carries the originating
/// <c>edges.payload</c> column when the edge has metadata (XAML binding paths, etc.); null
/// otherwise.
/// </summary>
public sealed record NeighborhoodRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("payload_json")] string? PayloadJson);
