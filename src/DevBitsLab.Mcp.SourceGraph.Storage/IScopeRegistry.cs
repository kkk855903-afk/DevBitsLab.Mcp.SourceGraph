using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Persists scope metadata in <c>&lt;repo&gt;/.sourcegraph/_meta.db</c>. Owns the
/// <c>scopes(id, name, root, project_set_json, isolated, last_indexed_at, status)</c> table.
/// Per-scope graph data lives in separate <c>scopes/&lt;id&gt;.db</c> files; this registry only
/// tracks the catalogue.
/// </summary>
public interface IScopeRegistry : IAsyncDisposable
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ScopeRow>> ListAsync(CancellationToken ct = default);

    Task<ScopeRow?> GetAsync(string id, CancellationToken ct = default);

    Task UpsertAsync(ScopeRow row, CancellationToken ct = default);

    Task RemoveAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// One row of the <c>scopes</c> registry. <see cref="ProjectSetJson"/> holds the serialised
/// <see cref="ScopeProjectSet"/> as JSON so we don't have to invent a per-variant schema; this
/// trades a tiny bit of query convenience for forward-compatibility with new scope kinds.
/// </summary>
/// <param name="Status">
///     One of <c>"ok"</c>, <c>"degraded"</c>, <c>"indexing"</c>. Surfaced by <c>list_scopes</c>.
///     A scope marked <c>degraded</c> still appears in the registry; queries against it return
///     a "scope is degraded" hint instead of crashing the host.
/// </param>
public sealed record ScopeRow(
    string Id,
    string Name,
    string Root,
    string ProjectSetJson,
    bool Isolated,
    DateTimeOffset LastIndexedAt,
    string Status,
    string? StatusMessage = null);

/// <summary>
/// Factory that opens a per-scope <see cref="IGraphStore"/> against
/// <c>&lt;repo&gt;/.sourcegraph/scopes/&lt;id&gt;.db</c>. Pulled out as an interface so the host
/// wiring can stub it in tests without touching the SQLite/vec0 install.
/// </summary>
public interface IGraphStoreFactory
{
    /// <summary>Open the graph store for <paramref name="scopeId"/>; ensure schema is applied.</summary>
    Task<IGraphStore> CreateForScopeAsync(string scopeId, CancellationToken ct = default);
}

/// <summary>
/// Default factory: opens <c>&lt;repo&gt;/.sourcegraph/scopes/&lt;id&gt;.db</c>, optionally loads
/// the vec0 extension when an embedding dimension is provided. Each call returns a fresh store —
/// callers own its lifecycle.
/// </summary>
public sealed class SqliteGraphStoreFactory : IGraphStoreFactory
{
    private readonly string _repoRoot;
    private readonly int _embeddingDimension;
    private readonly Microsoft.Extensions.Logging.ILoggerFactory? _loggerFactory;

    public SqliteGraphStoreFactory(string repoRoot, int embeddingDimension = 0, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        _repoRoot = repoRoot;
        _embeddingDimension = embeddingDimension;
        _loggerFactory = loggerFactory;
    }

    public async Task<IGraphStore> CreateForScopeAsync(string scopeId, CancellationToken ct = default)
    {
        ScopeIdValidator.Validate(scopeId);
        var dbPath = ScopeLayout.ScopeDbPath(_repoRoot, scopeId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var store = new SqliteGraphStore(
            dbPath,
            _loggerFactory?.CreateLogger<SqliteGraphStore>());
        if (_embeddingDimension > 0)
        {
            store.TryLoadVectorExtension(_embeddingDimension);
        }
        await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
        return store;
    }
}
