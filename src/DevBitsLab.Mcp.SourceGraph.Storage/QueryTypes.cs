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
    string? Signature);

public sealed record ReferenceHit(
    long Id,
    long SymbolId,
    string FilePath,
    int Line,
    int Col,
    ReferenceKind Kind);
