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
    string? XmlSummary = null,
    string? TestFramework = null);

/// <summary>
/// Last-touch git history for a symbol, derived from <c>git blame --line-porcelain</c>
/// over the symbol's <c>start_line..end_line</c> span. Cached against
/// <see cref="BlamedContentSha"/> = the source file's <c>content_sha256</c>; if the file
/// hash matches the previously blamed value, the row is reused as-is.
/// </summary>
public sealed record SymbolHistory(
    long SymbolId,
    string? LastCommitSha,
    string? LastAuthor,
    DateTimeOffset? LastAuthoredAt,
    int LineCount,
    byte[]? BlamedContentSha);

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

/// <summary>
/// One attribute attached to an indexed symbol, e.g. <c>[HttpGet("/api/users")]</c>.
/// <see cref="ArgsJson"/> carries a JSON-serialised <see cref="AttributeArgs"/> payload;
/// <see cref="AttributeSymbolId"/> joins back to <c>symbols</c> when the attribute class
/// is itself indexed (user-defined attributes), <c>null</c> otherwise (framework/BCL).
/// </summary>
public sealed record AttributeRecord(
    long SymbolId,
    string Name,
    string FullName,
    string? ArgsJson,
    long? AttributeSymbolId);

/// <summary>
/// Constructor and named arguments captured from a Roslyn <c>AttributeData</c>. Values are
/// language primitives (string, numeric, bool), enum members, <see cref="System.Type"/>-like
/// type display strings, or arrays of the same. Use <see cref="AttributeArgsJson"/> to
/// serialise to the canonical on-disk shape.
/// </summary>
public sealed record AttributeArgs(
    IReadOnlyList<object?> Ctor,
    IReadOnlyDictionary<string, object?> Named);
