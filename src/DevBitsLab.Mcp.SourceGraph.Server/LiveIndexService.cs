using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
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
    private readonly ICodeEmbeddingGenerator _embeddingGenerator;
    private readonly EmbeddingModelInfo _modelInfo;
    private readonly AnalyzerPipeline _analyzerPipeline;
    private readonly ILogger<LiveIndexService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public LiveIndexService(
        LiveIndexConfig config,
        ScopeRouter router,
        IScopeRegistry registry,
        HistoryQueue historyQueue,
        HistoryOptions historyOptions,
        ICodeEmbeddingGenerator embeddingGenerator,
        EmbeddingModelInfo modelInfo,
        AnalyzerPipeline analyzerPipeline,
        ILogger<LiveIndexService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _router = router;
        _registry = registry;
        _historyQueue = historyQueue;
        _historyOptions = historyOptions;
        _embeddingGenerator = embeddingGenerator;
        _modelInfo = modelInfo;
        _analyzerPipeline = analyzerPipeline;
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
        ChannelEmbeddingsRequestSink? scopeSink = null;
        EmbeddingsHostedService? scopeEmbeddings = null;
        try
        {
            store = new SqliteGraphStore(dbPath, _loggerFactory.CreateLogger<SqliteGraphStore>());
            store.TryLoadVectorExtension(_modelInfo.Dimension);
            await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
            var embeddingsStore = store.CreateEmbeddingsStore(_modelInfo.Dimension, _loggerFactory.CreateLogger<SqliteEmbeddingsStore>());

            // Per-scope embeddings drain. The ONNX generator is shared (singleton) but every
            // scope owns its own channel + drain task so EmbedRequest.SymbolId stays unambiguous
            // (each id is local to a per-scope DB). When either the generator or the vec0-backed
            // store is unavailable we wire a no-op sink so the indexer's enqueue site stays
            // unconditional.
            //
            // Probe the cheap store flag first: ICodeEmbeddingGenerator.IsAvailable lazy-loads
            // the ~280 MB ONNX session on first access, so checking it is only worthwhile when
            // we actually have a vec0-backed store to write into.
            IEmbeddingsRequestSink indexerSink;
            if (embeddingsStore.IsAvailable && _embeddingGenerator.IsAvailable)
            {
                scopeSink = new ChannelEmbeddingsRequestSink();
                scopeEmbeddings = new EmbeddingsHostedService(
                    scopeSink,
                    _embeddingGenerator,
                    embeddingsStore,
                    _loggerFactory.CreateLogger<EmbeddingsHostedService>());
                await scopeEmbeddings.StartAsync(ct).ConfigureAwait(false);
                indexerSink = scopeSink;
            }
            else
            {
                indexerSink = new NoOpEmbeddingsRequestSink();
            }

            indexer = new RoslynIndexer(store, _loggerFactory.CreateLogger<RoslynIndexer>(), indexerSink);
            if (!_historyOptions.Disabled)
            {
                indexer.OnFileIndexed = (fileId, path, sha) =>
                    _historyQueue.Writer.WriteAsync(new HistoryRequest(fileId, path, sha, scope.Id)).AsTask();
            }

            var host = new ScopeHost(scope, store, embeddingsStore, indexer, solutionPath ?? "")
            {
                EmbeddingsSink = scopeSink,
                EmbeddingsService = scopeEmbeddings,
            };
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

                // Plugin analyzers: walk every indexed file and dispatch the registered analyzers.
                // Done after the cold index so the per-scope graph already has the symbols /
                // attributes the analyzers want to consume. Skipped silently when the plugin host
                // has no analyzers, which is the v0.5.0 zero-config path.
                if (_analyzerPipeline.HasAnalyzers)
                {
                    await DispatchAnalyzersForScopeAsync(host, ct).ConfigureAwait(false);
                }

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
            // Stop the embeddings drain first so its in-flight upsert isn't racing against the
            // store disposal below; then dispose so the BackgroundService stop CTS is released.
            if (scopeEmbeddings is not null)
            {
                scopeSink?.Complete();
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await scopeEmbeddings.StopAsync(stopCts.Token).ConfigureAwait(false); }
                catch { /* best-effort */ }
                scopeEmbeddings.Dispose();
            }
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

    /// <summary>
    /// Walk every file the indexer just touched and dispatch every loaded analyzer against it.
    /// Events are synthesised from the per-scope store's current rows: <c>SymbolDeclared</c> per
    /// indexed symbol in the file plus <c>AnnotationAttached</c> per annotation on those symbols.
    /// This is the simplest way to bridge the workspace-aware bulk indexer to the per-document
    /// SDK contract without requiring the indexer itself to capture the live event stream — the
    /// store is the source of truth either way.
    /// </summary>
    private async Task DispatchAnalyzersForScopeAsync(ScopeHost host, CancellationToken ct)
    {
        var files = await host.Store.GetAllFilesAsync(ct).ConfigureAwait(false);
        var symbolKeys = await host.Store.GetAllSymbolKeysAsync(ct).ConfigureAwait(false);

        // Build canonical-key -> id map for the emitter; analyzers can target any indexed symbol
        // by canonical key, including ones in files other than the one currently being analysed.
        var symbolIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var keysByFileId = new Dictionary<long, List<string>>();
        foreach (var k in symbolKeys)
        {
            symbolIdByKey[k.CanonicalKey] = k.Id;
            if (!keysByFileId.TryGetValue(k.FileId, out var list))
            {
                list = new List<string>();
                keysByFileId[k.FileId] = list;
            }
            list.Add(k.CanonicalKey);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] contents;
            try
            {
                if (!File.Exists(file.Path)) continue;
                contents = await File.ReadAllBytesAsync(file.Path, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Skipping {Path} for analyzer dispatch (read failed)", file.Path);
                continue;
            }

            // Synthesise the IndexEvent stream: every symbol declared in this file plus every
            // annotation attached to those symbols.
            var events = new List<Sdk.IndexEvent>();
            if (keysByFileId.TryGetValue(file.Id, out var keys))
            {
                foreach (var key in keys)
                {
                    var sym = await host.Store.GetSymbolByIdAsync(symbolIdByKey[key], ct).ConfigureAwait(false);
                    if (sym is null) continue;
                    events.Add(new Sdk.IndexEvent.SymbolDeclared(
                        canonicalKey: key,
                        name: sym.Name,
                        fqn: sym.Fqn,
                        kind: sym.Kind,
                        startLine: sym.StartLine,
                        startColumn: sym.StartCol,
                        endLine: sym.EndLine,
                        endColumn: sym.EndCol,
                        signature: sym.Signature,
                        modifiers: sym.Modifiers,
                        accessibility: sym.Accessibility,
                        xmlSummary: sym.XmlSummary));

                    var anns = await host.Store.GetAnnotationsForSymbolAsync(sym.Id, ct).ConfigureAwait(false);
                    foreach (var a in anns)
                    {
                        events.Add(new Sdk.IndexEvent.AnnotationAttached(
                            symbolCanonicalKey: key,
                            annotationName: a.Name,
                            flavor: a.Flavor,
                            fullName: a.FullName,
                            argsJson: a.ArgsJson));
                    }
                }
            }

            await _analyzerPipeline.RunAsync(
                host.Store,
                file.Id,
                file.Path,
                contents,
                host.Scope.Id,
                host.Scope.Root,
                events,
                symbolIdByKey,
                ct).ConfigureAwait(false);
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
