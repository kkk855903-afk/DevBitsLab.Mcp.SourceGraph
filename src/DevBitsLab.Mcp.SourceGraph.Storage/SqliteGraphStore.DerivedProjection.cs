using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using Microsoft.Data.Sqlite;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    public Task ReplaceFileDerivedProjectionAsync(
        string producingFilePath,
        string producer,
        IReadOnlyCollection<string> annotationFlavors,
        IReadOnlyList<FileAnnotationFact> annotations,
        IReadOnlyList<ProducerEdgeEvidenceFact> edges,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producingFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentNullException.ThrowIfNull(annotationFlavors);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(edges);

        return ReplaceFileDerivedProjectionsAsync(
            [
                new FileDerivedProjectionReplacement(
                    producingFilePath,
                    producer,
                    annotationFlavors,
                    annotations,
                    edges),
            ],
            ct);
    }

    public async Task ReplaceFileDerivedProjectionsAsync(
        IReadOnlyList<FileDerivedProjectionReplacement> projections,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projections);
        ct.ThrowIfCancellationRequested();

        // Snapshot all top-level and nested caller-owned collections before publication. This
        // prevents a producer from changing a later projection while an earlier one is being
        // resolved in the shared transaction.
        var candidates = projections.ToArray();
        var snapshots = new SnapshotFileDerivedProjection[candidates.Length];
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var normalizedProducingPaths = new HashSet<string>(pathComparer);
        for (var projectionIndex = 0;
             projectionIndex < candidates.Length;
             projectionIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = candidates[projectionIndex]
                ?? throw new ArgumentException(
                    $"Derived projection batch contains a null projection at index "
                    + $"{projectionIndex}.",
                    nameof(projections));
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ProducingFilePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Producer);
            ArgumentNullException.ThrowIfNull(candidate.AnnotationFlavors);
            ArgumentNullException.ThrowIfNull(candidate.Annotations);
            ArgumentNullException.ThrowIfNull(candidate.Edges);

            var normalizedProducingPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate.ProducingFilePath));
            if (!normalizedProducingPaths.Add(normalizedProducingPath))
            {
                throw new ArgumentException(
                    "Derived projection batch contains duplicate producing file path "
                    + $"`{candidate.ProducingFilePath}` (index {projectionIndex}).",
                    nameof(projections));
            }

            var candidateFlavors = candidate.AnnotationFlavors.ToArray();
            var candidateAnnotations = candidate.Annotations.ToArray();
            var candidateEdges = candidate.Edges.ToArray();
            if (candidateFlavors.Length == 0)
            {
                throw new ArgumentException(
                    "At least one annotation flavor must be selected "
                    + $"(projection index {projectionIndex}).",
                    nameof(projections));
            }

            var flavorSet = new HashSet<string>(StringComparer.Ordinal);
            for (var flavorIndex = 0;
                 flavorIndex < candidateFlavors.Length;
                 flavorIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var flavor = candidateFlavors[flavorIndex];
                KebabCaseValidator.Validate(flavor, nameof(projections));
                if (!flavorSet.Add(flavor))
                {
                    throw new ArgumentException(
                        $"Annotation flavor `{flavor}` is duplicated "
                        + $"(projection index {projectionIndex}, flavor index {flavorIndex}).",
                        nameof(projections));
                }
            }
            var orderedFlavors = flavorSet
                .OrderBy(flavor => flavor, StringComparer.Ordinal)
                .ToArray();

            for (var annotationIndex = 0;
                 annotationIndex < candidateAnnotations.Length;
                 annotationIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var annotation = candidateAnnotations[annotationIndex]
                    ?? throw new ArgumentException(
                        "Derived projection contains a null annotation "
                        + $"(projection index {projectionIndex}, "
                        + $"annotation index {annotationIndex}).",
                        nameof(projections));
                CanonicalKeyValidator.Validate(
                    annotation.SymbolCanonicalKey,
                    nameof(projections));
                ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(annotation.FullName);
                KebabCaseValidator.Validate(annotation.Flavor, nameof(projections));
                if (!flavorSet.Contains(annotation.Flavor))
                {
                    throw new ArgumentException(
                        "Every derived annotation flavor must belong to the selected flavor set; "
                        + $"`{annotation.Flavor}` is not selected "
                        + $"(projection index {projectionIndex}, "
                        + $"annotation index {annotationIndex}).",
                        nameof(projections));
                }
                if (annotation.AttributeCanonicalKey is not null)
                {
                    CanonicalKeyValidator.Validate(
                        annotation.AttributeCanonicalKey,
                        nameof(projections));
                }
                if (annotation.ArgsJson is not null)
                {
                    try
                    {
                        using var _ =
                            System.Text.Json.JsonDocument.Parse(annotation.ArgsJson);
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        throw new ArgumentException(
                            "Annotation args_json must contain valid JSON "
                            + $"(projection index {projectionIndex}, "
                            + $"annotation index {annotationIndex}).",
                            nameof(projections),
                            ex);
                    }
                }

                candidateAnnotations[annotationIndex] = annotation with { };
            }

            for (var edgeIndex = 0;
                 edgeIndex < candidateEdges.Length;
                 edgeIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var edge = candidateEdges[edgeIndex]
                    ?? throw new ArgumentException(
                        "Derived projection contains a null edge "
                        + $"(projection index {projectionIndex}, edge index {edgeIndex}).",
                        nameof(projections));
                CanonicalKeyValidator.Validate(
                    edge.SourceCanonicalKey,
                    nameof(projections));
                CanonicalKeyValidator.Validate(
                    edge.TargetCanonicalKey,
                    nameof(projections));
                KebabCaseValidator.Validate(edge.Kind, nameof(projections));
                ArgumentNullException.ThrowIfNull(edge.Evidence);
                ArgumentNullException.ThrowIfNull(edge.Evidence.Location);
                if (!string.Equals(
                        edge.Evidence.Producer,
                        candidate.Producer,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Every derived edge evidence producer must exactly match "
                        + $"the projection producer `{candidate.Producer}` "
                        + $"(projection index {projectionIndex}, edge index {edgeIndex}).",
                        nameof(projections));
                }

                var logicalMetadata = SnapshotProjectionMetadata(
                    edge.Metadata,
                    nameof(projections),
                    edgeIndex,
                    "edge");
                var evidenceMetadata = SnapshotProjectionMetadata(
                    edge.Evidence.Metadata,
                    nameof(projections),
                    edgeIndex,
                    "evidence");
                candidateEdges[edgeIndex] = new ProducerEdgeEvidenceFact(
                    edge.SourceCanonicalKey,
                    edge.TargetCanonicalKey,
                    edge.Kind,
                    logicalMetadata,
                    edge.Evidence with
                    {
                        Location = edge.Evidence.Location with { },
                        Metadata = evidenceMetadata,
                    });
            }

            snapshots[projectionIndex] = new SnapshotFileDerivedProjection(
                candidate.ProducingFilePath,
                candidate.Producer,
                orderedFlavors,
                candidateAnnotations,
                candidateEdges);
        }

        if (snapshots.Length == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var symbols = new Dictionary<string, RawAnnotationSymbol>(
                StringComparer.Ordinal);
            var resolved = new ResolvedFileDerivedProjection[snapshots.Length];

            // Resolve every file, annotation host/attribute, and edge endpoint in the complete
            // batch before changing a single prior row.
            for (var index = 0; index < snapshots.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                resolved[index] = await ResolveDerivedProjectionAsync(
                        snapshots[index],
                        symbols,
                        tx,
                        ct)
                    .ConfigureAwait(false);
            }

            foreach (var projection in resolved)
            {
                ct.ThrowIfCancellationRequested();
                await PublishDerivedProjectionAsync(projection, tx, ct)
                    .ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<ResolvedFileDerivedProjection> ResolveDerivedProjectionAsync(
        SnapshotFileDerivedProjection projection,
        Dictionary<string, RawAnnotationSymbol> symbols,
        SqliteTransaction tx,
        CancellationToken ct)
    {
        var producingFile = await _connection.QuerySingleOrDefaultAsync<RawProducingFile>(
            new CommandDefinition(
                """
                SELECT id AS FileId, path AS FilePath
                FROM files
                WHERE path = @ProducingFilePath;
                """,
                new { projection.ProducingFilePath },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Derived projection could not resolve indexed file "
                + $"`{projection.ProducingFilePath}`.");

        async Task<RawAnnotationSymbol> ResolveSymbolAsync(string canonicalKey)
        {
            if (symbols.TryGetValue(canonicalKey, out var cached))
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
                    $"Derived projection could not resolve symbol `{canonicalKey}`.");
            symbols[canonicalKey] = resolved;
            return resolved;
        }

        var resolvedAnnotations =
            new List<ResolvedFileAnnotationFact>(projection.Annotations.Count);
        foreach (var annotation in projection.Annotations)
        {
            ct.ThrowIfCancellationRequested();
            var host = await ResolveSymbolAsync(annotation.SymbolCanonicalKey)
                .ConfigureAwait(false);
            if (host.FileId != producingFile.FileId)
            {
                throw new InvalidOperationException(
                    "Derived annotation hosts must belong to indexed producing file "
                    + $"`{producingFile.FilePath}`; "
                    + $"`{annotation.SymbolCanonicalKey}` is external.");
            }

            long? attributeSymbolId = null;
            if (annotation.AttributeCanonicalKey is not null)
            {
                attributeSymbolId = (await ResolveSymbolAsync(
                        annotation.AttributeCanonicalKey)
                    .ConfigureAwait(false)).SymbolId;
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

        var resolvedEdges =
            new List<ResolvedProducerEdgeEvidenceFact>(projection.Edges.Count);
        for (var index = 0; index < projection.Edges.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var edge = projection.Edges[index];
            var evidence = new Evidence(
                producingFile.FileId,
                edge.Evidence.Location,
                edge.Evidence.Confidence,
                projection.Producer,
                edge.Evidence.Metadata);
            ValidateEvidence(evidence);
            if (!PathsEquivalent(producingFile.FilePath, evidence.Location.FilePath))
            {
                throw new InvalidOperationException(
                    "Derived edge evidence path does not match indexed producing file "
                    + $"`{producingFile.FilePath}` (index {index}).");
            }

            var source = await ResolveSymbolAsync(edge.SourceCanonicalKey)
                .ConfigureAwait(false);
            var target = await ResolveSymbolAsync(edge.TargetCanonicalKey)
                .ConfigureAwait(false);
            resolvedEdges.Add(new ResolvedProducerEdgeEvidenceFact(
                edge.SourceCanonicalKey,
                edge.TargetCanonicalKey,
                source.SymbolId,
                target.SymbolId,
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
            .ToArray();

        return new ResolvedFileDerivedProjection(
            producingFile,
            projection.Producer,
            projection.AnnotationFlavors,
            orderedAnnotations,
            orderedEdges);
    }

    private async Task PublishDerivedProjectionAsync(
        ResolvedFileDerivedProjection projection,
        SqliteTransaction tx,
        CancellationToken ct)
    {
        foreach (var flavor in projection.AnnotationFlavors)
        {
            ct.ThrowIfCancellationRequested();
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
                new
                {
                    Flavor = flavor,
                    projection.ProducingFile.FileId,
                },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        var selector = new
        {
            ProducingFileId = projection.ProducingFile.FileId,
            projection.Producer,
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

        const string insertAnnotationSql = """
            INSERT INTO annotations(
                symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)
            VALUES (
                @SymbolId, @Name, @FullName, @Flavor, @ArgsJson, @AttributeSymbolId);
            """;
        foreach (var annotation in projection.Annotations)
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                insertAnnotationSql,
                annotation,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
        }

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
        foreach (var edge in projection.Edges)
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
                    ProducingFileId = projection.ProducingFile.FileId,
                    edge.FilePath,
                    edge.StartLine,
                    edge.StartColumn,
                    edge.EndLine,
                    edge.EndColumn,
                    Confidence = (int)edge.Confidence,
                    projection.Producer,
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
    }

    private static IReadOnlyDictionary<string, string>? SnapshotProjectionMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string parameterName,
        int factIndex,
        string metadataKind)
    {
        if (metadata is null)
        {
            return null;
        }

        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            if (pair.Key is null)
            {
                throw new ArgumentException(
                    $"{metadataKind} metadata contains a null key (fact index {factIndex}).",
                    parameterName);
            }
            if (pair.Value is null)
            {
                throw new ArgumentException(
                    $"{metadataKind} metadata contains a null value for key `{pair.Key}` "
                    + $"(fact index {factIndex}).",
                    parameterName);
            }
            snapshot[pair.Key] = pair.Value;
        }
        return snapshot;
    }

    private sealed record SnapshotFileDerivedProjection(
        string ProducingFilePath,
        string Producer,
        IReadOnlyList<string> AnnotationFlavors,
        IReadOnlyList<FileAnnotationFact> Annotations,
        IReadOnlyList<ProducerEdgeEvidenceFact> Edges);

    private sealed record ResolvedFileDerivedProjection(
        RawProducingFile ProducingFile,
        string Producer,
        IReadOnlyList<string> AnnotationFlavors,
        IReadOnlyList<ResolvedFileAnnotationFact> Annotations,
        IReadOnlyList<ResolvedProducerEdgeEvidenceFact> Edges);
}
