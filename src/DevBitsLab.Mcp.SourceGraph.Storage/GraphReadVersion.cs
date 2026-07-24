namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// A connection-local observation used to detect whether graph reads may have become stale.
/// <see cref="ConnectionChanges"/> advances for writes performed by this connection, while
/// <see cref="DataVersion"/> advances when another connection commits a database change.
/// </summary>
public readonly record struct GraphReadVersion(
    long ConnectionChanges,
    long DataVersion);
