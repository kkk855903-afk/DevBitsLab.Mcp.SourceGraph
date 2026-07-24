using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    public async Task ReplaceProducerEdgeEvidenceProjectionAsync(
        string producer,
        IReadOnlyList<ProducerEdgeEvidenceFact> edges,
        CancellationToken ct = default)
    {
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
            var files = new Dictionary<string, RawProducingFile>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            async Task<RawProducingFile> ResolveFileAsync(string path)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var normalized = Path.GetFullPath(path);
                if (files.TryGetValue(normalized, out var cached)) return cached;

                var resolved = await _connection.QuerySingleOrDefaultAsync<RawProducingFile>(
                    new CommandDefinition(
                        """
                        SELECT id AS FileId, path AS FilePath
                        FROM files
                        WHERE path = @Path;
                        """,
                        new { Path = path },
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false);
                if (resolved is null)
                {
                    throw new InvalidOperationException(
                        $"Producer projection replacement could not resolve indexed file `{path}`.");
                }
                if (!PathsEquivalent(resolved.FilePath, path))
                {
                    throw new InvalidOperationException(
                        "Producer projection replacement resolved a non-equivalent indexed file "
                        + $"for `{path}`.");
                }

                files[normalized] = resolved;
                return resolved;
            }

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
                        $"Producer projection replacement could not resolve symbol `{canonicalKey}`.");
                }

                symbolIds[canonicalKey] = resolved.Value;
                return resolved.Value;
            }

            // Resolve the entire generation before deleting a single prior occurrence. Any
            // invalid endpoint, file, evidence range, or cancellation rolls back untouched.
            var resolvedEdges = new List<ResolvedProjectionEdge>(edges.Count);
            for (var index = 0; index < edges.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var edge = edges[index]
                    ?? throw new ArgumentException(
                        $"Producer projection replacement contains a null edge at index {index}.",
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

                var producingFile = await ResolveFileAsync(
                        edge.Evidence.Location.FilePath)
                    .ConfigureAwait(false);
                var evidence = new Evidence(
                    producingFile.FileId,
                    edge.Evidence.Location,
                    edge.Evidence.Confidence,
                    producer,
                    edge.Evidence.Metadata);
                ValidateEvidence(evidence);
                if (!PathsEquivalent(
                        producingFile.FilePath,
                        evidence.Location.FilePath))
                {
                    throw new InvalidOperationException(
                        "Replacement evidence path does not match indexed producing file "
                        + $"`{producingFile.FilePath}` (index {index}).");
                }

                var src = await ResolveSymbolIdAsync(
                        edge.SourceCanonicalKey)
                    .ConfigureAwait(false);
                var dst = await ResolveSymbolIdAsync(
                        edge.TargetCanonicalKey)
                    .ConfigureAwait(false);
                resolvedEdges.Add(new ResolvedProjectionEdge(
                    edge.SourceCanonicalKey,
                    edge.TargetCanonicalKey,
                    src,
                    dst,
                    edge.Kind,
                    SerializeMetadata(edge.Metadata),
                    producingFile.FileId,
                    producingFile.FilePath,
                    evidence.Location.StartLine,
                    evidence.Location.StartColumn,
                    evidence.Location.EndLine,
                    evidence.Location.EndColumn,
                    evidence.Confidence,
                    SerializeMetadata(evidence.Metadata ?? edge.Metadata)
                        ?? string.Empty));
            }

            var orderedEdges = resolvedEdges
                .GroupBy(edge => new ProjectionEvidenceIdentity(
                    edge.Src,
                    edge.Dst,
                    edge.Kind,
                    edge.ProducingFileId,
                    edge.FilePath,
                    edge.StartLine,
                    edge.StartColumn,
                    edge.EndLine,
                    edge.EndColumn,
                    edge.Confidence,
                    edge.EvidencePayload))
                .Select(group => group
                    .OrderBy(
                        edge => edge.LogicalPayload ?? string.Empty,
                        StringComparer.Ordinal)
                    .First())
                .OrderBy(
                    edge => edge.SourceCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(
                    edge => edge.TargetCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
                .ThenBy(edge => edge.FilePath, StringComparer.Ordinal)
                .ThenBy(edge => edge.StartLine)
                .ThenBy(edge => edge.StartColumn)
                .ThenBy(edge => edge.EndLine)
                .ThenBy(edge => edge.EndColumn)
                .ThenBy(edge => edge.Confidence)
                .ThenBy(edge => edge.EvidencePayload, StringComparer.Ordinal)
                .ToList();

            // Reconcile the old generation producer-wide in this transaction. Logical edges
            // retain other producers' evidence and compatibility payload; edges supported only
            // by this producer disappear before the new generation is inserted.
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE edges AS edge
                SET payload = (
                    SELECT NULLIF(survivor.payload, '')
                    FROM edge_evidence survivor
                    WHERE survivor.src = edge.src
                      AND survivor.dst = edge.dst
                      AND survivor.kind_name = edge.kind_name
                      AND survivor.producer <> @Producer
                    ORDER BY survivor.id
                    LIMIT 1
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edge.src
                      AND owned.dst = edge.dst
                      AND owned.kind_name = edge.kind_name
                      AND owned.producer = @Producer
                );

                DELETE FROM edges
                WHERE EXISTS (
                    SELECT 1
                    FROM edge_evidence owned
                    WHERE owned.src = edges.src
                      AND owned.dst = edges.dst
                      AND owned.kind_name = edges.kind_name
                      AND owned.producer = @Producer
                )
                  AND NOT EXISTS (
                    SELECT 1
                    FROM edge_evidence survivor
                    WHERE survivor.src = edges.src
                      AND survivor.dst = edges.dst
                      AND survivor.kind_name = edges.kind_name
                      AND survivor.producer <> @Producer
                );

                DELETE FROM edge_evidence
                WHERE producer = @Producer;
                """,
                new { Producer = producer },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            foreach (var edge in orderedEdges)
            {
                ct.ThrowIfCancellationRequested();
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
                        edge.ProducingFileId,
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

    private sealed record ResolvedProjectionEdge(
        string SourceCanonicalKey,
        string TargetCanonicalKey,
        long Src,
        long Dst,
        string Kind,
        string? LogicalPayload,
        long ProducingFileId,
        string FilePath,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        EvidenceConfidence Confidence,
        string EvidencePayload);

    private sealed record ProjectionEvidenceIdentity(
        long Src,
        long Dst,
        string Kind,
        long ProducingFileId,
        string FilePath,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        EvidenceConfidence Confidence,
        string EvidencePayload);
}
