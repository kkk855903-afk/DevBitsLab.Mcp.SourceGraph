namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// One-way producer surface used by the indexer to enqueue per-symbol embedding work.
/// The default implementation (registered by the host) wraps a bounded
/// <see cref="System.Threading.Channels.Channel{T}"/> drained by a single background worker.
/// When <c>--no-embeddings</c> is set, a no-op implementation is registered so the indexer
/// path stays the same shape.
/// </summary>
public interface IEmbeddingsRequestSink
{
    /// <summary>True when the indexer should bother computing the synthesised text + hash.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Enqueue a request. Implementations MUST NOT block the indexer; if the queue is full,
    /// they SHOULD drop the oldest pending entry rather than wait.
    /// </summary>
    void Enqueue(EmbedRequest request);
}

/// <summary>
/// No-op sink used when <c>--no-embeddings</c> is set, when the model isn't cached, or when
/// the vector extension didn't load. Makes the indexer's enqueue site unconditional.
/// </summary>
public sealed class NoOpEmbeddingsRequestSink : IEmbeddingsRequestSink
{
    public bool IsEnabled => false;
    public void Enqueue(EmbedRequest request) { /* swallow */ }
}
