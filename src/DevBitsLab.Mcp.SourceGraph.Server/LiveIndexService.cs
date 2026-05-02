using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Watcher;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Background service that owns the long-lived <see cref="RoslynIndexer"/> and a
/// <see cref="SolutionWatcher"/>. On start it loads the solution and runs a full index;
/// while running it consumes change batches and triggers incremental reindexing.
/// </summary>
public sealed class LiveIndexService : BackgroundService
{
    private readonly RoslynIndexer _indexer;
    private readonly LiveIndexOptions _options;
    private readonly ILogger<LiveIndexService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public LiveIndexService(
        RoslynIndexer indexer,
        LiveIndexOptions options,
        ILogger<LiveIndexService> logger,
        ILoggerFactory loggerFactory)
    {
        _indexer = indexer;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.SolutionPath))
        {
            _logger.LogInformation("No --solution provided; live indexing is disabled. Tools will only see whatever's already in the database.");
            return;
        }

        try
        {
            await _indexer.OpenAsync(_options.SolutionPath, stoppingToken).ConfigureAwait(false);
            var initial = await _indexer.IndexAllAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Initial index complete in {Elapsed}: {Files} files re-processed", initial.Elapsed, initial.FilesIndexed);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial indexing failed; live updates will not run");
            return;
        }

        var watchRoot = Path.GetDirectoryName(_options.SolutionPath)!;
        await using var watcher = new SolutionWatcher(
            watchRoot,
            debounce: TimeSpan.FromMilliseconds(_options.DebounceMs),
            logger: _loggerFactory.CreateLogger<SolutionWatcher>());

        _logger.LogInformation("Watching {Root} for .cs and .git/HEAD changes", watchRoot);

        try
        {
            await foreach (var batch in watcher.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (batch.Reason == FileChangeReason.GitHeadChanged)
                    {
                        _logger.LogInformation("Git HEAD changed; running full reindex");
                        var r = await _indexer.ReloadAndIndexAllAsync(stoppingToken).ConfigureAwait(false);
                        _logger.LogInformation("Reindex done in {Elapsed}", r.Elapsed);
                    }
                    else if (batch.Paths.Count > 0)
                    {
                        var r = await _indexer.IndexChangedFilesAsync(batch.Paths, stoppingToken).ConfigureAwait(false);
                        _logger.LogInformation("Reindexed {Count} changed file(s) in {Elapsed}", r.FilesIndexed, r.Elapsed);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply change batch");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

public sealed record LiveIndexOptions(string? SolutionPath, int DebounceMs = 200);
