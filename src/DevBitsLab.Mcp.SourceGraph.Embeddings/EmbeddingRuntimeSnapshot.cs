namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>Runtime resource state for a lazily-loaded embedding generator.</summary>
public sealed record EmbeddingRuntimeSnapshot(
    bool Loaded,
    bool Available,
    DateTimeOffset? LoadedAt,
    DateTimeOffset? LastUsedAt,
    TimeSpan IdleTimeout,
    long ModelFileBytes,
    long ModelResidentEstimateBytes);

/// <summary>
/// Optional resource-management surface implemented by generators that own heavyweight native
/// inference state. It is separate from <see cref="ICodeEmbeddingGenerator"/> so lightweight
/// mocks and remote generators do not need to emulate local memory management.
/// </summary>
public interface IManagedEmbeddingRuntime
{
    EmbeddingRuntimeSnapshot GetRuntimeSnapshot();

    /// <summary>Unload native model state when it has exceeded the configured idle timeout.</summary>
    bool TryUnloadIfIdle(DateTimeOffset now);
}
