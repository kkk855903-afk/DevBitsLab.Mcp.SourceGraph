using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using DevBitsLab.Mcp.SourceGraph.Watcher;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Per-scope runtime state. Owns the graph store, the embeddings store, and the Roslyn indexer
/// for one scope; the watcher is held by <c>LiveIndexService</c> directly. Constructed lazily by
/// <see cref="ScopeRouter"/> on first use of the scope.
/// </summary>
public sealed class ScopeHost : IAsyncDisposable
{
    private readonly TaskCompletionSource<bool> _readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScopeHost(
        Scope scope,
        SqliteGraphStore store,
        IEmbeddingsStore embeddingsStore,
        RoslynIndexer indexer,
        string solutionPath)
    {
        Scope = scope;
        Store = store;
        EmbeddingsStore = embeddingsStore;
        Indexer = indexer;
        SolutionPath = solutionPath;
        Status = "ok";
        ProgressSource = new IndexingProgressSource();
    }

    /// <summary>
    /// Per-scope cold-start progress broadcaster. <c>LiveIndexService</c> emits coarse phase
    /// events through this during initial indexing; tools that block on <see cref="Ready"/>
    /// subscribe to forward those events as MCP <c>notifications/progress</c>.
    /// </summary>
    internal IndexingProgressSource ProgressSource { get; }

    /// <summary>
    /// Completes the first time this scope's initial bring-up settles into either <c>"ok"</c> or
    /// <c>"degraded"</c>. Tools wait on this when called against a scope that's still
    /// <c>"indexing"</c> so the lazy-index-on-first-query path doesn't return "no scopes" while a
    /// cold index is in flight. Subsequent watcher-driven reindexes do not toggle <see cref="Status"/>
    /// back to <c>"indexing"</c>, so a one-shot completion is sufficient.
    /// </summary>
    public Task Ready => _readiness.Task;

    /// <summary>
    /// Mark the initial bring-up as settled. Idempotent — only the first call has effect, so it's
    /// safe to invoke from every status-transition site in <c>LiveIndexService.RunInitialIndexAsync</c>
    /// (notably from its <c>finally</c>, which fires on both the ok and degraded paths so waiters
    /// always see the host's terminal status rather than hang).
    /// </summary>
    public void MarkReady() => _readiness.TrySetResult(true);

    public Scope Scope { get; }
    public SqliteGraphStore Store { get; }
    public IEmbeddingsStore EmbeddingsStore { get; }
    public RoslynIndexer Indexer { get; }
    public string SolutionPath { get; }
    /// <summary>
    /// Query-safe status for this scope's optional native interop pipeline. Null means the
    /// scope has no interop configuration.
    /// </summary>
    public NativeInteropRuntimeState? NativeInteropState =>
        NativeInteropCoordinator?.State;
    internal NativeInteropCoordinator? NativeInteropCoordinator { get; set; }
    /// <summary>
    /// Whether the stored managed-import universe came from a complete Roslyn pass. Native
    /// rematching fails closed while this is false.
    /// </summary>
    internal bool ManagedInteropInputComplete { get; set; } = true;
    /// <summary>
    /// Current scope status: one of <c>ok</c>, <c>partial</c>, <c>degraded</c>, <c>indexing</c>.
    /// <c>partial</c> means at least one project produced symbols and at least one project or
    /// file failed; queries succeed but results may be incomplete (see
    /// <see cref="FailedProjects"/> / <see cref="FailedFiles"/>).
    /// </summary>
    public string Status { get; set; }
    /// <summary>Free-form error message attached to <see cref="Status"/> = degraded.</summary>
    public string? StatusMessage { get; set; }
    /// <summary>
    /// Projects whose Roslyn <c>Compilation</c> could not be obtained during the most recent
    /// COLD index. Populated by <c>LiveIndexService.RunInitialIndexAsync</c> from
    /// <c>IndexResult.FailedProjects</c>; surfaced via <c>list_scopes</c> and persisted to
    /// <c>_meta.db</c> alongside the scope row. Empty for healthy scopes. Note: watcher-driven
    /// incremental re-indexes don't currently refresh this list — restart the server to force
    /// a refresh.
    /// </summary>
    public IReadOnlyList<ProjectFailure> FailedProjects { get; set; } = Array.Empty<ProjectFailure>();
    /// <summary>
    /// Files whose Pass 1 walk threw during the most recent COLD index. Empty for healthy
    /// scopes. Like <see cref="FailedProjects"/>, this reflects only the cold-index pass; a
    /// watcher-driven re-index that would re-attempt the failed file does not currently
    /// update this list.
    /// </summary>
    public IReadOnlyList<FileFailure> FailedFiles { get; set; } = Array.Empty<FileFailure>();
    /// <summary>Active solution watcher, set by <c>LiveIndexService</c> when watching is enabled.</summary>
    public SolutionWatcher? Watcher { get; set; }
    /// <summary>
    /// Time of the most recent successful initial / live reindex. Surfaced by <c>list_scopes</c>;
    /// updated alongside the registry every time the scope settles into a new state.
    /// </summary>
    public DateTimeOffset LastIndexedAt { get; set; }
    /// <summary>
    /// Per-scope embed-request channel the indexer writes to. Null when embeddings are disabled
    /// for this scope (no model, no vec extension, or <c>--no-embeddings</c>).
    /// </summary>
    public ChannelEmbeddingsRequestSink? EmbeddingsSink { get; set; }
    /// <summary>
    /// Background drain task for <see cref="EmbeddingsSink"/>. Null when embeddings are disabled.
    /// Started by <c>LiveIndexService.OpenScopeAsync</c> and stopped by <see cref="DisposeAsync"/>.
    /// </summary>
    public EmbeddingsHostedService? EmbeddingsService { get; set; }

    /// <summary>
    /// Per-scope file-path → <see cref="ILanguageProject"/> map populated at scope startup by
    /// running every registered <see cref="ILanguageProjectFactory"/>'s <c>DiscoverAsync</c>. The
    /// dispatcher reads from this map to populate <see cref="IndexContext.Project"/> for every
    /// dispatched document; a file outside any project receives <c>Project = null</c>. Keys are
    /// absolute paths compared case-insensitively
    /// (<see cref="StringComparer.OrdinalIgnoreCase"/>) regardless of OS — a deliberate v1
    /// simplification that costs the rare two-files-with-same-name-different-case pair on
    /// case-sensitive filesystems but spares every other path the cross-platform comparer dance.
    /// </summary>
    public Dictionary<string, ILanguageProject> ProjectByFilePath { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every project returned by the last complete discovery pass, including projects that share
    /// every source path with another project. The single-value file map remains the document
    /// indexing compatibility view; invalidation uses this collection so shared XAML contributors
    /// can fan out to all owners.
    /// </summary>
    public IReadOnlyList<ILanguageProject> LanguageProjects { get; internal set; } =
        Array.Empty<ILanguageProject>();

    /// <summary>
    /// Whether <see cref="ProjectByFilePath"/> reflects a complete successful discovery pass.
    /// A failed cold/control-plane discovery keeps the prior dictionary for query safety but
    /// clears this flag so the next ordinary source event retries discovery before indexing.
    /// </summary>
    public bool ProjectMapReady { get; internal set; }

    public async ValueTask DisposeAsync()
    {
        // Stop the embeddings drain first so any in-flight upserts complete before the underlying
        // SQLite store is disposed. Best-effort with a short bound — losing a few queued requests
        // on shutdown is preferable to hanging the process. BackgroundService implements
        // IDisposable (it owns a stop CancellationTokenSource) so dispose it after stop.
        if (EmbeddingsService is not null)
        {
            EmbeddingsSink?.Complete();
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await EmbeddingsService.StopAsync(stopCts.Token).ConfigureAwait(false); }
            catch { /* best-effort drain */ }
            EmbeddingsService.Dispose();
            EmbeddingsService = null;
            EmbeddingsSink = null;
        }
        if (Watcher is not null) await Watcher.DisposeAsync().ConfigureAwait(false);
        if (NativeInteropCoordinator is not null)
        {
            await NativeInteropCoordinator.DisposeAsync().ConfigureAwait(false);
            NativeInteropCoordinator = null;
        }
        await Indexer.DisposeAsync().ConfigureAwait(false);
        await Store.DisposeAsync().ConfigureAwait(false);
    }
}
