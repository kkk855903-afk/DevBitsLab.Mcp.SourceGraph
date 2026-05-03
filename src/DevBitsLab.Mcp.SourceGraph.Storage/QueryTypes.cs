using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed record SymbolHit(
    long Id,
    string Name,
    string Fqn,
    SymbolKind Kind,
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
    string? CanonicalKey = null);

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
    long FileId,
    string FilePath,
    int Severity,
    string Code,
    string Message,
    int Line,
    int Col);

/// <summary>One row of <c>list_generated_files</c>: file path + symbol count emitted from that file.</summary>
public sealed record GeneratedFileRow(long FileId, string FilePath, int SymbolCount);
