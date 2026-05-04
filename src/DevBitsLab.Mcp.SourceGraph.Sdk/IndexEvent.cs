using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Stream of facts emitted by an <see cref="ILanguageIndexer"/> for one document. The host
/// translates each event into a SQL-level insert/upsert against the per-scope graph store. We use
/// a closed sealed-record union (no <c>OneOf</c> dependency) so plugin authors can pattern-match
/// without taking a binding on a third-party library.
/// </summary>
/// <remarks>
/// The base class is abstract; the only inhabitants are the nested sealed records. Plugin authors
/// should switch on the concrete type:
/// <code>
/// foreach (var ev in events)
/// {
///     switch (ev)
///     {
///         case IndexEvent.SymbolDeclared sd: /* ... */ break;
///         case IndexEvent.EdgeEmitted ee:    /* ... */ break;
///         /* ... */
///     }
/// }
/// </code>
/// </remarks>
public abstract record IndexEvent
{
    // The constructor is private-protected so only members of this assembly can derive new
    // variants. Third-party plugins consume the closed set; if we add a new variant in a later
    // SDK we bump the major.
    private protected IndexEvent() { }

    /// <summary>
    /// A symbol declaration was discovered (class, method, property, etc.). The host upserts a
    /// row into <c>symbols</c> keyed by <see cref="CanonicalKey"/>. The same canonical key may
    /// appear across multiple files (partial classes); the host de-duplicates per file.
    /// </summary>
    /// <param name="CanonicalKey">
    ///     A globally-unique identity for the symbol — for the built-in C# indexer this is the
    ///     Roslyn <c>DocumentationCommentId</c>; for other languages, plugins MUST produce a key
    ///     stable across edits to the same logical declaration. Acts as the upsert primary key
    ///     in the storage layer so the symbol's integer id stays consistent across re-indexes.
    /// </param>
    /// <param name="Name">Short name (e.g. <c>"Calculate"</c>).</param>
    /// <param name="Fqn">Fully-qualified name (e.g. <c>"Sample.Domain.Calculator.Calculate"</c>).</param>
    /// <param name="Kind">Symbol kind. See <see cref="PluginSymbolKind"/>.</param>
    /// <param name="StartLine">1-based start line.</param>
    /// <param name="StartColumn">1-based start column.</param>
    /// <param name="EndLine">1-based end line.</param>
    /// <param name="EndColumn">1-based end column.</param>
    /// <param name="Signature">Optional signature string (e.g. <c>"int Calculate(int)"</c>).</param>
    /// <param name="ContainerCanonicalKey">Optional canonical key of the enclosing symbol, e.g. the type for a method.</param>
    /// <param name="Modifiers">Optional space-joined modifiers (e.g. <c>"public static readonly"</c>).</param>
    /// <param name="Accessibility">
    ///     Integer mirror of <c>Microsoft.CodeAnalysis.Accessibility</c> (NotApplicable=0, Private=1,
    ///     ProtectedAndInternal=2, Protected=3, Internal=4, ProtectedOrInternal=5, Public=6). Plugins
    ///     for non-CLR languages should map their visibility model into the closest constant.
    /// </param>
    /// <param name="XmlSummary">Optional one-line documentation summary.</param>
    public sealed record SymbolDeclared(
        string CanonicalKey,
        string Name,
        string Fqn,
        PluginSymbolKind Kind,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        string? Signature = null,
        string? ContainerCanonicalKey = null,
        string? Modifiers = null,
        int Accessibility = 0,
        string? XmlSummary = null) : IndexEvent;

    /// <summary>
    /// An edge between two symbols: <see cref="SourceCanonicalKey"/> -[<see cref="EdgeKindName"/>]-> <see cref="TargetCanonicalKey"/>.
    /// The host resolves the keys to symbol ids at flush time. <see cref="EdgeKindName"/> is a
    /// short canonical name from the storage-level <c>EdgeKind</c> enum (e.g. <c>"calls"</c>,
    /// <c>"inherits"</c>, <c>"uses_type"</c>). Plugins targeting v1 SHOULD reuse the built-in
    /// edge kinds; the SDK does not (yet) expose a mechanism for declaring new kinds.
    /// </summary>
    public sealed record EdgeEmitted(
        string SourceCanonicalKey,
        string TargetCanonicalKey,
        string EdgeKindName) : IndexEvent;

    /// <summary>
    /// A symbol carries an attribute. The host turns this into a row in <c>attributes</c>; if the
    /// attribute class is itself indexed (via a <see cref="SymbolDeclared"/> with a matching
    /// canonical key) the join is wired automatically.
    /// </summary>
    /// <param name="SymbolCanonicalKey">The symbol the attribute is attached to.</param>
    /// <param name="AttributeName">Short attribute name (e.g. <c>"HttpGet"</c>).</param>
    /// <param name="AttributeFullName">Fully-qualified attribute name (e.g. <c>"Microsoft.AspNetCore.Mvc.HttpGetAttribute"</c>).</param>
    /// <param name="ArgsJson">Optional JSON payload of constructor + named args, see <c>AttributeArgsJson</c>.</param>
    /// <param name="AttributeClassCanonicalKey">Optional canonical key of the attribute class itself when it's a user-defined type the indexer also walked.</param>
    public sealed record AttributeAttached(
        string SymbolCanonicalKey,
        string AttributeName,
        string AttributeFullName,
        string? ArgsJson = null,
        string? AttributeClassCanonicalKey = null) : IndexEvent;

    /// <summary>
    /// A position-level reference (call site, type-use, member access, ...). The host turns this
    /// into a row in <c>refs</c>. Different from <see cref="EdgeEmitted"/> in that an edge is
    /// per-symbol (denormalised graph) while a reference is per-position (used by
    /// <c>find_references</c> to render exact file:line locations).
    /// </summary>
    /// <param name="TargetCanonicalKey">The symbol being referenced.</param>
    /// <param name="Line">1-based line of the reference.</param>
    /// <param name="Column">1-based column of the reference.</param>
    /// <param name="Kind">
    ///     Reference kind name, mirroring the storage-level <c>ReferenceKind</c> enum:
    ///     <c>"reference"</c>, <c>"call"</c>, <c>"read"</c>, <c>"write"</c>.
    /// </param>
    public sealed record ReferenceFound(
        string TargetCanonicalKey,
        int Line,
        int Column,
        string Kind = "reference") : IndexEvent;

    /// <summary>
    /// Sentinel emitted once per document the indexer fully processed. Lets the host log a single
    /// per-file completion event and gives plugin authors a hook for "I am done with this file".
    /// </summary>
    /// <param name="FilePath">Absolute path the indexer just walked.</param>
    /// <param name="ContentSha256">SHA-256 of the file contents the indexer used (32 bytes).</param>
    /// <param name="IsGenerated">True for source-generated files (skip from default ref queries).</param>
    public sealed record FileScanned(
        string FilePath,
        IReadOnlyList<byte> ContentSha256,
        bool IsGenerated = false) : IndexEvent;
}

/// <summary>
/// Plugin-facing mirror of the storage layer's <c>SymbolKind</c>. Defined in the SDK so plugins
/// don't take a hard reference on the storage assembly. The host maps these constants 1:1 to the
/// internal enum at flush time; new values added in later SDK versions degrade gracefully to
/// <see cref="Other"/>.
/// </summary>
public enum PluginSymbolKind
{
    Other = 0,
    Namespace = 1,
    Class = 2,
    Interface = 3,
    Struct = 4,
    Enum = 5,
    Delegate = 6,
    Method = 7,
    Constructor = 8,
    Property = 9,
    Field = 10,
    Event = 11,
    EnumMember = 12,
    Operator = 13,
    Record = 14,
}
