using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>impact_of_change</c> MCP tool. Pairs the resolved root
/// symbol with the transitive set of upstream callers reached by walking <see cref="EdgeKind"/>
/// edges backwards up to <see cref="MaxDepth"/>.
/// </summary>
public sealed record ImpactOfChangeResult(
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    IReadOnlyList<ImpactOfChangeRow> Upstream);

/// <summary>
/// One upstream caller — <see cref="Depth"/> is the BFS distance from the root (1 = direct
/// caller, 2 = caller-of-caller, etc.).
/// </summary>
public sealed record ImpactOfChangeRow(
    int Depth,
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column);
