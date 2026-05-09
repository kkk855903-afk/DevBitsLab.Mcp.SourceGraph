using System.Text.Json;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed class SqliteGraphStore : IGraphStore
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteGraphStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _vectorExtensionLoaded;
    private int _embeddingDimension;

    public SqliteGraphStore(string databasePath, ILogger<SqliteGraphStore>? logger = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        _connection.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        _logger = logger ?? NullLogger<SqliteGraphStore>.Instance;
    }

    /// <summary>True once <see cref="TryLoadVectorExtension"/> has succeeded for this connection.</summary>
    public bool VectorExtensionLoaded => _vectorExtensionLoaded;

    /// <summary>
    /// Active embedding dimension on this DB. Returns 0 until <see cref="TryLoadVectorExtension"/>
    /// runs. Used by callers to size the vec0 virtual table consistently.
    /// </summary>
    public int EmbeddingDimension => _embeddingDimension;

    /// <summary>
    /// Attempt to load the <c>sqlite-vec</c> (<c>vec0</c>) extension into this connection.
    /// Returns <c>true</c> on success, <c>false</c> if the native binary isn't on the load path
    /// (graceful disable). Safe to call multiple times — second and subsequent calls are no-ops.
    /// Must be called BEFORE <see cref="EnsureSchemaAsync"/> so the <c>symbol_embeddings</c>
    /// virtual table can be created.
    /// </summary>
    public bool TryLoadVectorExtension(int dimension)
    {
        if (_vectorExtensionLoaded) return true;
        try
        {
            _connection.EnableExtensions(true);
            // Microsoft.Data.Sqlite's LoadVector helper from the sqlite-vec NuGet ships an
            // extension method on SqliteConnection; we call it via reflection-free invocation.
            _connection.LoadExtension("vec0");
            _vectorExtensionLoaded = true;
            _embeddingDimension = dimension;
            _logger.LogInformation("Loaded sqlite-vec (vec0) extension; semantic search enabled (dim={Dim})", dimension);
            return true;
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Could not load sqlite-vec extension; semantic search disabled");
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not load sqlite-vec extension; semantic search disabled");
            return false;
        }
    }

    /// <summary>
    /// Build an <see cref="IEmbeddingsStore"/> sharing this graph store's connection. Returns
    /// a working <see cref="SqliteEmbeddingsStore"/> when the vec0 extension was loaded;
    /// otherwise returns <see cref="DisabledEmbeddingsStore"/> so callers don't need to branch.
    /// </summary>
    public IEmbeddingsStore CreateEmbeddingsStore(int dimension, ILogger? logger = null)
    {
        if (_vectorExtensionLoaded && _embeddingDimension == dimension)
        {
            return new SqliteEmbeddingsStore(_connection, dimension, _writeLock, logger);
        }
        return new DisabledEmbeddingsStore(dimension);
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read current version (if any). schema_version is created idempotently in V1.
            await _connection.ExecuteAsync(new CommandDefinition(
                "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY);",
                cancellationToken: ct)).ConfigureAwait(false);
            var current = await _connection.ExecuteScalarAsync<int?>(
                new CommandDefinition("SELECT MAX(version) FROM schema_version;", cancellationToken: ct)).ConfigureAwait(false);

            if (current is not null && current < Schema.Version)
            {
                _logger.LogInformation("On-disk graph schema is v{Old}; rebuilding to v{New}", current, Schema.Version);
                await _connection.ExecuteAsync(new CommandDefinition(Schema.DropAll, cancellationToken: ct)).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM schema_version;", cancellationToken: ct)).ConfigureAwait(false);
            }

            using var tx = _connection.BeginTransaction();
            await _connection.ExecuteAsync(new CommandDefinition(Schema.V1, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(Schema.V2, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            // Vec0 virtual table only when the extension actually loaded. We can still persist
            // schema_version=7 without it; the embedding pipeline degrades gracefully and the
            // semantic_search tool reports the disabled state.
            if (_vectorExtensionLoaded && _embeddingDimension > 0)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    Schema.V7Embeddings(_embeddingDimension), transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            await _connection.ExecuteAsync(
                new CommandDefinition("INSERT OR REPLACE INTO schema_version(version) VALUES (@v);", new { v = Schema.Version }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> UpsertFileAsync(string path, byte[] contentSha256, DateTimeOffset indexedAt, bool isGenerated = false, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO files(path, content_sha256, last_indexed_at, is_generated)
            VALUES (@path, @sha, @at, @gen)
            ON CONFLICT(path) DO UPDATE SET
                content_sha256 = excluded.content_sha256,
                last_indexed_at = excluded.last_indexed_at,
                is_generated   = excluded.is_generated
            RETURNING id;
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _connection.ExecuteScalarAsync<long>(new CommandDefinition(
                sql,
                new { path, sha = contentSha256, at = indexedAt.ToUnixTimeMilliseconds(), gen = isGenerated ? 1 : 0 },
                cancellationToken: ct)).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<byte[]?> GetFileContentHashAsync(string path, CancellationToken ct = default)
    {
        return await _connection.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            "SELECT content_sha256 FROM files WHERE path = @path;",
            new { path },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task ClearFileOutgoingAsync(long fileId, CancellationToken ct = default)
    {
        // Wipe outgoing-only: refs whose file_id is this file, and edges whose src is one of this
        // file's symbols. Symbol rows themselves are NOT touched here — they're upserted by
        // canonical key in pass-1 to keep stable ids, which keeps incoming refs/edges valid.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM edges WHERE src IN (SELECT id FROM symbols WHERE file_id = @id);",
                new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM refs WHERE file_id = @id;",
                new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteSymbolsForFileNotInAsync(long fileId, IReadOnlyCollection<string> keysToKeep, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            // Stage the keep-set in a temp table so we don't have to inline a giant IN clause.
            await _connection.ExecuteAsync(new CommandDefinition(
                "CREATE TEMP TABLE IF NOT EXISTS keep_keys(key TEXT PRIMARY KEY);",
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM keep_keys;", transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            if (keysToKeep.Count > 0)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO keep_keys(key) VALUES (@key);",
                    keysToKeep.Select(k => new { key = k }),
                    transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            }

            // Find symbols to remove for this file:
            // those declared in @id whose canonical_key isn't in keep_keys.
            const string deleteRefsSql = """
                DELETE FROM refs
                WHERE symbol_id IN (
                    SELECT id FROM symbols
                    WHERE file_id = @id AND canonical_key NOT IN (SELECT key FROM keep_keys)
                );
                """;
            const string deleteEdgesSql = """
                DELETE FROM edges
                WHERE src IN (
                    SELECT id FROM symbols
                    WHERE file_id = @id AND canonical_key NOT IN (SELECT key FROM keep_keys)
                ) OR dst IN (
                    SELECT id FROM symbols
                    WHERE file_id = @id AND canonical_key NOT IN (SELECT key FROM keep_keys)
                );
                """;
            // Wipe annotations for every symbol attributed to this file. The indexer rebuilds
            // the annotation set per-file in pass 1 and bulk-inserts immediately after this
            // reconcile step, so dropping the rows for surviving symbols too is correct (and
            // avoids stale rows when a `[Foo]` is removed from a method that itself stays).
            const string deleteAnnotationsSql = """
                DELETE FROM annotations
                WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);
                """;
            // v7: diagnostics are file-scoped (and optionally symbol-scoped). They get rebuilt by
            // UpsertDiagnosticsForFileAsync after pass 2; clearing them here keeps the deletion
            // path (file removed from solution) clean too — the indexer drops symbols but never
            // re-runs diagnostics for a vanished file, so without this wipe the rows would orphan.
            const string deleteDiagnosticsSql = "DELETE FROM diagnostics WHERE file_id = @id;";
            const string deleteSymbolsSql = """
                DELETE FROM symbols
                WHERE file_id = @id AND canonical_key NOT IN (SELECT key FROM keep_keys);
                """;
            await _connection.ExecuteAsync(new CommandDefinition(deleteRefsSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(deleteEdgesSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(deleteAnnotationsSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(deleteDiagnosticsSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(deleteSymbolsSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition("DELETE FROM keep_keys;", transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> UpsertSymbolAsync(string canonicalKey, Symbol symbol, CancellationToken ct = default)
    {
        // Stable id by canonical_key. On conflict we update the location/signature/etc but the
        // integer id is preserved, so refs/edges from other files that point to this symbol stay
        // correct across edits. NOTE: container_id is intentionally NOT included in the conflict
        // update path — it's set separately by BatchUpdateContainerIdsAsync after pass-1 inserts
        // every symbol, so the parent row exists by the time the lookup happens. test_framework
        // is also set separately by UpdateTestFrameworksAsync after attribute walk so we don't
        // race on first-insert vs. attribute-extraction ordering; the conflict path therefore
        // does NOT clobber an already-detected framework on re-upserts of the same symbol.
        const string sql = """
            INSERT INTO symbols(canonical_key, name, fqn, kind_name, file_id, start_line, start_col, end_line, end_col, signature, container_id, modifiers, accessibility, xml_summary, test_framework)
            VALUES (@Key, @Name, @Fqn, @Kind, @FileId, @StartLine, @StartCol, @EndLine, @EndCol, @Signature, @ContainerId, @Modifiers, @Accessibility, @XmlSummary, @TestFramework)
            ON CONFLICT(canonical_key) DO UPDATE SET
                name          = excluded.name,
                fqn           = excluded.fqn,
                kind_name     = excluded.kind_name,
                file_id       = excluded.file_id,
                start_line    = excluded.start_line,
                start_col     = excluded.start_col,
                end_line      = excluded.end_line,
                end_col       = excluded.end_col,
                signature     = excluded.signature,
                modifiers     = excluded.modifiers,
                accessibility = excluded.accessibility,
                xml_summary   = excluded.xml_summary
            RETURNING id;
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
            {
                Key = canonicalKey,
                symbol.Name,
                symbol.Fqn,
                Kind = symbol.Kind,
                symbol.FileId,
                symbol.StartLine,
                symbol.StartCol,
                symbol.EndLine,
                symbol.EndCol,
                symbol.Signature,
                symbol.ContainerId,
                symbol.Modifiers,
                symbol.Accessibility,
                symbol.XmlSummary,
                symbol.TestFramework,
            }, cancellationToken: ct)).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateTestFrameworksAsync(IReadOnlyList<(long SymbolId, string Framework)> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        const string sql = "UPDATE symbols SET test_framework = @Framework WHERE id = @SymbolId;";
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            foreach (var (symbolId, framework) in rows)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    sql, new { SymbolId = symbolId, Framework = framework }, transaction: tx, cancellationToken: ct))
                    .ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task BatchUpdateContainerIdsAsync(IReadOnlyList<(long ChildId, long ParentId)> pairs, CancellationToken ct = default)
    {
        if (pairs.Count == 0) return;
        const string sql = "UPDATE symbols SET container_id = @ParentId WHERE id = @ChildId;";
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            foreach (var (childId, parentId) in pairs)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    sql, new { ChildId = childId, ParentId = parentId }, transaction: tx, cancellationToken: ct))
                    .ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task BulkInsertReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO refs(symbol_id, file_id, line, col, kind)
            VALUES (@SymbolId, @FileId, @Line, @Col, @Kind);
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            foreach (var r in references)
            {
                await _connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    r.SymbolId,
                    r.FileId,
                    r.Line,
                    r.Col,
                    Kind = (int)r.Kind,
                }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task BulkInsertEdgesAsync(IEnumerable<Edge> edges, CancellationToken ct = default)
    {
        // INSERT OR IGNORE preserves the dedup behaviour against the new (src, dst, kind_name)
        // PK; if a logical edge appears twice with different metadata payloads, the first write
        // wins. That matches the legacy semantics — callers don't expect "merge" behaviour for
        // edge metadata; they emit each edge once per file and rely on ClearFileOutgoingAsync
        // to wipe stale rows.
        const string sql = """
            INSERT OR IGNORE INTO edges(src, dst, kind_name, payload)
            VALUES (@Src, @Dst, @Kind, @Payload);
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            foreach (var e in edges)
            {
                // Serialise metadata to JSON when non-empty; bind SQL NULL otherwise.
                // System.Text.Json default options give a stable, compact representation; the
                // payload column is opaque to the indexer (only consumed by tools that already
                // know the kind-specific shape).
                string? payload = null;
                if (e.Metadata is not null && e.Metadata.Count > 0)
                {
                    payload = JsonSerializer.Serialize(e.Metadata);
                }

                await _connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    e.Src,
                    e.Dst,
                    Kind = e.Kind,
                    Payload = payload,
                }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(string query, string? filePathHint = null, int limit = 25, CancellationToken ct = default)
    {
        // Strategy:
        //  1. exact name match (highest)
        //  2. exact FQN match
        //  3. FQN suffix match (e.g. user types "Calculator.Add")
        //  4. case-insensitive prefix match on name
        // Optionally restrict to a file-path hint.
        const string sql = """
            WITH ranked AS (
                SELECT s.id, s.name, s.fqn, s.kind_name, f.path, s.start_line, s.start_col, s.end_line, s.end_col, s.signature,
                    s.modifiers, s.accessibility, s.xml_summary, f.is_generated, s.test_framework, s.canonical_key,
                    CASE
                        WHEN s.name = @q THEN 1
                        WHEN s.fqn  = @q THEN 2
                        WHEN s.fqn LIKE '%' || @q THEN 3
                        WHEN s.name LIKE @q || '%' COLLATE NOCASE THEN 4
                        WHEN s.fqn LIKE '%' || @q || '%' COLLATE NOCASE THEN 5
                        ELSE 99
                    END AS rank
                FROM symbols s
                JOIN files   f ON f.id = s.file_id
                WHERE (@hint IS NULL OR f.path LIKE '%' || @hint || '%')
                  AND (
                    s.name = @q
                    OR s.fqn = @q
                    OR s.fqn LIKE '%' || @q
                    OR s.name LIKE @q || '%' COLLATE NOCASE
                    OR s.fqn LIKE '%' || @q || '%' COLLATE NOCASE
                  )
            )
            SELECT id, name, fqn, kind_name AS Kind, path AS FilePath, start_line AS StartLine, start_col AS StartCol,
                   end_line AS EndLine, end_col AS EndCol, signature,
                   modifiers AS Modifiers, accessibility AS Accessibility, xml_summary AS XmlSummary,
                   is_generated AS IsGenerated, test_framework AS TestFramework,
                   canonical_key AS CanonicalKey
            FROM ranked
            ORDER BY rank, length(fqn), fqn
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { q = query, hint = filePathHint, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(long symbolId, int limit = 200, CancellationToken ct = default)
        => FindReferencesAsync(symbolId, includeGenerated: true, limit, ct);

    public async Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(long symbolId, bool includeGenerated, int limit = 200, CancellationToken ct = default)
    {
        // includeGenerated = false filters out refs whose source file is marked is_generated = 1.
        // The legacy 3-argument overload above defaults to true so existing callers (resources,
        // tests) keep their pre-v7 behaviour.
        var sql = $"""
            SELECT r.id, r.symbol_id AS SymbolId, f.path AS FilePath, r.line, r.col, r.kind,
                   f.is_generated AS IsGenerated
            FROM refs r
            JOIN files f ON f.id = r.file_id
            WHERE r.symbol_id = @id
              {(includeGenerated ? "" : "AND f.is_generated = 0")}
            ORDER BY f.path, r.line, r.col
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawReferenceHit>(new CommandDefinition(
            sql, new { id = symbolId, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<SymbolHit>> SearchSymbolsAsync(string ftsQuery, string? kindFilter = null, int limit = 25, CancellationToken ct = default)
    {
        // FTS5 expects bareword tokens; we wrap each whitespace-separated chunk and prefix with NEAR if multi-word.
        // For trigram tokenizer, just quote to be safe.
        var fts = "\"" + ftsQuery.Replace("\"", "\"\"") + "\"";
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey
            FROM symbols_fts t
            JOIN symbols s ON s.id = t.rowid
            JOIN files   f ON f.id = s.file_id
            WHERE t.symbols_fts MATCH @q
              {(kindFilter is null ? "" : "AND s.kind_name = @kind")}
            ORDER BY rank
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { q = fts, kind = kindFilter, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<ModuleSymbol>> ModuleSummaryAsync(string namespaceOrPathPrefix, int limit = 25, CancellationToken ct = default)
    {
        // QueryAsync<dynamic> instead of QueryAsync<RawModuleSymbol>: InDegree is an undeclared
        // aggregate (COALESCE(COUNT(*), 0)), so Microsoft.Data.Sqlite reports its column type as
        // BLOB whenever the prefix matches no symbols and the result set is empty. Dapper's typed
        // deserializer is built upfront from that schema and rejects byte[]→long, throwing the
        // misleading "(... System.Byte[] InDegree) is required for ... materialization" error.
        // The dynamic path uses a name-keyed DapperRow that reads each value lazily, so it never
        // touches the column type metadata until a row is actually present (when the value's real
        // type is Int64 as expected). CAST AS INTEGER does NOT help here — sqlite3_column_decltype
        // doesn't propagate CAST.
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   COALESCE((SELECT COUNT(*) FROM edges e WHERE e.dst = s.id AND e.kind_name = 'calls'), 0) AS InDegree
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.fqn = @prefix
               OR s.fqn LIKE @prefix || '.%'
               OR f.path LIKE '%' || @prefix || '%'
            ORDER BY InDegree DESC, length(s.fqn), s.fqn
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync(new CommandDefinition(
            sql, new { prefix = namespaceOrPathPrefix, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new ModuleSymbol(
            new SymbolHit(
                Id: (long)r.id,
                Name: (string)r.name,
                Fqn: (string)r.fqn,
                Kind: r.kind_name,
                FilePath: (string)r.FilePath,
                StartLine: Convert.ToInt32(r.StartLine),
                StartCol: Convert.ToInt32(r.StartCol),
                EndLine: Convert.ToInt32(r.EndLine),
                EndCol: Convert.ToInt32(r.EndCol),
                Signature: (string?)r.signature,
                Modifiers: (string?)r.Modifiers,
                Accessibility: Convert.ToInt32(r.Accessibility),
                XmlSummary: (string?)r.XmlSummary,
                IsGenerated: ((long)r.IsGenerated) != 0,
                TestFramework: (string?)r.TestFramework,
                CanonicalKey: (string?)r.CanonicalKey),
            InDegree: Convert.ToInt32(r.InDegree))).ToList();
    }

    public async Task<IReadOnlyList<ImpactedSymbol>> ImpactOfChangeAsync(long symbolId, int maxDepth = 4, int limit = 100, string? edgeKind = "calls", CancellationToken ct = default)
    {
        // The recursive CTE has to know whether to filter by edge kind. Inlining the @kind value
        // is safer than two near-identical queries because dapper still parameterises @id/@maxDepth.
        var kindClause = edgeKind is null ? "" : "AND kind_name = @kind";
        var kindClauseRecursive = edgeKind is null ? "" : "AND e.kind_name = @kind";
        var sql = $"""
            WITH RECURSIVE upstream(id, depth) AS (
                SELECT src, 1 FROM edges WHERE dst = @id {kindClause}
                UNION
                SELECT e.src, u.depth + 1
                FROM edges e
                JOIN upstream u ON e.dst = u.id
                WHERE u.depth < @maxDepth {kindClauseRecursive}
            )
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   MIN(u.depth) AS Depth
            FROM upstream u
            JOIN symbols s ON s.id = u.id
            JOIN files   f ON f.id = s.file_id
            GROUP BY s.id
            ORDER BY Depth, s.fqn
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawImpactedSymbol>(new CommandDefinition(
            sql, new { id = symbolId, maxDepth, limit, kind = edgeKind }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new ImpactedSymbol(r.ToHit(), (int)r.Depth)).ToList();
    }

    public async Task<IReadOnlyList<SymbolHit>> ListMembersAsync(long containerId, int? accessibilityFilter = null, int limit = 200, CancellationToken ct = default)
    {
        // Direct children of the named container, ordered by start_line. Inherited members are
        // resolved at the tool layer (would require walking inherits/implements edges to map a
        // base type's children onto this container).
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey
            FROM symbols s
            JOIN files   f ON f.id = s.file_id
            WHERE s.container_id = @id
              {(accessibilityFilter is null ? "" : "AND s.accessibility = @acc")}
            ORDER BY f.path, s.start_line, s.start_col
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { id = containerId, acc = accessibilityFilter, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    private sealed record RawImpactedSymbol(long Id, string Name, string Fqn, string Kind, string FilePath,
        long StartLine, long StartCol, long EndLine, long EndCol, string? Signature,
        string? Modifiers, long Accessibility, string? XmlSummary, long IsGenerated, string? TestFramework,
        string? CanonicalKey, long Depth)
    {
        public SymbolHit ToHit() => new(Id, Name, Fqn, Kind, FilePath,
            (int)StartLine, (int)StartCol, (int)EndLine, (int)EndCol, Signature,
            Modifiers, (int)Accessibility, XmlSummary, IsGenerated != 0, TestFramework, CanonicalKey);
    }

    public async Task<IReadOnlyList<SymbolHit>> ListCallersAsync(long symbolId, int limit = 50, string? edgeKind = "calls", CancellationToken ct = default)
    {
        // edgeKind = null => walk every kind (used for kind = "all" in the MCP tool).
        // payload column is projected through to SymbolHit.PayloadJson so the markdown renderer
        // can surface per-edge metadata as an indented sub-line when non-null.
        var kindClause = edgeKind is null ? "" : "AND e.kind_name = @kind";
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   e.payload     AS PayloadJson
            FROM edges e
            JOIN symbols s ON s.id = e.src
            JOIN files   f ON f.id = s.file_id
            WHERE e.dst = @id {kindClause}
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawEdgeHit>(new CommandDefinition(
            sql, new { id = symbolId, limit, kind = edgeKind }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<SymbolHit>> ListCalleesAsync(long symbolId, int limit = 50, string? edgeKind = "calls", CancellationToken ct = default)
    {
        // payload column is projected through to SymbolHit.PayloadJson so the markdown renderer
        // can surface per-edge metadata as an indented sub-line when non-null.
        var kindClause = edgeKind is null ? "" : "AND e.kind_name = @kind";
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   e.payload     AS PayloadJson
            FROM edges e
            JOIN symbols s ON s.id = e.dst
            JOIN files   f ON f.id = s.file_id
            WHERE e.src = @id {kindClause}
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawEdgeHit>(new CommandDefinition(
            sql, new { id = symbolId, limit, kind = edgeKind }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public Task<IReadOnlyList<SymbolHit>> ListImplementationsAsync(long symbolId, int limit = 50, CancellationToken ct = default)
        => ListCallersAsync(symbolId, limit, "implements-member", ct);

    public Task<IReadOnlyList<SymbolHit>> ListUsersOfTypeAsync(long symbolId, int limit = 50, CancellationToken ct = default)
        => ListCallersAsync(symbolId, limit, "uses-type", ct);

    public async Task<SymbolHit?> GetSymbolByIdAsync(long symbolId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey
            FROM symbols s
            JOIN files   f ON f.id = s.file_id
            WHERE s.id = @id;
            """;
        var row = await _connection.QueryFirstOrDefaultAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { id = symbolId }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToHit();
    }

    public async Task<IReadOnlyList<SymbolHit>> ListSymbolsInFileAsync(string filePath, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = @path OR f.path LIKE '%' || @path
            ORDER BY s.start_line, s.start_col;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { path = filePath }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    private sealed record RawSymbolHit(long Id, string Name, string Fqn, string Kind, string FilePath,
        long StartLine, long StartCol, long EndLine, long EndCol, string? Signature,
        string? Modifiers, long Accessibility, string? XmlSummary, long IsGenerated, string? TestFramework,
        string? CanonicalKey = null)
    {
        public SymbolHit ToHit() => new(
            Id, Name, Fqn, Kind, FilePath,
            (int)StartLine, (int)StartCol, (int)EndLine, (int)EndCol, Signature,
            Modifiers, (int)Accessibility, XmlSummary, IsGenerated != 0, TestFramework, CanonicalKey);
    }

    /// <summary>
    /// Edge-walking projection used by <see cref="ListCallersAsync"/> /
    /// <see cref="ListCalleesAsync"/>: identical to <see cref="RawSymbolHit"/> plus the
    /// originating <c>edges.payload</c> column. Kept as a separate record because Dapper's
    /// constructor binding looks for an exact column-to-parameter match — adding a default-null
    /// trailing parameter to <see cref="RawSymbolHit"/> would silently break every other SQL
    /// projection that doesn't return <c>PayloadJson</c>.
    /// </summary>
    private sealed record RawEdgeHit(long Id, string Name, string Fqn, string Kind, string FilePath,
        long StartLine, long StartCol, long EndLine, long EndCol, string? Signature,
        string? Modifiers, long Accessibility, string? XmlSummary, long IsGenerated, string? TestFramework,
        string? CanonicalKey, string? PayloadJson)
    {
        public SymbolHit ToHit() => new(
            Id, Name, Fqn, Kind, FilePath,
            (int)StartLine, (int)StartCol, (int)EndLine, (int)EndCol, Signature,
            Modifiers, (int)Accessibility, XmlSummary, IsGenerated != 0, TestFramework, CanonicalKey,
            PayloadJson);
    }

    private sealed record RawReferenceHit(long Id, long SymbolId, string FilePath, long Line, long Col, long Kind, long IsGenerated)
    {
        public ReferenceHit ToHit() => new(Id, SymbolId, FilePath, (int)Line, (int)Col, (Core.ReferenceKind)Kind, IsGenerated != 0);
    }

    public async Task<IReadOnlyList<SymbolKeyRow>> GetAllSymbolKeysAsync(CancellationToken ct = default)
    {
        var rows = await _connection.QueryAsync<SymbolKeyRow>(new CommandDefinition(
            "SELECT canonical_key AS CanonicalKey, id AS Id, file_id AS FileId FROM symbols;",
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FileRow>> GetAllFilesAsync(CancellationToken ct = default)
    {
        var rows = await _connection.QueryAsync<FileRow>(new CommandDefinition(
            "SELECT path AS Path, id AS Id FROM files;",
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<GraphStats> GetStatsAsync(CancellationToken ct = default)
    {
        var files = await _connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM files;", cancellationToken: ct)).ConfigureAwait(false);
        var symbols = await _connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM symbols;", cancellationToken: ct)).ConfigureAwait(false);
        var refs = await _connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM refs;", cancellationToken: ct)).ConfigureAwait(false);
        var edges = await _connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM edges;", cancellationToken: ct)).ConfigureAwait(false);
        return new GraphStats(files, symbols, refs, edges);
    }

    public async Task BulkInsertAnnotationsAsync(IEnumerable<AnnotationRecord> annotations, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO annotations(symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)
            VALUES (@SymbolId, @Name, @FullName, @Flavor, @ArgsJson, @AttributeSymbolId);
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            foreach (var a in annotations)
            {
                await _connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    a.SymbolId,
                    a.Name,
                    a.FullName,
                    a.Flavor,
                    a.ArgsJson,
                    a.AttributeSymbolId,
                }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SymbolHit>> FindByAnnotationAsync(string name, string? flavor, string? argSubstring, string? kindFilter, int limit, CancellationToken ct = default)
    {
        // Strict match on annotation short name. When argSubstring is supplied we also join
        // annotations_fts on the row id and run a trigram MATCH; otherwise we skip that join
        // entirely. Trigrams need >= 3 characters to do anything useful, so we silently
        // ignore short substrings rather than producing zero matches.
        var useFts = !string.IsNullOrWhiteSpace(argSubstring) && argSubstring.Trim().Length >= 3;
        var ftsTerm = useFts ? "\"" + argSubstring!.Trim().Replace("\"", "\"\"") + "\"" : null;

        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey
            FROM annotations a
            JOIN symbols s ON s.id = a.symbol_id
            JOIN files   f ON f.id = s.file_id
            {(useFts ? "JOIN annotations_fts t ON t.rowid = a.id" : "")}
            WHERE a.name = @name
              {(flavor is null ? "" : "AND a.flavor = @flavor")}
              {(useFts ? "AND t.annotations_fts MATCH @fts" : "")}
              {(kindFilter is null ? "" : "AND s.kind_name = @kind")}
            GROUP BY s.id
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql,
            new { name, flavor, fts = ftsTerm, kind = kindFilter, limit },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<AnnotationRecord>> GetAnnotationsForSymbolAsync(long symbolId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol_id    AS SymbolId,
                   name         AS Name,
                   full_name    AS FullName,
                   flavor       AS Flavor,
                   args_json    AS ArgsJson,
                   attribute_symbol_id AS AttributeSymbolId
            FROM annotations
            WHERE symbol_id = @id
            ORDER BY id;
            """;
        var rows = await _connection.QueryAsync<AnnotationRecord>(new CommandDefinition(
            sql, new { id = symbolId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<string>> GetDistinctEdgeKindsAsync(CancellationToken ct = default)
    {
        // SELECT DISTINCT delivers dedup; ORDER BY uses the same column so the result is sorted.
        // Edge kind names are persisted lowercase by the indexer (kebab-case identifiers like
        // "calls", "implements-member"), so no extra LOWER() is needed.
        const string sql = "SELECT DISTINCT kind_name FROM edges ORDER BY kind_name;";
        var rows = await _connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<string>> GetDistinctSymbolKindsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT DISTINCT kind_name FROM symbols ORDER BY kind_name;";
        var rows = await _connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<string>> GetDistinctAnnotationFlavorsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT DISTINCT flavor FROM annotations ORDER BY flavor;";
        var rows = await _connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task UpsertDiagnosticsForFileAsync(long fileId, IEnumerable<DiagnosticRecord> diagnostics, CancellationToken ct = default)
    {
        const string deleteSql = "DELETE FROM diagnostics WHERE file_id = @id;";
        const string insertSql = """
            INSERT INTO diagnostics(symbol_id, file_id, severity, code, message, line, col)
            VALUES (@SymbolId, @FileId, @Severity, @Code, @Message, @Line, @Col);
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            await _connection.ExecuteAsync(new CommandDefinition(
                deleteSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var d in diagnostics)
            {
                await _connection.ExecuteAsync(new CommandDefinition(insertSql, new
                {
                    d.SymbolId,
                    d.FileId,
                    d.Severity,
                    d.Code,
                    d.Message,
                    d.Line,
                    d.Col,
                }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertSymbolHistoryAsync(SymbolHistory history, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO symbol_history(symbol_id, last_commit_sha, last_author, last_authored_at, line_count, blamed_content_sha)
            VALUES (@SymbolId, @LastCommitSha, @LastAuthor, @LastAuthoredAt, @LineCount, @BlamedContentSha)
            ON CONFLICT(symbol_id) DO UPDATE SET
                last_commit_sha    = excluded.last_commit_sha,
                last_author        = excluded.last_author,
                last_authored_at   = excluded.last_authored_at,
                line_count         = excluded.line_count,
                blamed_content_sha = excluded.blamed_content_sha;
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                history.SymbolId,
                history.LastCommitSha,
                history.LastAuthor,
                LastAuthoredAt = history.LastAuthoredAt?.ToUnixTimeMilliseconds(),
                history.LineCount,
                history.BlamedContentSha,
            }, cancellationToken: ct)).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DiagnosticHit>> FindDiagnosticsAsync(int? severity, string? code, long? symbolId, int limit = 100, CancellationToken ct = default)
    {
        // severity is treated as a >= filter so callers can ask "show me warnings and above" with
        // severity = 2 (Warning). Code matches the diagnostic id (e.g. CS0618). symbolId restricts
        // to diagnostics whose source span fell inside the named symbol.
        var clauses = new List<string>();
        if (severity is not null) clauses.Add("d.severity >= @sev");
        if (!string.IsNullOrEmpty(code)) clauses.Add("d.code = @code");
        if (symbolId is not null) clauses.Add("d.symbol_id = @sid");
        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);

        var sql = $"""
            SELECT d.id, d.symbol_id AS SymbolId, d.file_id AS FileId, f.path AS FilePath,
                   d.severity, d.code, d.message, d.line, d.col
            FROM diagnostics d
            JOIN files f ON f.id = d.file_id
            {where}
            ORDER BY f.path, d.line, d.col
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawDiagnosticHit>(new CommandDefinition(
            sql, new { sev = severity, code, sid = symbolId, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<GeneratedFileRow>> ListGeneratedFilesAsync(int limit = 100, CancellationToken ct = default)
    {
        // Manual reader instead of Dapper's QueryAsync<T> because the SymbolCount column is an
        // undeclared aggregate expression. Dapper builds its deserializer upfront from the
        // reader's column metadata, and Microsoft.Data.Sqlite reports the field type as BLOB
        // (byte[]) whenever an undeclared expression's result set is empty — even with an
        // explicit CAST(... AS INTEGER), since sqlite3_column_decltype doesn't propagate CAST.
        // That makes Dapper reject the (long, string, long) record signature for any solution
        // with no source-generated files (test fixtures and prod alike). Reading row-by-row with
        // GetInt64 sidesteps the schema check: by the time we touch a value we have a real row.
        const string sql = """
            SELECT f.id, f.path,
                   COALESCE((SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id), 0)
            FROM files f
            WHERE f.is_generated = 1
            ORDER BY 3 DESC, f.path
            LIMIT @limit;
            """;
        var rows = new List<GeneratedFileRow>();
        await using var reader = await _connection.ExecuteReaderAsync(new CommandDefinition(
            sql, new { limit }, cancellationToken: ct)).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GeneratedFileRow(reader.GetInt64(0), reader.GetString(1), (int)reader.GetInt64(2)));
        }
        return rows;
    }

    public async Task<bool> IsGeneratedFileAsync(long fileId, CancellationToken ct = default)
    {
        var v = await _connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT is_generated FROM files WHERE id = @id;",
            new { id = fileId }, cancellationToken: ct)).ConfigureAwait(false);
        return v is not null && v.Value != 0;
    }

    private sealed record RawDiagnosticHit(long Id, long? SymbolId, long FileId, string FilePath,
        long Severity, string Code, string Message, long Line, long Col)
    {
        public DiagnosticHit ToHit() => new(Id, SymbolId, FileId, FilePath,
            (int)Severity, Code, Message, (int)Line, (int)Col);
    }

    public async Task<IReadOnlyDictionary<long, byte[]?>> GetBlamedShasForFileAsync(long fileId, CancellationToken ct = default)
    {
        // We project NULL for symbols that have a row but no sha yet (defensive — schema makes it
        // nullable). Symbols without a row simply don't appear in the result; the pipeline treats
        // their absence as "needs blame".
        const string sql = """
            SELECT s.id AS SymbolId, h.blamed_content_sha AS BlamedContentSha
            FROM symbols s
            LEFT JOIN symbol_history h ON h.symbol_id = s.id
            WHERE s.file_id = @id;
            """;
        var rows = await _connection.QueryAsync<RawBlamedSha>(new CommandDefinition(
            sql, new { id = fileId }, cancellationToken: ct)).ConfigureAwait(false);
        var dict = new Dictionary<long, byte[]?>();
        foreach (var row in rows) dict[row.SymbolId] = row.BlamedContentSha;
        return dict;
    }

    private sealed record RawBlamedSha(long SymbolId, byte[]? BlamedContentSha);

    public async Task<SymbolHistory?> GetSymbolHistoryAsync(long symbolId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol_id          AS SymbolId,
                   last_commit_sha    AS LastCommitSha,
                   last_author        AS LastAuthor,
                   last_authored_at   AS LastAuthoredAtMs,
                   line_count         AS LineCount,
                   blamed_content_sha AS BlamedContentSha
            FROM symbol_history WHERE symbol_id = @id;
            """;
        var row = await _connection.QueryFirstOrDefaultAsync<RawSymbolHistory>(new CommandDefinition(
            sql, new { id = symbolId }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToHistory();
    }

    public async Task<IReadOnlyDictionary<long, SymbolHistory>> GetSymbolHistoryBatchAsync(
        IReadOnlyCollection<long> symbolIds, CancellationToken ct = default)
    {
        if (symbolIds.Count == 0) return new Dictionary<long, SymbolHistory>();
        // Inline the ids — they're integers, no injection risk, and Dapper's IN-clause expansion
        // keeps the prepared statement clean.
        const string sql = """
            SELECT symbol_id          AS SymbolId,
                   last_commit_sha    AS LastCommitSha,
                   last_author        AS LastAuthor,
                   last_authored_at   AS LastAuthoredAtMs,
                   line_count         AS LineCount,
                   blamed_content_sha AS BlamedContentSha
            FROM symbol_history
            WHERE symbol_id IN @ids;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHistory>(new CommandDefinition(
            sql, new { ids = symbolIds }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.SymbolId, r => r.ToHistory());
    }

    public async Task<IReadOnlyList<RecentChangeHit>> ListRecentChangesAsync(
        long sinceUnixMs, string? authorSubstring, int limit, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   h.last_commit_sha    AS LastCommitSha,
                   h.last_author        AS LastAuthor,
                   h.last_authored_at   AS LastAuthoredAtMs,
                   h.line_count         AS LineCount,
                   h.blamed_content_sha AS BlamedContentSha
            FROM symbol_history h
            JOIN symbols s ON s.id = h.symbol_id
            JOIN files   f ON f.id = s.file_id
            WHERE h.last_authored_at IS NOT NULL
              AND h.last_authored_at >= @since
              {(string.IsNullOrEmpty(authorSubstring) ? "" : "AND h.last_author LIKE '%' || @author || '%' COLLATE NOCASE")}
            ORDER BY h.last_authored_at DESC
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawRecentChange>(new CommandDefinition(
            sql, new { since = sinceUnixMs, author = authorSubstring, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new RecentChangeHit(r.ToHit(), r.ToHistory())).ToList();
    }

    public async Task<IReadOnlyList<SymbolSpan>> GetSymbolSpansForFileAsync(long fileId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id AS SymbolId, f.path AS FilePath, s.start_line AS StartLine, s.end_line AS EndLine
            FROM symbols s
            JOIN files   f ON f.id = s.file_id
            WHERE s.file_id = @id;
            """;
        var rows = await _connection.QueryAsync<RawSymbolSpan>(new CommandDefinition(
            sql, new { id = fileId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new SymbolSpan(r.SymbolId, r.FilePath, (int)r.StartLine, (int)r.EndLine)).ToList();
    }

    private sealed record RawSymbolSpan(long SymbolId, string FilePath, long StartLine, long EndLine);

    public async Task<IReadOnlyList<TestForHit>> ListTestsForAsync(long symbolId, int limit, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey
            FROM edges e
            JOIN symbols s ON s.id = e.src
            JOIN files   f ON f.id = s.file_id
            WHERE e.dst = @id AND e.kind_name = @kind
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { id = symbolId, kind = "tests", limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r =>
        {
            var hit = r.ToHit();
            return new TestForHit(hit, hit.TestFramework);
        }).ToList();
    }

    private sealed record RawSymbolHistory(long SymbolId, string? LastCommitSha, string? LastAuthor,
        long? LastAuthoredAtMs, long LineCount, byte[]? BlamedContentSha)
    {
        public SymbolHistory ToHistory() => new(
            SymbolId,
            LastCommitSha,
            LastAuthor,
            LastAuthoredAtMs is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(LastAuthoredAtMs.Value),
            (int)LineCount,
            BlamedContentSha);
    }

    private sealed record RawRecentChange(long Id, string Name, string Fqn, string Kind, string FilePath,
        long StartLine, long StartCol, long EndLine, long EndCol, string? Signature,
        string? Modifiers, long Accessibility, string? XmlSummary, long IsGenerated, string? TestFramework,
        string? CanonicalKey,
        string? LastCommitSha, string? LastAuthor, long? LastAuthoredAtMs, long LineCount, byte[]? BlamedContentSha)
    {
        public SymbolHit ToHit() => new(Id, Name, Fqn, Kind, FilePath,
            (int)StartLine, (int)StartCol, (int)EndLine, (int)EndCol, Signature,
            Modifiers, (int)Accessibility, XmlSummary, IsGenerated != 0, TestFramework, CanonicalKey);
        public SymbolHistory ToHistory() => new(
            Id, LastCommitSha, LastAuthor,
            LastAuthoredAtMs is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(LastAuthoredAtMs.Value),
            (int)LineCount, BlamedContentSha);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
