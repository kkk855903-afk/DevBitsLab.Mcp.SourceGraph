namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Persistence boundary for the per-symbol code embeddings produced by the embedding pipeline.
/// Concrete implementations are free to back the data with <c>sqlite-vec</c>, an in-memory
/// brute-force index (tests), or an alternative vector store; the indexer and the
/// <c>semantic_search</c> tool only depend on this interface.
/// </summary>
public interface IEmbeddingsStore
{
    /// <summary>True when the underlying vector index is available and writes/queries will succeed.</summary>
    bool IsAvailable { get; }

    /// <summary>Embedding dimension this store is configured for (e.g. 768).</summary>
    int Dimension { get; }

    /// <summary>
    /// Insert or replace the embedding for the given symbol id. The <paramref name="contentHash"/>
    /// SHOULD be the SHA-256 of the synthesised text so a follow-up reindex can short-circuit
    /// when the hash is unchanged. The <paramref name="modelVersion"/> is the active embedding
    /// model identity; rows whose model_version doesn't match the current model are treated as
    /// missing by <see cref="ShouldReembedAsync"/>.
    /// </summary>
    Task UpsertAsync(long symbolId, byte[] contentHash, IReadOnlyList<float> embedding, string modelVersion, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the symbol either has no row yet, has a different content_hash, or has
    /// a different model_version. Used by the indexer to gate re-embedding.
    /// </summary>
    Task<bool> ShouldReembedAsync(long symbolId, byte[] contentHash, string modelVersion, CancellationToken ct = default);

    /// <summary>
    /// Top-k nearest-neighbour query against the configured vector index. Returns
    /// <c>(symbolId, similarity)</c> pairs in descending similarity order. Similarity is the
    /// cosine similarity in <c>[-1, 1]</c>; a vec0 distance of <c>d</c> on L2-normalised vectors
    /// is converted via <c>1 - d^2 / 2</c>. <paramref name="kindFilter"/> is the kebab-case
    /// symbol kind name (e.g. <c>"method"</c>); <c>null</c> matches every kind.
    /// </summary>
    Task<IReadOnlyList<EmbeddingHit>> SearchAsync(IReadOnlyList<float> queryEmbedding, int k, string? kindFilter = null, CancellationToken ct = default);

    /// <summary>Total number of stored embeddings (count of rows in <c>symbol_embeddings</c>).</summary>
    Task<long> CountAsync(CancellationToken ct = default);
}

/// <summary>One row in a top-k <c>semantic_search</c> result. <see cref="Score"/> is in [-1, 1].</summary>
public sealed record EmbeddingHit(long SymbolId, double Score);
