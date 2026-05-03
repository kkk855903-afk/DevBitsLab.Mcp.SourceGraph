using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// <see cref="IEmbeddingsStore"/> backed by the <c>vec0</c> virtual table from
/// <c>sqlite-vec</c>. The connection comes in pre-loaded (the host calls
/// <see cref="SqliteGraphStore.TryLoadVectorExtension"/> before constructing this store).
/// When the extension is missing we instantiate a no-op store via
/// <see cref="DisabledEmbeddingsStore"/> instead.
/// </summary>
public sealed class SqliteEmbeddingsStore : IEmbeddingsStore
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock;
    private readonly int _dimension;

    public SqliteEmbeddingsStore(SqliteConnection connection, int dimension, SemaphoreSlim writeLock, ILogger? logger = null)
    {
        _connection = connection;
        _dimension = dimension;
        _writeLock = writeLock;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsAvailable => true;
    public int Dimension => _dimension;

    public async Task UpsertAsync(long symbolId, byte[] contentHash, IReadOnlyList<float> embedding, string modelVersion, CancellationToken ct = default)
    {
        if (embedding.Count != _dimension)
        {
            throw new ArgumentException($"Expected embedding of dimension {_dimension}; got {embedding.Count}.", nameof(embedding));
        }

        // vec0 prefers raw float-blob payloads. Convert the float[] to a little-endian byte buffer
        // (the format vec0 expects when binding a BLOB to a FLOAT[N] column).
        var bytes = new byte[embedding.Count * sizeof(float)];
        Buffer.BlockCopy(embedding switch
        {
            float[] arr => arr,
            _ => embedding.ToArray(),
        }, 0, bytes, 0, bytes.Length);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();

            // vec0 doesn't support UPSERT; emulate via DELETE + INSERT, atomic in this tx.
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM symbol_embeddings WHERE symbol_id = @id;",
                new { id = symbolId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO symbol_embeddings(symbol_id, embedding) VALUES (@id, @vec);",
                new { id = symbolId, vec = bytes }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO embedding_meta(symbol_id, content_hash, model_version)
                VALUES (@id, @hash, @ver)
                ON CONFLICT(symbol_id) DO UPDATE SET
                    content_hash  = excluded.content_hash,
                    model_version = excluded.model_version;
                """,
                new { id = symbolId, hash = contentHash, ver = modelVersion },
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> ShouldReembedAsync(long symbolId, byte[] contentHash, string modelVersion, CancellationToken ct = default)
    {
        var existing = await _connection.QueryFirstOrDefaultAsync<EmbeddingMetaRow>(new CommandDefinition(
            "SELECT content_hash AS ContentHash, model_version AS ModelVersion FROM embedding_meta WHERE symbol_id = @id;",
            new { id = symbolId }, cancellationToken: ct)).ConfigureAwait(false);
        if (existing is null) return true;
        if (!string.Equals(existing.ModelVersion, modelVersion, StringComparison.Ordinal)) return true;
        return !existing.ContentHash.AsSpan().SequenceEqual(contentHash);
    }

    public async Task<IReadOnlyList<EmbeddingHit>> SearchAsync(IReadOnlyList<float> queryEmbedding, int k, Core.SymbolKind? kindFilter = null, CancellationToken ct = default)
    {
        if (queryEmbedding.Count != _dimension)
        {
            throw new ArgumentException($"Expected embedding of dimension {_dimension}; got {queryEmbedding.Count}.", nameof(queryEmbedding));
        }
        var bytes = new byte[queryEmbedding.Count * sizeof(float)];
        Buffer.BlockCopy(queryEmbedding switch
        {
            float[] arr => arr,
            _ => queryEmbedding.ToArray(),
        }, 0, bytes, 0, bytes.Length);

        // vec0 KNN: SELECT … MATCH … `AND k = ?` ORDER BY distance. The vec0 virtual table
        // requires either an explicit `k = ?` constraint OR a LIMIT clause but not both. Use
        // `k = ?` so the query is constant-shape and the kind-filter join works without an
        // extra LIMIT round-trip. Distance is L2 by default; for L2-normalised vectors
        // `cosine = 1 - distance^2 / 2`, which we compute in C# from the returned distance.
        var sql = $"""
            SELECT s.symbol_id AS SymbolId, s.distance AS Distance
            FROM symbol_embeddings s
            {(kindFilter is null ? "" : "JOIN symbols sym ON sym.id = s.symbol_id")}
            WHERE s.embedding MATCH @vec
              AND k = @k
              {(kindFilter is null ? "" : "AND sym.kind = @kind")}
            ORDER BY s.distance;
            """;
        var rows = await _connection.QueryAsync<RawHit>(new CommandDefinition(
            sql, new { vec = bytes, k, kind = (int?)kindFilter }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new EmbeddingHit(r.SymbolId, CosineFromL2(r.Distance))).ToList();
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        // symbol_embeddings is a virtual table; COUNT(*) on it returns the number of
        // (symbol_id, embedding) rows.
        return await _connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM symbol_embeddings;", cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Convert vec0's L2 distance on unit-norm vectors to cosine similarity in <c>[-1, 1]</c>.
    /// For unit-norm vectors <c>||a-b||^2 = 2 - 2*cos(theta)</c>, so <c>cos = 1 - d^2/2</c>.
    /// </summary>
    private static double CosineFromL2(double l2distance)
    {
        return 1.0 - (l2distance * l2distance) / 2.0;
    }

    private sealed record RawHit(long SymbolId, double Distance);
    private sealed record EmbeddingMetaRow(byte[] ContentHash, string ModelVersion);
}

/// <summary>
/// No-op <see cref="IEmbeddingsStore"/> used when the extension didn't load or when
/// <c>--no-embeddings</c> is passed. Reports unavailable; <see cref="UpsertAsync"/> swallows
/// the call and <see cref="SearchAsync"/> returns nothing.
/// </summary>
public sealed class DisabledEmbeddingsStore : IEmbeddingsStore
{
    private readonly int _dimension;
    public DisabledEmbeddingsStore(int dimension) { _dimension = dimension; }
    public bool IsAvailable => false;
    public int Dimension => _dimension;
    public Task UpsertAsync(long symbolId, byte[] contentHash, IReadOnlyList<float> embedding, string modelVersion, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ShouldReembedAsync(long symbolId, byte[] contentHash, string modelVersion, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<EmbeddingHit>> SearchAsync(IReadOnlyList<float> queryEmbedding, int k, Core.SymbolKind? kindFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EmbeddingHit>>(Array.Empty<EmbeddingHit>());
    public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult(0L);
}
