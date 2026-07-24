using Dapper;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    private const int MaximumGrpcContractBaselines = 10_000;
    private const int MaximumGrpcContractPayloadCharacters = 64 * 1024;

    public async Task<int> EnsureGrpcContractBaselinesAsync(
        IReadOnlyList<GrpcContractBaselineFact> facts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count > MaximumGrpcContractBaselines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(facts),
                facts.Count,
                $"At most {MaximumGrpcContractBaselines} gRPC baselines may be established at once.");
        }

        var ordered = facts
            .OrderBy(fact => fact.SymbolCanonicalKey, StringComparer.Ordinal)
            .ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in ordered)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                fact.SymbolCanonicalKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(fact.ContractJson);
            ArgumentException.ThrowIfNullOrWhiteSpace(fact.FilePath);
            if (!fact.SymbolCanonicalKey.StartsWith(
                    "proto:",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A gRPC baseline canonical key must use the exact `proto:` namespace.",
                    nameof(facts));
            }
            if (fact.ContractJson.Length
                > MaximumGrpcContractPayloadCharacters)
            {
                throw new ArgumentException(
                    $"A gRPC baseline payload exceeds {MaximumGrpcContractPayloadCharacters} characters.",
                    nameof(facts));
            }
            if (!keys.Add(fact.SymbolCanonicalKey))
            {
                throw new ArgumentException(
                    $"Duplicate gRPC baseline canonical key `{fact.SymbolCanonicalKey}`.",
                    nameof(facts));
            }
            ValidateGrpcContractBaselineRange(fact);
        }

        var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var inserted = 0;
            foreach (var fact in ordered)
            {
                ct.ThrowIfCancellationRequested();
                inserted += await _connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT OR IGNORE INTO grpc_contract_baselines(
                            symbol_canonical_key, contract_json, file_path,
                            start_line, start_col, end_line, end_col,
                            observed_at)
                        VALUES (
                            @SymbolCanonicalKey, @ContractJson, @FilePath,
                            @StartLine, @StartColumn, @EndLine, @EndColumn,
                            @ObservedAt);
                        """,
                        new
                        {
                            fact.SymbolCanonicalKey,
                            fact.ContractJson,
                            fact.FilePath,
                            fact.StartLine,
                            fact.StartColumn,
                            fact.EndLine,
                            fact.EndColumn,
                            ObservedAt = observedAt,
                        },
                        transaction: tx,
                        cancellationToken: ct))
                    .ConfigureAwait(false);
            }
            var total = await _connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT COUNT(*) FROM grpc_contract_baselines;",
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            if (total > MaximumGrpcContractBaselines)
            {
                throw new InvalidOperationException(
                    $"Persisted gRPC baseline history exceeds the {MaximumGrpcContractBaselines}-row limit.");
            }
            tx.Commit();
            return inserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<GrpcContractBaselineRow>>
        ListGrpcContractBaselinesAsync(
            int limit,
            CancellationToken ct = default)
    {
        if (limit is <= 0 or > MaximumGrpcContractBaselines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"gRPC baseline limit must be between 1 and {MaximumGrpcContractBaselines}.");
        }

        var rows = await _connection.QueryAsync<RawGrpcContractBaselineRow>(
            new CommandDefinition(
                """
                SELECT
                    symbol_canonical_key AS SymbolCanonicalKey,
                    contract_json AS ContractJson,
                    file_path AS FilePath,
                    start_line AS StartLine,
                    start_col AS StartColumn,
                    end_line AS EndLine,
                    end_col AS EndColumn,
                    observed_at AS ObservedAtUnixMs
                FROM grpc_contract_baselines
                ORDER BY symbol_canonical_key
                LIMIT @Limit;
                """,
                new { Limit = limit + 1 },
                cancellationToken: ct)).ConfigureAwait(false);
        var materialized = rows.AsList();
        if (materialized.Count > limit)
        {
            throw new InvalidOperationException(
                $"Persisted gRPC baseline history exceeds the requested {limit}-row limit.");
        }
        return materialized
            .Select(row => new GrpcContractBaselineRow(
                row.SymbolCanonicalKey,
                row.ContractJson,
                row.FilePath,
                checked((int)row.StartLine),
                checked((int)row.StartColumn),
                checked((int)row.EndLine),
                checked((int)row.EndColumn),
                row.ObservedAtUnixMs))
            .ToArray();
    }

    private static void ValidateGrpcContractBaselineRange(
        GrpcContractBaselineFact fact)
    {
        if (fact.StartLine < 1
            || fact.StartColumn < 1
            || fact.EndLine < fact.StartLine
            || fact.EndColumn < 1
            || (fact.EndLine == fact.StartLine
                && fact.EndColumn < fact.StartColumn))
        {
            throw new ArgumentException(
                $"Invalid source range for gRPC baseline `{fact.SymbolCanonicalKey}`.",
                nameof(fact));
        }
    }

    private sealed record RawGrpcContractBaselineRow(
        string SymbolCanonicalKey,
        string ContractJson,
        string FilePath,
        long StartLine,
        long StartColumn,
        long EndLine,
        long EndColumn,
        long ObservedAtUnixMs);
}
