namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>graph_stats</c> MCP tool. Singleton DTO — the tool
/// returns one summary row of counts for the active scope's database. Wraps the four counts in
/// an object as required by the MCP SDK's <c>"type":"object"</c> root constraint.
/// </summary>
public sealed record GraphStatsResult(
    int Files,
    int Symbols,
    int References,
    int Edges);
