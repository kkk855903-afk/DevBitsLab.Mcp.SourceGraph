using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using SymbolKinds = DevBitsLab.Mcp.SourceGraph.Sdk.SymbolKinds;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

internal sealed record NativeInteropSnapshotPublicationResult(
    bool IsComplete,
    int FilesPublished,
    int SymbolsPublished,
    int AnnotationsPublished,
    IReadOnlyList<string> StaleCanonicalKeys,
    IReadOnlyList<NativeInteropSnapshotFailure> SnapshotFailures,
    string? Failure);

/// <summary>
/// Converts one complete, content-bound native snapshot into storage facts and replaces every
/// native annotation flavor in one transaction. Prior declaration rows remain available until
/// managed boundary publication succeeds, allowing the coordinator to preserve last-good edges.
/// </summary>
internal sealed class NativeInteropSnapshotPublisher
{
    private static readonly string[] _annotationFlavors =
    [
        InteropAnnotationFlavors.NativeExport,
        InteropAnnotationFlavors.AbiRecord,
    ];

    private readonly IGraphStore _store;

    public NativeInteropSnapshotPublisher(IGraphStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<NativeInteropSnapshotPublicationResult> PublishAsync(
        NativeInteropSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.IsComplete)
        {
            return new NativeInteropSnapshotPublicationResult(
                IsComplete: false,
                FilesPublished: 0,
                SymbolsPublished: 0,
                AnnotationsPublished: 0,
                StaleCanonicalKeys: [],
                SnapshotFailures: snapshot.Failures,
                Failure:
                    "The candidate native snapshot is incomplete; the prior "
                    + "stored snapshot was retained.");
        }

        NativeInteropSnapshotReplacement replacement;
        try
        {
            replacement = Compile(snapshot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or InteropFactPayloadException
                or OverflowException)
        {
            return Failed(snapshot, ex);
        }

        try
        {
            var result = await _store.ReplaceNativeInteropSnapshotAsync(
                    replacement,
                    cancellationToken)
                .ConfigureAwait(false);
            var current = result.CurrentCanonicalKeys.ToHashSet(
                StringComparer.Ordinal);
            var stale = result.PriorCanonicalKeys
                .Where(key => !current.Contains(key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            return new NativeInteropSnapshotPublicationResult(
                IsComplete: true,
                result.FilesUpdated,
                result.SymbolsUpdated,
                result.AnnotationsUpdated,
                stale,
                SnapshotFailures: [],
                Failure: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or OverflowException)
        {
            return Failed(snapshot, ex);
        }
    }

    private static NativeInteropSnapshotReplacement Compile(
        NativeInteropSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var effectiveExports = snapshot.SourceExports
            .ToDictionary(
                export => export.SymbolCanonicalKey,
                StringComparer.Ordinal);
        foreach (var export in snapshot.VerifiedExports)
        {
            effectiveExports[export.SymbolCanonicalKey] = export;
        }

        var files = new Dictionary<string, MutableNativeFile>(PathComparer);
        foreach (var pair in snapshot.ContentHashes
                     .OrderBy(pair => pair.Key, PathComparer)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentHash = pair.Value
                ?? throw new InvalidOperationException(
                    "A native snapshot contains a null content hash.");
            if (!PathsEquivalent(pair.Key, contentHash.FilePath)
                || contentHash.Sha256 is not { Length: 32 })
            {
                throw new InvalidOperationException(
                    "A native snapshot content hash is malformed or mis-keyed.");
            }
            files.Add(
                contentHash.FilePath,
                new MutableNativeFile(
                    contentHash.FilePath,
                    contentHash.Sha256.ToArray()));
        }

        foreach (var export in effectiveExports.Values
                     .OrderBy(
                         export => export.SymbolCanonicalKey,
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = ResolveOwner(files, export.Evidence.Location.FilePath);
            file.Add(
                ExportSymbol(export),
                new FileAnnotationFact(
                    export.SymbolCanonicalKey,
                    "InteropFact",
                    "MedInterop.NativeExport",
                    InteropAnnotationFlavors.NativeExport,
                    InteropFactPayloadCodec.EncodeNativeExport(export),
                    AttributeCanonicalKey: null));
        }
        foreach (var record in snapshot.RecordLayouts
                     .OrderBy(
                         record => record.SymbolCanonicalKey,
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = ResolveOwner(files, record.Evidence.Location.FilePath);
            file.Add(
                RecordSymbol(record),
                new FileAnnotationFact(
                    record.SymbolCanonicalKey,
                    "InteropFact",
                    "MedInterop.AbiRecord",
                    InteropAnnotationFlavors.AbiRecord,
                    InteropFactPayloadCodec.EncodeAbiRecord(record),
                    AttributeCanonicalKey: null));
        }

        var indexedAt = DateTimeOffset.UtcNow;
        return new NativeInteropSnapshotReplacement(
            _annotationFlavors,
            files.Values
                .OrderBy(file => file.Path, PathComparer)
                .ThenBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => file.ToFacts(indexedAt))
                .ToArray());
    }

    private static MutableNativeFile ResolveOwner(
        IReadOnlyDictionary<string, MutableNativeFile> files,
        string evidencePath)
    {
        if (files.TryGetValue(evidencePath, out var file))
        {
            return file;
        }
        var pair = files.FirstOrDefault(item =>
            PathsEquivalent(item.Key, evidencePath));
        return pair.Value
            ?? throw new InvalidOperationException(
                "A native fact is not owned by a content-bound included file.");
    }

    private static FileSymbolFact ExportSymbol(NativeExport export)
    {
        var location = export.Evidence.Location;
        var parameters = string.Join(
            ", ",
            export.Parameters
                .OrderBy(parameter => parameter.Position)
                .Select(parameter =>
                    $"{parameter.Type.CanonicalName} {parameter.Name}"));
        return new FileSymbolFact(
            export.SymbolCanonicalKey,
            export.ExportName,
            string.IsNullOrWhiteSpace(export.LibraryName)
                ? export.ExportName
                : $"{export.LibraryName}!{export.ExportName}",
            SymbolKinds.NativeExport,
            location.StartLine,
            location.StartColumn,
            location.EndLine,
            location.EndColumn,
            $"{export.ReturnType.CanonicalName} "
                + $"{export.ExportName}({parameters})",
            ContainerCanonicalKey: null,
            Modifiers: export.HasCLinkage ? "extern-c" : null,
            Accessibility: 0,
            XmlSummary: null);
    }

    private static FileSymbolFact RecordSymbol(AbiRecordLayout record)
    {
        var location = record.Evidence.Location;
        var name = NativeName(record.SymbolCanonicalKey);
        return new FileSymbolFact(
            record.SymbolCanonicalKey,
            name,
            name,
            SymbolKinds.Struct,
            location.StartLine,
            location.StartColumn,
            location.EndLine,
            location.EndColumn,
            $"struct {name}",
            ContainerCanonicalKey: null,
            Modifiers: null,
            Accessibility: 0,
            XmlSummary: null);
    }

    private static string NativeName(string canonicalKey)
    {
        var separator = canonicalKey.LastIndexOf(
            "::",
            StringComparison.Ordinal);
        return separator >= 0 && separator + 2 < canonicalKey.Length
            ? canonicalKey[(separator + 2)..]
            : canonicalKey;
    }

    private static NativeInteropSnapshotPublicationResult Failed(
        NativeInteropSnapshot snapshot,
        Exception exception)
    {
        const int maximumCharacters = 512;
        var message = $"{exception.GetType().Name}: {exception.Message}";
        if (message.Length > maximumCharacters)
        {
            message = message[..maximumCharacters];
        }
        return new NativeInteropSnapshotPublicationResult(
            IsComplete: false,
            FilesPublished: 0,
            SymbolsPublished: 0,
            AnnotationsPublished: 0,
            StaleCanonicalKeys: [],
            SnapshotFailures: snapshot.Failures,
            Failure: message);
    }

    private static bool PathsEquivalent(string left, string right)
    {
        try
        {
            return PathComparer.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right));
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class MutableNativeFile
    {
        private readonly List<FileSymbolFact> _symbols = [];
        private readonly List<FileAnnotationFact> _annotations = [];
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public MutableNativeFile(string path, byte[] contentSha256)
        {
            Path = path;
            ContentSha256 = contentSha256;
        }

        public string Path { get; }
        public byte[] ContentSha256 { get; }

        public void Add(
            FileSymbolFact symbol,
            FileAnnotationFact annotation)
        {
            if (!_keys.Add(symbol.CanonicalKey))
            {
                throw new InvalidOperationException(
                    $"Native snapshot key `{symbol.CanonicalKey}` is duplicated.");
            }
            _symbols.Add(symbol);
            _annotations.Add(annotation);
        }

        public NativeInteropFileFacts ToFacts(DateTimeOffset indexedAt) =>
            new(
                Path,
                ContentSha256.ToArray(),
                indexedAt,
                _symbols
                    .OrderBy(
                        symbol => symbol.CanonicalKey,
                        StringComparer.Ordinal)
                    .ToArray(),
                _annotations
                    .OrderBy(
                        annotation => annotation.SymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ThenBy(
                        annotation => annotation.Flavor,
                        StringComparer.Ordinal)
                    .ToArray());
    }
}
