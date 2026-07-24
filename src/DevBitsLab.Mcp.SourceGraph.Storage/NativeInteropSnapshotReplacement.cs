namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// One physical native source/header file in a complete interop snapshot. Symbols and
/// annotations are restricted to normalized native interop declarations; ordinary language
/// facts already owned by the same file are preserved.
/// </summary>
public sealed record NativeInteropFileFacts(
    string Path,
    byte[] ContentSha256,
    DateTimeOffset IndexedAt,
    IReadOnlyList<FileSymbolFact> Symbols,
    IReadOnlyList<FileAnnotationFact> Annotations);

/// <summary>
/// Complete native declaration projection replaced in one database transaction. An empty file
/// list is a valid zero-fact snapshot and clears the selected annotation flavors only from
/// lower-case <c>c:</c>/<c>cpp:</c> owners; managed ABI-record annotations are not part of this
/// projection.
/// </summary>
public sealed record NativeInteropSnapshotReplacement(
    IReadOnlyCollection<string> AnnotationFlavors,
    IReadOnlyList<NativeInteropFileFacts> Files);

public sealed record NativeInteropSnapshotReplacementResult(
    int FilesUpdated,
    int SymbolsUpdated,
    int AnnotationsUpdated,
    IReadOnlyList<string> PriorCanonicalKeys,
    IReadOnlyList<string> CurrentCanonicalKeys);
