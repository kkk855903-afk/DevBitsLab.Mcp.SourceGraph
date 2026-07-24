namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Outcome of a fail-closed stale native-symbol cleanup. Retained keys still have a stored
/// declaration but could not be proven orphaned. Missing keys make repeated cleanup calls
/// idempotent without reporting a deletion that did not occur.
/// </summary>
public sealed record NativeInteropStaleSymbolCleanupResult(
    IReadOnlyList<string> DeletedCanonicalKeys,
    IReadOnlyList<string> RetainedCanonicalKeys,
    IReadOnlyList<string> MissingCanonicalKeys);
