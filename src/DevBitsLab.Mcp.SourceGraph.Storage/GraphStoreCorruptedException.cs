using Microsoft.Data.Sqlite;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Stable typed-throw contract for SQLite-level on-disk corruption (error code
/// <c>11 = SQLITE_CORRUPT</c> or <c>26 = SQLITE_NOTADB</c>). Provided so any layer that wants
/// to surface corruption with a typed exception (now or in the future) has a contract to throw
/// instead of leaking <see cref="SqliteException"/> error codes through its public API.
///
/// Current behaviour: <see cref="SqliteGraphStore"/> does NOT wrap raw <see cref="SqliteException"/>
/// at the storage boundary — corruption surfaces as the underlying SQLite exception unchanged.
/// The dispatch layer's <c>CorruptionGuard.IsCorruptionError</c> recognises both forms (raw
/// <see cref="SqliteException"/> with codes 11/26 AND <see cref="GraphStoreCorruptedException"/>),
/// so the user-facing behaviour is identical regardless of which form a caller throws. The
/// wrap-at-storage-boundary contract was deferred during <c>add-corruption-recovery</c> as a
/// scope reduction (46-method rewrite vs. one dispatch-layer catch); see that change's
/// <c>tasks.md</c> for the deviation rationale.
/// </summary>
public sealed class GraphStoreCorruptedException : Exception
{
    public string ScopeId { get; }
    public SqliteException InnerSqliteException { get; }

    public GraphStoreCorruptedException(string scopeId, SqliteException inner)
        : base($"Scope `{scopeId}` SQLite store reported corruption: {inner.Message} (code {inner.SqliteErrorCode})", inner)
    {
        ScopeId = scopeId;
        InnerSqliteException = inner;
    }
}
