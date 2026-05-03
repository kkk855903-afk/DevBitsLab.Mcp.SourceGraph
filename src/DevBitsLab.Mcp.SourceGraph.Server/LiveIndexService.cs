using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using DevBitsLab.Mcp.SourceGraph.Watcher;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Background service that owns one <see cref="RoslynIndexer"/> + one <see cref="SolutionWatcher"/>
/// per scope. On start it loads each scope's solution and runs a full index in parallel; while
/// running it consumes per-scope file-change batches and triggers incremental reindexing on the
/// matching scope.
///
/// A failing scope marks itself <c>degraded</c> in the registry and stops watching; the rest of
/// the host stays up so queries against healthy scopes still work. Replaces the v0.4 single-scope
/// service in place.
/// </summary>
public sealed class LiveIndexService : BackgroundService
{
    private readonly LiveIndexConfig _config;
    private readonly ScopeRouter _router;
    private readonly IScopeRegistry _registry;
    private readonly HistoryQueue _historyQueue;
    private readonly HistoryOptions _historyOptions;
    private readonly IEmbeddingsRequestSink _embeddingsSink;
    private readonly EmbeddingModelInfo _modelInfo;
    private readonly ILogger<LiveIndexService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public LiveIndexService(
        LiveIndexConfig config,
        ScopeRouter router,
        IScopeRegistry registry,
        HistoryQueue historyQueue,
        HistoryOptions historyOptions,
        IEmbeddingsRequestSink embeddingsSink,
        EmbeddingModelInfo modelInfo,
        ILogger<LiveIndexService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _router = router;
        _registry = registry;
        _historyQueue = historyQueue;
        _historyOptions = historyOptions;
        _embeddingsSink = embeddingsSink;
        _modelInfo = modelInfo;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.ExecuteAsync is awaited by the host's StartAsync chain in some
        // BackgroundService variants; the explicit Task.Yield here detaches LiveIndexService's
        // long-running cold-index work from the startup path so MCP stdio transport (which sits
        // alongside us in the same host) starts processing requests right away.
        await Task.Yield();
        if (_config.Scopes.Count == 0)
        {
            _logger.LogInformation("No scopes registered; live indexing is disabled. Tools will only see whatever's already in the database.");
            return;
        }

        await _registry.EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);

        // Open every scope concurrently. A scope that fails initial-index marks itself degraded
        // in the registry; the rest of the host stays up.
        _logger.LogInformation("Opening {Count} scope(s) in parallel: {Ids}",
            _config.Scopes.Count, string.Join(", ", _config.Scopes.Select(s => s.Id)));
        var openTasks = _config.Scopes.Select(scope => OpenScopeAsync(scope, stoppingToken)).ToArray();
        var hosts = await Task.WhenAll(openTasks).ConfigureAwait(false);

        foreach (var host in hosts.Where(h => h is not null))
        {
            _router.Register(host!);
        }

        // Start watching every scope that opened cleanly.
        foreach (var host in hosts.Where(h => h is not null && h!.Status == "ok"))
        {
            StartWatcher(host!, stoppingToken);
        }

        // Block until shutdown; watchers run on tasks they started themselves.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task<ScopeHost?> OpenScopeAsync(Scope scope, CancellationToken ct)
    {
        var solutionPath = ResolvePrimarySolution(scope);
        var dbPath = ScopeLayout.ScopeDbPath(scope.Root, scope.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        SqliteGraphStore? store = null;
        RoslynIndexer? indexer = null;
        try
        {
            store = new SqliteGraphStore(dbPath, _loggerFactory.CreateLogger<SqliteGraphStore>());
            store.TryLoadVectorExtension(_modelInfo.Dimension);
            await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
            var embeddingsStore = store.CreateEmbeddingsStore(_modelInfo.Dimension, _loggerFactory.CreateLogger<SqliteEmbeddingsStore>());
            indexer = new RoslynIndexer(store, _loggerFactory.CreateLogger<RoslynIndexer>(), _embeddingsSink);
            if (!_historyOptions.Disabled)
            {
                indexer.OnFileIndexed = (fileId, path, sha) =>
                    _historyQueue.Writer.WriteAsync(new HistoryRequest(fileId, path, sha, scope.Id)).AsTask();
            }

            var host = new ScopeHost(scope, store, embeddingsStore, indexer, solutionPath ?? "");
            // Register with status=indexing first so list_scopes can show progress.
            host.Status = "indexing";
            await _registry.UpsertAsync(ToRow(scope, host.Status, null), ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(solutionPath))
            {
                _logger.LogInformation("Scope `{Id}` has no resolvable solution; skipping cold index", scope.Id);
                host.Status = "ok"; // empty graph but openable
                await _registry.UpsertAsync(ToRow(scope, host.Status, null), ct).ConfigureAwait(false);
                return host;
            }

            try
            {
                await indexer.OpenAsync(solutionPath, ct).ConfigureAwait(false);
                var initial = await indexer.IndexAllAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Scope `{Id}` initial index complete in {Elapsed}: {Files} files re-processed",
                    scope.Id, initial.Elapsed, initial.FilesIndexed);
                host.Status = "ok";
                host.LastIndexedAt = DateTimeOffset.UtcNow;
                await _registry.UpsertAsync(ToRow(scope, host.Status, null), ct).ConfigureAwait(false);
                return host;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scope `{Id}` initial indexing failed; marking degraded", scope.Id);
                host.Status = "degraded";
                host.StatusMessage = ex.Message;
                await _registry.UpsertAsync(ToRow(scope, host.Status, host.StatusMessage), ct).ConfigureAwait(false);
                return host;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scope `{Id}` failed to open", scope.Id);
            // Best-effort cleanup; the registry still reflects the degraded state for visibility.
            if (indexer is not null) await indexer.DisposeAsync().ConfigureAwait(false);
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
            await _registry.UpsertAsync(ToRow(scope, "degraded", ex.Message), ct).ConfigureAwait(false);
            return null;
        }
    }

    private void StartWatcher(ScopeHost host, CancellationToken stoppingToken)
    {
        var solutionPath = host.SolutionPath;
        if (string.IsNullOrEmpty(solutionPath)) return;

        var watchRoot = Path.GetDirectoryName(solutionPath)!;
        var watcher = new SolutionWatcher(
            watchRoot,
            debounce: TimeSpan.FromMilliseconds(_config.DebounceMs),
            logger: _loggerFactory.CreateLogger<SolutionWatcher>());
        host.Watcher = watcher;

        _logger.LogInformation("Scope `{Id}`: watching {Root} for .cs and .git/HEAD changes", host.Scope.Id, watchRoot);

        // Run the watcher loop on a dedicated task so we can supervise multiple scopes from one
        // ExecuteAsync. Failures inside the loop are logged per-scope; we never let one scope's
        // exception unwind the whole host.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in watcher.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        if (batch.Reason == FileChangeReason.GitHeadChanged)
                        {
                            _logger.LogInformation("Scope `{Id}`: git HEAD changed; running full reindex", host.Scope.Id);
                            var r = await host.Indexer.ReloadAndIndexAllAsync(stoppingToken).ConfigureAwait(false);
                            host.LastIndexedAt = DateTimeOffset.UtcNow;
                            _logger.LogInformation("Scope `{Id}`: reindex done in {Elapsed}", host.Scope.Id, r.Elapsed);
                        }
                        else if (batch.Paths.Count > 0)
                        {
                            var r = await host.Indexer.IndexChangedFilesAsync(batch.Paths, stoppingToken).ConfigureAwait(false);
                            host.LastIndexedAt = DateTimeOffset.UtcNow;
                            _logger.LogInformation("Scope `{Id}`: reindexed {Count} changed file(s) in {Elapsed}",
                                host.Scope.Id, r.FilesIndexed, r.Elapsed);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scope `{Id}`: failed to apply change batch", host.Scope.Id);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var host in _router.All())
        {
            try { await host.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Disposing scope `{Id}` raised", host.Scope.Id); }
        }
    }

    private static string? ResolvePrimarySolution(Scope scope)
    {
        // For solutions-based scopes, return the first solution path. For projects/paths, we
        // currently don't open a single .sln (Roslyn workspace-wise that's a TODO); the indexer
        // will refuse to open without a solution. This mirrors the v1 design's "solutions are the
        // first-class kind"; project-globs are accepted via the schema but warn at runtime when
        // chosen alone.
        if (scope.ProjectSet is ScopeProjectSet.Solutions s && s.Items.Count > 0)
        {
            var path = s.Items[0];
            return Path.IsPathRooted(path) ? path : Path.Combine(scope.Root, path);
        }
        return null;
    }

    private static ScopeRow ToRow(Scope scope, string status, string? statusMessage) =>
        new(
            Id: scope.Id,
            Name: scope.Name,
            Root: scope.Root,
            ProjectSetJson: ScopeProjectSetSerialiser.Serialise(scope.ProjectSet),
            Isolated: scope.Isolated,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: status,
            StatusMessage: statusMessage);
}

/// <summary>
/// Configuration injected into <see cref="LiveIndexService"/> via DI. Carries the resolved scope
/// list (already validated by <c>ScopeConfigLoader</c>) plus the watcher debounce.
/// </summary>
public sealed record LiveIndexConfig(IReadOnlyList<Scope> Scopes, int DebounceMs = 200);

/// <summary>
/// Trivial JSON serialiser for <see cref="ScopeProjectSet"/> so the registry can persist the
/// variant kind alongside the data. Kept here (rather than in <c>Storage</c>) so the storage
/// layer stays oblivious to the discriminated union shape.
/// </summary>
public static class ScopeProjectSetSerialiser
{
    public static string Serialise(ScopeProjectSet ps)
    {
        var dto = ps switch
        {
            ScopeProjectSet.Solutions s => new ScopeProjectSetDto("solutions", s.Items, s.Exclude),
            ScopeProjectSet.Projects p => new ScopeProjectSetDto("projects", p.Items, p.Exclude),
            ScopeProjectSet.Paths g => new ScopeProjectSetDto("paths", g.Globs, g.Exclude),
            _ => throw new InvalidOperationException($"Unknown ScopeProjectSet kind: {ps.GetType().Name}"),
        };
        return System.Text.Json.JsonSerializer.Serialize(dto);
    }

    public static ScopeProjectSet Deserialise(string json)
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<ScopeProjectSetDto>(json)
            ?? throw new InvalidOperationException("Empty project_set JSON");
        return dto.Kind switch
        {
            "solutions" => new ScopeProjectSet.Solutions(dto.Items, dto.Exclude ?? Array.Empty<string>()),
            "projects" => new ScopeProjectSet.Projects(dto.Items, dto.Exclude ?? Array.Empty<string>()),
            "paths" => new ScopeProjectSet.Paths(dto.Items, dto.Exclude ?? Array.Empty<string>()),
            _ => throw new InvalidOperationException($"Unknown project_set kind: {dto.Kind}"),
        };
    }

    private sealed record ScopeProjectSetDto(string Kind, IReadOnlyList<string> Items, IReadOnlyList<string>? Exclude);
}
