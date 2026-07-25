using System.Text.Json;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using EdgeKinds = DevBitsLab.Mcp.SourceGraph.Sdk.EdgeKinds;
using SymbolKinds = DevBitsLab.Mcp.SourceGraph.Sdk.SymbolKinds;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    private const int MaximumNativeInteropFiles = 10_000;
    private const int MaximumNativeInteropSymbols = 100_000;
    private const int MaximumNativeInteropAnnotations = 200_000;
    private const int MaximumNativeInteropEdges = 200_000;
    private const string NativeCallProducer = "clang-native-call";

    public async Task<NativeInteropSnapshotReplacementResult>
        ReplaceNativeInteropSnapshotAsync(
            NativeInteropSnapshotReplacement replacement,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(replacement.AnnotationFlavors);
        ArgumentNullException.ThrowIfNull(replacement.Files);
        ct.ThrowIfCancellationRequested();

        var expectedFlavors = new HashSet<string>(StringComparer.Ordinal)
        {
            InteropAnnotationFlavors.NativeExport,
            InteropAnnotationFlavors.AbiRecord,
        };
        var flavors = replacement.AnnotationFlavors.ToArray();
        if (flavors.Length != expectedFlavors.Count
            || !expectedFlavors.SetEquals(flavors))
        {
            throw new ArgumentException(
                "A native interop snapshot must replace exactly the native-export "
                + "and ABI-record annotation flavors.",
                nameof(replacement));
        }

        var files = SnapshotAndValidateNativeInteropFiles(
            replacement.Files,
            expectedFlavors,
            ct);

        const string upsertFileSql = """
            INSERT INTO files(path, content_sha256, last_indexed_at, is_generated)
            VALUES (@Path, @ContentSha256, @IndexedAt, 0)
            ON CONFLICT(path) DO UPDATE SET
                content_sha256 = excluded.content_sha256,
                last_indexed_at = excluded.last_indexed_at
            RETURNING id;
            """;
        const string upsertSymbolSql = """
            INSERT INTO symbols(
                canonical_key, name, fqn, kind_name, file_id,
                start_line, start_col, end_line, end_col,
                signature, container_id, modifiers, accessibility,
                xml_summary, test_framework)
            VALUES (
                @CanonicalKey, @Name, @Fqn, @Kind, @FileId,
                @StartLine, @StartColumn, @EndLine, @EndColumn,
                @Signature, NULL, @Modifiers, @Accessibility,
                @XmlSummary, NULL)
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
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var priorKeys = (await _connection.QueryAsync<string>(
                    new CommandDefinition(
                        """
                        SELECT DISTINCT symbol.canonical_key
                        FROM symbols symbol
                        WHERE (
                                symbol.canonical_key GLOB 'c:*'
                                OR symbol.canonical_key GLOB 'cpp:*'
                              )
                          AND COALESCE(symbol.modifiers, '')
                              NOT LIKE '%syntax-only%'
                          AND (
                                symbol.kind_name IN @ProjectionKinds
                                OR EXISTS (
                                    SELECT 1
                                    FROM annotations annotation
                                    WHERE annotation.symbol_id = symbol.id
                                      AND annotation.flavor IN @Flavors
                                )
                              )
                        ORDER BY symbol.canonical_key;
                        """,
                        new
                        {
                            Flavors = flavors,
                            ProjectionKinds = new[]
                            {
                                SymbolKinds.Function,
                                SymbolKinds.Method,
                                SymbolKinds.Struct,
                                SymbolKinds.Union,
                                SymbolKinds.Enum,
                                SymbolKinds.TypeAlias,
                            },
                        },
                        transaction: tx,
                        cancellationToken: ct))
                    .ConfigureAwait(false))
                .ToArray();

            var fileIdsByPath = new Dictionary<string, long>(PathComparer);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var fileId = await _connection.ExecuteScalarAsync<long>(
                        new CommandDefinition(
                            upsertFileSql,
                            new
                            {
                                file.Path,
                                file.ContentSha256,
                                IndexedAt =
                                    file.IndexedAt.ToUnixTimeMilliseconds(),
                            },
                            transaction: tx,
                            cancellationToken: ct))
                    .ConfigureAwait(false);
                fileIdsByPath.Add(file.Path, fileId);
            }

            var symbolsByKey =
                new Dictionary<string, ResolvedNativeInteropSymbol>(
                    StringComparer.Ordinal);
            foreach (var file in files)
            {
                var fileId = fileIdsByPath[file.Path];
                foreach (var symbol in file.Symbols)
                {
                    ct.ThrowIfCancellationRequested();
                    var symbolId = await _connection.ExecuteScalarAsync<long>(
                            new CommandDefinition(
                                upsertSymbolSql,
                                new
                                {
                                    symbol.CanonicalKey,
                                    symbol.Name,
                                    symbol.Fqn,
                                    symbol.Kind,
                                    FileId = fileId,
                                    symbol.StartLine,
                                    symbol.StartColumn,
                                    symbol.EndLine,
                                    symbol.EndColumn,
                                    symbol.Signature,
                                    symbol.Modifiers,
                                    symbol.Accessibility,
                                    symbol.XmlSummary,
                                },
                                transaction: tx,
                                cancellationToken: ct))
                        .ConfigureAwait(false);
                    symbolsByKey.Add(
                        symbol.CanonicalKey,
                        new ResolvedNativeInteropSymbol(
                            symbolId,
                            fileId,
                            file.Path));
                }
            }

            // Remove only call occurrences produced by this projection. Logical edges with
            // independent evidence survive and have their compatibility payload resynchronised.
            await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE edges AS edge
                    SET payload = (
                        SELECT NULLIF(evidence.payload, '')
                        FROM edge_evidence evidence
                        WHERE evidence.src = edge.src
                          AND evidence.dst = edge.dst
                          AND evidence.kind_name = edge.kind_name
                          AND evidence.producer <> @Producer
                        ORDER BY evidence.id
                        LIMIT 1
                    )
                    WHERE edge.kind_name = @Kind
                      AND EXISTS (
                          SELECT 1
                          FROM edge_evidence owned
                          WHERE owned.src = edge.src
                            AND owned.dst = edge.dst
                            AND owned.kind_name = edge.kind_name
                            AND owned.producer = @Producer
                      );

                    DELETE FROM edges
                    WHERE kind_name = @Kind
                      AND EXISTS (
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
                    WHERE kind_name = @Kind
                      AND producer = @Producer;
                    """,
                    new
                    {
                        Kind = EdgeKinds.Calls,
                        Producer = NativeCallProducer,
                    },
                    transaction: tx,
                    cancellationToken: ct))
                .ConfigureAwait(false);

            await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM annotations
                    WHERE flavor IN @Flavors
                      AND symbol_id IN (
                          SELECT id
                          FROM symbols
                          WHERE canonical_key GLOB 'c:*'
                             OR canonical_key GLOB 'cpp:*'
                      );
                    """,
                    new { Flavors = flavors },
                    transaction: tx,
                    cancellationToken: ct))
                .ConfigureAwait(false);

            var annotationCount = 0;
            foreach (var file in files)
            {
                var expectedFileId = fileIdsByPath[file.Path];
                foreach (var annotation in file.Annotations)
                {
                    ct.ThrowIfCancellationRequested();
                    var host = symbolsByKey[annotation.SymbolCanonicalKey];
                    if (host.FileId != expectedFileId)
                    {
                        throw new InvalidOperationException(
                            "A native interop annotation host moved to a different "
                            + "candidate file during publication.");
                    }

                    await _connection.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT INTO annotations(
                                symbol_id, name, full_name, flavor,
                                args_json, attribute_symbol_id)
                            VALUES (
                                @SymbolId, @Name, @FullName, @Flavor,
                                @ArgsJson, NULL);
                            """,
                            new
                            {
                                SymbolId = host.SymbolId,
                                annotation.Name,
                                annotation.FullName,
                                annotation.Flavor,
                                annotation.ArgsJson,
                            },
                            transaction: tx,
                            cancellationToken: ct))
                        .ConfigureAwait(false);
                    annotationCount++;
                }
            }

            var edgeCount = 0;
            foreach (var file in files)
            {
                var producingFileId = fileIdsByPath[file.Path];
                foreach (var edge in file.Edges)
                {
                    ct.ThrowIfCancellationRequested();
                    var sourceSymbol = symbolsByKey[edge.SourceCanonicalKey];
                    var targetSymbol = symbolsByKey[edge.TargetCanonicalKey];
                    if (sourceSymbol.FileId != producingFileId)
                    {
                        throw new InvalidOperationException(
                            "Native call evidence is not owned by its source declaration file.");
                    }
                    var payload = SerializeMetadata(edge.Metadata);
                    await _connection.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT OR IGNORE INTO edges(
                                src, dst, kind_name, payload)
                            VALUES (@Src, @Dst, @Kind, @Payload);
                            """,
                            new
                            {
                                Src = sourceSymbol.SymbolId,
                                Dst = targetSymbol.SymbolId,
                                edge.Kind,
                                Payload = payload,
                            },
                            transaction: tx,
                            cancellationToken: ct))
                        .ConfigureAwait(false);

                    var evidence = edge.Evidence!;
                    await _connection.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT OR IGNORE INTO edge_evidence(
                                src, dst, kind_name, producing_file_id,
                                file_path, start_line, start_col,
                                end_line, end_col, confidence, producer, payload)
                            VALUES (
                                @Src, @Dst, @Kind, @ProducingFileId,
                                @FilePath, @StartLine, @StartColumn,
                                @EndLine, @EndColumn, @Confidence, @Producer,
                                @Payload);
                            """,
                            new
                            {
                                Src = sourceSymbol.SymbolId,
                                Dst = targetSymbol.SymbolId,
                                edge.Kind,
                                ProducingFileId = producingFileId,
                                evidence.Location.FilePath,
                                evidence.Location.StartLine,
                                evidence.Location.StartColumn,
                                evidence.Location.EndLine,
                                evidence.Location.EndColumn,
                                Confidence = (int)evidence.Confidence,
                                evidence.Producer,
                                Payload = SerializeMetadata(
                                        evidence.Metadata ?? edge.Metadata)
                                    ?? string.Empty,
                            },
                            transaction: tx,
                            cancellationToken: ct))
                        .ConfigureAwait(false);
                    await _connection.ExecuteAsync(new CommandDefinition(
                            """
                            UPDATE edges
                            SET payload = (
                                SELECT NULLIF(evidence.payload, '')
                                FROM edge_evidence evidence
                                WHERE evidence.src = @Src
                                  AND evidence.dst = @Dst
                                  AND evidence.kind_name = @Kind
                                ORDER BY evidence.id
                                LIMIT 1
                            )
                            WHERE src = @Src
                              AND dst = @Dst
                              AND kind_name = @Kind;
                            """,
                            new
                            {
                                Src = sourceSymbol.SymbolId,
                                Dst = targetSymbol.SymbolId,
                                edge.Kind,
                            },
                            transaction: tx,
                            cancellationToken: ct))
                        .ConfigureAwait(false);
                    edgeCount++;
                }
            }

            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return new NativeInteropSnapshotReplacementResult(
                files.Length,
                symbolsByKey.Count,
                annotationCount,
                priorKeys,
                symbolsByKey.Keys
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray())
            {
                EdgesUpdated = edgeCount,
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static NativeInteropFileFacts[]
        SnapshotAndValidateNativeInteropFiles(
            IReadOnlyList<NativeInteropFileFacts> source,
            IReadOnlySet<string> expectedFlavors,
            CancellationToken ct)
    {
        if (source.Count > MaximumNativeInteropFiles)
        {
            throw new ArgumentException(
                $"A native interop snapshot exceeds the "
                + $"{MaximumNativeInteropFiles}-file limit.",
                nameof(source));
        }

        var result = new NativeInteropFileFacts[source.Count];
        var paths = new HashSet<string>(PathComparer);
        var symbolKeys = new HashSet<string>(StringComparer.Ordinal);
        var annotationHostKeys = new HashSet<string>(StringComparer.Ordinal);
        var requiredAnnotationKeys = new HashSet<string>(StringComparer.Ordinal);
        var callOccurrences = new HashSet<string>(StringComparer.Ordinal);
        var symbolCount = 0;
        var annotationCount = 0;
        var edgeCount = 0;
        for (var fileIndex = 0; fileIndex < source.Count; fileIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var file = source[fileIndex]
                ?? throw new ArgumentException(
                    $"Native interop file {fileIndex} is null.",
                    nameof(source));
            ArgumentException.ThrowIfNullOrWhiteSpace(file.Path);
            if (!Path.IsPathFullyQualified(file.Path))
            {
                throw new ArgumentException(
                    $"Native interop file {fileIndex} must use an absolute path.",
                    nameof(source));
            }
            var path = Path.GetFullPath(file.Path);
            if (!paths.Add(path))
            {
                throw new ArgumentException(
                    $"Native interop file path `{path}` is duplicated.",
                    nameof(source));
            }
            ArgumentNullException.ThrowIfNull(file.ContentSha256);
            if (file.ContentSha256.Length != 32)
            {
                throw new ArgumentException(
                    $"Native interop file {fileIndex} does not carry a SHA-256 hash.",
                    nameof(source));
            }
            ArgumentNullException.ThrowIfNull(file.Symbols);
            ArgumentNullException.ThrowIfNull(file.Annotations);
            ArgumentNullException.ThrowIfNull(file.Edges);
            symbolCount = checked(symbolCount + file.Symbols.Count);
            annotationCount = checked(annotationCount + file.Annotations.Count);
            edgeCount = checked(edgeCount + file.Edges.Count);
            if (symbolCount > MaximumNativeInteropSymbols
                || annotationCount > MaximumNativeInteropAnnotations
                || edgeCount > MaximumNativeInteropEdges)
            {
                throw new ArgumentException(
                    "Native interop snapshot declaration or annotation limit exceeded.",
                    nameof(source));
            }

            var symbols = file.Symbols.ToArray();
            var localKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var symbolIndex = 0;
                 symbolIndex < symbols.Length;
                 symbolIndex++)
            {
                var symbol = symbols[symbolIndex]
                    ?? throw new ArgumentException(
                        $"Native interop symbol {fileIndex}:{symbolIndex} is null.",
                        nameof(source));
                ValidateNativeInteropSymbol(symbol, source);
                if (!localKeys.Add(symbol.CanonicalKey)
                    || !symbolKeys.Add(symbol.CanonicalKey))
                {
                    throw new ArgumentException(
                        $"Native interop canonical key "
                        + $"`{symbol.CanonicalKey}` is duplicated.",
                        nameof(source));
                }
                if (symbol.Kind == SymbolKinds.NativeExport)
                {
                    requiredAnnotationKeys.Add(symbol.CanonicalKey);
                }
                symbols[symbolIndex] = symbol with { };
            }

            var annotations = file.Annotations.ToArray();
            for (var annotationIndex = 0;
                 annotationIndex < annotations.Length;
                 annotationIndex++)
            {
                var annotation = annotations[annotationIndex]
                    ?? throw new ArgumentException(
                        $"Native interop annotation "
                        + $"{fileIndex}:{annotationIndex} is null.",
                        nameof(source));
                ValidateNativeInteropAnnotation(
                    annotation,
                    localKeys,
                    expectedFlavors,
                    source);
                if (!annotationHostKeys.Add(
                        annotation.SymbolCanonicalKey))
                {
                    throw new ArgumentException(
                        "A native interop annotation host is duplicated.",
                        nameof(source));
                }
                annotations[annotationIndex] = annotation with { };
            }

            var edges = file.Edges.ToArray();
            for (var edgeIndex = 0;
                 edgeIndex < edges.Length;
                 edgeIndex++)
            {
                var edge = edges[edgeIndex]
                    ?? throw new ArgumentException(
                        $"Native interop edge {fileIndex}:{edgeIndex} is null.",
                        nameof(source));
                ValidateNativeCallEdge(edge, localKeys, path, source);
                var evidence = edge.Evidence!;
                var occurrence = string.Join(
                    "\n",
                    edge.SourceCanonicalKey,
                    edge.TargetCanonicalKey,
                    evidence.Location.FilePath,
                    evidence.Location.StartLine,
                    evidence.Location.StartColumn,
                    evidence.Location.EndLine,
                    evidence.Location.EndColumn);
                if (!callOccurrences.Add(occurrence))
                {
                    throw new ArgumentException(
                        "A native call occurrence is duplicated.",
                        nameof(source));
                }
                edges[edgeIndex] = edge with
                {
                    Metadata = CopyMetadata(edge.Metadata),
                    Evidence = evidence with
                    {
                        Metadata = CopyMetadata(evidence.Metadata),
                    },
                };
            }

            result[fileIndex] = new NativeInteropFileFacts(
                path,
                file.ContentSha256.ToArray(),
                file.IndexedAt,
                symbols,
                annotations)
            {
                Edges = edges,
            };
        }

        if (!requiredAnnotationKeys.IsSubsetOf(annotationHostKeys))
        {
            throw new ArgumentException(
                "Every native export declaration must own one selected "
                + "interop annotation.",
                nameof(source));
        }
        foreach (var edge in result.SelectMany(file => file.Edges))
        {
            if (!symbolKeys.Contains(edge.TargetCanonicalKey))
            {
                throw new ArgumentException(
                    "A native call target must be a definition in the same complete snapshot.",
                    nameof(source));
            }
        }

        return result
            .OrderBy(file => file.Path, PathComparer)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateNativeInteropSymbol(
        FileSymbolFact symbol,
        IReadOnlyList<NativeInteropFileFacts> source)
    {
        CanonicalKeyValidator.Validate(symbol.CanonicalKey, nameof(source));
        if (!IsNativeCanonicalKey(symbol.CanonicalKey))
        {
            throw new ArgumentException(
                "Native interop declarations must use a lower-case c: or cpp: "
                + "canonical-key scheme.",
                nameof(source));
        }
        var kindMatchesKey =
            (IsNativeKey(symbol.CanonicalKey, "E")
                && symbol.Kind == SymbolKinds.NativeExport)
            || (IsNativeKey(symbol.CanonicalKey, "F")
                && symbol.Kind is SymbolKinds.Function or SymbolKinds.Method)
            || (IsNativeKey(symbol.CanonicalKey, "T")
                && symbol.Kind is
                    SymbolKinds.Struct
                    or SymbolKinds.Union
                    or SymbolKinds.Enum)
            || (IsNativeKey(symbol.CanonicalKey, "A")
                && symbol.Kind == SymbolKinds.TypeAlias);
        if (!kindMatchesKey)
        {
            throw new ArgumentException(
                "A native interop declaration kind does not match its canonical-key prefix.",
                nameof(source));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol.Fqn);
        KebabCaseValidator.Validate(symbol.Kind, nameof(source));
        if (symbol.ContainerCanonicalKey is not null)
        {
            throw new ArgumentException(
                "Native interop snapshot declarations cannot specify graph containers.",
                nameof(source));
        }
        if (symbol.StartLine <= 0
            || symbol.StartColumn <= 0
            || symbol.EndLine < symbol.StartLine
            || symbol.EndColumn <= 0
            || (symbol.EndLine == symbol.StartLine
                && symbol.EndColumn < symbol.StartColumn))
        {
            throw new ArgumentException(
                "Native interop declaration locations must be valid 1-based ranges.",
                nameof(source));
        }
    }

    private static void ValidateNativeInteropAnnotation(
        FileAnnotationFact annotation,
        IReadOnlySet<string> localKeys,
        IReadOnlySet<string> expectedFlavors,
        IReadOnlyList<NativeInteropFileFacts> source)
    {
        CanonicalKeyValidator.Validate(
            annotation.SymbolCanonicalKey,
            nameof(source));
        if (!localKeys.Contains(annotation.SymbolCanonicalKey))
        {
            throw new ArgumentException(
                "Native interop annotations must target a declaration in the "
                + "same candidate file.",
                nameof(source));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotation.FullName);
        if (!expectedFlavors.Contains(annotation.Flavor))
        {
            throw new ArgumentException(
                $"Native interop annotation flavor `{annotation.Flavor}` "
                + "is not selected.",
                nameof(source));
        }
        if ((annotation.Flavor == InteropAnnotationFlavors.NativeExport
                && !IsNativeKey(annotation.SymbolCanonicalKey, "E"))
            || (annotation.Flavor == InteropAnnotationFlavors.AbiRecord
                && !IsNativeKey(annotation.SymbolCanonicalKey, "T")))
        {
            throw new ArgumentException(
                "A native interop annotation flavor does not match its host declaration.",
                nameof(source));
        }
        if (annotation.AttributeCanonicalKey is not null)
        {
            throw new ArgumentException(
                "Native interop facts cannot reference attribute declarations.",
                nameof(source));
        }
        if (annotation.ArgsJson is null)
        {
            throw new ArgumentException(
                "Native interop annotations require a normalized JSON payload.",
                nameof(source));
        }
        try
        {
            using var _ = JsonDocument.Parse(annotation.ArgsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "Native interop annotation payload is not valid JSON.",
                nameof(source),
                ex);
        }
    }

    private static void ValidateNativeCallEdge(
        FileEdgeFact edge,
        IReadOnlySet<string> localKeys,
        string ownerPath,
        IReadOnlyList<NativeInteropFileFacts> source)
    {
        CanonicalKeyValidator.Validate(
            edge.SourceCanonicalKey,
            nameof(source));
        CanonicalKeyValidator.Validate(
            edge.TargetCanonicalKey,
            nameof(source));
        if (!localKeys.Contains(edge.SourceCanonicalKey)
            || !IsNativeCanonicalKey(edge.SourceCanonicalKey)
            || !IsNativeCanonicalKey(edge.TargetCanonicalKey))
        {
            throw new ArgumentException(
                "Native calls must originate from a local c/cpp declaration and target "
                + "another c/cpp declaration.",
                nameof(source));
        }
        if (!string.Equals(edge.Kind, EdgeKinds.Calls, StringComparison.Ordinal)
            || edge.Evidence is null
            || !string.Equals(
                edge.Evidence.Producer,
                NativeCallProducer,
                StringComparison.Ordinal)
            || edge.Evidence.Confidence != EvidenceConfidence.Exact)
        {
            throw new ArgumentException(
                "Native snapshot edges must be exact clang-native-call occurrences.",
                nameof(source));
        }
        var location = edge.Evidence.Location;
        if (location is null
            || !PathsEquivalent(location.FilePath, ownerPath)
            || location.StartLine <= 0
            || location.StartColumn <= 0
            || location.EndLine < location.StartLine
            || location.EndColumn <= 0
            || (location.EndLine == location.StartLine
                && location.EndColumn < location.StartColumn))
        {
            throw new ArgumentException(
                "Native call evidence must be a valid range in its source owner file.",
                nameof(source));
        }
        ValidateMetadata(edge.Metadata, source);
        ValidateMetadata(edge.Evidence.Metadata, source);
    }

    private static IReadOnlyDictionary<string, string>? CopyMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return null;
        }
        return metadata
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
    }

    private static void ValidateMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyList<NativeInteropFileFacts> source)
    {
        const int maximumEntries = 256;
        const int maximumCharacters = 32 * 1024;
        if (metadata is null)
        {
            return;
        }
        if (metadata.Count > maximumEntries
            || metadata.Any(item =>
                string.IsNullOrWhiteSpace(item.Key)
                || item.Key.Length > maximumCharacters
                || item.Value is null
                || item.Value.Length > maximumCharacters))
        {
            throw new ArgumentException(
                "Native call metadata is malformed or exceeds its bound.",
                nameof(source));
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static bool IsNativeCanonicalKey(string canonicalKey) =>
        canonicalKey.StartsWith("c:", StringComparison.Ordinal)
        || canonicalKey.StartsWith("cpp:", StringComparison.Ordinal);

    private static bool IsNativeKey(
        string canonicalKey,
        string kindPrefix) =>
        canonicalKey.StartsWith(
            $"c:{kindPrefix}:",
            StringComparison.Ordinal)
        || canonicalKey.StartsWith(
            $"cpp:{kindPrefix}:",
            StringComparison.Ordinal);

    private sealed record ResolvedNativeInteropSymbol(
        long SymbolId,
        long FileId,
        string FilePath);
}
