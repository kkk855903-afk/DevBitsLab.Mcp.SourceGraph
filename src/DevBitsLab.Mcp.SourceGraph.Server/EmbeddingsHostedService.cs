using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Long-running worker that drains the embed-request channel in batches of up to 32, calls
/// <see cref="ICodeEmbeddingGenerator.EmbedAsync"/> once per batch, and persists each result
/// via <see cref="IEmbeddingsStore.UpsertAsync"/>.
///
/// <para>
/// Hash-gated: skip rows whose <c>(content_hash, model_version)</c> already match the request.
/// Failures are logged at warning and dropped — the indexer is never blocked on the embedding
/// pipeline.
/// </para>
/// </summary>
public sealed class EmbeddingsHostedService : BackgroundService
{
    private readonly ChannelEmbeddingsRequestSink _sink;
    private readonly ICodeEmbeddingGenerator _generator;
    private readonly IEmbeddingsStore _store;
    private readonly ModelDownloadGate _modelDownloadGate;
    private readonly ILogger<EmbeddingsHostedService> _logger;

    private const int MinBatch = 1;
    private const int MaxBatch = 32;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    public EmbeddingsHostedService(
        ChannelEmbeddingsRequestSink sink,
        ICodeEmbeddingGenerator generator,
        IEmbeddingsStore store,
        ModelDownloadGate modelDownloadGate,
        ILogger<EmbeddingsHostedService> logger)
    {
        _sink = sink;
        _generator = generator;
        _store = store;
        _modelDownloadGate = modelDownloadGate;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the auto-downloader to finish before probing IsAvailable (the probe is sticky).
        // For bypassed installs the gate is pre-completed, so this is free.
        // Honour stoppingToken so a host shutdown during cold-start doesn't hang the worker
        // on the gate. WaitAsync throws OperationCanceledException on shutdown; the catch
        // below absorbs it as the documented exit shape.
        await _modelDownloadGate.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

        if (!_store.IsAvailable)
        {
            _logger.LogInformation("Embeddings store unavailable; embedding worker is idle.");
            return;
        }
        if (!_generator.IsAvailable)
        {
            _logger.LogInformation("Embedding generator unavailable; embedding worker is idle.");
            return;
        }

        var modelVersion = _generator.Model.Version;
        var batch = new List<EmbedRequest>(MaxBatch);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                batch.Clear();
                // Wait for at least one item, then drain up to MaxBatch with a short timeout
                // so we coalesce rapid bursts.
                if (!await _sink.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
                while (batch.Count < MaxBatch && _sink.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }
                if (batch.Count < MinBatch)
                {
                    continue;
                }

                var failed = 0;
                try
                {
                    // Skip already-up-to-date rows (hash match) before paying the ONNX cost.
                    var pending = new List<EmbedRequest>(batch.Count);
                    foreach (var req in batch)
                    {
                        if (await _store.ShouldReembedAsync(req.SymbolId, req.ContentHash, modelVersion, stoppingToken).ConfigureAwait(false))
                        {
                            pending.Add(req);
                        }
                    }
                    if (pending.Count == 0)
                    {
                        // Optional small wait so we don't spin on a stream of unchanged hashes.
                        try { await Task.Delay(FlushInterval, stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
                        continue;
                    }

                    var inputs = pending.Select(r => r.Text).ToList();
                    var vectors = await _generator.EmbedAsync(inputs, stoppingToken).ConfigureAwait(false);
                    if (vectors.Count != pending.Count)
                    {
                        throw new InvalidOperationException(
                            $"Embedding generator returned {vectors.Count} vectors for {pending.Count} inputs.");
                    }

                    for (var i = 0; i < pending.Count; i++)
                    {
                        try
                        {
                            await _store.UpsertAsync(pending[i].SymbolId, pending[i].ContentHash, vectors[i], modelVersion, stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed++;
                            _logger.LogWarning(ex, "Persist failed for symbol {Id}", pending[i].SymbolId);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed = batch.Count;
                    _logger.LogWarning(ex, "Batch embed failed (size={Size}); producer checkpoint will remain incomplete", batch.Count);
                }
                finally
                {
                    _sink.MarkProcessed(batch.Count, failed);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sink.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
