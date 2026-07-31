using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed record SymbolHit(
    long Id,
    string Name,
    string Fqn,
    string Kind,
    string FilePath,
    int StartLine,
    int StartCol,
    int EndLine,
    int EndCol,
    string? Signature,
    string? Modifiers = null,
    int Accessibility = 0,
    string? XmlSummary = null,
    bool IsGenerated = false,
    string? TestFramework = null,
    /// <summary>Roslyn DocumentationCommentId; identifies the same symbol across scope DBs.</summary>
    string? CanonicalKey = null,
    /// <summary>
    /// When the hit comes from an edge-walking query (<c>list_callers</c>, <c>list_callees</c>,
    /// <c>neighborhood</c>), this carries the originating <c>edges.payload</c> column verbatim —
    /// itself nullable when the originating edge had no
    /// <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.IndexEvent.EdgeEmitted.Metadata"/>. When the hit
    /// comes from any other query (symbol search, FTS, annotation scan, etc.), this is always
    /// <c>null</c>. The renderer decodes the JSON into per-edge metadata sub-lines; storage does
    /// not validate the shape.
    /// </summary>
    string? PayloadJson = null);

public sealed record ReferenceHit(
    long Id,
    long SymbolId,
    string FilePath,
    int Line,
    int Col,
    ReferenceKind Kind,
    bool IsGenerated = false);

/// <summary>
/// One Roslyn diagnostic captured during indexing. <see cref="Severity"/> matches
/// <c>Microsoft.CodeAnalysis.DiagnosticSeverity</c> (Hidden=0, Info=1, Warning=2, Error=3).
/// <see cref="SymbolId"/> is <c>null</c> when the diagnostic's location lies between symbol
/// boundaries (file-scoped).
/// </summary>
public sealed record DiagnosticHit(
    long Id,
    long? SymbolId,
    string? SymbolFqn,
    string? SymbolCanonicalKey,
    long FileId,
    string FilePath,
    int Severity,
    string Code,
    string Message,
    int Line,
    int Col);

/// <summary>One row of <c>list_generated_files</c>: file path + symbol count emitted from that file.</summary>
public sealed record GeneratedFileRow(long FileId, string FilePath, int SymbolCount);

/// <summary>
/// Both endpoints of one edge plus its raw <c>edges.payload</c> JSON. Returned by the payload-
/// projecting helpers (<see cref="IGraphStore.FindDataBindingsAsync"/>,
/// <see cref="IGraphStore.FindEventHandlersAsync"/>) so the tool renderer has the source and
/// target symbols on hand without a second round-trip.
/// </summary>
public sealed record EdgeWithPayload(SymbolHit Source, SymbolHit Target, string? PayloadJson);

/// <summary>
/// One side of a logical edge returned by an evidence-first traversal query.
/// <see cref="Relation"/> is the edge's actual stored kind, including when the caller requested
/// every kind. Storage only returns rows whose logical edge has at least one matching
/// <c>edge_evidence</c> occurrence; the tool layer loads and renders those occurrences.
/// </summary>
public sealed record EdgeTraversalHit(
    SymbolHit Symbol,
    string Relation,
    string? PayloadJson);

public enum SourceTextSearchMode
{
    Literal,
    Regex,
}

public sealed record SourceTextSearchHit(
    string FilePath,
    int Line,
    int Column,
    int EndColumn,
    int MatchCount,
    string LineText,
    IReadOnlyList<string> BeforeContext,
    IReadOnlyList<string> AfterContext);

public sealed record SourceTextSearchPage(
    IReadOnlyList<SourceTextSearchHit> Hits,
    long TotalMatches,
    long TotalMatchingLines,
    int CandidateDocuments,
    bool Truncated);

public sealed record SourceDocumentCoverage(
    IReadOnlyList<string> EligibleGraphFiles,
    IReadOnlyList<string> IndexedSourceDocuments,
    IReadOnlyList<string> MissingSourceDocuments);

/// <summary>
/// One persisted annotation together with its declaration owner. <see cref="AnnotationId"/> is
/// the stable, store-local cursor used by <see cref="IGraphStore.ListAnnotationsByFlavorAsync"/>.
/// Payload consumers must still validate <see cref="ArgsJson"/> with the codec for the selected
/// <see cref="Flavor"/> before treating it as a domain fact.
/// </summary>
public sealed record StoredAnnotationRow(
    long AnnotationId,
    long SymbolId,
    string SymbolCanonicalKey,
    long FileId,
    string FilePath,
    string Name,
    string FullName,
    string Flavor,
    string? ArgsJson,
    long? AttributeSymbolId);
