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
    string? TestFramework = null);

public sealed record ReferenceHit(
    long Id,
    long SymbolId,
    string FilePath,
    int Line,
    int Col,
    ReferenceKind Kind);
