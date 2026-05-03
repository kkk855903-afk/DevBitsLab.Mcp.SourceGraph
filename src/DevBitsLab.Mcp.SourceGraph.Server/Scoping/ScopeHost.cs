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

    public async ValueTask DisposeAsync()
    {
        if (Watcher is not null) await Watcher.DisposeAsync().ConfigureAwait(false);
        await Indexer.DisposeAsync().ConfigureAwait(false);
        await Store.DisposeAsync().ConfigureAwait(false);
    }
}
