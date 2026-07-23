using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Runs every loaded <see cref="ICodeAnalyzer"/> against a single document with a 30-second
/// per-analyzer timeout. Failures are isolated: a throwing analyzer is marked
/// <see cref="PluginStatus.Failed"/> on its owning plugin record, and the rest of the pipeline
/// continues. The pipeline is invoked from the language indexer's per-file path; the host wires
/// it up after `IndexAllAsync` completes.
/// </summary>
public sealed class AnalyzerPipeline
{
    /// <summary>Per-document timeout. Brief specifies 30s; tunable for tests.</summary>
    public static readonly TimeSpan DefaultPerDocumentTimeout = TimeSpan.FromSeconds(30);

    private readonly PluginHost _pluginHost;
    private readonly ILogger<AnalyzerPipeline> _logger;
    private readonly TimeSpan _timeout;

    public AnalyzerPipeline(
        PluginHost pluginHost,
        ILogger<AnalyzerPipeline>? logger = null,
        TimeSpan? perDocumentTimeout = null)
    {
        _pluginHost = pluginHost;
        _logger = logger ?? NullLogger<AnalyzerPipeline>.Instance;
        _timeout = perDocumentTimeout ?? DefaultPerDocumentTimeout;
    }

    /// <summary>True if at least one analyzer is registered. Cheap pre-check.</summary>
    public bool HasAnalyzers => _pluginHost.Plugins.Any(p =>
        p.Status == PluginStatus.Loaded && p.Analyzers.Count > 0);

    /// <summary>
    /// Run every loaded analyzer against one document and return only the events emitted by
    /// analyzers that completed successfully. Each analyzer receives a private buffer, so a
    /// throw or timeout discards that analyzer's partial output while later analyzers continue.
    /// The caller may combine the returned events with the language-indexer events and commit
    /// them through one atomic file replacement.
    /// </summary>
    public async Task<IReadOnlyList<IndexEvent>> CollectEventsAsync(
        string filePath,
        byte[] contents,
        string scopeId,
        string repoRoot,
        IReadOnlyList<IndexEvent> indexerEvents,
        CancellationToken ct = default)
    {
        if (!HasAnalyzers) return Array.Empty<IndexEvent>();

        var ctx = new AnalyzerContext(filePath, contents, scopeId, repoRoot, indexerEvents);
        var collected = new List<IndexEvent>();
        foreach (var record in _pluginHost.Plugins.Where(p => p.Status == PluginStatus.Loaded))
        {
            foreach (var analyzer in record.Analyzers)
            {
                using var perDocCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perDocCts.CancelAfter(_timeout);
                var buffer = new BufferingGraphEmitter();
                try
                {
                    await analyzer
                        .AnalyzeAsync(ctx, buffer, perDocCts.Token)
                        .ConfigureAwait(false);
                    collected.AddRange(buffer.Events);
                }
                catch (OperationCanceledException)
                    when (perDocCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage =
                        $"Analyzer `{analyzer.Name}` exceeded the {_timeout.TotalSeconds:F0}s "
                        + $"per-document timeout on `{filePath}`.";
                    _logger.LogWarning(
                        "Plugin `{Identity}` analyzer `{Name}` timed out on {File}",
                        record.Identity,
                        analyzer.Name,
                        filePath);
                }
                catch (OperationCanceledException ex)
                    when (!ct.IsCancellationRequested)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage =
                        $"Analyzer `{analyzer.Name}` cancelled itself on `{filePath}`: "
                        + ex.Message;
                    _logger.LogError(
                        ex,
                        "Plugin `{Identity}` analyzer `{Name}` self-cancelled on {File}",
                        record.Identity,
                        analyzer.Name,
                        filePath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage =
                        $"Analyzer `{analyzer.Name}` threw on `{filePath}`: {ex.Message}";
                    _logger.LogError(
                        ex,
                        "Plugin `{Identity}` analyzer `{Name}` threw on {File}",
                        record.Identity,
                        analyzer.Name,
                        filePath);
                }
            }
        }

        return collected;
    }

    /// <summary>
    /// Run every loaded analyzer against the document described by (<paramref name="filePath"/>,
    /// <paramref name="contents"/>, <paramref name="scopeId"/>, <paramref name="repoRoot"/>).
    /// Persisted via <paramref name="store"/> through a per-file <see cref="GraphStoreEmitter"/>;
    /// the file id and the live symbol-key map are supplied so analyzers can target symbols the
    /// language indexer just produced.
    /// </summary>
    public async Task RunAsync(
        IGraphStore store,
        long fileId,
        string filePath,
        byte[] contents,
        string scopeId,
        string repoRoot,
        IReadOnlyList<IndexEvent> indexerEvents,
        Dictionary<string, long> symbolIdByCanonicalKey,
        CancellationToken ct = default)
    {
        if (!HasAnalyzers) return;

        var ctx = new AnalyzerContext(filePath, contents, scopeId, repoRoot, indexerEvents);
        foreach (var record in _pluginHost.Plugins.Where(p => p.Status == PluginStatus.Loaded))
        {
            foreach (var analyzer in record.Analyzers)
            {
                using var perDocCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perDocCts.CancelAfter(_timeout);

                var emitter = new GraphStoreEmitter(store, fileId, symbolIdByCanonicalKey, _logger);
                try
                {
                    await analyzer.AnalyzeAsync(ctx, emitter, perDocCts.Token).ConfigureAwait(false);
                    await emitter.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (perDocCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage = $"Analyzer `{analyzer.Name}` exceeded the {_timeout.TotalSeconds:F0}s per-document timeout on `{filePath}`.";
                    _logger.LogWarning("Plugin `{Identity}` analyzer `{Name}` timed out on {File}",
                        record.Identity, analyzer.Name, filePath);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage =
                        $"Analyzer `{analyzer.Name}` cancelled itself on `{filePath}`: "
                        + ex.Message;
                    _logger.LogError(
                        ex,
                        "Plugin `{Identity}` analyzer `{Name}` self-cancelled on {File}",
                        record.Identity,
                        analyzer.Name,
                        filePath);
                }
                catch (Exception ex)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage = $"Analyzer `{analyzer.Name}` threw on `{filePath}`: {ex.Message}";
                    _logger.LogError(ex, "Plugin `{Identity}` analyzer `{Name}` threw on {File}",
                        record.Identity, analyzer.Name, filePath);
                }
            }
        }
    }

    private sealed class BufferingGraphEmitter : IGraphEmitter
    {
        private readonly List<IndexEvent> _events = new();

        public IReadOnlyList<IndexEvent> Events => _events;

        public void EmitSymbol(IndexEvent.SymbolDeclared symbol) => _events.Add(symbol);

        public void EmitEdge(IndexEvent.EdgeEmitted edge) => _events.Add(edge);

        public void EmitAnnotation(IndexEvent.AnnotationAttached annotation) =>
            _events.Add(annotation);

        public void EmitReference(IndexEvent.ReferenceFound reference) =>
            _events.Add(reference);
    }
}
