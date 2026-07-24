using Dapper;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    private const int MaximumStaleNativeInteropSymbols = 100_000;
    private const int MaximumNativeInteropCanonicalKeyLength = 16_384;

    public async Task<NativeInteropStaleSymbolCleanupResult>
        DeleteOrphanedNativeInteropSymbolsAsync(
            IReadOnlyCollection<string> staleCanonicalKeys,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(staleCanonicalKeys);
        ct.ThrowIfCancellationRequested();
        var requested = SnapshotAndValidateStaleNativeKeys(
            staleCanonicalKeys,
            ct);
        if (requested.Length == 0)
        {
            return new NativeInteropStaleSymbolCleanupResult([], [], []);
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    CREATE TEMP TABLE IF NOT EXISTS native_stale_cleanup_keys(
                        canonical_key TEXT PRIMARY KEY NOT NULL,
                        expected_kind TEXT NOT NULL
                    );
                    CREATE TEMP TABLE IF NOT EXISTS native_stale_cleanup_delete_ids(
                        symbol_id INTEGER PRIMARY KEY NOT NULL,
                        canonical_key TEXT UNIQUE NOT NULL
                    );
                    DELETE FROM native_stale_cleanup_keys;
                    DELETE FROM native_stale_cleanup_delete_ids;
                    """,
                    transaction: tx,
                    cancellationToken: ct))
                .ConfigureAwait(false);

            await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO native_stale_cleanup_keys(
                        canonical_key,
                        expected_kind)
                    VALUES (@CanonicalKey, @ExpectedKind);
                    """,
                    requested,
                    transaction: tx,
                    cancellationToken: ct))
                .ConfigureAwait(false);

            // An eligible row must be exactly the native declaration shape encoded by its key
            // and completely isolated from every other persisted fact. The broad orphan check
            // deliberately subsumes the required pinvoke-maps-to target check: unexpected
            // ordinary facts are preserved rather than deleted or left dangling.
            await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO native_stale_cleanup_delete_ids(
                        symbol_id,
                        canonical_key)
                    SELECT symbol.id, requested.canonical_key
                    FROM native_stale_cleanup_keys requested
                    JOIN symbols symbol
                      ON symbol.canonical_key = requested.canonical_key
                    WHERE (
                            symbol.kind_name = requested.expected_kind
                            OR (
                                requested.expected_kind = @NativeFunctionKind
                                AND symbol.kind_name IN @FunctionKinds
                            )
                          )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM annotations annotation
                          WHERE annotation.symbol_id = symbol.id
                             OR annotation.attribute_symbol_id = symbol.id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM refs reference
                          WHERE reference.symbol_id = symbol.id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM edges edge
                          WHERE edge.src = symbol.id
                             OR edge.dst = symbol.id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM edge_evidence evidence
                          WHERE evidence.src = symbol.id
                             OR evidence.dst = symbol.id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM diagnostics diagnostic
                          WHERE diagnostic.symbol_id = symbol.id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM symbol_history history
                          WHERE history.symbol_id = symbol.id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM embedding_meta embedding
                          WHERE embedding.symbol_id = symbol.id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM symbols child
                          WHERE child.container_id = symbol.id
                      );
                    """,
                    new
                    {
                        NativeFunctionKind = "native-function",
                        FunctionKinds = new[]
                        {
                            SymbolKinds.Function,
                            SymbolKinds.Method,
                        },
                    },
                    transaction: tx,
                    cancellationToken: ct))
                .ConfigureAwait(false);

            var deletedKeys = (await _connection.QueryAsync<string>(
                    new CommandDefinition(
                        """
                        SELECT canonical_key
                        FROM native_stale_cleanup_delete_ids
                        ORDER BY canonical_key;
                        """,
                        transaction: tx,
                        cancellationToken: ct))
                    .ConfigureAwait(false))
                .ToArray();
            var retainedKeys = (await _connection.QueryAsync<string>(
                    new CommandDefinition(
                        """
                        SELECT requested.canonical_key
                        FROM native_stale_cleanup_keys requested
                        JOIN symbols symbol
                          ON symbol.canonical_key = requested.canonical_key
                        LEFT JOIN native_stale_cleanup_delete_ids deletion
                          ON deletion.symbol_id = symbol.id
                        WHERE deletion.symbol_id IS NULL
                        ORDER BY requested.canonical_key;
                        """,
                        transaction: tx,
                        cancellationToken: ct))
                    .ConfigureAwait(false))
                .ToArray();
            var missingKeys = (await _connection.QueryAsync<string>(
                    new CommandDefinition(
                        """
                        SELECT requested.canonical_key
                        FROM native_stale_cleanup_keys requested
                        LEFT JOIN symbols symbol
                          ON symbol.canonical_key = requested.canonical_key
                        WHERE symbol.id IS NULL
                        ORDER BY requested.canonical_key;
                        """,
                        transaction: tx,
                        cancellationToken: ct))
                    .ConfigureAwait(false))
                .ToArray();

            ct.ThrowIfCancellationRequested();
            var deletedCount = await _connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        DELETE FROM symbols
                        WHERE id IN (
                            SELECT symbol_id
                            FROM native_stale_cleanup_delete_ids
                        );
                        """,
                        transaction: tx,
                        cancellationToken: ct))
                .ConfigureAwait(false);
            if (deletedCount != deletedKeys.Length)
            {
                throw new InvalidOperationException(
                    "The stale native-symbol candidate set changed during cleanup.");
            }

            await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM native_stale_cleanup_keys;
                    DELETE FROM native_stale_cleanup_delete_ids;
                    """,
                    transaction: tx,
                    cancellationToken: ct))
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return new NativeInteropStaleSymbolCleanupResult(
                deletedKeys,
                retainedKeys,
                missingKeys);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static StaleNativeInteropKey[]
        SnapshotAndValidateStaleNativeKeys(
            IReadOnlyCollection<string> source,
            CancellationToken ct)
    {
        if (source.Count > MaximumStaleNativeInteropSymbols)
        {
            throw new ArgumentException(
                $"Stale native-symbol cleanup exceeds the "
                + $"{MaximumStaleNativeInteropSymbols}-key limit.",
                nameof(source));
        }

        var keys = new List<StaleNativeInteropKey>(
            Math.Min(source.Count, MaximumStaleNativeInteropSymbols));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in source)
        {
            ct.ThrowIfCancellationRequested();
            if (keys.Count >= MaximumStaleNativeInteropSymbols)
            {
                throw new ArgumentException(
                    $"Stale native-symbol cleanup exceeds the "
                    + $"{MaximumStaleNativeInteropSymbols}-key limit.",
                    nameof(source));
            }
            if (key is null)
            {
                throw new ArgumentException(
                    "Stale native canonical keys cannot contain null.",
                    nameof(source));
            }
            if (key.Length > MaximumNativeInteropCanonicalKeyLength)
            {
                throw new ArgumentException(
                    $"Stale native canonical key exceeds the "
                    + $"{MaximumNativeInteropCanonicalKeyLength}-character limit.",
                    nameof(source));
            }
            CanonicalKeyValidator.Validate(key, nameof(source));
            var expectedKind = ParseStaleNativeKeyKind(key, source);
            if (!seen.Add(key))
            {
                throw new ArgumentException(
                    $"Stale native canonical key `{key}` is duplicated.",
                    nameof(source));
            }
            keys.Add(new StaleNativeInteropKey(key, expectedKind));
        }

        return keys
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ParseStaleNativeKeyKind(
        string key,
        IReadOnlyCollection<string> source)
    {
        string expectedKind;
        int pathStart;
        if (key.StartsWith("c:E:", StringComparison.Ordinal))
        {
            expectedKind = SymbolKinds.NativeExport;
            pathStart = 4;
        }
        else if (key.StartsWith("cpp:E:", StringComparison.Ordinal))
        {
            expectedKind = SymbolKinds.NativeExport;
            pathStart = 6;
        }
        else if (key.StartsWith("c:T:", StringComparison.Ordinal))
        {
            expectedKind = SymbolKinds.Struct;
            pathStart = 4;
        }
        else if (key.StartsWith("cpp:T:", StringComparison.Ordinal))
        {
            expectedKind = SymbolKinds.Struct;
            pathStart = 6;
        }
        else if (key.StartsWith("c:F:", StringComparison.Ordinal))
        {
            expectedKind = "native-function";
            pathStart = 4;
        }
        else if (key.StartsWith("cpp:F:", StringComparison.Ordinal))
        {
            expectedKind = "native-function";
            pathStart = 6;
        }
        else
        {
            throw new ArgumentException(
                "Stale native-symbol cleanup accepts only c/cpp export, type, or function keys.",
                nameof(source));
        }

        var identitySeparator = key.IndexOf(
            "::",
            pathStart,
            StringComparison.Ordinal);
        if (identitySeparator <= pathStart
            || identitySeparator + 2 >= key.Length)
        {
            throw new ArgumentException(
                $"Stale native canonical key `{key}` is not a complete "
                + "path-qualified identity.",
                nameof(source));
        }

        var path = key[pathStart..identitySeparator];
        var identity = key[(identitySeparator + 2)..];
        if (path.Length != path.Trim().Length
            || path[0] == '/'
            || path[^1] == '/'
            || identity.Length != identity.Trim().Length
            || HasControlCharacter(identity))
        {
            throw new ArgumentException(
                $"Stale native canonical key `{key}` is not normalized.",
                nameof(source));
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.Contains(':')
                || HasControlCharacter(segment))
            {
                throw new ArgumentException(
                    $"Stale native canonical key `{key}` contains an invalid path.",
                    nameof(source));
            }
        }

        return expectedKind;
    }

    private static bool HasControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }
        return false;
    }

    private sealed record StaleNativeInteropKey(
        string CanonicalKey,
        string ExpectedKind);
}
