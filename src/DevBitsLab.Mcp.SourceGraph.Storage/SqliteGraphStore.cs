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
            await _connection.ExecuteAsync(
                new CommandDefinition("INSERT OR REPLACE INTO schema_version(version) VALUES (@v);", new { v = Schema.Version }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> UpsertFileAsync(string path, byte[] contentSha256, DateTimeOffset indexedAt, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO files(path, content_sha256, last_indexed_at)
            VALUES (@path, @sha, @at)
            ON CONFLICT(path) DO UPDATE SET
                content_sha256 = excluded.content_sha256,
                last_indexed_at = excluded.last_indexed_at
            RETURNING id;
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _connection.ExecuteScalarAsync<long>(new CommandDefinition(
                sql,
                new { path, sha = contentSha256, at = indexedAt.ToUnixTimeMilliseconds() },
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
            const string deleteSymbolsSql = """
                DELETE FROM symbols
                WHERE file_id = @id AND canonical_key NOT IN (SELECT key FROM keep_keys);
                """;
            await _connection.ExecuteAsync(new CommandDefinition(deleteRefsSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(deleteEdgesSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
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
        // every symbol, so the parent row exists by the time the lookup happens.
        const string sql = """
            INSERT INTO symbols(canonical_key, name, fqn, kind, file_id, start_line, start_col, end_line, end_col, signature, container_id, modifiers, accessibility, xml_summary)
            VALUES (@Key, @Name, @Fqn, @Kind, @FileId, @StartLine, @StartCol, @EndLine, @EndCol, @Signature, @ContainerId, @Modifiers, @Accessibility, @XmlSummary)
            ON CONFLICT(canonical_key) DO UPDATE SET
                name          = excluded.name,
                fqn           = excluded.fqn,
                kind          = excluded.kind,
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
                Kind = (int)symbol.Kind,
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
            }, cancellationToken: ct)).ConfigureAwait(false);
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
        const string sql = """
            INSERT OR IGNORE INTO edges(src, dst, kind)
            VALUES (@Src, @Dst, @Kind);
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            foreach (var e in edges)
            {
                await _connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    e.Src,
                    e.Dst,
                    Kind = (int)e.Kind,
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
                SELECT s.id, s.name, s.fqn, s.kind, f.path, s.start_line, s.start_col, s.end_line, s.end_col, s.signature,
                    s.modifiers, s.accessibility, s.xml_summary,
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
            SELECT id, name, fqn, kind, path AS FilePath, start_line AS StartLine, start_col AS StartCol,
                   end_line AS EndLine, end_col AS EndCol, signature,
                   modifiers AS Modifiers, accessibility AS Accessibility, xml_summary AS XmlSummary
            FROM ranked
            ORDER BY rank, length(fqn), fqn
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { q = query, hint = filePathHint, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(long symbolId, int limit = 200, CancellationToken ct = default)
    {
        const string sql = """
            SELECT r.id, r.symbol_id AS SymbolId, f.path AS FilePath, r.line, r.col, r.kind
            FROM refs r
            JOIN files f ON f.id = r.file_id
            WHERE r.symbol_id = @id
            ORDER BY f.path, r.line, r.col
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawReferenceHit>(new CommandDefinition(
            sql, new { id = symbolId, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<SymbolHit>> SearchSymbolsAsync(string ftsQuery, Core.SymbolKind? kindFilter = null, int limit = 25, CancellationToken ct = default)
    {
        // FTS5 expects bareword tokens; we wrap each whitespace-separated chunk and prefix with NEAR if multi-word.
        // For trigram tokenizer, just quote to be safe.
        var fts = "\"" + ftsQuery.Replace("\"", "\"\"") + "\"";
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary
            FROM symbols_fts t
            JOIN symbols s ON s.id = t.rowid
            JOIN files   f ON f.id = s.file_id
            WHERE t.symbols_fts MATCH @q
              {(kindFilter is null ? "" : "AND s.kind = @kind")}
            ORDER BY rank
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { q = fts, kind = (int?)kindFilter, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<ModuleSymbol>> ModuleSummaryAsync(string namespaceOrPathPrefix, int limit = 25, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   COALESCE((SELECT COUNT(*) FROM edges e WHERE e.dst = s.id AND e.kind = 0), 0) AS InDegree
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.fqn = @prefix
               OR s.fqn LIKE @prefix || '.%'
               OR f.path LIKE '%' || @prefix || '%'
            ORDER BY InDegree DESC, length(s.fqn), s.fqn
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawModuleSymbol>(new CommandDefinition(
            sql, new { prefix = namespaceOrPathPrefix, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new ModuleSymbol(r.ToHit(), (int)r.InDegree)).ToList();
    }

    public async Task<IReadOnlyList<ImpactedSymbol>> ImpactOfChangeAsync(long symbolId, int maxDepth = 4, int limit = 100, CancellationToken ct = default)
    {
        const string sql = """
            WITH RECURSIVE upstream(id, depth) AS (
                SELECT src, 1 FROM edges WHERE dst = @id AND kind = 0
                UNION
                SELECT e.src, u.depth + 1
                FROM edges e
                JOIN upstream u ON e.dst = u.id
                WHERE e.kind = 0 AND u.depth < @maxDepth
            )
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   MIN(u.depth) AS Depth
            FROM upstream u
            JOIN symbols s ON s.id = u.id
            JOIN files   f ON f.id = s.file_id
            GROUP BY s.id
            ORDER BY Depth, s.fqn
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawImpactedSymbol>(new CommandDefinition(
            sql, new { id = symbolId, maxDepth, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new ImpactedSymbol(r.ToHit(), (int)r.Depth)).ToList();
    }

    public async Task<IReadOnlyList<SymbolHit>> ListMembersAsync(long containerId, int? accessibilityFilter = null, int limit = 200, CancellationToken ct = default)
    {
        // Direct children of the named container, ordered by start_line. Inherited members are
        // resolved at the tool layer (would require walking inherits/implements edges to map a
        // base type's children onto this container).
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary
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

    private sealed record RawModuleSymbol(long Id, string Name, string Fqn, long Kind, string FilePath,
        long StartLine, long StartCol, long EndLine, long EndCol, string? Signature,
        string? Modifiers, long Accessibility, string? XmlSummary, long InDegree)
    {
        public SymbolHit ToHit() => new(Id, Name, Fqn, (Core.SymbolKind)Kind, FilePath,
            (int)StartLine, (int)StartCol, (int)EndLine, (int)EndCol, Signature,
            Modifiers, (int)Accessibility, XmlSummary);
    }

    private sealed record RawImpactedSymbol(long Id, string Name, string Fqn, long Kind, string FilePath,
        long StartLine, long StartCol, long EndLine, long EndCol, string? Signature,
        string? Modifiers, long Accessibility, string? XmlSummary, long Depth)
    {
        public SymbolHit ToHit() => new(Id, Name, Fqn, (Core.SymbolKind)Kind, FilePath,
            (int)StartLine, (int)StartCol, (int)EndLine, (int)EndCol, Signature,
            Modifiers, (int)Accessibility, XmlSummary);
    }

    public async Task<IReadOnlyList<SymbolHit>> ListCallersAsync(long symbolId, int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary
            FROM edges e
            JOIN symbols s ON s.id = e.src
            JOIN files   f ON f.id = s.file_id
            WHERE e.dst = @id AND e.kind = 0  -- EdgeKind.Calls
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { id = symbolId, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<SymbolHit>> ListCalleesAsync(long symbolId, int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary
            FROM edges e
            JOIN symbols s ON s.id = e.dst
            JOIN files   f ON f.id = s.file_id
            WHERE e.src = @id AND e.kind = 0  -- EdgeKind.Calls
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { id = symbolId, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    public async Task<SymbolHit?> GetSymbolByIdAsync(long symbolId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary
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
            SELECT s.id, s.name, s.fqn, s.kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = @path OR f.path LIKE '%' || @path
            ORDER BY s.start_line, s.start_col;
            """;
        var rows = await _connection.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { path = filePath }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHit()).ToList();
    }

    private sealed record RawSymbolHit(long Id, string Name, string Fqn, long Kind, string FilePath,
        long StartLine, long StartCol, long EndLine, long EndCol, string? Signature,
        string? Modifiers, long Accessibility, string? XmlSummary)
    {
        public SymbolHit ToHit() => new(
            Id, Name, Fqn, (Core.SymbolKind)Kind, FilePath,
            (int)StartLine, (int)StartCol, (int)EndLine, (int)EndCol, Signature,
            Modifiers, (int)Accessibility, XmlSummary);
    }

    private sealed record RawReferenceHit(long Id, long SymbolId, string FilePath, long Line, long Col, long Kind)
    {
        public ReferenceHit ToHit() => new(Id, SymbolId, FilePath, (int)Line, (int)Col, (Core.ReferenceKind)Kind);
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

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
