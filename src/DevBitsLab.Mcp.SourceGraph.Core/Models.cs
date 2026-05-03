namespace DevBitsLab.Mcp.SourceGraph.Core;

public sealed record FileNode(
    long Id,
    string Path,
    byte[] ContentSha256,
    DateTimeOffset LastIndexedAt);

public sealed record Symbol(
    long Id,
    string Name,
    string Fqn,
    SymbolKind Kind,
    long FileId,
    int StartLine,
    int StartCol,
    int EndLine,
    int EndCol,
    string? Signature,
    long? ContainerId,
    string? Modifiers = null,
    int Accessibility = 0,
    string? XmlSummary = null);

public sealed record SymbolReference(
    long Id,
    long SymbolId,
    long FileId,
    int Line,
    int Col,
    ReferenceKind Kind);

public sealed record Edge(
    long Src,
    long Dst,
    EdgeKind Kind);
