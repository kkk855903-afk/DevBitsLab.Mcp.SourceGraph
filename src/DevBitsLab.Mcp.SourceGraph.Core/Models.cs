using System.Collections.Generic;

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
    string Kind,
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

/// <summary>
/// One edge between two symbols in the graph. <see cref="Kind"/> is a kebab-case identifier
/// (<c>"calls"</c>, <c>"inherits"</c>, …); the SDK's <c>EdgeKinds</c> static class supplies
/// the well-known constants. <see cref="Metadata"/> carries per-edge facts (binding paths,
/// event names, prop names) that don't fit in the <c>(src, dst, kind)</c> triple — stored as
/// JSON in the <c>edges.payload</c> column when non-null.
/// </summary>
public sealed record Edge(
    long Src,
    long Dst,
    string Kind,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// One annotation attached to an indexed symbol — a .NET <c>[Attribute]</c>, a TS
/// <c>@Decorator</c>, a Vue <c>v-directive</c>, etc. <see cref="Flavor"/> discriminates the
/// annotation pattern across languages (e.g. <c>"csharp-attribute"</c>, <c>"ts-decorator"</c>,
/// <c>"vue-directive"</c>). <see cref="ArgsJson"/> carries a JSON-serialised
/// <see cref="AttributeArgs"/> payload; <see cref="AttributeSymbolId"/> joins back to
/// <c>symbols</c> when the annotation's defining type is itself indexed (user-defined),
/// <c>null</c> otherwise.
/// </summary>
public sealed record AnnotationRecord(
    long SymbolId,
    string Name,
    string FullName,
    string Flavor,
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

/// <summary>
/// One Roslyn diagnostic record persisted to the graph. <see cref="Severity"/> mirrors
/// <c>Microsoft.CodeAnalysis.DiagnosticSeverity</c> (Hidden=0, Info=1, Warning=2, Error=3);
/// storing as int avoids a cross-package enum dependency in <c>Core</c>. <see cref="SymbolId"/>
/// is <c>null</c> for file-scoped diagnostics whose source span doesn't fall within an
/// indexed declaration.
/// </summary>
public sealed record DiagnosticRecord(
    long? SymbolId,
    long FileId,
    int Severity,
    string Code,
    string Message,
    int Line,
    int Col);
