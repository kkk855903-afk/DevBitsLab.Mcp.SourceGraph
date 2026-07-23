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
/// 768-dim, code-trained, ~640 MB FP32 ONNX (the upstream `onnx/model.onnx` file at HF) —
/// not the smaller INT8-quantised variant. Subsequent runs hit the local cache.
/// </summary>
public static class DefaultEmbeddingModel
{
    public const string ModelId = "jinaai/jina-embeddings-v2-base-code";
    public const int Dimension = 768;

    public static EmbeddingModelInfo Info { get; } = new(ModelId, Dimension);

    /// <summary>
    /// Files the auto-downloader fetches on first run for this model. The ONNX graph lives
    /// at <c>onnx/model.onnx</c> on Hugging Face but is flattened to <c>model.onnx</c> in the
    /// cache so the generator's path resolution stays simple.
    ///
    /// <para>
    /// SHA-256s captured 2026-05-10 against the live HF release (these are the bytes shipped
    /// at <c>https://huggingface.co/jinaai/jina-embeddings-v2-base-code/resolve/main/</c> at
    /// the time of capture). The downloader rejects partial / tampered downloads against
    /// these hashes; if HF re-uploads the model with new bytes the next pull will fail loudly
    /// with a SHA-256 mismatch — capture the new hashes and ship a patch release.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ModelFile> Manifest { get; } = new[]
    {
        new ModelFile("onnx/model.onnx", "model.onnx", "63363fc178428b74620c6f3780cbc7191883fa5c7f84c0945c45eb5c4256733b"),
        new ModelFile("tokenizer.json", null, "b01c78a902aa4facb2f47f95449f48e2f7bbfea5d2472ee2f6ce92323c6f86e5"),
    };
}
