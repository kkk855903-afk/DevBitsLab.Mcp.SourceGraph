using System.Text.Json;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    private const int MaximumNativeInteropFiles = 10_000;
    private const int MaximumNativeInteropSymbols = 100_000;
    private const int MaximumNativeInteropAnnotations = 200_000;

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
                        FROM annotations annotation
                        JOIN symbols symbol ON symbol.id = annotation.symbol_id
                        WHERE annotation.flavor IN @Flavors
                          AND (
                              symbol.canonical_key GLOB 'c:*'
                              OR symbol.canonical_key GLOB 'cpp:*'
                          )
                        ORDER BY symbol.canonical_key;
                        """,
                        new { Flavors = flavors },
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

            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return new NativeInteropSnapshotReplacementResult(
                files.Length,
                symbolsByKey.Count,
                annotationCount,
                priorKeys,
                symbolsByKey.Keys
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray());
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
        var symbolCount = 0;
        var annotationCount = 0;
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
            symbolCount = checked(symbolCount + file.Symbols.Count);
            annotationCount = checked(annotationCount + file.Annotations.Count);
            if (symbolCount > MaximumNativeInteropSymbols
                || annotationCount > MaximumNativeInteropAnnotations)
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
                annotationHostKeys.Add(annotation.SymbolCanonicalKey);
                annotations[annotationIndex] = annotation with { };
            }

            result[fileIndex] = new NativeInteropFileFacts(
                path,
                file.ContentSha256.ToArray(),
                file.IndexedAt,
                symbols,
                annotations);
        }

        if (!symbolKeys.SetEquals(annotationHostKeys))
        {
            throw new ArgumentException(
                "Every native interop declaration must own at least one selected "
                + "interop annotation.",
                nameof(source));
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

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static bool IsNativeCanonicalKey(string canonicalKey) =>
        canonicalKey.StartsWith("c:", StringComparison.Ordinal)
        || canonicalKey.StartsWith("cpp:", StringComparison.Ordinal);

    private sealed record ResolvedNativeInteropSymbol(
        long SymbolId,
        long FileId,
        string FilePath);
}
