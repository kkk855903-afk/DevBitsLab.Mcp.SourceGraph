using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    public async Task ReplaceFileDerivedProjectionAsync(
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
        ct.ThrowIfCancellationRequested();

        // Snapshot every caller-owned collection before validation or publication. Metadata maps
        // are copied as well so a producer cannot mutate a nested dictionary while this
        // transaction is resolving facts.
        var candidateFlavors = annotationFlavors.ToArray();
        var candidateAnnotations = annotations.ToArray();
        var candidateEdges = edges.ToArray();

        if (candidateFlavors.Length == 0)
        {
            throw new ArgumentException(
                "At least one annotation flavor must be selected.",
                nameof(annotationFlavors));
        }

        var flavorSet = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < candidateFlavors.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var flavor = candidateFlavors[index];
            KebabCaseValidator.Validate(flavor, nameof(annotationFlavors));
            if (!flavorSet.Add(flavor))
            {
                throw new ArgumentException(
                    $"Annotation flavor `{flavor}` is duplicated (index {index}).",
                    nameof(annotationFlavors));
            }
        }
        var orderedFlavors = flavorSet
            .OrderBy(flavor => flavor, StringComparer.Ordinal)
            .ToArray();

        for (var index = 0; index < candidateAnnotations.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var annotation = candidateAnnotations[index]
                ?? throw new ArgumentException(
                    $"Derived projection contains a null annotation at index {index}.",
                    nameof(annotations));
            CanonicalKeyValidator.Validate(
                annotation.SymbolCanonicalKey,
                nameof(annotations));
            ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(annotation.FullName);
            KebabCaseValidator.Validate(annotation.Flavor, nameof(annotations));
            if (!flavorSet.Contains(annotation.Flavor))
            {
                throw new ArgumentException(
                    "Every derived annotation flavor must belong to the selected flavor set; "
                    + $"`{annotation.Flavor}` is not selected (index {index}).",
                    nameof(annotations));
            }
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
                    using var _ = System.Text.Json.JsonDocument.Parse(annotation.ArgsJson);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    throw new ArgumentException(
                        $"Annotation args_json must contain valid JSON (index {index}).",
                        nameof(annotations),
                        ex);
                }
            }

            candidateAnnotations[index] = annotation with { };
        }

        for (var index = 0; index < candidateEdges.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var edge = candidateEdges[index]
                ?? throw new ArgumentException(
                    $"Derived projection contains a null edge at index {index}.",
                    nameof(edges));
            CanonicalKeyValidator.Validate(edge.SourceCanonicalKey, nameof(edges));
            CanonicalKeyValidator.Validate(edge.TargetCanonicalKey, nameof(edges));
            KebabCaseValidator.Validate(edge.Kind, nameof(edges));
            ArgumentNullException.ThrowIfNull(edge.Evidence);
            ArgumentNullException.ThrowIfNull(edge.Evidence.Location);
            if (!string.Equals(edge.Evidence.Producer, producer, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Every derived edge evidence producer must exactly match "
                    + $"the method producer `{producer}` (index {index}).",
                    nameof(edges));
            }

            var logicalMetadata = SnapshotProjectionMetadata(
                edge.Metadata,
                nameof(edges),
                index,
                "edge");
            var evidenceMetadata = SnapshotProjectionMetadata(
                edge.Evidence.Metadata,
                nameof(edges),
                index,
                "evidence");
            candidateEdges[index] = new ProducerEdgeEvidenceFact(
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
                    $"Derived projection could not resolve indexed file `{producingFilePath}`.");

            var symbols = new Dictionary<string, RawAnnotationSymbol>(StringComparer.Ordinal);
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

            // Resolve the entire candidate graph before changing prior projection rows.
            var resolvedAnnotations =
                new List<ResolvedFileAnnotationFact>(candidateAnnotations.Length);
            foreach (var annotation in candidateAnnotations)
            {
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
                new List<ResolvedProducerEdgeEvidenceFact>(candidateEdges.Length);
            for (var index = 0; index < candidateEdges.Length; index++)
            {
                var edge = candidateEdges[index];
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

            ct.ThrowIfCancellationRequested();

            foreach (var flavor in orderedFlavors)
            {
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
                    new { Flavor = flavor, FileId = producingFile.FileId },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

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

            ct.ThrowIfCancellationRequested();
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
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
}
