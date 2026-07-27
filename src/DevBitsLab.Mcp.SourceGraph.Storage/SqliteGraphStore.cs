using System.Text.Json;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore : IGraphStore
{
    private readonly SqliteConnection _connection;
    private readonly string _readerConnectionString;
    private readonly ILogger<SqliteGraphStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _vectorExtensionLoaded;
    private int _embeddingDimension;
    private bool _disposed;

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
        builder.Mode = SqliteOpenMode.ReadOnly;
        builder.ForeignKeys = false;
        _readerConnectionString = builder.ConnectionString;
        _logger = logger ?? NullLogger<SqliteGraphStore>.Instance;
    }

    private async Task<SqliteConnection> OpenReaderAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_readerConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

    public Task<GraphReadVersion> GetReadVersionAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                total_changes() AS ConnectionChanges,
                data_version AS DataVersion
            FROM pragma_data_version;
            """;
        return _connection.QuerySingleAsync<GraphReadVersion>(
            new CommandDefinition(sql, cancellationToken: ct));
    }

    public Task<int?> GetProjectionVersionAsync(
        string producer,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        return _connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT version FROM projection_versions WHERE producer = @producer;",
            new { producer },
            cancellationToken: ct));
    }

    public async Task SetProjectionVersionAsync(
        string producer,
        int version,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        const string sql = """
            INSERT INTO projection_versions(producer, version, completed_at)
            VALUES (@producer, @version, @completedAt)
            ON CONFLICT(producer) DO UPDATE SET
                version = excluded.version,
                completed_at = excluded.completed_at;
            """;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    producer,
                    version,
                    completedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                cancellationToken: ct)).ConfigureAwait(false);
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

    public Task ClearFileOutgoingAsync(
        long fileId,
        CancellationToken ct = default) =>
        ClearFileOutgoingAsync(
            fileId,
            Array.Empty<string>(),
            ct);

    public async Task ClearFileOutgoingAsync(
        long fileId,
        IReadOnlyCollection<string> edgeEvidenceProducersToPreserve,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileId, 1);
        ArgumentNullException.ThrowIfNull(edgeEvidenceProducersToPreserve);
        ct.ThrowIfCancellationRequested();

        var candidateProducers = edgeEvidenceProducersToPreserve.ToArray();
        var producerSet = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < candidateProducers.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var producer = candidateProducers[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(
                producer,
                nameof(edgeEvidenceProducersToPreserve));
            if (!producerSet.Add(producer))
            {
                throw new ArgumentException(
                    $"Preserved edge-evidence producer `{producer}` is duplicated "
                    + $"(index {index}).",
                    nameof(edgeEvidenceProducersToPreserve));
            }
        }
        var orderedProducers = producerSet
            .OrderBy(producer => producer, StringComparer.Ordinal)
            .ToArray();

        // Evidence is owned by the file pass that produced it, not necessarily by the file that
        // declares the logical edge's source symbol. Remove only this producer's occurrences,
        // then discard logical edges whose final supporting occurrence disappeared.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TEMP TABLE IF NOT EXISTS
                    clear_outgoing_preserved_producers(
                        producer TEXT PRIMARY KEY);
                DELETE FROM clear_outgoing_preserved_producers;
                """,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            if (orderedProducers.Length > 0)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO clear_outgoing_preserved_producers(producer)
                    VALUES (@producer);
                    """,
                    orderedProducers.Select(producer => new { producer }),
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE edges AS edge
                SET payload = (
                    SELECT NULLIF(ev.payload, '')
                    FROM edge_evidence ev
                    WHERE ev.src = edge.src
                      AND ev.dst = edge.dst
                      AND ev.kind_name = edge.kind_name
                      AND (
                          ev.producing_file_id <> @id
                          OR ev.producer IN (
                              SELECT producer
                              FROM clear_outgoing_preserved_producers
                          )
                      )
                    ORDER BY ev.id
                    LIMIT 1
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edge.src
                      AND owned.dst = edge.dst
                      AND owned.kind_name = edge.kind_name
                      AND owned.producing_file_id = @id
                      AND owned.producer NOT IN (
                          SELECT producer
                          FROM clear_outgoing_preserved_producers
                      )
                );
                """,
                new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM edge_evidence
                WHERE producing_file_id = @id
                  AND producer NOT IN (
                      SELECT producer
                      FROM clear_outgoing_preserved_producers
                  );
                """,
                new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM refs WHERE file_id = @id;",
                new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM edges
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM edge_evidence ev
                    WHERE ev.src = edges.src
                      AND ev.dst = edges.dst
                      AND ev.kind_name = edges.kind_name
                );
                """,
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM clear_outgoing_preserved_producers;",
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ClearEdgeEvidenceAsync(
        long producingFileId,
        string producer,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(producingFileId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);

        var parameters = new
        {
            ProducingFileId = producingFileId,
            Producer = producer,
        };
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();

            // Keep the legacy edges.payload projection aligned with the earliest occurrence that
            // will survive this exact producer cleanup.
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE edges AS edge
                SET payload = (
                    SELECT NULLIF(survivor.payload, '')
                    FROM edge_evidence survivor
                    WHERE survivor.src = edge.src
                      AND survivor.dst = edge.dst
                      AND survivor.kind_name = edge.kind_name
                      AND NOT (
                          survivor.producing_file_id = @ProducingFileId
                          AND survivor.producer = @Producer
                      )
                    ORDER BY survivor.id
                    LIMIT 1
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edge.src
                      AND owned.dst = edge.dst
                      AND owned.kind_name = edge.kind_name
                      AND owned.producing_file_id = @ProducingFileId
                      AND owned.producer = @Producer
                );
                """,
                parameters,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            // Delete only logical edges touched by this cleanup and left with no other evidence.
            // This avoids sweeping unrelated legacy rows that may predate occurrence evidence.
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM edges
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edges.src
                      AND owned.dst = edges.dst
                      AND owned.kind_name = edges.kind_name
                      AND owned.producing_file_id = @ProducingFileId
                      AND owned.producer = @Producer
                )
                  AND NOT EXISTS (
                    SELECT 1
                    FROM edge_evidence survivor
                    WHERE survivor.src = edges.src
                      AND survivor.dst = edges.dst
                      AND survivor.kind_name = edges.kind_name
                      AND NOT (
                          survivor.producing_file_id = @ProducingFileId
                          AND survivor.producer = @Producer
                      )
                );
                """,
                parameters,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            var removed = await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM edge_evidence
                WHERE producing_file_id = @ProducingFileId
                  AND producer = @Producer;
                """,
                parameters,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            tx.Commit();
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ReplaceProducerEdgeEvidenceAsync(
        string producingFilePath,
        string producer,
        IReadOnlyList<ProducerEdgeEvidenceFact> edges,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producingFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentNullException.ThrowIfNull(edges);

        const string insertEdgeSql = """
            INSERT OR IGNORE INTO edges(src, dst, kind_name, payload)
            VALUES (@Src, @Dst, @Kind, @Payload);
            """;
        const string insertEvidenceSql = """
            INSERT OR IGNORE INTO edge_evidence(
                src, dst, kind_name, producing_file_id, file_path,
                start_line, start_col, end_line, end_col,
                confidence, producer, payload)
            VALUES (
                @Src, @Dst, @Kind, @ProducingFileId, @FilePath,
                @StartLine, @StartColumn, @EndLine, @EndColumn,
                @Confidence, @Producer, @Payload);
            """;
        const string syncLogicalPayloadSql = """
            UPDATE edges
            SET payload = (
                SELECT NULLIF(ev.payload, '')
                FROM edge_evidence ev
                WHERE ev.src = @Src
                  AND ev.dst = @Dst
                  AND ev.kind_name = @Kind
                ORDER BY ev.id
                LIMIT 1
            )
            WHERE src = @Src
              AND dst = @Dst
              AND kind_name = @Kind;
            """;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var producingFile = await _connection.QuerySingleOrDefaultAsync<RawProducingFile>(
                new CommandDefinition(
                    """
                    SELECT id AS FileId, path AS FilePath
                    FROM files
                    WHERE path = @ProducingFilePath;
                    """,
                    new { ProducingFilePath = producingFilePath },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Producer edge replacement could not resolve indexed file `{producingFilePath}`.");

            // Resolve and validate the complete candidate set before deleting any prior
            // occurrence. A lookup or validation failure therefore rolls this transaction back
            // without exposing a partially rebuilt producer result.
            var symbolIds = new Dictionary<string, long>(StringComparer.Ordinal);
            async Task<long> ResolveSymbolIdAsync(string canonicalKey)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
                if (symbolIds.TryGetValue(canonicalKey, out var cached)) return cached;

                var resolved = await _connection.ExecuteScalarAsync<long?>(
                    new CommandDefinition(
                        "SELECT id FROM symbols WHERE canonical_key = @canonicalKey;",
                        new { canonicalKey },
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false);
                if (resolved is null)
                {
                    throw new InvalidOperationException(
                        $"Producer edge replacement could not resolve symbol `{canonicalKey}`.");
                }

                symbolIds[canonicalKey] = resolved.Value;
                return resolved.Value;
            }

            var resolvedEdges = new List<ResolvedProducerEdgeEvidenceFact>(edges.Count);
            for (var index = 0; index < edges.Count; index++)
            {
                var edge = edges[index]
                    ?? throw new ArgumentException(
                        $"Producer edge replacement contains a null edge at index {index}.",
                        nameof(edges));
                ArgumentException.ThrowIfNullOrWhiteSpace(edge.Kind);
                ArgumentNullException.ThrowIfNull(edge.Evidence);
                ArgumentNullException.ThrowIfNull(edge.Evidence.Location);
                if (!string.Equals(edge.Evidence.Producer, producer, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Every replacement evidence producer must exactly match "
                        + $"the method producer `{producer}` (index {index}).",
                        nameof(edges));
                }

                var evidence = new Evidence(
                    producingFile.FileId,
                    edge.Evidence.Location,
                    edge.Evidence.Confidence,
                    producer,
                    edge.Evidence.Metadata);
                ValidateEvidence(evidence);
                if (!PathsEquivalent(producingFile.FilePath, evidence.Location.FilePath))
                {
                    throw new InvalidOperationException(
                        "Replacement evidence path does not match indexed producing file "
                        + $"`{producingFile.FilePath}` (index {index}).");
                }

                var src = await ResolveSymbolIdAsync(edge.SourceCanonicalKey).ConfigureAwait(false);
                var dst = await ResolveSymbolIdAsync(edge.TargetCanonicalKey).ConfigureAwait(false);
                resolvedEdges.Add(new ResolvedProducerEdgeEvidenceFact(
                    edge.SourceCanonicalKey,
                    edge.TargetCanonicalKey,
                    src,
                    dst,
                    edge.Kind,
                    SerializeMetadata(edge.Metadata),
                    producingFile.FilePath,
                    evidence.Location.StartLine,
                    evidence.Location.StartColumn,
                    evidence.Location.EndLine,
                    evidence.Location.EndColumn,
                    evidence.Confidence,
                    SerializeMetadata(evidence.Metadata ?? edge.Metadata) ?? string.Empty));
            }

            // Input order cannot affect occurrence ids or the compatibility payload. Collapse
            // facts that map to the edge_evidence uniqueness key, then insert in canonical
            // source/target/location order.
            var orderedEdges = resolvedEdges
                .GroupBy(edge => new ProducerEdgeEvidenceIdentity(
                    edge.Src,
                    edge.Dst,
                    edge.Kind,
                    edge.FilePath,
                    edge.StartLine,
                    edge.StartColumn,
                    edge.EndLine,
                    edge.EndColumn,
                    edge.Confidence,
                    edge.EvidencePayload))
                .Select(group => group
                    .OrderBy(edge => edge.LogicalPayload ?? string.Empty, StringComparer.Ordinal)
                    .First())
                .OrderBy(edge => edge.SourceCanonicalKey, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetCanonicalKey, StringComparer.Ordinal)
                .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
                .ThenBy(edge => edge.FilePath, StringComparer.Ordinal)
                .ThenBy(edge => edge.StartLine)
                .ThenBy(edge => edge.StartColumn)
                .ThenBy(edge => edge.EndLine)
                .ThenBy(edge => edge.EndColumn)
                .ThenBy(edge => edge.Confidence)
                .ThenBy(edge => edge.EvidencePayload, StringComparer.Ordinal)
                .ToList();

            var selector = new
            {
                ProducingFileId = producingFile.FileId,
                Producer = producer,
            };
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE edges AS edge
                SET payload = (
                    SELECT NULLIF(survivor.payload, '')
                    FROM edge_evidence survivor
                    WHERE survivor.src = edge.src
                      AND survivor.dst = edge.dst
                      AND survivor.kind_name = edge.kind_name
                      AND NOT (
                          survivor.producing_file_id = @ProducingFileId
                          AND survivor.producer = @Producer
                      )
                    ORDER BY survivor.id
                    LIMIT 1
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edge.src
                      AND owned.dst = edge.dst
                      AND owned.kind_name = edge.kind_name
                      AND owned.producing_file_id = @ProducingFileId
                      AND owned.producer = @Producer
                );

                DELETE FROM edges
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edges.src
                      AND owned.dst = edges.dst
                      AND owned.kind_name = edges.kind_name
                      AND owned.producing_file_id = @ProducingFileId
                      AND owned.producer = @Producer
                )
                  AND NOT EXISTS (
                    SELECT 1
                    FROM edge_evidence survivor
                    WHERE survivor.src = edges.src
                      AND survivor.dst = edges.dst
                      AND survivor.kind_name = edges.kind_name
                      AND NOT (
                          survivor.producing_file_id = @ProducingFileId
                          AND survivor.producer = @Producer
                      )
                );

                DELETE FROM edge_evidence
                WHERE producing_file_id = @ProducingFileId
                  AND producer = @Producer;
                """,
                selector,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            foreach (var edge in orderedEdges)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    insertEdgeSql,
                    new
                    {
                        edge.Src,
                        edge.Dst,
                        edge.Kind,
                        Payload = edge.LogicalPayload,
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    insertEvidenceSql,
                    new
                    {
                        edge.Src,
                        edge.Dst,
                        edge.Kind,
                        ProducingFileId = producingFile.FileId,
                        edge.FilePath,
                        edge.StartLine,
                        edge.StartColumn,
                        edge.EndLine,
                        edge.EndColumn,
                        Confidence = (int)edge.Confidence,
                        Producer = producer,
                        Payload = edge.EvidencePayload,
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    syncLogicalPayloadSql,
                    new { edge.Src, edge.Dst, edge.Kind },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ReplaceAnnotationsForFileByFlavorAsync(
        string filePath,
        string flavor,
        IReadOnlyList<FileAnnotationFact> annotations,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        KebabCaseValidator.Validate(flavor, nameof(flavor));
        ArgumentNullException.ThrowIfNull(annotations);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var indexedFile = await _connection.QuerySingleOrDefaultAsync<RawProducingFile>(
                new CommandDefinition(
                    """
                    SELECT id AS FileId, path AS FilePath
                    FROM files
                    WHERE path = @FilePath;
                    """,
                    new { FilePath = filePath },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Annotation replacement could not resolve indexed file `{filePath}`.");

            // Resolve and validate the complete candidate set before deleting prior rows. This
            // keeps validation, lookup, cancellation, and SQL failures rollback-safe.
            var symbols = new Dictionary<string, RawAnnotationSymbol>(StringComparer.Ordinal);
            async Task<RawAnnotationSymbol> ResolveSymbolAsync(string canonicalKey)
            {
                CanonicalKeyValidator.Validate(canonicalKey, nameof(annotations));
                if (symbols.TryGetValue(canonicalKey, out var cached)) return cached;

                var resolved = await _connection.QuerySingleOrDefaultAsync<RawAnnotationSymbol>(
                    new CommandDefinition(
                        """
                        SELECT id AS SymbolId, file_id AS FileId
                        FROM symbols
                        WHERE canonical_key = @CanonicalKey;
                        """,
                        new { CanonicalKey = canonicalKey },
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Annotation replacement could not resolve symbol `{canonicalKey}`.");
                symbols[canonicalKey] = resolved;
                return resolved;
            }

            var resolvedAnnotations = new List<ResolvedFileAnnotationFact>(annotations.Count);
            for (var index = 0; index < annotations.Count; index++)
            {
                var annotation = annotations[index]
                    ?? throw new ArgumentException(
                        $"Annotation replacement contains a null fact at index {index}.",
                        nameof(annotations));
                if (!string.Equals(annotation.Flavor, flavor, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Every replacement annotation flavor must exactly match "
                        + $"the requested flavor `{flavor}` (index {index}).",
                        nameof(annotations));
                }
                ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(annotation.FullName);
                if (annotation.ArgsJson is not null)
                {
                    try
                    {
                        using var _ = JsonDocument.Parse(annotation.ArgsJson);
                    }
                    catch (JsonException ex)
                    {
                        throw new ArgumentException(
                            $"Annotation args_json must contain valid JSON (index {index}).",
                            nameof(annotations),
                            ex);
                    }
                }

                var host = await ResolveSymbolAsync(annotation.SymbolCanonicalKey)
                    .ConfigureAwait(false);
                if (host.FileId != indexedFile.FileId)
                {
                    throw new InvalidOperationException(
                        "Annotation replacement hosts must belong to indexed file "
                        + $"`{indexedFile.FilePath}`; `{annotation.SymbolCanonicalKey}` is external.");
                }

                long? attributeSymbolId = null;
                if (annotation.AttributeCanonicalKey is not null)
                {
                    attributeSymbolId = (await ResolveSymbolAsync(annotation.AttributeCanonicalKey)
                        .ConfigureAwait(false)).SymbolId;
                }

                resolvedAnnotations.Add(new ResolvedFileAnnotationFact(
                    annotation.SymbolCanonicalKey,
                    host.SymbolId,
                    annotation.Name,
                    annotation.FullName,
                    flavor,
                    annotation.ArgsJson,
                    annotation.AttributeCanonicalKey,
                    attributeSymbolId));
            }

            // Exact duplicates collapse and canonical ordering makes row order independent of
            // extractor traversal order.
            var orderedAnnotations = resolvedAnnotations
                .Distinct()
                .OrderBy(annotation => annotation.SymbolCanonicalKey, StringComparer.Ordinal)
                .ThenBy(annotation => annotation.Name, StringComparer.Ordinal)
                .ThenBy(annotation => annotation.FullName, StringComparer.Ordinal)
                .ThenBy(annotation => annotation.ArgsJson ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(
                    annotation => annotation.AttributeCanonicalKey ?? string.Empty,
                    StringComparer.Ordinal)
                .ToList();

            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM annotations
                WHERE flavor = @Flavor
                  AND symbol_id IN (
                      SELECT id
                      FROM symbols
                      WHERE file_id = @FileId
                  );
                """,
                new { Flavor = flavor, indexedFile.FileId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            const string insertSql = """
                INSERT INTO annotations(
                    symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)
                VALUES (
                    @SymbolId, @Name, @FullName, @Flavor, @ArgsJson, @AttributeSymbolId);
                """;
            foreach (var annotation in orderedAnnotations)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    insertSql,
                    annotation,
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task ReconcileFileDeclarationsAndAnnotationsAsync(
        string filePath,
        IReadOnlyCollection<string> keysToKeep,
        IReadOnlyList<FileAnnotationFact> annotations,
        CancellationToken ct = default)
        => ReconcileFileDeclarationsAndAnnotationsAsync(
            filePath,
            keysToKeep,
            annotations,
            Array.Empty<string>(),
            ct);

    public async Task ReconcileFileDeclarationsAndAnnotationsAsync(
        string filePath,
        IReadOnlyCollection<string> keysToKeep,
        IReadOnlyList<FileAnnotationFact> annotations,
        IReadOnlyCollection<string> annotationFlavorsToPreserve,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(keysToKeep);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(annotationFlavorsToPreserve);
        ct.ThrowIfCancellationRequested();

        // Snapshot and validate caller-owned collections before acquiring the write lock. This
        // prevents concurrent collection mutation from changing the keep/annotation set between
        // validation and publication.
        var keepKeySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keysToKeep)
        {
            CanonicalKeyValidator.Validate(key, nameof(keysToKeep));
            keepKeySet.Add(key);
        }
        var orderedKeepKeys = keepKeySet
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        var preservedFlavorSet = new HashSet<string>(StringComparer.Ordinal);
        var candidatePreservedFlavors = annotationFlavorsToPreserve.ToArray();
        for (var index = 0; index < candidatePreservedFlavors.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var flavor = candidatePreservedFlavors[index];
            KebabCaseValidator.Validate(
                flavor,
                nameof(annotationFlavorsToPreserve));
            if (!preservedFlavorSet.Add(flavor))
            {
                throw new ArgumentException(
                    $"Preserved annotation flavor `{flavor}` is duplicated (index {index}).",
                    nameof(annotationFlavorsToPreserve));
            }
        }
        var orderedPreservedFlavors = preservedFlavorSet
            .OrderBy(flavor => flavor, StringComparer.Ordinal)
            .ToArray();

        var candidateAnnotations = new FileAnnotationFact[annotations.Count];
        for (var index = 0; index < annotations.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var annotation = annotations[index]
                ?? throw new ArgumentException(
                    $"Annotation reconciliation contains a null fact at index {index}.",
                    nameof(annotations));
            CanonicalKeyValidator.Validate(
                annotation.SymbolCanonicalKey,
                nameof(annotations));
            if (!keepKeySet.Contains(annotation.SymbolCanonicalKey))
            {
                throw new InvalidOperationException(
                    "Annotation reconciliation hosts must be present in the declaration keep set; "
                    + $"`{annotation.SymbolCanonicalKey}` is not retained.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(annotation.FullName);
            KebabCaseValidator.Validate(annotation.Flavor, nameof(annotations));
            if (annotation.AttributeCanonicalKey is not null)
            {
                CanonicalKeyValidator.Validate(
                    annotation.AttributeCanonicalKey,
                    nameof(annotations));
            }
            if (annotation.ArgsJson is not null)
            {
                try
                {
                    using var _ = JsonDocument.Parse(annotation.ArgsJson);
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException(
                        $"Annotation args_json must contain valid JSON (index {index}).",
                        nameof(annotations),
                        ex);
                }
            }
            candidateAnnotations[index] = annotation with { };
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var indexedFile = await _connection.QuerySingleOrDefaultAsync<RawProducingFile>(
                new CommandDefinition(
                    """
                    SELECT id AS FileId, path AS FilePath
                    FROM files
                    WHERE path = @FilePath;
                    """,
                    new { FilePath = filePath },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Declaration reconciliation could not resolve indexed file `{filePath}`.");

            // Temp tables avoid SQLite's parameter-count limit and give every cleanup statement
            // the exact same immutable keep/stale sets.
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TEMP TABLE IF NOT EXISTS reconcile_keep_keys(
                    key TEXT PRIMARY KEY);
                CREATE TEMP TABLE IF NOT EXISTS reconcile_stale_symbol_ids(
                    id INTEGER PRIMARY KEY);
                CREATE TEMP TABLE IF NOT EXISTS reconcile_preserved_annotation_flavors(
                    flavor TEXT PRIMARY KEY);
                DELETE FROM reconcile_keep_keys;
                DELETE FROM reconcile_stale_symbol_ids;
                DELETE FROM reconcile_preserved_annotation_flavors;
                """,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            if (orderedKeepKeys.Length > 0)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO reconcile_keep_keys(key) VALUES (@key);",
                    orderedKeepKeys.Select(key => new { key }),
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }
            if (orderedPreservedFlavors.Length > 0)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO reconcile_preserved_annotation_flavors(flavor)
                    VALUES (@flavor);
                    """,
                    orderedPreservedFlavors.Select(flavor => new { flavor }),
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            var retainedSymbols = (await _connection.QueryAsync<RawReconciledSymbol>(
                    new CommandDefinition(
                        """
                        SELECT canonical_key AS CanonicalKey,
                               id AS SymbolId,
                               file_id AS FileId
                        FROM symbols
                        WHERE canonical_key IN (
                            SELECT key FROM reconcile_keep_keys
                        );
                        """,
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false))
                .ToDictionary(symbol => symbol.CanonicalKey, StringComparer.Ordinal);
            foreach (var key in orderedKeepKeys)
            {
                if (!retainedSymbols.TryGetValue(key, out var symbol))
                {
                    throw new InvalidOperationException(
                        $"Declaration reconciliation could not resolve retained symbol `{key}`.");
                }
                if (symbol.FileId != indexedFile.FileId)
                {
                    throw new InvalidOperationException(
                        "Declaration reconciliation keep keys must belong to indexed file "
                        + $"`{indexedFile.FilePath}`; `{key}` is external.");
                }
            }

            var resolvedSymbols = new Dictionary<string, RawAnnotationSymbol>(
                StringComparer.Ordinal);
            foreach (var symbol in retainedSymbols.Values)
            {
                resolvedSymbols[symbol.CanonicalKey] =
                    new RawAnnotationSymbol(symbol.SymbolId, symbol.FileId);
            }

            async Task<RawAnnotationSymbol> ResolveSymbolAsync(string canonicalKey)
            {
                if (resolvedSymbols.TryGetValue(canonicalKey, out var cached))
                {
                    return cached;
                }
                var resolved = await _connection.QuerySingleOrDefaultAsync<RawAnnotationSymbol>(
                    new CommandDefinition(
                        """
                        SELECT id AS SymbolId, file_id AS FileId
                        FROM symbols
                        WHERE canonical_key = @CanonicalKey;
                        """,
                        new { CanonicalKey = canonicalKey },
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Annotation reconciliation could not resolve symbol `{canonicalKey}`.");
                resolvedSymbols[canonicalKey] = resolved;
                return resolved;
            }

            // Resolve the complete annotation candidate set before materializing or deleting the
            // stale set. Optional attribute definitions may live in another indexed file.
            var resolvedAnnotations =
                new List<ResolvedFileAnnotationFact>(candidateAnnotations.Length);
            for (var index = 0; index < candidateAnnotations.Length; index++)
            {
                var annotation = candidateAnnotations[index];
                var host = await ResolveSymbolAsync(annotation.SymbolCanonicalKey)
                    .ConfigureAwait(false);
                if (host.FileId != indexedFile.FileId)
                {
                    throw new InvalidOperationException(
                        "Annotation reconciliation hosts must belong to indexed file "
                        + $"`{indexedFile.FilePath}`; "
                        + $"`{annotation.SymbolCanonicalKey}` is external.");
                }

                long? attributeSymbolId = null;
                if (annotation.AttributeCanonicalKey is not null)
                {
                    var attribute = await ResolveSymbolAsync(
                            annotation.AttributeCanonicalKey)
                        .ConfigureAwait(false);
                    if (attribute.FileId == indexedFile.FileId
                        && !keepKeySet.Contains(annotation.AttributeCanonicalKey))
                    {
                        throw new InvalidOperationException(
                            "Annotation reconciliation cannot reference a same-file attribute "
                            + $"definition that is not retained: "
                            + $"`{annotation.AttributeCanonicalKey}`.");
                    }
                    attributeSymbolId = attribute.SymbolId;
                }

                resolvedAnnotations.Add(new ResolvedFileAnnotationFact(
                    annotation.SymbolCanonicalKey,
                    host.SymbolId,
                    annotation.Name,
                    annotation.FullName,
                    annotation.Flavor,
                    annotation.ArgsJson,
                    annotation.AttributeCanonicalKey,
                    attributeSymbolId));
            }

            var orderedAnnotations = resolvedAnnotations
                .Distinct()
                .OrderBy(annotation => annotation.SymbolCanonicalKey, StringComparer.Ordinal)
                .ThenBy(annotation => annotation.Flavor, StringComparer.Ordinal)
                .ThenBy(annotation => annotation.Name, StringComparer.Ordinal)
                .ThenBy(annotation => annotation.FullName, StringComparer.Ordinal)
                .ThenBy(annotation => annotation.ArgsJson ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(
                    annotation => annotation.AttributeCanonicalKey ?? string.Empty,
                    StringComparer.Ordinal)
                .ToArray();

            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO reconcile_stale_symbol_ids(id)
                SELECT id
                FROM symbols
                WHERE file_id = @FileId
                  AND canonical_key NOT IN (
                      SELECT key FROM reconcile_keep_keys
                  );

                DELETE FROM refs
                WHERE symbol_id IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );

                DELETE FROM edge_evidence
                WHERE src IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                ) OR dst IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );

                DELETE FROM edges
                WHERE src IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                ) OR dst IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );

                UPDATE annotations
                SET attribute_symbol_id = NULL
                WHERE attribute_symbol_id IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );

                DELETE FROM annotations
                WHERE symbol_id IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                )
                   OR (
                       symbol_id IN (
                           SELECT id FROM symbols WHERE file_id = @FileId
                       )
                       AND flavor NOT IN (
                           SELECT flavor
                           FROM reconcile_preserved_annotation_flavors
                       )
                   );

                DELETE FROM diagnostics
                WHERE file_id = @FileId
                   OR symbol_id IN (
                       SELECT id FROM reconcile_stale_symbol_ids
                   );

                DELETE FROM symbol_history
                WHERE symbol_id IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );

                DELETE FROM embedding_meta
                WHERE symbol_id IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );

                UPDATE symbols
                SET container_id = NULL
                WHERE container_id IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );
                """,
                new { indexedFile.FileId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            if (_vectorExtensionLoaded)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM symbol_embeddings
                    WHERE symbol_id IN (
                        SELECT id FROM reconcile_stale_symbol_ids
                    );
                    """,
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM symbols
                WHERE id IN (
                    SELECT id FROM reconcile_stale_symbol_ids
                );
                """,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            const string insertAnnotationSql = """
                INSERT INTO annotations(
                    symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)
                VALUES (
                    @SymbolId, @Name, @FullName, @Flavor, @ArgsJson, @AttributeSymbolId);
                """;
            foreach (var annotation in orderedAnnotations)
            {
                ct.ThrowIfCancellationRequested();
                await _connection.ExecuteAsync(new CommandDefinition(
                    insertAnnotationSql,
                    annotation,
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM reconcile_keep_keys;
                DELETE FROM reconcile_stale_symbol_ids;
                DELETE FROM reconcile_preserved_annotation_flavors;
                """,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
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
            const string deleteEdgeEvidenceSql = """
                DELETE FROM edge_evidence
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
            await _connection.ExecuteAsync(new CommandDefinition(deleteEdgeEvidenceSql, new { id = fileId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
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

    public async Task<bool> DeleteFileAsync(long fileId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var deleted = await DeleteFileCoreAsync(fileId, tx, ct).ConfigureAwait(false);
            tx.Commit();
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var fileId = await _connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM files WHERE path = @path;",
                new { path },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            if (fileId is null)
            {
                tx.Commit();
                return false;
            }

            var deleted = await DeleteFileCoreAsync(fileId.Value, tx, ct).ConfigureAwait(false);
            tx.Commit();
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<FileFactsReplacementResult> ReplaceFileFactsAsync(
        FileFactsReplacement replacement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.Path);
        ArgumentNullException.ThrowIfNull(replacement.ContentSha256);
        ArgumentNullException.ThrowIfNull(replacement.Symbols);
        ArgumentNullException.ThrowIfNull(replacement.Edges);
        ArgumentNullException.ThrowIfNull(replacement.Annotations);
        ArgumentNullException.ThrowIfNull(replacement.References);

        const string upsertFileSql = """
            INSERT INTO files(path, content_sha256, last_indexed_at, is_generated)
            VALUES (@path, @sha, @at, @gen)
            ON CONFLICT(path) DO UPDATE SET
                content_sha256 = excluded.content_sha256,
                last_indexed_at = excluded.last_indexed_at,
                is_generated   = excluded.is_generated
            RETURNING id;
            """;
        const string upsertSymbolSql = """
            INSERT INTO symbols(canonical_key, name, fqn, kind_name, file_id, start_line, start_col, end_line, end_col, signature, container_id, modifiers, accessibility, xml_summary, test_framework)
            VALUES (@Key, @Name, @Fqn, @Kind, @FileId, @StartLine, @StartCol, @EndLine, @EndCol, @Signature, NULL, @Modifiers, @Accessibility, @XmlSummary, NULL)
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
                container_id  = NULL,
                modifiers     = excluded.modifiers,
                accessibility = excluded.accessibility,
                xml_summary   = excluded.xml_summary
            RETURNING id;
            """;
        const string insertEdgeSql = """
            INSERT OR IGNORE INTO edges(src, dst, kind_name, payload)
            VALUES (@Src, @Dst, @Kind, @Payload);
            """;
        const string insertEvidenceSql = """
            INSERT OR IGNORE INTO edge_evidence(
                src, dst, kind_name, producing_file_id, file_path,
                start_line, start_col, end_line, end_col,
                confidence, producer, payload)
            VALUES (
                @Src, @Dst, @Kind, @ProducingFileId, @FilePath,
                @StartLine, @StartColumn, @EndLine, @EndColumn,
                @Confidence, @Producer, @Payload);
            """;
        const string syncLogicalPayloadSql = """
            UPDATE edges
            SET payload = (
                SELECT NULLIF(ev.payload, '')
                FROM edge_evidence ev
                WHERE ev.src = @Src
                  AND ev.dst = @Dst
                  AND ev.kind_name = @Kind
                ORDER BY ev.id
                LIMIT 1
            )
            WHERE src = @Src
              AND dst = @Dst
              AND kind_name = @Kind;
            """;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var fileId = await _connection.ExecuteScalarAsync<long>(new CommandDefinition(
                upsertFileSql,
                new
                {
                    path = replacement.Path,
                    sha = replacement.ContentSha256,
                    at = replacement.IndexedAt.ToUnixTimeMilliseconds(),
                    gen = replacement.IsGenerated ? 1 : 0,
                },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            // Remove only facts produced by the prior pass for this file, retaining logical
            // edges that still have evidence from another producer file.
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE edges AS edge
                SET payload = (
                    SELECT NULLIF(ev.payload, '')
                    FROM edge_evidence ev
                    WHERE ev.src = edge.src
                      AND ev.dst = edge.dst
                      AND ev.kind_name = edge.kind_name
                      AND ev.producing_file_id <> @id
                    ORDER BY ev.id
                    LIMIT 1
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edge.src
                      AND owned.dst = edge.dst
                      AND owned.kind_name = edge.kind_name
                      AND owned.producing_file_id = @id
                );
                DELETE FROM edge_evidence WHERE producing_file_id = @id;
                DELETE FROM refs WHERE file_id = @id;
                DELETE FROM edges
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM edge_evidence ev
                    WHERE ev.src = edges.src
                      AND ev.dst = edges.dst
                      AND ev.kind_name = edges.kind_name
                );
                """,
                new { id = fileId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            // Reconcile declarations and per-symbol facts against the complete new declaration
            // set. Temp id materialization lets every dependent-table cleanup use the exact same
            // stale declaration set, including surviving cross-file container/attribute links.
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TEMP TABLE IF NOT EXISTS keep_keys(key TEXT PRIMARY KEY);
                CREATE TEMP TABLE IF NOT EXISTS stale_symbol_ids(id INTEGER PRIMARY KEY);
                DELETE FROM keep_keys;
                DELETE FROM stale_symbol_ids;
                """,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            if (replacement.Symbols.Count > 0)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO keep_keys(key) VALUES (@key);",
                    replacement.Symbols.Select(symbol => new { key = symbol.CanonicalKey }),
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT OR IGNORE INTO stale_symbol_ids(id)
                SELECT id
                FROM symbols
                WHERE file_id = @id
                  AND canonical_key NOT IN (SELECT key FROM keep_keys);

                DELETE FROM refs
                WHERE symbol_id IN (SELECT id FROM stale_symbol_ids);

                DELETE FROM edge_evidence
                WHERE src IN (SELECT id FROM stale_symbol_ids)
                   OR dst IN (SELECT id FROM stale_symbol_ids);

                DELETE FROM edges
                WHERE src IN (SELECT id FROM stale_symbol_ids)
                   OR dst IN (SELECT id FROM stale_symbol_ids);

                UPDATE annotations
                SET attribute_symbol_id = NULL
                WHERE attribute_symbol_id IN (SELECT id FROM stale_symbol_ids);

                DELETE FROM annotations
                WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);

                DELETE FROM diagnostics
                WHERE file_id = @id
                   OR symbol_id IN (SELECT id FROM stale_symbol_ids);

                DELETE FROM symbol_history
                WHERE symbol_id IN (SELECT id FROM stale_symbol_ids);

                DELETE FROM embedding_meta
                WHERE symbol_id IN (SELECT id FROM stale_symbol_ids);

                UPDATE symbols
                SET container_id = NULL
                WHERE container_id IN (SELECT id FROM stale_symbol_ids);

                DELETE FROM keep_keys;
                """,
                new { id = fileId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            if (_vectorExtensionLoaded)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM symbol_embeddings
                    WHERE symbol_id IN (SELECT id FROM stale_symbol_ids);
                    """,
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM symbols
                WHERE id IN (SELECT id FROM stale_symbol_ids);

                DELETE FROM stale_symbol_ids;
                """,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            var symbolIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var symbol in replacement.Symbols)
            {
                var symbolId = await _connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    upsertSymbolSql,
                    new
                    {
                        Key = symbol.CanonicalKey,
                        symbol.Name,
                        symbol.Fqn,
                        Kind = symbol.Kind,
                        FileId = fileId,
                        symbol.StartLine,
                        StartCol = symbol.StartColumn,
                        symbol.EndLine,
                        EndCol = symbol.EndColumn,
                        symbol.Signature,
                        symbol.Modifiers,
                        symbol.Accessibility,
                        symbol.XmlSummary,
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                symbolIds[symbol.CanonicalKey] = symbolId;
            }

            // A canonical declaration may move between files while retaining its stable id.
            // Clear symbol-owned replaceable facts after the upserts as well so such a move does
            // not carry annotations or diagnostics emitted for the declaration's old location.
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM annotations
                WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);

                DELETE FROM diagnostics
                WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);
                """,
                new { id = fileId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            async Task<long> ResolveSymbolIdAsync(string canonicalKey)
            {
                if (symbolIds.TryGetValue(canonicalKey, out var cached)) return cached;
                var resolved = await _connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT id FROM symbols WHERE canonical_key = @canonicalKey;",
                    new { canonicalKey },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                if (resolved is null)
                {
                    throw new InvalidOperationException(
                        $"Atomic file replacement could not resolve symbol `{canonicalKey}`.");
                }
                symbolIds[canonicalKey] = resolved.Value;
                return resolved.Value;
            }

            foreach (var symbol in replacement.Symbols)
            {
                if (symbol.ContainerCanonicalKey is null) continue;
                var childId = await ResolveSymbolIdAsync(symbol.CanonicalKey).ConfigureAwait(false);
                var parentId = await ResolveSymbolIdAsync(symbol.ContainerCanonicalKey).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE symbols
                    SET container_id = @parentId
                    WHERE id = @childId
                      AND EXISTS (SELECT 1 FROM symbols WHERE id = @parentId);
                    """,
                    new { childId, parentId },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            var replacementSymbolsByKey = replacement.Symbols
                .GroupBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var fallbackBySource = new Dictionary<long, Evidence>();
            foreach (var edge in replacement.Edges)
            {
                var src = await ResolveSymbolIdAsync(edge.SourceCanonicalKey).ConfigureAwait(false);
                var dst = await ResolveSymbolIdAsync(edge.TargetCanonicalKey).ConfigureAwait(false);
                var logicalPayload = SerializeMetadata(edge.Metadata);
                await _connection.ExecuteAsync(new CommandDefinition(
                    insertEdgeSql,
                    new { Src = src, Dst = dst, Kind = edge.Kind, Payload = logicalPayload },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);

                Evidence evidence;
                if (edge.Evidence is null)
                {
                    if (!fallbackBySource.TryGetValue(src, out evidence!))
                    {
                        replacementSymbolsByKey.TryGetValue(
                            edge.SourceCanonicalKey,
                            out var source);
                        var startLine = Math.Max(1, source?.StartLine ?? 1);
                        var startColumn = Math.Max(1, source?.StartColumn ?? 1);
                        var endLine = Math.Max(startLine, source?.EndLine ?? startLine);
                        var endColumn = Math.Max(
                            endLine == startLine ? startColumn : 1,
                            source?.EndColumn ?? startColumn);
                        evidence = new Evidence(
                            fileId,
                            new SourceLocation(
                                replacement.Path,
                                startLine,
                                startColumn,
                                endLine,
                                endColumn),
                            EvidenceConfidence.Inferred,
                            "legacy-declaration");
                        fallbackBySource[src] = evidence;
                    }
                }
                else
                {
                    evidence = new Evidence(
                        fileId,
                        edge.Evidence.Location,
                        edge.Evidence.Confidence,
                        edge.Evidence.Producer,
                        edge.Evidence.Metadata);
                }

                ValidateEvidence(evidence);
                var producerPath = await _connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT path FROM files WHERE id = @ProducingFileId;",
                    new { evidence.ProducingFileId },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Evidence producing file {evidence.ProducingFileId} is not indexed.");
                if (!PathsEquivalent(producerPath, evidence.Location.FilePath))
                {
                    throw new InvalidOperationException(
                        $"Evidence path does not match producing file {evidence.ProducingFileId}.");
                }

                await _connection.ExecuteAsync(new CommandDefinition(
                    insertEvidenceSql,
                    new
                    {
                        Src = src,
                        Dst = dst,
                        Kind = edge.Kind,
                        evidence.ProducingFileId,
                        evidence.Location.FilePath,
                        evidence.Location.StartLine,
                        evidence.Location.StartColumn,
                        evidence.Location.EndLine,
                        evidence.Location.EndColumn,
                        Confidence = (int)evidence.Confidence,
                        evidence.Producer,
                        Payload = SerializeMetadata(evidence.Metadata ?? edge.Metadata) ?? string.Empty,
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    syncLogicalPayloadSql,
                    new { Src = src, Dst = dst, Kind = edge.Kind },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            foreach (var annotation in replacement.Annotations)
            {
                if (!replacementSymbolsByKey.ContainsKey(annotation.SymbolCanonicalKey))
                {
                    throw new InvalidOperationException(
                        "Atomic file replacement annotations must target a symbol declared "
                        + $"by the same file; `{annotation.SymbolCanonicalKey}` is external.");
                }
                var symbolId =
                    await ResolveSymbolIdAsync(annotation.SymbolCanonicalKey).ConfigureAwait(false);
                long? attributeSymbolId = annotation.AttributeCanonicalKey is null
                    ? null
                    : await ResolveSymbolIdAsync(annotation.AttributeCanonicalKey).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO annotations(symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)
                    VALUES (@SymbolId, @Name, @FullName, @Flavor, @ArgsJson, @AttributeSymbolId);
                    """,
                    new
                    {
                        SymbolId = symbolId,
                        annotation.Name,
                        annotation.FullName,
                        annotation.Flavor,
                        annotation.ArgsJson,
                        AttributeSymbolId = attributeSymbolId,
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            foreach (var reference in replacement.References)
            {
                var symbolId =
                    await ResolveSymbolIdAsync(reference.TargetCanonicalKey).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO refs(symbol_id, file_id, line, col, kind)
                    VALUES (@SymbolId, @FileId, @Line, @Col, @Kind);
                    """,
                    new
                    {
                        SymbolId = symbolId,
                        FileId = fileId,
                        reference.Line,
                        Col = reference.Column,
                        Kind = (int)reference.Kind,
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            tx.Commit();
            var declaredIds = replacement.Symbols
                .Select(symbol => symbol.CanonicalKey)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(key => key, key => symbolIds[key], StringComparer.Ordinal);
            return new FileFactsReplacementResult(fileId, declaredIds);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> DeleteFileCoreAsync(
        long fileId,
        SqliteTransaction tx,
        CancellationToken ct)
    {
        var parameters = new { id = fileId };

        // Preserve the compatibility payload of any logical edge that also has evidence from a
        // surviving file. Edges whose final evidence belongs to this file are deleted below.
        await _connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE edges AS edge
            SET payload = (
                SELECT NULLIF(ev.payload, '')
                FROM edge_evidence ev
                WHERE ev.src = edge.src
                  AND ev.dst = edge.dst
                  AND ev.kind_name = edge.kind_name
                  AND ev.producing_file_id <> @id
                ORDER BY ev.id
                LIMIT 1
            )
            WHERE EXISTS (
                SELECT 1
                FROM edge_evidence owned
                WHERE owned.src = edge.src
                  AND owned.dst = edge.dst
                  AND owned.kind_name = edge.kind_name
                  AND owned.producing_file_id = @id
            );
            """,
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        // Remove logical edges whose endpoint is disappearing, plus edges whose last supporting
        // occurrence was produced by this file. Do this before deleting evidence so the latter
        // predicate can distinguish touched edges from unrelated legacy rows.
        await _connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM edges
            WHERE src IN (SELECT id FROM symbols WHERE file_id = @id)
               OR dst IN (SELECT id FROM symbols WHERE file_id = @id)
               OR (
                    EXISTS (
                        SELECT 1
                        FROM edge_evidence owned
                        WHERE owned.src = edges.src
                          AND owned.dst = edges.dst
                          AND owned.kind_name = edges.kind_name
                          AND owned.producing_file_id = @id
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM edge_evidence survivor
                        WHERE survivor.src = edges.src
                          AND survivor.dst = edges.dst
                          AND survivor.kind_name = edges.kind_name
                          AND survivor.producing_file_id <> @id
                    )
               );
            """,
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM edge_evidence
            WHERE producing_file_id = @id
               OR src IN (SELECT id FROM symbols WHERE file_id = @id)
               OR dst IN (SELECT id FROM symbols WHERE file_id = @id);
            """,
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM refs
            WHERE file_id = @id
               OR symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);
            """,
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        // An annotation belongs to its host symbol, not to its optional attribute-definition
        // symbol. Keep annotations on surviving hosts and sever only the disappearing join.
        await _connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE annotations
            SET attribute_symbol_id = NULL
            WHERE attribute_symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);

            DELETE FROM annotations
            WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);
            """,
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM diagnostics
            WHERE file_id = @id
               OR symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);

            DELETE FROM symbol_history
            WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);

            DELETE FROM embedding_meta
            WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);

            UPDATE symbols
            SET container_id = NULL
            WHERE container_id IN (SELECT id FROM symbols WHERE file_id = @id);
            """,
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        // The vec0 table is optional and only queryable on a connection that loaded the native
        // extension. Its metadata table above exists unconditionally.
        if (_vectorExtensionLoaded)
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM symbol_embeddings
                WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @id);
                """,
                parameters,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM symbols WHERE file_id = @id;",
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);
        var deletedFiles = await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM files WHERE id = @id;",
            parameters,
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);
        return deletedFiles > 0;
    }

    public async Task<long> UpsertSymbolAsync(string canonicalKey, Symbol symbol, CancellationToken ct = default)
    {
        // Stable id by canonical_key. On conflict we update the location/signature/etc but the
        // integer id is preserved, so refs/edges from other files that point to this symbol stay
        // correct across edits. container_id is reset to the caller's value on conflict, then
        // reconciled by BatchUpdateContainerIdsAsync after pass-1 inserts every symbol. Resetting
        // prevents a declaration moved to the top level (or carrying an unresolved container key)
        // from retaining a stale parent. test_framework is set separately by
        // UpdateTestFrameworksAsync after attribute walk, so the conflict path does NOT clobber an
        // already-detected framework on re-upserts of the same symbol.
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
                container_id  = excluded.container_id,
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
        const string sql = """
            UPDATE symbols
            SET container_id = @ParentId
            WHERE id = @ChildId
              AND EXISTS (SELECT 1 FROM symbols WHERE id = @ParentId);
            """;
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
        const string insertEdgeSql = """
            INSERT OR IGNORE INTO edges(src, dst, kind_name, payload)
            VALUES (@Src, @Dst, @Kind, @Payload);
            """;
        const string insertEvidenceSql = """
            INSERT OR IGNORE INTO edge_evidence(
                src, dst, kind_name, producing_file_id, file_path,
                start_line, start_col, end_line, end_col,
                confidence, producer, payload)
            VALUES (
                @Src, @Dst, @Kind, @ProducingFileId, @FilePath,
                @StartLine, @StartColumn, @EndLine, @EndColumn,
                @Confidence, @Producer, @Payload);
            """;
        const string fallbackEvidenceSql = """
            SELECT
                s.file_id AS ProducingFileId,
                f.path AS FilePath,
                s.start_line AS StartLine,
                s.start_col AS StartColumn,
                s.end_line AS EndLine,
                s.end_col AS EndColumn
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.id = @SourceSymbolId;
            """;
        const string producerFilePathSql = """
            SELECT path
            FROM files
            WHERE id = @ProducingFileId;
            """;
        const string syncLogicalPayloadSql = """
            UPDATE edges
            SET payload = (
                SELECT NULLIF(ev.payload, '')
                FROM edge_evidence ev
                WHERE ev.src = @Src
                  AND ev.dst = @Dst
                  AND ev.kind_name = @Kind
                ORDER BY ev.id
                LIMIT 1
            )
            WHERE src = @Src
              AND dst = @Dst
              AND kind_name = @Kind;
            """;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var fallbackBySource = new Dictionary<long, Evidence>();
            var producerPathByFileId = new Dictionary<long, string>();
            foreach (var e in edges)
            {
                var logicalPayload = SerializeMetadata(e.Metadata);
                await _connection.ExecuteAsync(new CommandDefinition(insertEdgeSql, new
                {
                    e.Src,
                    e.Dst,
                    Kind = e.Kind,
                    Payload = logicalPayload,
                }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

                var evidence = e.Evidence;
                if (evidence is null)
                {
                    if (!fallbackBySource.TryGetValue(e.Src, out evidence))
                    {
                        var source = await _connection.QuerySingleOrDefaultAsync<RawEvidenceSource>(
                            new CommandDefinition(
                                fallbackEvidenceSql,
                                new { SourceSymbolId = e.Src },
                                transaction: tx,
                                cancellationToken: ct)).ConfigureAwait(false);
                        if (source is null)
                        {
                            throw new InvalidOperationException(
                                $"Cannot create evidence for edge {e.Src} -[{e.Kind}]-> {e.Dst}: source symbol is missing.");
                        }

                        var startLine = Math.Max(1L, source.StartLine);
                        var startColumn = Math.Max(1L, source.StartColumn);
                        var endLine = Math.Max(startLine, source.EndLine);
                        var endColumn = Math.Max(
                            endLine == startLine ? startColumn : 1L,
                            source.EndColumn);
                        evidence = new Evidence(
                            source.ProducingFileId,
                            new SourceLocation(
                                source.FilePath,
                                checked((int)startLine),
                                checked((int)startColumn),
                                checked((int)endLine),
                                checked((int)endColumn)),
                            EvidenceConfidence.Inferred,
                            "legacy-declaration",
                            Metadata: null);
                        fallbackBySource[e.Src] = evidence;
                        producerPathByFileId[evidence.ProducingFileId] =
                            evidence.Location.FilePath;
                    }
                }
                ValidateEvidence(evidence);
                if (!producerPathByFileId.TryGetValue(
                        evidence.ProducingFileId,
                        out var producerPath))
                {
                    producerPath = await _connection.ExecuteScalarAsync<string?>(
                        new CommandDefinition(
                            producerFilePathSql,
                            new { evidence.ProducingFileId },
                            transaction: tx,
                            cancellationToken: ct)).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"Evidence producing file {evidence.ProducingFileId} is not indexed.");
                    producerPathByFileId[evidence.ProducingFileId] = producerPath;
                }
                if (!PathsEquivalent(producerPath, evidence.Location.FilePath))
                {
                    throw new InvalidOperationException(
                        $"Evidence path does not match producing file {evidence.ProducingFileId}.");
                }

                await _connection.ExecuteAsync(new CommandDefinition(insertEvidenceSql, new
                {
                    e.Src,
                    e.Dst,
                    Kind = e.Kind,
                    evidence.ProducingFileId,
                    evidence.Location.FilePath,
                    evidence.Location.StartLine,
                    evidence.Location.StartColumn,
                    evidence.Location.EndLine,
                    evidence.Location.EndColumn,
                    Confidence = (int)evidence.Confidence,
                    evidence.Producer,
                    Payload = SerializeMetadata(evidence.Metadata ?? e.Metadata) ?? string.Empty,
                }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
                await _connection.ExecuteAsync(new CommandDefinition(
                    syncLogicalPayloadSql,
                    new { e.Src, e.Dst, Kind = e.Kind },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<Evidence>> ListEdgeEvidenceAsync(
        long sourceSymbolId,
        long targetSymbolId,
        string edgeKind,
        int limit = 100,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeKind);

        const string sql = """
            SELECT
                producing_file_id AS ProducingFileId,
                file_path AS FilePath,
                start_line AS StartLine,
                start_col AS StartColumn,
                end_line AS EndLine,
                end_col AS EndColumn,
                confidence AS Confidence,
                producer AS Producer,
                payload AS Payload
            FROM edge_evidence
            WHERE src = @SourceSymbolId
              AND dst = @TargetSymbolId
              AND kind_name = @EdgeKind
            ORDER BY file_path, start_line, start_col, id
            LIMIT @Limit;
            """;
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawEvidenceRow>(new CommandDefinition(
            sql,
            new
            {
                SourceSymbolId = sourceSymbolId,
                TargetSymbolId = targetSymbolId,
                EdgeKind = edgeKind,
                Limit = limit,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(row => new Evidence(
                row.ProducingFileId,
                new SourceLocation(
                    row.FilePath,
                    checked((int)row.StartLine),
                    checked((int)row.StartColumn),
                    checked((int)row.EndLine),
                    checked((int)row.EndColumn)),
                row.Confidence is >= (long)EvidenceConfidence.Inferred
                    and <= (long)EvidenceConfidence.Exact
                    ? (EvidenceConfidence)row.Confidence
                    : EvidenceConfidence.Inferred,
                row.Producer,
                DeserializeMetadata(row.Payload)))
            .ToList();
    }

    public async Task<bool> HasEdgeEvidenceByProducerAsync(
        string producer,
        CancellationToken ct = default)
    {
        const int maximumProducerCharacters = 256;

        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        if (producer.Length > maximumProducerCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(producer),
                producer.Length,
                $"Evidence producer cannot exceed {maximumProducerCharacters} characters.");
        }

        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM edge_evidence
                WHERE producer = @producer
                LIMIT 1
            );
            """;
        var exists = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql,
                new { producer },
                cancellationToken: ct)).ConfigureAwait(false);
        return exists != 0;
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            canonical[pair.Key] = pair.Value;
        }
        return JsonSerializer.Serialize(canonical);
    }

    private static IReadOnlyDictionary<string, string>? DeserializeMetadata(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(payload);
        }
        catch (JsonException)
        {
            // Evidence locations remain useful if a manually edited/corrupt legacy payload cannot
            // be decoded. Integrity tooling can report the malformed JSON independently.
            return null;
        }
    }

    private static bool PathsEquivalent(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(
                normalizedLeft,
                normalizedRight,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    private static void ValidateEvidence(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.ProducingFileId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.ProducingFileId,
                "Producing file id must be positive.");
        }
        if (string.IsNullOrWhiteSpace(evidence.Location.FilePath))
        {
            throw new ArgumentException("Evidence file path is required.", nameof(evidence));
        }
        if (evidence.Location.StartLine <= 0
            || evidence.Location.StartColumn <= 0
            || evidence.Location.EndLine < evidence.Location.StartLine
            || evidence.Location.EndColumn <= 0
            || (evidence.Location.EndLine == evidence.Location.StartLine
                && evidence.Location.EndColumn < evidence.Location.StartColumn))
        {
            throw new ArgumentException("Evidence must use a valid 1-based source range.", nameof(evidence));
        }
        if (!Enum.IsDefined(evidence.Confidence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.Confidence,
                "Evidence confidence is not defined.");
        }
        if (string.IsNullOrWhiteSpace(evidence.Producer))
        {
            throw new ArgumentException("Evidence producer is required.", nameof(evidence));
        }
    }

    public async Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(string query, string? filePathHint = null, int limit = 25, CancellationToken ct = default)
    {
        // Strategy: canonical key, exact FQN, exact name, FQN suffix, name prefix, fuzzy.
        // Optionally restrict to a file-path hint.
        const string sql = """
            WITH ranked AS (
                SELECT s.id, s.name, s.fqn, s.kind_name, f.path, s.start_line, s.start_col, s.end_line, s.end_col, s.signature,
                    s.modifiers, s.accessibility, s.xml_summary, f.is_generated, s.test_framework, s.canonical_key,
                    CASE
                        WHEN s.canonical_key = @q THEN 0
                        WHEN s.fqn = @q THEN 1
                        WHEN s.name = @q THEN 2
                        WHEN s.fqn LIKE '%' || @q THEN 3
                        WHEN s.name LIKE @q || '%' COLLATE NOCASE THEN 4
                        WHEN s.fqn LIKE '%' || @q || '%' COLLATE NOCASE THEN 5
                        ELSE 99
                    END AS rank
                FROM symbols s
                JOIN files   f ON f.id = s.file_id
                WHERE (@hint IS NULL OR f.path LIKE '%' || @hint || '%')
                  AND (
                    s.canonical_key = @q
                    OR s.name = @q
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
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawSymbolHit>(new CommandDefinition(
            sql, new { q = query, hint = filePathHint, limit }, cancellationToken: ct)).ConfigureAwait(false);
        var hits = rows.Select(r => r.ToHit()).ToList();
        var canonical = hits.Where(hit =>
            string.Equals(hit.CanonicalKey, query, StringComparison.Ordinal)).ToList();
        if (canonical.Count > 0) return canonical;
        var exactFqn = hits.Where(hit =>
            string.Equals(hit.Fqn, query, StringComparison.Ordinal)).ToList();
        return exactFqn.Count > 0 ? exactFqn : hits;
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

    public async Task<IReadOnlyList<EdgeTraversalHit>> ListAuditableInboundEdgesAsync(
        long symbolId,
        int limit = 50,
        string? edgeKind = "calls",
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var kindClause = edgeKind is null ? "" : "AND e.kind_name = @kind";
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath,
                   s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility,
                   s.xml_summary AS XmlSummary, f.is_generated AS IsGenerated,
                   s.test_framework AS TestFramework, s.canonical_key AS CanonicalKey,
                   e.kind_name AS Relation, e.payload AS PayloadJson
            FROM edges e
            JOIN symbols s ON s.id = e.src
            JOIN files   f ON f.id = s.file_id
            WHERE e.dst = @id {kindClause}
              AND EXISTS (
                  SELECT 1
                  FROM edge_evidence ee
                  WHERE ee.src = e.src
                    AND ee.dst = e.dst
                    AND ee.kind_name = e.kind_name
              )
            ORDER BY e.kind_name, f.path, s.start_line, s.start_col, s.id
            LIMIT @limit;
            """;
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawAuditableEdgeHit>(new CommandDefinition(
            sql,
            new { id = symbolId, limit, kind = edgeKind },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(row => row.ToHit()).ToList();
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

    public async Task<IReadOnlyList<EdgeTraversalHit>> ListAuditableOutboundEdgesAsync(
        long symbolId,
        int limit = 50,
        string? edgeKind = "calls",
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var kindClause = edgeKind is null ? "" : "AND e.kind_name = @kind";
        var sql = $"""
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath,
                   s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility,
                   s.xml_summary AS XmlSummary, f.is_generated AS IsGenerated,
                   s.test_framework AS TestFramework, s.canonical_key AS CanonicalKey,
                   e.kind_name AS Relation, e.payload AS PayloadJson
            FROM edges e
            JOIN symbols s ON s.id = e.dst
            JOIN files   f ON f.id = s.file_id
            WHERE e.src = @id {kindClause}
              AND EXISTS (
                  SELECT 1
                  FROM edge_evidence ee
                  WHERE ee.src = e.src
                    AND ee.dst = e.dst
                    AND ee.kind_name = e.kind_name
              )
            ORDER BY e.kind_name, f.path, s.start_line, s.start_col, s.id
            LIMIT @limit;
            """;
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawAuditableEdgeHit>(new CommandDefinition(
            sql,
            new { id = symbolId, limit, kind = edgeKind },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(row => row.ToHit()).ToList();
    }

    public async Task<IReadOnlyList<EdgeTraversalHit>> ListAuditableOutboundEdgesByKindsAsync(
        long symbolId,
        IReadOnlyCollection<string> edgeKinds,
        int limit = 50,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edgeKinds);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1024);
        if (edgeKinds.Count is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edgeKinds),
                edgeKinds.Count,
                "Between 1 and 32 edge kinds are required.");
        }

        var normalizedKinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var edgeKind in edgeKinds)
        {
            KebabCaseValidator.Validate(edgeKind, nameof(edgeKinds));
            normalizedKinds.Add(edgeKind);
        }

        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath,
                   s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility,
                   s.xml_summary AS XmlSummary, f.is_generated AS IsGenerated,
                   s.test_framework AS TestFramework, s.canonical_key AS CanonicalKey,
                   e.kind_name AS Relation, e.payload AS PayloadJson
            FROM edges e
            JOIN symbols s ON s.id = e.dst
            JOIN files   f ON f.id = s.file_id
            WHERE e.src = @id
              AND e.kind_name IN @kinds
              AND EXISTS (
                  SELECT 1
                  FROM edge_evidence ee
                  WHERE ee.src = e.src
                    AND ee.dst = e.dst
                    AND ee.kind_name = e.kind_name
              )
            ORDER BY e.kind_name, f.path, s.start_line, s.start_col, s.id
            LIMIT @limit;
            """;
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawAuditableEdgeHit>(new CommandDefinition(
            sql,
            new { id = symbolId, kinds = normalizedKinds.ToArray(), limit },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(row => row.ToHit()).ToList();
    }

    public Task<IReadOnlyList<SymbolHit>> ListImplementationsAsync(long symbolId, int limit = 50, CancellationToken ct = default)
        => ListCallersAsync(symbolId, limit, "implements-member", ct);

    public Task<IReadOnlyList<SymbolHit>> ListUsersOfTypeAsync(long symbolId, int limit = 50, CancellationToken ct = default)
        => ListCallersAsync(symbolId, limit, "uses-type", ct);

    public Task<IReadOnlyList<EdgeWithPayload>> FindDataBindingsAsync(
        string? targetCanonicalKey,
        string? sourceCanonicalKey,
        string? pathContains,
        string? modeExact,
        string? converterExact,
        int limit = 50,
        CancellationToken ct = default,
        string? elementNameExact = null)
    {
        // path uses INSTR for substring match; mode/converter are exact-equality on json_extract.
        // The kebab JSON keys ("path", "mode", "converter") mirror SDK PayloadKeys constants;
        // Storage stays SDK-independent so the literals live here.
        var pathClause = pathContains is null ? "" : "AND INSTR(json_extract(e.payload, '$.path'), @pathContains) > 0";
        var modeClause = modeExact is null ? "" : "AND json_extract(e.payload, '$.mode') = @modeExact";
        var converterClause = converterExact is null ? "" : "AND json_extract(e.payload, '$.converter') = @converterExact";
        var elementNameClause = elementNameExact is null
            ? ""
            : "AND json_extract(e.payload, '$.element-name') = @elementNameExact";
        return QueryEdgesWithPayloadAsync(
            edgeKind: EdgeKindBindsPath,
            targetCanonicalKey: targetCanonicalKey,
            sourceCanonicalKey: sourceCanonicalKey,
            extraWhere:
                $"{pathClause} {modeClause} {converterClause} {elementNameClause}",
            extraParameters: new
            {
                pathContains,
                modeExact,
                converterExact,
                elementNameExact,
            },
            limit: limit,
            ct: ct);
    }

    public Task<IReadOnlyList<EdgeWithPayload>> FindEventHandlersAsync(
        string? handlerCanonicalKey,
        string? eventExact,
        string? elementCanonicalKey,
        string? commandExact,
        int limit = 50,
        CancellationToken ct = default)
    {
        // handler maps to dst (the resolved C# method symbol the edge wires to); element maps to
        // src (the XAML element that owns the event attribute). Mirrors find_data_bindings'
        // (target = dst, source = src) convention.
        var eventClause = eventExact is null ? "" : "AND json_extract(e.payload, '$.event') = @eventExact";
        var commandClause = commandExact is null ? "" : "AND json_extract(e.payload, '$.command') = @commandExact";
        return QueryEdgesWithPayloadAsync(
            edgeKind: EdgeKindHandlesEvent,
            targetCanonicalKey: handlerCanonicalKey,
            sourceCanonicalKey: elementCanonicalKey,
            extraWhere: $"{eventClause} {commandClause}",
            extraParameters: new { eventExact, commandExact },
            limit: limit,
            ct: ct);
    }

    public Task<IReadOnlyList<EdgeWithPayload>> FindResourceReferencesAsync(
        string edgeKind,
        string? keyExact,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (edgeKind is not (EdgeKindUsesResource or EdgeKindAppliesStyle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(edgeKind),
                edgeKind,
                $"Only '{EdgeKindUsesResource}' and '{EdgeKindAppliesStyle}' are supported.");
        }

        var keyClause = keyExact is null
            ? ""
            : "AND json_extract(e.payload, '$.key') = @keyExact";
        return QueryEdgesWithPayloadAsync(
            edgeKind: edgeKind,
            targetCanonicalKey: null,
            sourceCanonicalKey: null,
            extraWhere: keyClause,
            extraParameters: new { keyExact },
            limit: limit,
            ct: ct);
    }

    // Edge kind name constants kept local — the Storage layer does not depend on the SDK and
    // these strings already appear in indexer code and tools as the well-known kebab vocabulary.
    private const string EdgeKindBindsPath = "binds-path";
    private const string EdgeKindHandlesEvent = "handles-event";
    private const string EdgeKindUsesResource = "uses-resource";
    private const string EdgeKindAppliesStyle = "applies-style";

    /// <summary>
    /// Two-sided edge walk projecting both endpoints + payload. Used by the payload-aware
    /// <c>find_*</c> helpers; <paramref name="extraWhere"/> is interpolated verbatim — every
    /// caller must build it from a fixed set of clauses (no user strings).
    /// </summary>
    private async Task<IReadOnlyList<EdgeWithPayload>> QueryEdgesWithPayloadAsync(
        string edgeKind,
        string? targetCanonicalKey,
        string? sourceCanonicalKey,
        string extraWhere,
        object extraParameters,
        int limit,
        CancellationToken ct)
    {
        var targetClause = targetCanonicalKey is null ? "" : "AND dst_s.canonical_key = @targetCanonicalKey";
        var sourceClause = sourceCanonicalKey is null ? "" : "AND src_s.canonical_key = @sourceCanonicalKey";

        var sql = $"""
            SELECT
                src_s.id        AS Src_Id,
                src_s.name      AS Src_Name,
                src_s.fqn       AS Src_Fqn,
                src_s.kind_name AS Src_Kind,
                src_f.path      AS Src_FilePath,
                src_s.start_line AS Src_StartLine,
                src_s.start_col  AS Src_StartCol,
                src_s.end_line   AS Src_EndLine,
                src_s.end_col    AS Src_EndCol,
                src_s.signature  AS Src_Signature,
                src_s.modifiers  AS Src_Modifiers,
                src_s.accessibility AS Src_Accessibility,
                src_s.xml_summary   AS Src_XmlSummary,
                src_f.is_generated  AS Src_IsGenerated,
                src_s.test_framework AS Src_TestFramework,
                src_s.canonical_key  AS Src_CanonicalKey,
                dst_s.id        AS Dst_Id,
                dst_s.name      AS Dst_Name,
                dst_s.fqn       AS Dst_Fqn,
                dst_s.kind_name AS Dst_Kind,
                dst_f.path      AS Dst_FilePath,
                dst_s.start_line AS Dst_StartLine,
                dst_s.start_col  AS Dst_StartCol,
                dst_s.end_line   AS Dst_EndLine,
                dst_s.end_col    AS Dst_EndCol,
                dst_s.signature  AS Dst_Signature,
                dst_s.modifiers  AS Dst_Modifiers,
                dst_s.accessibility AS Dst_Accessibility,
                dst_s.xml_summary   AS Dst_XmlSummary,
                dst_f.is_generated  AS Dst_IsGenerated,
                dst_s.test_framework AS Dst_TestFramework,
                dst_s.canonical_key  AS Dst_CanonicalKey,
                e.payload AS PayloadJson
            FROM edges e
            JOIN symbols src_s ON src_s.id = e.src
            JOIN files   src_f ON src_f.id = src_s.file_id
            JOIN symbols dst_s ON dst_s.id = e.dst
            JOIN files   dst_f ON dst_f.id = dst_s.file_id
            WHERE e.kind_name = @edgeKind
              {targetClause}
              {sourceClause}
              {extraWhere}
            ORDER BY src_f.path, src_s.start_line
            LIMIT @limit;
            """;

        // Merge the kind/key/limit parameters with the caller's payload-filter parameters in a
        // single DynamicParameters bag so Dapper binds them all from one source.
        var parameters = new DynamicParameters();
        parameters.Add("edgeKind", edgeKind);
        parameters.Add("targetCanonicalKey", targetCanonicalKey);
        parameters.Add("sourceCanonicalKey", sourceCanonicalKey);
        parameters.Add("limit", limit);
        parameters.AddDynamicParams(extraParameters);

        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawEdgeWithPayload>(new CommandDefinition(
            sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToEdgeWithPayload()).ToList();
    }

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

    public async Task<SymbolHit?> GetSymbolByCanonicalKeyAsync(
        string canonicalKey,
        CancellationToken ct = default)
    {
        CanonicalKeyValidator.Validate(canonicalKey, nameof(canonicalKey));

        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath,
                   s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility,
                   s.xml_summary AS XmlSummary, f.is_generated AS IsGenerated,
                   s.test_framework AS TestFramework, s.canonical_key AS CanonicalKey
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.canonical_key = @canonicalKey;
            """;
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var row = await reader.QueryFirstOrDefaultAsync<RawSymbolHit>(
            new CommandDefinition(
                sql,
                new { canonicalKey },
                cancellationToken: ct)).ConfigureAwait(false);
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
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawSymbolHit>(new CommandDefinition(
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

    private sealed record RawAuditableEdgeHit(
        long Id,
        string Name,
        string Fqn,
        string Kind,
        string FilePath,
        long StartLine,
        long StartCol,
        long EndLine,
        long EndCol,
        string? Signature,
        string? Modifiers,
        long Accessibility,
        string? XmlSummary,
        long IsGenerated,
        string? TestFramework,
        string? CanonicalKey,
        string Relation,
        string? PayloadJson)
    {
        public EdgeTraversalHit ToHit() =>
            new(
                new SymbolHit(
                    Id,
                    Name,
                    Fqn,
                    Kind,
                    FilePath,
                    (int)StartLine,
                    (int)StartCol,
                    (int)EndLine,
                    (int)EndCol,
                    Signature,
                    Modifiers,
                    (int)Accessibility,
                    XmlSummary,
                    IsGenerated != 0,
                    TestFramework,
                    CanonicalKey,
                    PayloadJson),
                Relation,
                PayloadJson);
    }

    private sealed record RawEvidenceSource(
        long ProducingFileId,
        string FilePath,
        long StartLine,
        long StartColumn,
        long EndLine,
        long EndColumn);

    private sealed record RawProducingFile(long FileId, string FilePath);

    private sealed record RawAnnotationSymbol(long SymbolId, long FileId);

    private sealed record RawReconciledSymbol(
        string CanonicalKey,
        long SymbolId,
        long FileId);

    private sealed record ResolvedFileAnnotationFact(
        string SymbolCanonicalKey,
        long SymbolId,
        string Name,
        string FullName,
        string Flavor,
        string? ArgsJson,
        string? AttributeCanonicalKey,
        long? AttributeSymbolId);

    private sealed record ResolvedProducerEdgeEvidenceFact(
        string SourceCanonicalKey,
        string TargetCanonicalKey,
        long Src,
        long Dst,
        string Kind,
        string? LogicalPayload,
        string FilePath,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        EvidenceConfidence Confidence,
        string EvidencePayload);

    private sealed record ProducerEdgeEvidenceIdentity(
        long Src,
        long Dst,
        string Kind,
        string FilePath,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        EvidenceConfidence Confidence,
        string EvidencePayload);

    private sealed record RawEvidenceRow(
        long ProducingFileId,
        string FilePath,
        long StartLine,
        long StartColumn,
        long EndLine,
        long EndColumn,
        long Confidence,
        string Producer,
        string? Payload);

    /// <summary>
    /// Two-sided edge projection used by <see cref="FindDataBindingsAsync"/> /
    /// <see cref="FindEventHandlersAsync"/>: full SymbolHit columns for both endpoints
    /// (Src_*, Dst_*) plus the originating <c>edges.payload</c> column. Constructor parameter
    /// order matches the SELECT column order in <see cref="QueryEdgesWithPayloadAsync"/>.
    /// </summary>
    private sealed record RawEdgeWithPayload(
        long Src_Id, string Src_Name, string Src_Fqn, string Src_Kind, string Src_FilePath,
        long Src_StartLine, long Src_StartCol, long Src_EndLine, long Src_EndCol, string? Src_Signature,
        string? Src_Modifiers, long Src_Accessibility, string? Src_XmlSummary,
        long Src_IsGenerated, string? Src_TestFramework, string? Src_CanonicalKey,
        long Dst_Id, string Dst_Name, string Dst_Fqn, string Dst_Kind, string Dst_FilePath,
        long Dst_StartLine, long Dst_StartCol, long Dst_EndLine, long Dst_EndCol, string? Dst_Signature,
        string? Dst_Modifiers, long Dst_Accessibility, string? Dst_XmlSummary,
        long Dst_IsGenerated, string? Dst_TestFramework, string? Dst_CanonicalKey,
        string? PayloadJson)
    {
        public EdgeWithPayload ToEdgeWithPayload() => new(
            Source: new SymbolHit(
                Src_Id, Src_Name, Src_Fqn, Src_Kind, Src_FilePath,
                (int)Src_StartLine, (int)Src_StartCol, (int)Src_EndLine, (int)Src_EndCol, Src_Signature,
                Src_Modifiers, (int)Src_Accessibility, Src_XmlSummary, Src_IsGenerated != 0,
                Src_TestFramework, Src_CanonicalKey),
            Target: new SymbolHit(
                Dst_Id, Dst_Name, Dst_Fqn, Dst_Kind, Dst_FilePath,
                (int)Dst_StartLine, (int)Dst_StartCol, (int)Dst_EndLine, (int)Dst_EndCol, Dst_Signature,
                Dst_Modifiers, (int)Dst_Accessibility, Dst_XmlSummary, Dst_IsGenerated != 0,
                Dst_TestFramework, Dst_CanonicalKey),
            PayloadJson: PayloadJson);
    }

    private sealed record RawReferenceHit(long Id, long SymbolId, string FilePath, long Line, long Col, long Kind, long IsGenerated)
    {
        public ReferenceHit ToHit() => new(Id, SymbolId, FilePath, (int)Line, (int)Col, (Core.ReferenceKind)Kind, IsGenerated != 0);
    }

    public async Task<IReadOnlyList<SymbolKeyRow>> GetAllSymbolKeysAsync(CancellationToken ct = default)
    {
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<SymbolKeyRow>(new CommandDefinition(
            "SELECT canonical_key AS CanonicalKey, id AS Id, file_id AS FileId FROM symbols;",
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FileRow>> GetAllFilesAsync(CancellationToken ct = default)
    {
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<FileRow>(new CommandDefinition(
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

    public async Task<RowCountsRow> RowCountsAsync(CancellationToken ct = default)
    {
        // One round-trip; subselects are cheap on indexed COUNT(*).
        const string sql = """
            SELECT
              (SELECT COUNT(*) FROM symbols)     AS Symbols,
              (SELECT COUNT(*) FROM refs)        AS Refs,
              (SELECT COUNT(*) FROM edges)       AS Edges,
              (SELECT COUNT(*) FROM files)       AS Files,
              (SELECT COUNT(*) FROM annotations) AS Annotations,
              (SELECT COUNT(*) FROM diagnostics) AS Diagnostics;
            """;
        var row = await _connection.QuerySingleAsync<RowCountsRow>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        return row;
    }

    public async Task<IReadOnlyList<FileShaRow>> SampleFileShasAsync(int limit, CancellationToken ct = default)
    {
        const string sql = """
            SELECT path AS Path, content_sha256 AS ContentSha256
            FROM files
            ORDER BY RANDOM()
            LIMIT @Limit;
            """;
        var rows = await _connection.QueryAsync<FileShaRow>(new CommandDefinition(sql, new { Limit = limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FileShaRow>> GetAllFileShasAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT path AS Path, content_sha256 AS ContentSha256 FROM files;";
        var rows = await _connection.QueryAsync<FileShaRow>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<string> IntegrityCheckAsync(CancellationToken ct = default)
    {
        // PRAGMA integrity_check returns one row "ok" on a clean DB, or N rows of failures
        // otherwise. On deeply-corrupt files SQLite throws while reading the result set rather
        // than returning a row — surface that as a non-"ok" string too, so the contract is
        // simple: this method never throws on integrity-related failures, it returns them.
        try
        {
            var rows = await _connection.QueryAsync<string>(new CommandDefinition("PRAGMA integrity_check;", cancellationToken: ct)).ConfigureAwait(false);
            var firstFailure = rows.FirstOrDefault(r => !string.Equals(r, "ok", StringComparison.Ordinal));
            if (firstFailure is not null) return firstFailure;
        }
        catch (SqliteException ex)
        {
            return $"integrity_check failed: {ex.Message}";
        }

        // FTS5 integrity-check throws when the FTS index has rotted but the main DB is intact.
        // The INSERT into the FTS shadow table is the documented integrity-check command.
        try
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO symbols_fts(symbols_fts) VALUES('integrity-check');",
                cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return $"fts5 integrity-check failed: {ex.Message}";
        }

        return "ok";
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
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<RawSymbolHit>(new CommandDefinition(
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
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync<AnnotationRecord>(new CommandDefinition(
            sql, new { id = symbolId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StoredAnnotationRow>> ListAnnotationsByFlavorAsync(
        string flavor,
        long afterId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flavor);
        ArgumentOutOfRangeException.ThrowIfNegative(afterId);
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "Annotation page size must be between 1 and 1000.");
        }

        const string sql = """
            SELECT a.id                  AS AnnotationId,
                   s.id                  AS SymbolId,
                   s.canonical_key       AS SymbolCanonicalKey,
                   f.id                  AS FileId,
                   f.path                AS FilePath,
                   a.name                AS Name,
                   a.full_name           AS FullName,
                   a.flavor              AS Flavor,
                   a.args_json           AS ArgsJson,
                   a.attribute_symbol_id AS AttributeSymbolId
            FROM annotations a
            JOIN symbols s ON s.id = a.symbol_id
            JOIN files f ON f.id = s.file_id
            WHERE a.flavor = @flavor
              AND a.id > @afterId
            ORDER BY a.id
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<StoredAnnotationRow>(
            new CommandDefinition(
                sql,
                new { flavor, afterId, limit },
                cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StoredAnnotationRow>>
        ListAnnotationsForFilesByFlavorsAsync(
            IReadOnlyCollection<string> filePaths,
            IReadOnlyCollection<string> flavors,
            int limit,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(flavors);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ct.ThrowIfCancellationRequested();

        var requestedPaths = filePaths
            .Select((path, index) =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(
                    path,
                    $"{nameof(filePaths)}[{index}]");
                return path;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var requestedFlavors = flavors
            .Select((flavor, index) =>
            {
                KebabCaseValidator.Validate(
                    flavor,
                    $"{nameof(flavors)}[{index}]");
                return flavor;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(flavor => flavor, StringComparer.Ordinal)
            .ToArray();
        if (requestedPaths.Length == 0 || requestedFlavors.Length == 0)
        {
            return [];
        }

        const string sql = """
            WITH requested_paths(path) AS (
                SELECT CAST(value AS TEXT)
                FROM json_each(@pathsJson)
            ),
            requested_flavors(flavor) AS (
                SELECT CAST(value AS TEXT)
                FROM json_each(@flavorsJson)
            )
            SELECT a.id                  AS AnnotationId,
                   s.id                  AS SymbolId,
                   s.canonical_key       AS SymbolCanonicalKey,
                   f.id                  AS FileId,
                   f.path                AS FilePath,
                   a.name                AS Name,
                   a.full_name           AS FullName,
                   a.flavor              AS Flavor,
                   a.args_json           AS ArgsJson,
                   a.attribute_symbol_id AS AttributeSymbolId
            FROM annotations a
            JOIN symbols s ON s.id = a.symbol_id
            JOIN files f ON f.id = s.file_id
            JOIN requested_paths p ON p.path = f.path
            JOIN requested_flavors rf ON rf.flavor = a.flavor
            ORDER BY f.path, a.flavor, s.canonical_key, a.id
            LIMIT @limit;
            """;
        var rows = await _connection.QueryAsync<StoredAnnotationRow>(
            new CommandDefinition(
                sql,
                new
                {
                    pathsJson = JsonSerializer.Serialize(requestedPaths),
                    flavorsJson = JsonSerializer.Serialize(requestedFlavors),
                    limit,
                },
                cancellationToken: ct)).ConfigureAwait(false);
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

    public async Task<bool> AnyEdgeHasPayloadKeyAsync(string edgeKind, string payloadKey, CancellationToken ct = default)
    {
        // EXISTS short-circuits at the first matching row — cheap on large edge sets. The kind
        // index narrows to the kind partition; json_extract pulls the field value (NULL when the
        // payload column is NULL or the key is absent).
        //
        // The JSON path is built with `'$."' || @payloadKey || '"'` so the key flows through a
        // bound parameter (no string interpolation, no SQL injection vector) AND the wrapping
        // double quotes make hyphenated kebab keys (`converter-parameter`, `data-type`,
        // `relative-source`) resolve correctly — `$.converter-parameter` would parse as
        // "$.converter MINUS parameter" without the quotes (SQLite's JSON path grammar).
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM edges
                WHERE kind_name = @edgeKind
                  AND json_extract(payload, '$."' || @payloadKey || '"') IS NOT NULL
                LIMIT 1
            );
            """;
        var hit = await _connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, new { edgeKind, payloadKey }, cancellationToken: ct)).ConfigureAwait(false);
        return hit != 0;
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

    public async Task<IReadOnlyList<long>> ListDiagnosticFileIdsByCodesAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var distinctCodes = codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctCodes.Length == 0)
        {
            return [];
        }

        const string sql = """
            SELECT DISTINCT file_id
            FROM diagnostics
            WHERE code IN @codes
            ORDER BY file_id;
            """;
        var rows = await _connection.QueryAsync<long>(new CommandDefinition(
                sql,
                new { codes = distinctCodes },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
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
            SELECT d.id, d.symbol_id AS SymbolId,
                   s.fqn AS SymbolFqn,
                   s.canonical_key AS SymbolCanonicalKey,
                   d.file_id AS FileId, f.path AS FilePath,
                   d.severity, d.code, d.message, d.line, d.col
            FROM diagnostics d
            JOIN files f ON f.id = d.file_id
            LEFT JOIN symbols s ON s.id = d.symbol_id
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

    public async Task<bool> HasOutgoingReferencesAsync(long fileId, CancellationToken ct = default)
    {
        // EXISTS short-circuits on the first hit. Both probes are directly indexed by the
        // producing file, including edges whose logical source symbol is declared elsewhere.
        const string sql = """
            SELECT
              EXISTS (SELECT 1 FROM refs WHERE file_id = @id)
              OR EXISTS (
                SELECT 1 FROM edge_evidence
                WHERE producing_file_id = @id
              );
            """;
        var v = await _connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            sql, new { id = fileId }, cancellationToken: ct)).ConfigureAwait(false);
        return v is not null && v.Value != 0;
    }

    private sealed record RawDiagnosticHit(
        long Id,
        long? SymbolId,
        string? SymbolFqn,
        string? SymbolCanonicalKey,
        long FileId,
        string FilePath,
        long Severity,
        string Code,
        string Message,
        long Line,
        long Col)
    {
        public DiagnosticHit ToHit() => new(
            Id,
            SymbolId,
            SymbolFqn,
            SymbolCanonicalKey,
            FileId,
            FilePath,
            (int)Severity,
            Code,
            Message,
            (int)Line,
            (int)Col);
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
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_connection.State != System.Data.ConnectionState.Closed)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogWarning(ex, "Sqlite connection was already disposed during store disposal.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Sqlite connection was in an invalid state during store disposal.");
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Sqlite exception occurred while disposing store connection.");
        }
        finally
        {
            _writeLock.Dispose();
        }
    }
}
