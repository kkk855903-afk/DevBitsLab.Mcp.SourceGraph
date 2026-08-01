namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed output for one MCP round-trip containing several independent symbol searches.
/// </summary>
public sealed record SearchSymbolsBatchResult(
    IReadOnlyList<SearchSymbolsBatchQueryResult> Queries);

public sealed record SearchSymbolsBatchQueryResult(
    string Query,
    IReadOnlyList<SearchSymbolHit> Hits);
