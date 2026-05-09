using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
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
    }

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
    /// <summary>Current scope status: <c>ok</c>, <c>degraded</c>, or <c>indexing</c>.</summary>
    public string Status { get; set; }
    /// <summary>Free-form error message attached to <see cref="Status"/> = degraded.</summary>
    public string? StatusMessage { get; set; }
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
        await Indexer.DisposeAsync().ConfigureAwait(false);
        await Store.DisposeAsync().ConfigureAwait(false);
    }
}
