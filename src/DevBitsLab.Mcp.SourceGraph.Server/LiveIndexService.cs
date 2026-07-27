using System.Diagnostics.CodeAnalysis;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
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
/// A scope with only failures and no usable graph marks itself <c>degraded</c> and stops watching;
/// a scope with usable output plus isolated failures is <c>partial</c> and remains watchable. The
/// rest of the host stays up so queries against healthy scopes still work.
/// </summary>
public sealed class LiveIndexService : BackgroundService
{
    private readonly LiveIndexConfig _config;
    private readonly ScopeRouter _router;
    private readonly IScopeRegistry _registry;
    private readonly HistoryQueue _historyQueue;
    private readonly HistoryOptions _historyOptions;
    private readonly ICodeEmbeddingGenerator _embeddingGenerator;
    private readonly ModelDownloadGate _modelDownloadGate;
    private readonly EmbeddingModelInfo _modelInfo;
    private readonly AnalyzerPipeline _analyzerPipeline;
    private readonly LanguageIndexerDispatcher _languageDispatcher;
    private readonly LanguageProjectFactoryRegistry _projectFactories;
    private readonly RepoRootInfo _repoRoot;
    private readonly IExecutionTrustPolicy _executionTrustPolicy;
    private readonly ILogger<LiveIndexService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DateTimeOffset _processStart = DateTimeOffset.UtcNow;
    // Set in ExecuteAsync from BackgroundService.stoppingToken; reads default(CancellationToken)
    // until then. Used by RebuildScopeAsync's StartWatcher call (the watcher must outlive the
    // tool request that triggered the rebuild) and by the autonomous-rebuild delegate set in
    // Program.cs (the fire-and-forget Task must outlive the failed tool call).
    private CancellationToken _hostStoppingToken;

    /// <summary>
    /// Cancellation token tied to the host's lifetime (set during <see cref="ExecuteAsync"/>).
    /// Callers that schedule work which must outlive the originating tool request — autonomous
    /// rebuild, watcher install after a rebuild — should use this token instead of the per-call
    /// <c>CancellationToken</c> the MCP SDK threads through tool method parameters.
    /// </summary>
    public CancellationToken HostStoppingToken => _hostStoppingToken;
    // Hosts prepared during StartAsync — registered with the router with status="indexing" before
    // the MCP transport (registered after us in DI order) starts accepting requests. ExecuteAsync
    // drives the cold-index against this list; ScopeHost.Ready completes for tools waiting on it.
    private List<ScopeHost> _preparedHosts = new();

    // Live-watch state. The watcher itself is owned by this service so StopAsync can dispose it
    // alongside the per-scope hosts. The two `_current*` fields are the baselines the diff
    // compares against — kept in sync with the registered scope set rather than re-derived every
    // event so plugin warnings don't repeat and default-scope flips diff against the actually-
    // applied state.
    private ScopeConfigWatcher? _configWatcher;
    private string? _currentDefaultScope;
    private IReadOnlyList<PluginRef> _currentPlugins = Array.Empty<PluginRef>();

    public LiveIndexService(
        LiveIndexConfig config,
        ScopeRouter router,
        IScopeRegistry registry,
        HistoryQueue historyQueue,
        HistoryOptions historyOptions,
        ICodeEmbeddingGenerator embeddingGenerator,
        ModelDownloadGate modelDownloadGate,
        EmbeddingModelInfo modelInfo,
        AnalyzerPipeline analyzerPipeline,
        LanguageIndexerDispatcher languageDispatcher,
        LanguageProjectFactoryRegistry projectFactories,
        RepoRootInfo repoRoot,
        IExecutionTrustPolicy executionTrustPolicy,
        ILogger<LiveIndexService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _router = router;
        _registry = registry;
        _historyQueue = historyQueue;
        _historyOptions = historyOptions;
        _embeddingGenerator = embeddingGenerator;
        _modelDownloadGate = modelDownloadGate;
        _modelInfo = modelInfo;
        _analyzerPipeline = analyzerPipeline;
        _languageDispatcher = languageDispatcher;
        _projectFactories = projectFactories;
        _repoRoot = repoRoot;
        _executionTrustPolicy = executionTrustPolicy;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // The MCP stdio transport is registered as an IHostedService after this one and starts
        // accepting JSON-RPC requests as soon as the host's StartAsync chain completes. To close
        // the race where list_scopes / find_definition fire against an empty router, prepare each
        // scope here — opening its graph store, registering it with the router with
        // status="indexing", and persisting the registry row — before yielding to the next
        // hosted service. The actual cold index runs in ExecuteAsync; tools that hit a still-
        // indexing scope wait on ScopeHost.Ready until the indexer settles.
        // Seed the diff baselines from startup config so the first observed `.sourcegraph.json`
        // edit compares against what's actually live, not what was on disk a moment before.
        _currentDefaultScope = _config.DefaultScope;
        _currentPlugins = _config.StartupPlugins;

        if (_config.Scopes.Count > 0)
        {
            await _registry.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            // Reconcile orphan / missing-DB / stuck-`indexing` state before per-scope bring-up
            // so the registry reflects the corrected state when PrepareScopeAsync runs against it.
            // Best-effort: any IO failure here is logged + emitted as a heal event with ok=false,
            // but never aborts the boot sequence.
            await ReconcileOnBootAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Preparing {Count} scope(s) for live indexing: {Ids}",
                _config.Scopes.Count, string.Join(", ", _config.Scopes.Select(s => s.Id)));

            // Prepare scopes concurrently — store creation + schema migration + registration are
            // independent across scopes. PrepareScopeAsync's broad catch swallows in-scope
            // failures and returns null; OperationCanceledException is rethrown (so cooperative
            // shutdown propagates) and registry writes in the catch path are best-effort
            // (try/catch around the degraded upsert), so neither path leaks past this barrier.
            var prepareTasks = _config.Scopes
                .Select(scope => PrepareScopeAsync(scope, cancellationToken))
                .ToArray();
            var prepared = await Task.WhenAll(prepareTasks).ConfigureAwait(false);
            _preparedHosts = prepared.Where(h => h is not null).Select(h => h!).ToList();
        }
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Capture the host's lifetime token so RebuildScopeAsync (and the autonomous-rebuild
        // delegate set in Program.cs) can use it instead of per-call MCP request tokens — work
        // scheduled mid-request must outlive the response.
        _hostStoppingToken = stoppingToken;

        // BackgroundService.ExecuteAsync is awaited by the host's StartAsync chain in some
        // BackgroundService variants; the explicit Task.Yield here detaches LiveIndexService's
        // long-running cold-index work from the startup path so MCP stdio transport (which sits
        // alongside us in the same host) starts processing requests right away.
        await Task.Yield();
        if (_preparedHosts.Count == 0)
        {
            if (_config.Scopes.Count == 0)
            {
                _logger.LogInformation("No scopes registered; live indexing is disabled. Tools will only see whatever's already in the database.");
            }
            return;
        }

        // Run the cold index for every prepared scope concurrently. Each call settles its own
        // host status to "ok", "partial", or "degraded" and calls MarkReady so tools waiting on
        // ScopeHost.Ready can proceed.
        var indexTasks = _preparedHosts.Select(host => RunInitialIndexAsync(host, stoppingToken)).ToArray();
        await Task.WhenAll(indexTasks).ConfigureAwait(false);

        // Start watching every clean or partially successful scope. An empty but clean `ok` scope
        // still needs a watcher so the first future source file is indexed. `degraded` scopes have
        // no usable graph, and `indexing` should not appear here after the cold pass settles.
        foreach (var host in _preparedHosts.Where(h => h.Status == "ok" || h.Status == "partial"))
        {
            StartWatcher(host, stoppingToken);
        }

        // Start the scope-config watcher only after every prepared scope's cold index has
        // settled. A config save during cold-indexing would race the very setup we're trying to
        // bring up; easier to start watching once the host is steady-state. The watcher's first
        // poll emits a synthetic event reflecting the on-disk state at that moment, so any save
        // that landed during cold-indexing is still picked up via the diff (which returns
        // "no-op" when the on-disk content matches what the server already loaded).
        if (_config.WatchConfig)
        {
            StartScopeConfigWatcher(stoppingToken);
        }

        // Block until shutdown; watchers run on tasks they started themselves.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>
    /// Boot-time reconciliation: delegates to <see cref="BootReconciler.ReconcileAsync"/> so the
    /// branch logic stays in a standalone, unit-testable helper.
    /// </summary>
    private Task ReconcileOnBootAsync(CancellationToken ct) =>
        BootReconciler.ReconcileAsync(_registry, _repoRoot.Path, _processStart, _logger, ct);

    /// <summary>
    /// Boot the scope-config watcher and a long-running consumer task that drives the diff-and-
    /// apply loop. The watcher itself stays alive until <see cref="StopAsync"/> disposes it; the
    /// consumer task observes <paramref name="stoppingToken"/> for cooperative shutdown.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "The consumer task wraps OnConfigChangedAsync which can fail in any number of ways (Roslyn workspace, plugin embeddings, transient I/O, etc.). A broad catch with logging keeps the watcher alive across failures so live reload doesn't silently disable itself for the rest of the server's lifetime; OperationCanceledException is handled separately for cooperative shutdown.")]
    private void StartScopeConfigWatcher(CancellationToken stoppingToken)
    {
        _configWatcher = new ScopeConfigWatcher(
            _config.RepoRoot,
            _config.DiscoveredSolutions,
            debounce: TimeSpan.FromMilliseconds(_config.DebounceMs),
            logger: _loggerFactory.CreateLogger<ScopeConfigWatcher>());

        _logger.LogInformation("Watching {Path} for scope-config edits",
            Path.Join(_config.RepoRoot, ".sourcegraph.json"));

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in _configWatcher.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        await OnConfigChangedAsync(change.Config, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scope-config change failed to apply; running scopes unchanged");
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }, stoppingToken);
    }

    /// <summary>
    /// Diff the freshly-loaded config against the live router state and route each delta through
    /// the existing per-scope lifecycle primitives. Plugin deltas are logged-and-skipped (hot-
    /// reloading <c>AssemblyLoadContext</c>-isolated plugins is out of scope for this change).
    /// </summary>
    private async Task OnConfigChangedAsync(ScopeConfig newConfig, CancellationToken ct)
    {
        var current = _router.All().Select(h => h.Scope).ToList();
        var diff = ScopeDiff.Compute(
            currentScopes: current,
            newScopes: newConfig.Scopes,
            currentDefaultScope: _currentDefaultScope,
            newDefaultScope: newConfig.DefaultScope,
            currentPlugins: _currentPlugins,
            newPlugins: newConfig.Plugins);

        if (!diff.HasAny)
        {
            return;
        }
        _logger.LogInformation("Scope-config delta: {Summary}", diff.Summary());

        if (diff.PluginsChanged)
        {
            _logger.LogWarning("Scope-config plugins[] changed; the server is still running with the previous plugin set. Restart to apply plugin changes.");
            // Advance the baseline so the warning fires once per *change*, not once per save. If
            // we left _currentPlugins pinned to startup, every subsequent save (even an unrelated
            // default_scope flip) would re-detect the same plugins[] delta and re-log. The
            // running plugin host is unchanged either way — we're only updating the diff
            // baseline, not loading anything.
            _currentPlugins = newConfig.Plugins;
        }

        // Iterate `diff.Removed` directly rather than scanning `_router.All()` and matching each
        // host against `diff.Removed.Any(...)` — that pattern is O(n*m) and allocates an
        // intermediate list per save. Looking up by id via `TryGet` keeps the tear-down path
        // linear in the number of removed scopes, regardless of how many other scopes are
        // registered. `OfType<ScopeHost>()` filters out the lookup-miss case (a Removed entry
        // whose id was never registered, e.g., live-add+live-remove during cold-index) without
        // needing an explicit if-guard inside the foreach body.
        var removedHosts = diff.Removed
            .Select(r => _router.TryGet(r.Id, out var host) ? host : null)
            .OfType<ScopeHost>();
        foreach (var host in removedHosts)
        {
            await TearDownScopeAsync(host, TimeSpan.FromMilliseconds(_config.ScopeReplaceGraceMs), ct).ConfigureAwait(false);
        }

        foreach (var scope in diff.Added)
        {
            await BringUpScopeLiveAsync(scope, ct).ConfigureAwait(false);
        }

        foreach (var replacement in diff.Modified)
        {
            await ReplaceScopeAsync(replacement, TimeSpan.FromMilliseconds(_config.ScopeReplaceGraceMs), ct).ConfigureAwait(false);
        }

        if (diff.DefaultScopeChanged)
        {
            _router.SetDefaultScope(newConfig.DefaultScope);
            _currentDefaultScope = newConfig.DefaultScope;
        }
    }

    /// <summary>
    /// Live tear-down of a scope removed from <c>.sourcegraph.json</c>: unregister from the
    /// router, drop its registry row, then dispose after a grace period so any in-flight tool
    /// query that already resolved against this host can complete.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Tear-down is best-effort: a failed registry remove or DisposeAsync raises a warning but must not abort the rest of the live-config delta or the server. Both broad catches log with the scope id; cancellation is handled via the deferred-disposal grace window pattern (see Task.Delay).")]
    private async Task TearDownScopeAsync(ScopeHost host, TimeSpan gracePeriod, CancellationToken ct)
    {
        _router.Unregister(host.Scope.Id);
        try { await _registry.RemoveAsync(host.Scope.Id, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Removing scope `{Id}` from registry failed", host.Scope.Id); }
        // Task.Run with CancellationToken.None: once we've unregistered the host, StopAsync's
        // _router.All() loop won't pick it up either, so we must guarantee the deferred-dispose
        // task runs. Passing `ct` to Task.Run would skip-then-orphan the dispose if `ct` is
        // already cancelled. `ct` is still observed inside — on the Task.Delay only — so an
        // expedited shutdown collapses the grace window without skipping DisposeAsync.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(gracePeriod, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expedited shutdown — proceed to dispose */ }
            try { await host.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Deferred dispose of removed scope `{Id}` raised", host.Scope.Id); }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Bring up a newly-added scope through the same Prepare → RunInitialIndex → StartWatcher
    /// chain used at startup. Cold indexing is fire-and-forget so the watcher consumer doesn't
    /// block on it; subsequent config saves can be processed concurrently.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "The fire-and-forget cold-index Task.Run wraps an unbounded surface (RoslynIndexer, plugin analyzers, embeddings drain). An unobserved exception in that lambda would surface as UnobservedTaskException noise — the broad catch with logging keeps fire-and-forget failures attributable to the scope id while still letting OperationCanceledException pass through silently for cooperative shutdown.")]
    private async Task BringUpScopeLiveAsync(Scope scope, CancellationToken ct)
    {
        var host = await PrepareScopeAsync(scope, ct).ConfigureAwait(false);
        if (host is null) return; // PrepareScopeAsync logged + persisted the degraded state
        // Task.Run with CancellationToken.None — the work observes `ct` cooperatively inside
        // (RunInitialIndexAsync / StartWatcher both honour it) but the scheduling itself must
        // not be gated on `ct` so a cancellation-during-handoff still kicks off the cold index.
        // The body is wrapped so an unobserved exception (notably OperationCanceledException on
        // shutdown) doesn't surface as UnobservedTaskException noise — RunInitialIndexAsync's
        // own catch already settles the host's status, so anything reaching this layer is either
        // cooperative cancellation or a programming error worth logging.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunInitialIndexAsync(host, ct).ConfigureAwait(false);
                if (IsWatchable(host)) StartWatcher(host, ct);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live bring-up of scope `{Id}` raised after Prepare", host.Scope.Id);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Replace a modified scope's host atomically: prepare the new host, then
    /// <see cref="ScopeRouter.Replace"/> swaps it under a single lock, then dispose the displaced
    /// host after a grace period so in-flight tool calls resolved against it can complete.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Two fire-and-forget Task.Runs here: the new host's cold index, and the displaced host's deferred disposal. Both must not propagate failures into the watcher consumer (which would then die and silently disable live reload). Each broad catch logs with the scope id so fire-and-forget failures stay attributable.")]
    private async Task ReplaceScopeAsync(ScopeReplacement replacement, TimeSpan gracePeriod, CancellationToken ct)
    {
        // Prepare the new host *without* registering it, so the atomic swap below captures the
        // actual old host as the displaced value. Registering inside PrepareScopeAsync would
        // overwrite the router slot first, making `Replace` return the new host as its own
        // "displaced" value — and the real old host would silently leak.
        var newHost = await PrepareScopeAsync(replacement.New, ct, registerWithRouter: false).ConfigureAwait(false);
        if (newHost is null) return;
        var displaced = _router.Replace(replacement.New.Id, newHost);

        // Task.Run with CancellationToken.None: we need this work to actually start even if `ct`
        // is cancelled (shutdown). Cooperative cancellation still happens inside the task — both
        // RunInitialIndexAsync and the watcher loop observe `ct` — but the task scheduling itself
        // mustn't gate on it. Wrapped to swallow `OperationCanceledException` on shutdown and
        // log anything else, so the fire-and-forget can't surface as UnobservedTaskException.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunInitialIndexAsync(newHost, ct).ConfigureAwait(false);
                if (IsWatchable(newHost)) StartWatcher(newHost, ct);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live replace cold-index for scope `{Id}` raised after Prepare", newHost.Scope.Id);
            }
        }, CancellationToken.None);

        if (displaced is not null)
        {
            // Same Task.Run-with-None pattern: the deferred-dispose must run even on shutdown.
            // The `ct` is used only on the Task.Delay so an expedited shutdown collapses the
            // grace window; DisposeAsync still runs afterwards.
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(gracePeriod, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expedited shutdown — proceed to dispose */ }
                try { await displaced.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Deferred dispose of displaced scope `{Id}` raised", displaced.Scope.Id); }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Phase 1 of scope bring-up. Runs during <see cref="StartAsync"/> before MCP starts accepting
    /// requests: opens the graph store, prepares the embeddings drain (if available), constructs
    /// the indexer, builds the <see cref="ScopeHost"/>, and registers it with the router with
    /// <c>status="indexing"</c>. No solution loading or cold-index work happens here — that's
    /// phase 2 (<see cref="RunInitialIndexAsync"/>) which runs in <see cref="ExecuteAsync"/>.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Bring-up of any single scope must not crash the host: a per-scope failure (Roslyn workspace, plugin embeddings, malformed config, transient I/O) marks that scope `degraded` in the registry and lets every other scope and the MCP transport keep running. The exception is logged + persisted before the catch returns.")]
    private async Task<ScopeHost?> PrepareScopeAsync(Scope scope, CancellationToken ct, bool registerWithRouter = true)
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
            // the ~640 MB ONNX session on first access, so checking it is only worthwhile when
            // we actually have a vec0-backed store to write into.
            //
            // Await the model-download gate before checking IsAvailable. The generator's first
            // IsAvailable probe is sticky (it caches the answer of its initial cache check), so
            // probing before the download lands would permanently disable embeddings for this
            // session. Bypassed installs (--no-embeddings or --no-model-download with empty
            // cache) wire an already-completed gate, so this await is free. WaitAsync(ct)
            // honours scope-prep cancellation so a shutdown during cold-start doesn't block on
            // the in-flight download.
            await _modelDownloadGate.Ready.WaitAsync(ct).ConfigureAwait(false);
            IEmbeddingsRequestSink indexerSink;
            if (embeddingsStore.IsAvailable && _embeddingGenerator.IsAvailable)
            {
                var embeddingProducer =
                    SymbolTextBuilder.ProducerName(_embeddingGenerator.Model.Version);
                var storedEmbeddingVersion = await store
                    .GetProjectionVersionAsync(embeddingProducer, ct)
                    .ConfigureAwait(false);
                scopeSink = new ChannelEmbeddingsRequestSink(
                    requiresFullRefresh:
                        storedEmbeddingVersion != SymbolTextBuilder.ProducerVersion);
                // Publish-on-success: remove the previous completion marker before indexing.
                // A crash after file hashes change but before embeddings drain will therefore
                // force a complete backfill on the next startup.
                await store.ClearProjectionVersionAsync(embeddingProducer, ct)
                    .ConfigureAwait(false);
                scopeEmbeddings = new EmbeddingsHostedService(
                    scopeSink,
                    _embeddingGenerator,
                    embeddingsStore,
                    _modelDownloadGate,
                    _loggerFactory.CreateLogger<EmbeddingsHostedService>());
                await scopeEmbeddings.StartAsync(ct).ConfigureAwait(false);
                indexerSink = scopeSink;
            }
            else
            {
                indexerSink = new NoOpEmbeddingsRequestSink();
            }

            indexer = new RoslynIndexer(
                store,
                _loggerFactory.CreateLogger<RoslynIndexer>(),
                indexerSink,
                scope.Root,
                scope.ProjectSet.Exclude,
                scope.Interop?.Target);
            if (!_historyOptions.Disabled)
            {
                indexer.OnFileIndexed = (fileId, path, sha) =>
                    _historyQueue.Writer.WriteAsync(new HistoryRequest(fileId, path, sha, scope.Id)).AsTask();
            }

            var host = new ScopeHost(scope, store, embeddingsStore, indexer, solutionPath ?? "")
            {
                EmbeddingsSink = scopeSink,
                EmbeddingsService = scopeEmbeddings,
                EmbeddingProducerName = scopeSink is null
                    ? null
                    : SymbolTextBuilder.ProducerName(_embeddingGenerator.Model.Version),
            };
            if (scope.Interop is not null)
            {
                host.NativeInteropCoordinator = new NativeInteropCoordinator(
                    scope.Root,
                    scope.Interop,
                    new ScopePathPolicy(
                        scope.Root,
                        scope.ProjectSet.Exclude),
                    store,
                    _executionTrustPolicy);
            }
            host.Status = "indexing";
            // Persist the registry row BEFORE registering with the router. If the registry
            // upsert throws, the catch path disposes the host and never registers it, so we
            // can't end up with a disposed ScopeHost stranded in the router with status
            // "indexing" (which would hang any tool waiting on ScopeHost.Ready and trip a
            // double-dispose during StopAsync). Once the upsert succeeds, registration is a
            // pure dictionary insert under a lock and is the last fallible step here.
            await _registry.UpsertAsync(ToRow(scope, host.Status, null), ct).ConfigureAwait(false);
            // The live-modify path passes registerWithRouter=false because it needs to atomically
            // swap this freshly-prepared host into the slot via ScopeRouter.Replace, capturing the
            // displaced *old* host. Registering here would cause Replace to return the new host as
            // its own "displaced" value and the old host would never be disposed.
            if (registerWithRouter) _router.Register(host);
            return host;
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
                catch (Exception stopEx)
                {
                    _logger.LogDebug(stopEx, "Scope `{Id}` embeddings drain failed to stop cleanly", scope.Id);
                }
                scopeEmbeddings.Dispose();
            }
            if (indexer is not null) await indexer.DisposeAsync().ConfigureAwait(false);
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
            // Best-effort registry write — if the original failure was a registry/storage issue
            // this second call probably also throws. Logging the secondary failure is enough;
            // the primary failure was already logged above and a missing degraded row in the
            // registry is recoverable on next bring-up.
            try
            {
                await _registry.UpsertAsync(ToRow(scope, "degraded", ex.Message), ct).ConfigureAwait(false);
            }
            catch (Exception upsertEx)
            {
                _logger.LogWarning(upsertEx, "Scope `{Id}` could not persist degraded row to registry", scope.Id);
            }
            return null;
        }
    }

    /// <summary>
    /// Phase 2 of scope bring-up. Runs during <see cref="ExecuteAsync"/> after MCP is already
    /// accepting requests against the prepared (status="indexing") host. Opens the solution,
    /// runs the cold index, dispatches plugin analyzers, then settles <see cref="ScopeHost.Status"/>
    /// to <c>"ok"</c>, <c>"partial"</c>, or <c>"degraded"</c> and calls
    /// <see cref="ScopeHost.MarkReady"/> so
    /// tools waiting on <see cref="ScopeHost.Ready"/> can proceed.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Cold-indexing surface area spans Roslyn workspace open, MSBuild project load, our own indexer, and arbitrary plugin analyzers — any of which can throw any exception type. The broad catch logs, marks the scope `degraded`, persists the message to the registry, and signals readiness via the finally so waiting tools see the failure rather than hang. The host stays up to serve healthy scopes.")]
    private async Task RunInitialIndexAsync(ScopeHost host, CancellationToken ct)
    {
        var scope = host.Scope;
        var solutionPath = host.SolutionPath;
        var nonCSharpFilesIndexed = 0;
        var roslynProducedUsableOutput = false;
        var nonCSharpProducedUsableOutput = false;
        var nativeProducedUsableOutput = false;
        var nativeInteropPartial = false;
        var grpcLinkPartial = false;
        var projectDiscoveryFailed = false;

        try
        {
            // Purge data captured under an older, wider scope policy before opening a workspace
            // or dispatching any file reader. Configuration replacements also enter through this
            // method, so newly-added excludes take effect immediately in the existing scope DB.
            var purgedFiles = await ExcludedFilePurger.PurgeAsync(host, ct).ConfigureAwait(false);
            if (purgedFiles > 0)
            {
                _logger.LogInformation(
                    "Scope `{Id}` purged {Count} previously indexed files now outside its boundary",
                    scope.Id,
                    purgedFiles);
            }

            if (!string.IsNullOrEmpty(solutionPath))
            {
                // Phase 1: workspace open. The MSBuildWorkspace pass dominates this section for
                // real solutions (10s+ on a 1000-doc tree). Emit the coarse phase event so any
                // tool waiting on Ready (and forwarding our progress) sees motion. The open +
                // index_all pair runs under the bounded-retry policy from
                // `add-scope-repair-tools` (3 attempts at [1s, 5s, 25s]).
                host.ProgressSource.Emit(new ModelContextProtocol.ProgressNotificationValue
                {
                    Progress = 0.0f, Total = 1.0f, Message = "opening workspace",
                });
                var initial = await WorkspaceOpenRetry.RunAsync(
                    host.Scope.Id,
                    tk => host.Indexer.OpenAsync(solutionPath, tk),
                    tk =>
                    {
                        host.ProgressSource.Emit(new ModelContextProtocol.ProgressNotificationValue
                        {
                            Progress = 0.5f, Total = 1.0f, Message = "indexing",
                        });
                        return host.Indexer.IndexAllAsync(tk);
                    },
                    WorkspaceOpenRetry.DefaultBackoffs,
                    _logger,
                    ct).ConfigureAwait(false);
                roslynProducedUsableOutput =
                    initial.SymbolsIndexed > 0 || initial.ReferencesIndexed > 0;
                _logger.LogInformation(
                    "Scope `{Id}` initial index complete in {Elapsed}: {Files} files re-processed",
                    scope.Id,
                    initial.Elapsed,
                    initial.FilesIndexed);

                // Capture per-project / per-file failures for surfacing via list_scopes. They feed
                // into the status decision below and ride along on the registry row persisted at
                // the end of this method.
                host.FailedProjects = initial.FailedProjects;
                host.FailedFiles = initial.FailedFiles;
                host.ManagedInteropInputComplete =
                    initial.FailedProjects.Count == 0
                    && initial.FailedFiles.Count == 0;
            }
            else
            {
                // A paths-only or non-.NET repository still has a valid cold-index path: every
                // extension claimed by ILanguageIndexer is discovered below. Roslyn is the only
                // phase omitted when no solution can be resolved.
                _logger.LogInformation(
                    "Scope `{Id}` has no resolvable solution; indexing registered non-C# languages",
                    scope.Id);
                host.ProgressSource.Emit(new ModelContextProtocol.ProgressNotificationValue
                {
                    Progress = 0.5f, Total = 1.0f, Message = "indexing registered languages",
                });
                host.FailedProjects = Array.Empty<ProjectFailure>();
                host.FailedFiles = Array.Empty<FileFailure>();
                host.ManagedInteropInputComplete = true;
            }

            // Build the per-scope file → project lookup and dispatch every registered non-C#
            // language whether or not a Roslyn solution exists. When a workspace is available,
            // CreateScopeLanguageDispatcher supplies the Roslyn-aware XAML/MSBuild factories;
            // otherwise it reuses the process-wide discovery factories.
            var coldDispatcher = CreateScopeLanguageDispatcher(host);
            var projectMapResult =
                await coldDispatcher.BuildProjectMapAsync(host, ct).ConfigureAwait(false);
            projectDiscoveryFailed = !projectMapResult.Succeeded;
            var languageResult = projectMapResult.Succeeded
                ? await coldDispatcher.DispatchAllAsync(host, ct).ConfigureAwait(false)
                : LanguageDispatchResult.Empty with
                {
                    FailedProjects = projectMapResult.FailedProjects,
                };
            nonCSharpFilesIndexed = languageResult.IndexedFiles;
            nonCSharpProducedUsableOutput = languageResult.UsableOutputFiles > 0;
            if (languageResult.FailedProjects.Count > 0)
            {
                host.FailedProjects = host.FailedProjects
                    .Concat(languageResult.FailedProjects)
                    .Distinct()
                    .ToArray();
            }
            if (languageResult.FailedFiles.Count > 0)
            {
                host.FailedFiles = host.FailedFiles
                    .Concat(languageResult.FailedFiles)
                    .ToArray();
            }
            if (nonCSharpFilesIndexed > 0)
            {
                _logger.LogInformation(
                    "Scope `{Id}` non-C# dispatch indexed {Count} files{WorkspaceSuffix}",
                    scope.Id,
                    nonCSharpFilesIndexed,
                    host.Indexer.SanitizedSolution is null ? " (no MSBuild workspace)" : string.Empty);
            }

            // Plugin analyzers: walk every indexed file and dispatch the registered analyzers.
            // Done after the cold index so the per-scope graph already has the symbols /
            // attributes the analyzers want to consume. Skipped silently when the plugin host
            // has no analyzers, which is the v0.5.0 zero-config path.
            if (_analyzerPipeline.HasAnalyzers)
            {
                await DispatchAnalyzersForScopeAsync(host, ct).ConfigureAwait(false);
            }

            host.ProgressSource.Emit(
                new ModelContextProtocol.ProgressNotificationValue
                {
                    Progress = 0.8f,
                    Total = 1.0f,
                    Message = "linking gRPC contracts",
                });
            var grpc = await RunGrpcLinkProjectionAsync(
                    host,
                    host.ManagedInteropInputComplete
                    && projectMapResult.Succeeded
                    && languageResult.FailedProjects.Count == 0
                    && languageResult.FailedFiles.Count == 0,
                    ct)
                .ConfigureAwait(false);
            grpcLinkPartial =
                grpc.State.Status == GrpcLinkRuntimeStatus.Partial;

            if (host.NativeInteropCoordinator is not null)
            {
                host.ProgressSource.Emit(
                    new ModelContextProtocol.ProgressNotificationValue
                    {
                        Progress = 0.85f,
                        Total = 1.0f,
                        Message = "indexing native interop",
                    });
                var native = await host.NativeInteropCoordinator.RunAsync(
                        host.ManagedInteropInputComplete,
                        ct)
                    .ConfigureAwait(false);
                nativeProducedUsableOutput =
                    native.State.Status == NativeInteropRuntimeStatus.Complete
                    && native.State.NativeSymbols > 0;
                nativeInteropPartial =
                    native.State.Status == NativeInteropRuntimeStatus.Partial;
                if (nativeInteropPartial)
                {
                    _logger.LogWarning(
                        "Scope `{Id}` native interop indexing retained last-good evidence: {Reason}",
                        scope.Id,
                        NativeInteropFailureSummary(native.State));
                }
                else
                {
                    _logger.LogInformation(
                        "Scope `{Id}` native interop indexing complete ({Symbols} native symbols, {Matches} managed matches, {Findings} findings)",
                        scope.Id,
                        native.State.NativeSymbols,
                        native.State.ManagedMatches,
                        native.State.Findings);
                }
            }
            else
            {
                // A scope can retain the same database when its interop block is removed.
                // Clear only the native-owned annotations/call evidence and proven orphan
                // declarations; managed, protobuf, and independently-produced edges survive.
                await new NativeInteropSnapshotPublisher(host.Store)
                    .ClearAsync(ct)
                    .ConfigureAwait(false);
            }

            // Settle against both this pass and the graph that remains transactionally stored.
            // A failed project-map rebuild intentionally preserves the last good graph; treating
            // that scope as degraded would hide queryable evidence and prevent the watcher from
            // retrying discovery on the next control/source event.
            var failedProjectCount = host.FailedProjects.Count;
            var failedFileCount = host.FailedFiles.Count;
            var currentPassProducedUsableOutput =
                roslynProducedUsableOutput
                || nonCSharpProducedUsableOutput
                || nativeProducedUsableOutput;
            var retainedCounts =
                await host.Store.RowCountsAsync(ct).ConfigureAwait(false);
            var status = ResolveColdIndexStatus(
                currentPassProducedUsableOutput,
                retainedCounts,
                failedProjectCount,
                failedFileCount,
                projectDiscoveryFailed);
            if ((grpcLinkPartial || nativeInteropPartial)
                && status.Status != "degraded")
            {
                var reasons = new List<string>(2);
                if (grpcLinkPartial)
                {
                    reasons.Add(
                        "gRPC contract linking is incomplete; last-good links were retained. "
                        + GrpcLinkFailureSummary(host.GrpcLinkState!));
                }
                if (nativeInteropPartial)
                {
                    reasons.Add(
                        "Native interop indexing is incomplete; last-good interop evidence was retained. "
                        + NativeInteropFailureSummary(host.NativeInteropState!));
                }
                status = new ColdIndexStatusResolution(
                    "partial",
                    string.Join(" ", reasons),
                    status.UsesRetainedGraph
                    || (host.GrpcLinkState?.RetainedLastGood ?? false)
                    || (host.NativeInteropState?.RetainedLastGood ?? false));
            }
            host.Status = status.Status;
            host.StatusMessage = status.StatusMessage;
            if (status.Status is "partial" or "degraded")
            {
                _logger.LogWarning(
                    "Scope `{Id}` cold index settled to {Status} ({ProjectFailures} project failures, {FileFailures} file failures, retained graph: {RetainedGraph})",
                    scope.Id,
                    status.Status,
                    failedProjectCount,
                    failedFileCount,
                    status.UsesRetainedGraph);
            }
            host.LastIndexedAt = DateTimeOffset.UtcNow;
            await _registry.UpsertAsync(
                ToRow(scope, host.Status, host.StatusMessage, host.FailedProjects, host.FailedFiles),
                ct).ConfigureAwait(false);

            // Autonomous embeddings prune: cold-index can leave behind embeddings for symbols
            // that were deleted (refactors, file renames, generator-output drift). Prune is
            // cheap (one DELETE) and reversible (the next producer-checkpoint backfill
            // regenerates missing rows).
            // Best-effort: a failure here does NOT revert the scope to degraded — the cold-index
            // outcome is what counts; the prune is opportunistic cleanup.
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pruned = await host.EmbeddingsStore.PruneOrphanedAsync(ct).ConfigureAwait(false);
                sw.Stop();
                if (pruned > 0)
                {
                    Observability.HealLog.Append(kind: "embeddings-pruned", scope: scope.Id, ok: true,
                        ms: sw.Elapsed.TotalMilliseconds, details: $"removed {pruned} orphan rows");
                }
                // Zero-noise convention: don't log a heal event when nothing was pruned.
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception pruneEx)
            {
                _logger.LogWarning(pruneEx, "Scope `{Id}`: embeddings prune after cold-index failed; ignoring", scope.Id);
                Observability.HealLog.Append(kind: "embeddings-pruned", scope: scope.Id, ok: false,
                    ms: 0, details: pruneEx.Message);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scope `{Id}` initial indexing failed; marking degraded", scope.Id);
            host.Status = "degraded";
            host.StatusMessage = ex.Message;
            await _registry.UpsertAsync(ToRow(scope, host.Status, host.StatusMessage, host.FailedProjects, host.FailedFiles), ct).ConfigureAwait(false);
        }
        finally
        {
            // Always settle the readiness signal — both ok and degraded are valid post-bring-up
            // terminal states for the per-scope Ready task. Emit the terminal `ready` progress
            // event before flipping the host's TCS so any subscriber that's about to unsubscribe
            // (because the Ready task completed) sees the final 1.0 first.
            host.ProgressSource.MarkReady();
            host.MarkReady();

            if (host.EmbeddingsSink is null)
            {
                host.MarkEmbeddingsReady(false);
            }
            else
            {
                try
                {
                    var complete = await host.EmbeddingsSink
                        .WaitForDrainAsync(ct)
                        .ConfigureAwait(false);
                    if (complete && host.EmbeddingProducerName is not null)
                    {
                        await host.Store.SetProjectionVersionAsync(
                                host.EmbeddingProducerName,
                                SymbolTextBuilder.ProducerVersion,
                                ct)
                            .ConfigureAwait(false);
                    }
                    host.MarkEmbeddingsReady(complete);
                }
                catch (OperationCanceledException)
                {
                    host.MarkEmbeddingsReady(false);
                }
                catch (Exception embeddingEx)
                {
                    _logger.LogWarning(
                        embeddingEx,
                        "Scope `{Id}` embedding backfill did not complete; semantic search will fail closed",
                        scope.Id);
                    host.MarkEmbeddingsReady(false);
                }
            }
        }
    }

    /// <summary>
    /// Drives the <c>minimal</c> repair path for the named scope: delegates to
    /// <see cref="MinimalRepair.RunAsync"/> with the production
    /// <see cref="WorkspaceOpenRetry.DefaultBackoffs"/>.
    /// </summary>
    public Task<MinimalRepairResult> MinimalRepairScopeAsync(
        string scopeId,
        CancellationToken ct,
        IProgress<ModelContextProtocol.ProgressNotificationValue>? progress = null)
    {
        if (!_router.TryGet(scopeId, out var host))
        {
            return Task.FromResult(new MinimalRepairResult(
                Refused: true, IntegrityCheck: "scope-not-found", PrunedEmbeddings: 0,
                Reindexed: false, Message: "scope not registered"));
        }
        return MinimalRepair.RunAsync(host, _registry, WorkspaceOpenRetry.DefaultBackoffs, _logger, ct, progress);
    }

    /// <summary>
    /// Drives a full rebuild of the named scope: archives the existing DB to
    /// <c>orphans/&lt;id&gt;-&lt;archiveDiscriminator&gt;-&lt;utc-iso&gt;.db</c> (if present), disposes the existing
    /// <see cref="ScopeHost"/>, runs <see cref="PrepareScopeAsync"/> + <see cref="RunInitialIndexAsync"/>
    /// for the scope, and registers the new host with the router (the call to
    /// <see cref="ScopeRouter.Register"/> overwrites the prior entry by id, so the rebuild is
    /// transparent to other components). Used by the <c>repair_scope</c> tool's <c>rebuild</c>
    /// mode and (in Phase 3) by the autonomous corrupt-DB recovery path.
    ///
    /// Returns the post-rebuild <see cref="ScopeHost"/> on success. The host's <see cref="ScopeHost.Status"/>
    /// reflects the cold-index outcome (<c>"ok"</c> or <c>"degraded"</c>); the caller decides
    /// whether to surface that as a tool-level success or failure.
    /// </summary>
    public async Task<ScopeHost?> RebuildScopeAsync(string scopeId, string archiveDiscriminator, CancellationToken ct)
    {
        if (!_router.TryGet(scopeId, out var oldHost))
        {
            // No registered host for this id — must be a config scope that's been removed, or
            // an id that doesn't exist. Nothing to rebuild.
            return null;
        }
        var scope = oldHost.Scope;
        var dbPath = ScopeLayout.ScopeDbPath(_repoRoot.Path, scopeId);

        // Step 1 — archive the existing DB if present. We dispose the host first to release SQLite
        // handles; otherwise the move would race with the running connection.
        await oldHost.DisposeAsync().ConfigureAwait(false);
        // Drop SQLite's connection pool so any other handle on this DB gets reaped before the move.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(dbPath))
        {
            try
            {
                var orphansDir = ScopeLayout.OrphansDirectory(_repoRoot.Path);
                Directory.CreateDirectory(orphansDir);
                var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
                var dest = Path.Join(orphansDir, $"{scopeId}-{archiveDiscriminator}-{ts}.db");
                File.Move(dbPath, dest);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var s = dbPath + suffix;
                    var d = dest + suffix;
                    if (File.Exists(s) && !File.Exists(d))
                    {
                        try { File.Move(s, d); } catch (IOException) { /* best-effort */ }
                    }
                }
                _logger.LogInformation("Archived scope `{Id}` DB to {Dest} ahead of rebuild", scopeId, dest);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to archive scope `{Id}` DB before rebuild; continuing", scopeId);
            }
        }

        // Step 2 — re-prepare + cold-index. PrepareScopeAsync registers the new host with the
        // router (overwriting the disposed entry by id). RunInitialIndexAsync settles status.
        var newHost = await PrepareScopeAsync(scope, ct).ConfigureAwait(false);
        if (newHost is null)
        {
            // Prepare failed — registry already reflects degraded state, the old entry has been
            // disposed but isn't in the router anymore (PrepareScopeAsync's catch path doesn't
            // call Register). Surface the null so the tool body can render the appropriate
            // diagnostic.
            return null;
        }
        await RunInitialIndexAsync(newHost, ct).ConfigureAwait(false);

        // Re-attach the watcher so live updates resume after the rebuild. The watcher's loop
        // runs as a `Task.Run(..., stoppingToken)` and is bound to whatever token we pass; if we
        // used the caller's `ct` (the MCP request token), the watcher would be cancelled when
        // the tool response goes back, silently breaking live updates after every rebuild. Use
        // the captured host-lifetime token so the watcher lives as long as the process.
        if (IsWatchable(newHost))
        {
            StartWatcher(newHost, _hostStoppingToken);
        }

        return newHost;
    }

    private LanguageIndexerDispatcher CreateScopeLanguageDispatcher(ScopeHost host)
    {
        if (host.Indexer.SanitizedSolution is null)
        {
            return _languageDispatcher;
        }

        var perScopeFactories = new LanguageProjectFactoryRegistry();
        perScopeFactories.Register(
            new DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageProjectFactory(
                () => host.Indexer.SanitizedSolution,
                host.Indexer.IsProjectSemanticInputComplete,
                host.Indexer.IsProjectXamlPositiveResolutionSafe));
        foreach (var factory in _projectFactories.All())
        {
            // Replace the process-wide discovery-only XAML factory with this scope's
            // Roslyn-aware instance. First-write-wins project mapping would otherwise pin each
            // .xaml file to a project with no semantic compilation.
            if (factory is DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageProjectFactory)
            {
                continue;
            }
            perScopeFactories.Register(factory);
        }
        perScopeFactories.Register(new MSBuildLanguageProjectFactory(host.Indexer));
        return new LanguageIndexerDispatcher(
            _languageDispatcher.Indexers,
            perScopeFactories,
            _loggerFactory.CreateLogger<LanguageIndexerDispatcher>(),
            _languageDispatcher.AnalyzerPipeline);
    }

    internal static bool IsWatchable(ScopeHost host) =>
        host.Status is "ok" or "partial";

    internal static async Task<LiveControlReconciliationResult>
        RunControlReconciliationAsync(
            Func<CancellationToken, Task<IndexResult?>> roslynAction,
            Func<CancellationToken, Task<LanguageDispatchResult>> languageAction,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roslynAction);
        ArgumentNullException.ThrowIfNull(languageAction);
        ct.ThrowIfCancellationRequested();

        IndexResult? roslynResult = null;
        ProjectFailure? roslynFailure = null;
        try
        {
            roslynResult = await roslynAction(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            roslynFailure = new ProjectFailure(
                "roslyn-reload",
                FailureMessage.Truncate(ex.Message));
        }

        ct.ThrowIfCancellationRequested();
        LanguageDispatchResult languageResult;
        var roslynPassFailed =
            roslynFailure is not null
            || (roslynResult is not null
                && (roslynResult.FailedProjects.Count > 0
                    || roslynResult.FailedFiles.Count > 0));
        if (roslynPassFailed)
        {
            roslynFailure ??= new ProjectFailure(
                "roslyn-reload",
                "Roslyn reconciliation reported incomplete project or file output; "
                + "registered-language facts were retained.");
            languageResult = LanguageDispatchResult.Empty with
            {
                FailedProjects = (roslynResult?.FailedProjects
                        ?? Array.Empty<ProjectFailure>())
                    .Prepend(roslynFailure)
                    .Distinct()
                    .ToArray(),
                FailedFiles = roslynResult?.FailedFiles
                    ?? Array.Empty<FileFailure>(),
            };
        }
        else
        {
            try
            {
                languageResult = await languageAction(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                languageResult = LanguageDispatchResult.Empty with
                {
                    FailedProjects =
                    [
                        new ProjectFailure(
                            "registered-language-reconciliation",
                            FailureMessage.Truncate(ex.Message)),
                    ],
                };
            }
        }

        return new LiveControlReconciliationResult(
            roslynResult,
            languageResult,
            roslynFailure);
    }

    internal static ColdIndexStatusResolution ResolveColdIndexStatus(
        bool currentPassProducedUsableOutput,
        RowCountsRow retainedCounts,
        int failedProjectCount,
        int failedFileCount,
        bool projectDiscoveryFailed)
    {
        var hasFailures = failedProjectCount > 0 || failedFileCount > 0;
        if (!hasFailures)
        {
            return new ColdIndexStatusResolution("ok", null, UsesRetainedGraph: false);
        }

        var storedGraphIsUsable = retainedCounts.Symbols > 0
            || retainedCounts.Refs > 0
            || retainedCounts.Edges > 0
            || retainedCounts.Annotations > 0;
        if (currentPassProducedUsableOutput || storedGraphIsUsable)
        {
            var usesRetainedGraph =
                !currentPassProducedUsableOutput && storedGraphIsUsable;
            var prefix = usesRetainedGraph
                ? projectDiscoveryFailed
                    ? "Project discovery failed; serving stale stored graph"
                    : "Cold index failed; serving stale stored graph"
                : "Indexed with failures";
            return new ColdIndexStatusResolution(
                "partial",
                BuildFailureSummary(prefix, failedProjectCount, failedFileCount),
                usesRetainedGraph);
        }

        var degradedPrefix = projectDiscoveryFailed
            ? "Project discovery failed and no usable graph remains"
            : "Cold index produced no usable graph";
        return new ColdIndexStatusResolution(
            "degraded",
            BuildFailureSummary(
                degradedPrefix,
                failedProjectCount,
                failedFileCount),
            UsesRetainedGraph: false);
    }

    private void StartWatcher(ScopeHost host, CancellationToken stoppingToken)
    {
        var solutionPath = host.SolutionPath;
        var watchRoot = Path.GetFullPath(host.Scope.Root);
        var watchedExtensions = _languageDispatcher.RegisteredSourceExtensions
            .Append(".csproj")
            .Concat(
                host.NativeInteropCoordinator?.WatchExtensions
                ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (watchedExtensions.Length == 0)
        {
            _logger.LogWarning(
                "Scope `{Id}` has no registered source extensions; live watching is disabled",
                host.Scope.Id);
            return;
        }

        var watcher = new SolutionWatcher(
            watchRoot,
            debounce: TimeSpan.FromMilliseconds(_config.DebounceMs),
            logger: _loggerFactory.CreateLogger<SolutionWatcher>(),
            sourceExtensions: watchedExtensions,
            excludePatterns: host.Scope.ProjectSet.Exclude,
            policyRoot: host.Scope.Root,
            projectSet: host.Scope.ProjectSet);
        host.Watcher = watcher;

        _logger.LogInformation(
            "Scope `{Id}`: watching {Root} for registered extensions {Extensions} and .git/HEAD changes",
            host.Scope.Id,
            watchRoot,
            string.Join(", ", watchedExtensions));

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
                            var dispatcher = CreateScopeLanguageDispatcher(host);
                            var reconciliation = await RunControlReconciliationAsync(
                                async tk =>
                                {
                                    if (string.IsNullOrEmpty(solutionPath)) return null;
                                    return await host.Indexer
                                        .ReloadAndIndexAllAsync(tk)
                                        .ConfigureAwait(false);
                                },
                                async tk =>
                                {
                                    var projectMapResult = await dispatcher
                                        .BuildProjectMapAsync(host, tk)
                                        .ConfigureAwait(false);
                                    return projectMapResult.Succeeded
                                        ? await dispatcher
                                            .DispatchAllAsync(host, tk)
                                            .ConfigureAwait(false)
                                        : LanguageDispatchResult.Empty with
                                        {
                                            FailedProjects =
                                                projectMapResult.FailedProjects,
                                        };
                                },
                                stoppingToken).ConfigureAwait(false);
                            if (reconciliation.RoslynFailure is not null)
                            {
                                _logger.LogWarning(
                                    "Scope `{Id}` Roslyn reload failed during git HEAD reconciliation: {Reason}; prior registered-language facts were retained",
                                    host.Scope.Id,
                                    reconciliation.RoslynFailure.Reason);
                            }
                            host.ManagedInteropInputComplete =
                                string.IsNullOrEmpty(solutionPath)
                                || (reconciliation.RoslynFailure is null
                                    && reconciliation.RoslynResult is not null
                                    && reconciliation.RoslynResult
                                        .FailedProjects.Count == 0
                                    && reconciliation.RoslynResult
                                        .FailedFiles.Count == 0);
                            var languageResult = reconciliation.LanguageResult;
                            await RecordLiveLanguageFailuresAsync(
                                host,
                                languageResult,
                                stoppingToken).ConfigureAwait(false);
                            await RunLiveGrpcLinkAsync(
                                    host,
                                    host.ManagedInteropInputComplete
                                    && languageResult.FailedProjects.Count == 0
                                    && languageResult.FailedFiles.Count == 0,
                                    stoppingToken)
                                .ConfigureAwait(false);
                            await RunLiveNativeInteropAsync(
                                    host,
                                    stoppingToken)
                                .ConfigureAwait(false);
                            host.LastIndexedAt = DateTimeOffset.UtcNow;
                            _logger.LogInformation(
                                "Scope `{Id}`: full reindex complete ({CSharpFiles} C# files, {OtherFiles} registered-language files)",
                                host.Scope.Id,
                                reconciliation.RoslynResult?.FilesIndexed ?? 0,
                                languageResult.IndexedFiles);
                        }
                        else if (batch.Paths.Count > 0)
                        {
                            var projectControlChanged = batch.Paths.Any(path =>
                                string.Equals(
                                    Path.GetExtension(path),
                                    ".csproj",
                                    StringComparison.OrdinalIgnoreCase));
                            var csharpPaths = string.IsNullOrEmpty(solutionPath)
                                ? Array.Empty<string>()
                                : batch.Paths
                                    .Where(path => string.Equals(
                                        Path.GetExtension(path),
                                        ".cs",
                                        StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
                            var dispatcher = CreateScopeLanguageDispatcher(host);
                            IndexResult? roslynResult;
                            LanguageDispatchResult languageResult;
                            if (projectControlChanged)
                            {
                                var reconciliation =
                                    await RunControlReconciliationAsync(
                                        async tk =>
                                        {
                                            if (string.IsNullOrEmpty(solutionPath)) return null;
                                            return await host.Indexer
                                                .ReloadAndIndexAllAsync(tk)
                                                .ConfigureAwait(false);
                                        },
                                        tk => dispatcher.DispatchChangedFilesAsync(
                                            host,
                                            batch.Paths,
                                            tk),
                                        stoppingToken).ConfigureAwait(false);
                                roslynResult = reconciliation.RoslynResult;
                                languageResult = reconciliation.LanguageResult;
                                if (reconciliation.RoslynFailure is not null)
                                {
                                    _logger.LogWarning(
                                        "Scope `{Id}` Roslyn reload failed for project-control event: {Reason}; prior registered-language facts were retained",
                                        host.Scope.Id,
                                        reconciliation.RoslynFailure.Reason);
                                }
                                host.ManagedInteropInputComplete =
                                    string.IsNullOrEmpty(solutionPath)
                                    || (reconciliation.RoslynFailure is null
                                        && reconciliation.RoslynResult is not null
                                        && reconciliation.RoslynResult
                                            .FailedProjects.Count == 0
                                        && reconciliation.RoslynResult
                                            .FailedFiles.Count == 0);
                            }
                            else
                            {
                                roslynResult = csharpPaths.Length > 0
                                    ? await host.Indexer
                                        .IndexChangedFilesAsync(
                                            csharpPaths,
                                            stoppingToken)
                                        .ConfigureAwait(false)
                                    : null;
                                var csharpSemanticUpdateSucceeded =
                                    roslynResult is not null
                                    && roslynResult.FailedProjects.Count == 0
                                    && roslynResult.FailedFiles.Count == 0;
                                if (csharpPaths.Length > 0)
                                {
                                    host.ManagedInteropInputComplete =
                                        roslynResult?
                                            .ReconciledCompleteUniverse
                                            == true
                                            ? csharpSemanticUpdateSucceeded
                                            : host.ManagedInteropInputComplete
                                              && csharpSemanticUpdateSucceeded;
                                }
                                if (csharpPaths.Length > 0
                                    && !csharpSemanticUpdateSucceeded)
                                {
                                    // A mixed C#/XAML batch is one semantic transaction. If the
                                    // Roslyn half is incomplete, reindexing only the XAML paths
                                    // would replace last-known-good edges with facts derived from
                                    // stale or partial compilations.
                                    languageResult = LanguageDispatchResult.Empty with
                                    {
                                        FailedProjects =
                                            roslynResult?.FailedProjects
                                            ?? Array.Empty<ProjectFailure>(),
                                        FailedFiles =
                                            roslynResult?.FailedFiles
                                            ?? Array.Empty<FileFailure>(),
                                    };
                                }
                                else
                                {
                                    languageResult = await dispatcher
                                        .DispatchChangedFilesAsync(
                                            host,
                                            batch.Paths,
                                            stoppingToken,
                                            csharpSemanticUpdateSucceeded)
                                        .ConfigureAwait(false);
                                }
                            }
                            await RecordLiveLanguageFailuresAsync(
                                host,
                                languageResult,
                                stoppingToken).ConfigureAwait(false);
                            if (ShouldRefreshGrpcProjection(
                                    projectControlChanged,
                                    batch.Paths))
                            {
                                await RunLiveGrpcLinkAsync(
                                        host,
                                        host.ManagedInteropInputComplete
                                        && languageResult
                                            .FailedProjects.Count == 0
                                        && languageResult
                                            .FailedFiles.Count == 0,
                                        stoppingToken)
                                    .ConfigureAwait(false);
                            }
                            var refreshNativeInterop =
                                projectControlChanged
                                || csharpPaths.Length > 0
                                || (host.NativeInteropCoordinator is not null
                                    && batch.Paths.Any(
                                        host.NativeInteropCoordinator
                                            .IsRelevantPath));
                            if (refreshNativeInterop)
                            {
                                await RunLiveNativeInteropAsync(
                                        host,
                                        stoppingToken)
                                    .ConfigureAwait(false);
                            }
                            host.LastIndexedAt = DateTimeOffset.UtcNow;
                            _logger.LogInformation(
                                "Scope `{Id}`: applied live batch ({CSharpFiles} C# indexed, {OtherFiles} registered-language indexed, {DeletedFiles} deleted)",
                                host.Scope.Id,
                                roslynResult?.FilesIndexed ?? 0,
                                languageResult.IndexedFiles,
                                languageResult.DeletedFiles);
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

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "StopAsync is the BackgroundService shutdown path: it disposes the scope-config watcher and every per-scope host. Any one disposal failing must not prevent the others from running, otherwise resources leak across the process exit. Both broad catches log a warning naming what failed and continue to the next disposal.")]
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the scope-config watcher first so no late event arrives mid-tear-down.
        // ScopeConfigWatcher.DisposeAsync cancels its internal CTS which both terminates the
        // poll loop and lets the channel writer's `TryComplete` (in the loop's finally) signal
        // any consumer awaiting `ReadAllAsync`.
        if (_configWatcher is not null)
        {
            try { await _configWatcher.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Disposing scope-config watcher raised"); }
            _configWatcher = null;
        }
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
        var pathPolicy = new ScopePathPolicy(
            Path.GetFullPath(host.Scope.Root),
            host.Scope.ProjectSet.Exclude);

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
            if (!string.Equals(
                    Path.GetExtension(file.Path),
                    ".cs",
                    StringComparison.OrdinalIgnoreCase)
                || pathPolicy.IsExcluded(file.Path))
            {
                continue;
            }
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

    private async Task RecordLiveLanguageFailuresAsync(
        ScopeHost host,
        LanguageDispatchResult result,
        CancellationToken ct)
    {
        if (!ApplyLiveLanguageFailures(host, result)) return;
        await _registry.UpsertAsync(
            ToRow(
                host.Scope,
                host.Status,
                host.StatusMessage,
                host.FailedProjects,
                host.FailedFiles),
            ct).ConfigureAwait(false);
        _logger.LogWarning(
            "Scope `{Id}` live indexing reported {ProjectFailures} project and {FileFailures} file failures",
            host.Scope.Id,
            result.FailedProjects.Count,
            result.FailedFiles.Count);
    }

    internal static bool ShouldRefreshGrpcProjection(
        bool projectControlChanged,
        IReadOnlyCollection<string> paths) =>
        projectControlChanged
        || paths.Any(path =>
            string.Equals(
                Path.GetExtension(path),
                ".cs",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.GetExtension(path),
                ".proto",
                StringComparison.OrdinalIgnoreCase));

    private async Task<GrpcLinkProjectionResult> RunGrpcLinkProjectionAsync(
        ScopeHost host,
        bool sourceUniverseComplete,
        CancellationToken ct)
    {
        var prior = host.GrpcLinkState;
        var retainedLastGood = prior is
            {
                Status: GrpcLinkRuntimeStatus.Complete,
            } || (prior?.RetainedLastGood ?? false);
        if (!retainedLastGood)
        {
            try
            {
                retainedLastGood =
                    await host.Store.HasEdgeEvidenceByProducerAsync(
                            GrpcContractLinker.Producer,
                            ct)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Scope `{Id}` could not probe prior gRPC evidence before refresh",
                    host.Scope.Id);
            }
        }
        host.GrpcLinkState = new GrpcLinkRuntimeState(
            GrpcLinkRuntimeStatus.Partial,
            prior?.ProtoContracts ?? 0,
            prior?.ClientLinks ?? 0,
            prior?.ServerLinks ?? 0,
            retainedLastGood,
            FailureCount: 1,
            OmittedFailures: 0,
            Failures:
            [
                new GrpcLinkFailure(
                    "grpc-projection-refreshing",
                    "The gRPC projection is being refreshed; absence conclusions are temporarily unavailable.",
                    SymbolCanonicalKey: null),
            ]);

        var result = await new GrpcContractLinker(host.Store)
            .RunAsync(sourceUniverseComplete, ct)
            .ConfigureAwait(false);
        host.GrpcLinkState = result.State;
        if (result.State.Status == GrpcLinkRuntimeStatus.Partial)
        {
            _logger.LogWarning(
                "Scope `{Id}` gRPC link projection retained last-good evidence: {Reason}",
                host.Scope.Id,
                GrpcLinkFailureSummary(result.State));
        }
        else
        {
            _logger.LogInformation(
                "Scope `{Id}` gRPC link projection complete ({Contracts} RPC contracts, {ClientLinks} client links, {ServerLinks} server links, {MatchFailures} unmatched/ambiguous candidates)",
                host.Scope.Id,
                result.State.ProtoContracts,
                result.State.ClientLinks,
                result.State.ServerLinks,
                result.State.FailureCount);
        }
        return result;
    }

    private async Task RunLiveGrpcLinkAsync(
        ScopeHost host,
        bool sourceUniverseComplete,
        CancellationToken ct)
    {
        var result = await RunGrpcLinkProjectionAsync(
                host,
                sourceUniverseComplete,
                ct)
            .ConfigureAwait(false);
        if (result.State.Status == GrpcLinkRuntimeStatus.Partial)
        {
            host.Status = "partial";
            host.StatusMessage =
                "gRPC contract linking is incomplete; last-good links were retained. "
                + GrpcLinkFailureSummary(result.State);
        }
        else if (host.FailedProjects.Count == 0
            && host.FailedFiles.Count == 0
            && host.NativeInteropState?.Status
                != NativeInteropRuntimeStatus.Partial)
        {
            host.Status = "ok";
            host.StatusMessage = null;
        }
    }

    private async Task RunLiveNativeInteropAsync(
        ScopeHost host,
        CancellationToken ct)
    {
        if (host.NativeInteropCoordinator is null)
        {
            return;
        }

        var result = await host.NativeInteropCoordinator.RunAsync(
                host.ManagedInteropInputComplete,
                ct)
            .ConfigureAwait(false);
        if (result.State.Status == NativeInteropRuntimeStatus.Partial)
        {
            host.Status = "partial";
            host.StatusMessage =
                "Native interop indexing is incomplete; last-good interop evidence was retained. "
                + NativeInteropFailureSummary(result.State);
            _logger.LogWarning(
                "Scope `{Id}` live native interop refresh is partial: {Reason}",
                host.Scope.Id,
                NativeInteropFailureSummary(result.State));
        }
        else
        {
            if (host.FailedProjects.Count == 0
                && host.FailedFiles.Count == 0
                && host.GrpcLinkState?.Status
                    != GrpcLinkRuntimeStatus.Partial)
            {
                host.Status = "ok";
                host.StatusMessage = null;
            }
            _logger.LogInformation(
                "Scope `{Id}` live native interop refresh complete ({Symbols} symbols, {Matches} matches, {Findings} findings)",
                host.Scope.Id,
                result.State.NativeSymbols,
                result.State.ManagedMatches,
                result.State.Findings);
        }

        await _registry.UpsertAsync(
                ToRow(
                    host.Scope,
                    host.Status,
                    host.StatusMessage,
                    host.FailedProjects,
                    host.FailedFiles),
                ct)
            .ConfigureAwait(false);
    }

    internal static bool ApplyLiveLanguageFailures(
        ScopeHost host,
        LanguageDispatchResult result)
    {
        if (!result.HasFailures) return false;
        host.FailedProjects = host.FailedProjects
            .Concat(result.FailedProjects)
            .Distinct()
            .ToArray();
        host.FailedFiles = host.FailedFiles
            .Concat(result.FailedFiles)
            .Distinct()
            .ToArray();
        host.Status = "partial";
        host.StatusMessage = BuildFailureSummary(
            "Live indexing retained the last usable graph after failures",
            host.FailedProjects.Count,
            host.FailedFiles.Count);
        return true;
    }

    private static string? ResolvePrimarySolution(Scope scope)
    {
        // For solutions-based scopes, return the first solution path. Projects/paths scopes do
        // not open a Roslyn workspace yet, but they remain fully indexable by every registered
        // non-C# language dispatcher and receive the same live watcher lifecycle.
        if (scope.ProjectSet is ScopeProjectSet.Solutions s && s.Items.Count > 0)
        {
            var path = s.Items[0];
            return Path.IsPathRooted(path) ? path : Path.Join(scope.Root, path);
        }
        return null;
    }

    /// <summary>
    /// Build a human-readable summary string for a partial / degraded scope status. Phrases
    /// the count fragment based on what actually failed: "2 project(s) failed",
    /// "3 file(s) failed", or "2 project(s), 3 file(s) failed" when both populations are
    /// non-empty. Avoids the "0 project(s)" wart that a naive interpolation produces when only
    /// file-level failures occurred.
    /// </summary>
    private static string BuildFailureSummary(string prefix, int projectCount, int fileCount)
    {
        if (projectCount == 0 && fileCount == 0) return prefix + ".";
        var parts = new List<string>(2);
        if (projectCount > 0) parts.Add($"{projectCount} project(s)");
        if (fileCount > 0) parts.Add($"{fileCount} file(s)");
        return $"{prefix}: {string.Join(", ", parts)} failed.";
    }

    private static string GrpcLinkFailureSummary(
        GrpcLinkRuntimeState state)
    {
        if (state.Failures.Count == 0)
        {
            return "Reason unavailable.";
        }
        var codes = state.Failures
            .Select(failure => failure.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var omitted = Math.Max(
            state.OmittedFailures,
            state.FailureCount - codes.Length);
        return omitted == 0
            ? $"Reason code(s): {string.Join(", ", codes)}."
            : $"Reason code(s): {string.Join(", ", codes)} (+{omitted} omitted).";
    }

    private static string NativeInteropFailureSummary(
        NativeInteropRuntimeState state)
    {
        if (state.Failures.Count == 0)
        {
            return "Reason unavailable.";
        }
        var codes = state.Failures
            .Select(failure => failure.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var omitted = Math.Max(0, state.Failures.Count - codes.Length);
        return omitted == 0
            ? $"Reason code(s): {string.Join(", ", codes)}."
            : $"Reason code(s): {string.Join(", ", codes)}; {omitted} additional failure(s) omitted.";
    }

    private static ScopeRow ToRow(
        Scope scope,
        string status,
        string? statusMessage,
        IReadOnlyList<ProjectFailure>? failedProjects = null,
        IReadOnlyList<FileFailure>? failedFiles = null) =>
        new(
            Id: scope.Id,
            Name: scope.Name,
            Root: scope.Root,
            ProjectSetJson: ScopeProjectSetSerialiser.Serialise(scope.ProjectSet),
            Isolated: scope.Isolated,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: status,
            StatusMessage: statusMessage,
            FailedProjects: failedProjects,
            FailedFiles: failedFiles);
}

internal sealed record ColdIndexStatusResolution(
    string Status,
    string? StatusMessage,
    bool UsesRetainedGraph);

internal sealed record LiveControlReconciliationResult(
    IndexResult? RoslynResult,
    LanguageDispatchResult LanguageResult,
    ProjectFailure? RoslynFailure);

/// <summary>
/// Configuration injected into <see cref="LiveIndexService"/> via DI. Carries the resolved scope
/// list (already validated by <c>ScopeConfigLoader</c>) plus the watcher debounce, the
/// scope-config-watcher root + opt-in flag, and the startup-time plugin list snapshot used as the
/// baseline for the live plugin-delta detector.
/// </summary>
/// <param name="Scopes">Scopes resolved at startup; live edits to <c>.sourcegraph.json</c> diff against this set.</param>
/// <param name="RepoRoot">Absolute repo root the scope-config watcher (and synthesised-default fallback) is rooted at.</param>
/// <param name="DiscoveredSolutions">Solutions list passed to <see cref="ScopeConfigLoader.Synthesise"/> when the watcher reverts to the default scope on file deletion.</param>
/// <param name="StartupPlugins">Plugin list at server start. Live <c>plugins[]</c> deltas are detected against this baseline so subsequent saves don't repeat the warning.</param>
/// <param name="DefaultScope">Initial <c>default_scope</c> from the loaded config; live edits to <c>default_scope</c> diff against this.</param>
/// <param name="WatchConfig">When <c>true</c>, <see cref="LiveIndexService"/> starts a <c>ScopeConfigWatcher</c> after the cold-index settles. Disabled when <c>--solution</c> overrides the JSON.</param>
/// <param name="DebounceMs">File-system debounce for both the per-scope <c>SolutionWatcher</c> and the <c>ScopeConfigWatcher</c>.</param>
/// <param name="ScopeReplaceGraceMs">Grace window before a displaced <see cref="ScopeHost"/> is disposed during a live modify, so in-flight tool calls against the old host can complete.</param>
public sealed record LiveIndexConfig(
    IReadOnlyList<Scope> Scopes,
    string RepoRoot,
    IReadOnlyList<string> DiscoveredSolutions,
    IReadOnlyList<PluginRef> StartupPlugins,
    string? DefaultScope,
    bool WatchConfig,
    int DebounceMs = 200,
    int ScopeReplaceGraceMs = 5000);

/// <summary>
/// Outcome of <see cref="LiveIndexService.MinimalRepairScopeAsync"/>. <see cref="Refused"/> = true
/// when the integrity check failed and the rebuild path is required; the agent uses this to
/// decide whether to escalate to <c>mode=rebuild</c>.
/// </summary>
public sealed record MinimalRepairResult(bool Refused, string IntegrityCheck, int PrunedEmbeddings, bool Reindexed, string Message);

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
