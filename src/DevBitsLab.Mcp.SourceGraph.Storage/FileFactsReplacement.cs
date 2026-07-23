using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Fully compiled, storage-native facts for one source file. Canonical-key references are
/// resolved by the store inside the same transaction that replaces the file's prior facts.
/// Annotation hosts must be declarations in <see cref="Symbols"/> so every inserted annotation
/// remains owned and removable by this file; edges and references may target external symbols.
/// </summary>
public sealed record FileFactsReplacement(
    string Path,
    byte[] ContentSha256,
    DateTimeOffset IndexedAt,
    bool IsGenerated,
    IReadOnlyList<FileSymbolFact> Symbols,
    IReadOnlyList<FileEdgeFact> Edges,
    IReadOnlyList<FileAnnotationFact> Annotations,
    IReadOnlyList<FileReferenceFact> References);

public sealed record FileSymbolFact(
    string CanonicalKey,
    string Name,
    string Fqn,
    string Kind,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string? Signature,
    string? ContainerCanonicalKey,
    string? Modifiers,
    int Accessibility,
    string? XmlSummary);

public sealed record FileEdgeFact(
    string SourceCanonicalKey,
    string TargetCanonicalKey,
    string Kind,
    IReadOnlyDictionary<string, string>? Metadata,
    FileEvidenceFact? Evidence);

public sealed record FileEvidenceFact(
    SourceLocation Location,
    EvidenceConfidence Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record FileAnnotationFact(
    string SymbolCanonicalKey,
    string Name,
    string FullName,
    string Flavor,
    string? ArgsJson,
    string? AttributeCanonicalKey);

public sealed record FileReferenceFact(
    string TargetCanonicalKey,
    int Line,
    int Column,
    ReferenceKind Kind);

/// <summary>Committed identifiers from one atomic file-facts replacement.</summary>
public sealed record FileFactsReplacementResult(
    long FileId,
    IReadOnlyDictionary<string, long> SymbolIds);
