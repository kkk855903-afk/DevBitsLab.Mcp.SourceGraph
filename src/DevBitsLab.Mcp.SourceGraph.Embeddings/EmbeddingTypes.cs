namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// One pending embedding job. The <see cref="SymbolId"/> is the row id of the symbol whose
/// synthesised text we want to embed; <see cref="Text"/> is the content; <see cref="ContentHash"/>
/// is the SHA-256 of <see cref="Text"/> the producer already computed (we don't recompute it).
/// </summary>
public sealed record EmbedRequest(long SymbolId, string Text, byte[] ContentHash);

/// <summary>
/// Identity of the embedding model in use. Surfaces in the <c>embedding_meta.model_version</c>
/// column so swapping models (different dimension or different training corpus) invalidates the
/// existing rows rather than mixing dimensions.
/// </summary>
/// <param name="ModelId">Hugging Face-style identifier, e.g. <c>jinaai/jina-embeddings-v2-base-code</c>.</param>
/// <param name="Dimension">Embedding dimension produced by this model (e.g. 768).</param>
public sealed record EmbeddingModelInfo(string ModelId, int Dimension)
{
    /// <summary>
    /// Canonical "model version" string stored alongside each row so we can invalidate
    /// existing rows when the active model changes.
    /// </summary>
    public string Version => $"{ModelId}/{Dimension}";
}

/// <summary>
/// Default embedding model identity used when no <c>--model</c> override is supplied.
/// 768-dim, code-trained, ~280 MB INT8-quantised ONNX from Hugging Face.
/// </summary>
public static class DefaultEmbeddingModel
{
    public const string ModelId = "jinaai/jina-embeddings-v2-base-code";
    public const int Dimension = 768;

    public static EmbeddingModelInfo Info { get; } = new(ModelId, Dimension);
}
