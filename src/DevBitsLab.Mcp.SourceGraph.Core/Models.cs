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
/// How strongly an analyzer established one relationship. Values are ordered from weakest to
/// strongest so callers can apply a minimum-confidence filter without language-specific rules.
/// </summary>
public enum EvidenceConfidence
{
    Inferred = 0,
    Semantic = 1,
    Exact = 2,
}

/// <summary>
/// One 1-based source range supporting a graph relationship.
/// </summary>
public sealed record SourceLocation(
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>
/// One independently attributable proof for a logical edge. <see cref="ProducingFileId"/> is the
/// indexed file whose pass emitted this evidence and is used for precise incremental cleanup;
/// it need not be the file that declares the edge's source symbol. <see cref="Producer"/> names
/// the analyzer that made the determination. Optional metadata is stored per evidence so two
/// call sites or bindings between the same symbols cannot overwrite one another.
/// </summary>
public sealed record Evidence(
    long ProducingFileId,
    SourceLocation Location,
    EvidenceConfidence Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// One edge between two symbols in the graph. <see cref="Kind"/> is a kebab-case identifier
/// (<c>"calls"</c>, <c>"inherits"</c>, …); the SDK's <c>EdgeKinds</c> static class supplies
/// the well-known constants. <see cref="Metadata"/> carries per-edge facts (binding paths,
/// event names, prop names) retained for compatibility with existing payload-aware queries.
/// <see cref="Evidence"/> identifies the exact source range and confidence for this emission.
/// Repeating the same logical <c>(src, dst, kind)</c> edge with different evidence preserves
/// every occurrence.
/// </summary>
public sealed record Edge(
    long Src,
    long Dst,
    string Kind,
    IReadOnlyDictionary<string, string>? Metadata = null,
    Evidence? Evidence = null);

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

/// <summary>
/// One project that the indexer could not compile during a cold or incremental indexing pass.
/// Surfaces as part of <c>IndexResult.FailedProjects</c> and is persisted on the scope row in
/// <c>_meta.db</c> so <c>list_scopes</c> can attribute partial-scope status to specific
/// projects without needing the indexer to be live. <see cref="Reason"/> is the failing
/// exception's <c>Message</c>, truncated via <see cref="FailureMessage.Truncate"/>; full
/// stack traces are written to the indexer log, not to clients.
/// </summary>
public sealed record ProjectFailure(string Name, string Reason);

/// <summary>
/// One file whose Pass 1 symbol walk threw during a cold or incremental indexing pass.
/// Surfaces as part of <c>IndexResult.FailedFiles</c> and is persisted on the scope row in
/// <c>_meta.db</c>. The file's prior store state is preserved untouched (no reconcile, no
/// Pass 2/3 for it) until the next successful walk. <see cref="Reason"/> is the failing
/// exception's <c>Message</c>, truncated via <see cref="FailureMessage.Truncate"/>.
/// </summary>
public sealed record FileFailure(string Path, string Reason);

/// <summary>
/// Helpers for normalising failure-record text. Exception messages are sometimes stack-trace
/// shaped or include very long paths; truncating keeps <c>list_scopes</c> output readable
/// without losing the leading bytes that usually carry the actionable detail.
/// </summary>
public static class FailureMessage
{
    /// <summary>Maximum length retained on a failure <c>Reason</c> string.</summary>
    public const int MaxReasonLength = 256;

    /// <summary>
    /// Returns <paramref name="message"/> unchanged when shorter than <paramref name="max"/>
    /// (default <see cref="MaxReasonLength"/>); otherwise returns the leading <c>max - 1</c>
    /// characters with a trailing ellipsis (<c>…</c>) so callers can tell the value was cut.
    /// Null or empty input is normalised to the empty string.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="max"/> is less than 2. Truncation needs at least one
    /// content character plus the ellipsis suffix, so smaller values can't produce a
    /// meaningful result.
    /// </exception>
    public static string Truncate(string? message, int max = MaxReasonLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 2);
        if (string.IsNullOrEmpty(message)) return string.Empty;
        if (message.Length <= max) return message;
        return message.AsSpan(0, max - 1).ToString() + "…";
    }
}
