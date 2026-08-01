using DevBitsLab.Mcp.SourceGraph.Embeddings;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Constructs the <see cref="ModelDownloadGate"/> singleton consumed by both the long-running
/// <c>serve</c> path and the one-shot <c>index</c> path.
///
/// <para>
/// Decision matrix:
/// <list type="bullet">
///   <item><c>!embeddingsEnabled</c> (the default, or <c>--no-embeddings</c>): pre-completed gate.</item>
///   <item><c>noModelDownload</c> + empty cache: pre-completed gate, warn naming the cache path
///         so an operator can pre-populate it.</item>
///   <item><c>noModelDownload</c> + populated cache: pre-completed gate, no warning, the
///         existing cache is used as-is.</item>
///   <item>Explicit opt-in path: spawn <c>EnsureAsync</c> as a fire-and-don't-await background task,
///         wrapped in a gate that completes when the download settles. <c>ModelDownloadException</c>
///         is caught and logged at warning so the awaiter never observes a faulted task.</item>
/// </list>
/// </para>
/// </summary>
internal static class ModelDownloadGateFactory
{
    public static ModelDownloadGate Build(
        ModelStore store,
        EmbeddingModelInfo modelInfo,
        bool embeddingsEnabled,
        bool noModelDownload,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!embeddingsEnabled) return ModelDownloadGate.Open();

        if (noModelDownload)
        {
            if (!store.IsCached(modelInfo.ModelId))
            {
                logger.LogWarning(
                    "Automatic model downloads are disabled and the cache at {Dir} is empty; embeddings disabled for this session. " +
                    "Run 'sourcegraph-mcp embeddings pull' for an explicit download, or restart with --allow-model-download. Expected files: {Files}.",
                    store.DirectoryFor(modelInfo.ModelId),
                    string.Join(", ", DefaultEmbeddingModel.Manifest.Select(f => f.ResolvedLocalName)));
            }
            return ModelDownloadGate.Open();
        }

        var isDefaultModel = string.Equals(modelInfo.ModelId, DefaultEmbeddingModel.ModelId, StringComparison.Ordinal);
        IReadOnlyList<ModelFile> manifest = isDefaultModel
            ? DefaultEmbeddingModel.Manifest
            : new[] { new ModelFile("model.onnx"), new ModelFile("tokenizer.json") };

        // Pass the cancellation token to both Task.Run and EnsureAsync so a host shutdown can
        // tear the in-flight HTTP/IO work down promptly. Awaiters of Ready (LiveIndexService,
        // EmbeddingsHostedService) honour the same token at their await sites, so a cancelled
        // gate unblocks them via OperationCanceledException — the documented contract for
        // shutdown.
        var task = Task.Run(async () =>
        {
            try
            {
                await store.EnsureAsync(modelInfo.ModelId, manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Gate's contract is that Ready transitions to RanToCompletion no matter what
                // EnsureAsync does (short of cancellation). Awaiters in LiveIndexService and
                // EmbeddingsHostedService treat the gate as a "settle" signal — a faulted Ready
                // would propagate through their awaits and stop background services that are
                // unrelated to embeddings. Catch every non-cancellation exception (network
                // failures, IO permissions, SHA mismatch via ModelDownloadException, an
                // unexpected runtime error inside the chunked-copy path, etc.) and log it.
                logger.LogWarning(ex,
                    "Failed to download embedding model {Model} into {Dir}; embeddings disabled for this session. " +
                    "Pre-populate the cache and retry, or omit --enable-embeddings.",
                    modelInfo.ModelId, store.DirectoryFor(modelInfo.ModelId));
            }
        }, cancellationToken);
        return new ModelDownloadGate(task);
    }
}
