namespace DevBitsLab.Mcp.SourceGraph.Watcher;

/// <summary>
/// A coalesced batch of paths that changed within a debounce window.
/// </summary>
public sealed record FileChangeBatch(IReadOnlyCollection<string> Paths, FileChangeReason Reason);

public enum FileChangeReason
{
    FileSystemEvent,
    GitHeadChanged,
    InitialSweep,
}
