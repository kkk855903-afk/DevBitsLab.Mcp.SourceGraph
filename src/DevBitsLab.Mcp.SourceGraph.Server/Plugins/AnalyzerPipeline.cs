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
}
