using System.Collections.Generic;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;

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
    /// <remarks>
    /// <para><see cref="CanonicalKey"/> MUST be of the form <c>&lt;scheme&gt;:&lt;rest&gt;</c> with
    /// <c>&lt;scheme&gt;</c> in the SDK's reserved-and-enforced set
    /// (see <see cref="CanonicalKeyValidator"/>). The constructor throws
    /// <see cref="System.ArgumentException"/> on a malformed key.</para>
    ///
    /// <para><see cref="Kind"/> MUST be a kebab-case identifier (validated via
    /// <see cref="KebabCaseValidator"/>); use the <see cref="SymbolKinds"/> constants for the
    /// well-known values, or any plugin-defined kebab-case name for new ones.</para>
    /// </remarks>
    public sealed record SymbolDeclared : IndexEvent
    {
        public SymbolDeclared(
            string canonicalKey,
            string name,
            string fqn,
            string kind,
            int startLine,
            int startColumn,
            int endLine,
            int endColumn,
            string? signature = null,
            string? containerCanonicalKey = null,
            string? modifiers = null,
            int accessibility = 0,
            string? xmlSummary = null)
        {
            CanonicalKeyValidator.Validate(canonicalKey, nameof(canonicalKey));
            KebabCaseValidator.Validate(kind, nameof(kind));
            if (containerCanonicalKey is not null)
            {
                CanonicalKeyValidator.Validate(containerCanonicalKey, nameof(containerCanonicalKey));
            }

            CanonicalKey = canonicalKey;
            Name = name;
            Fqn = fqn;
            Kind = kind;
            StartLine = startLine;
            StartColumn = startColumn;
            EndLine = endLine;
            EndColumn = endColumn;
            Signature = signature;
            ContainerCanonicalKey = containerCanonicalKey;
            Modifiers = modifiers;
            Accessibility = accessibility;
            XmlSummary = xmlSummary;
        }

        /// <summary>
        /// A globally-unique scheme-prefixed identity for the symbol — for the built-in C# indexer
        /// this is <c>csharp:</c> followed by the Roslyn <c>DocumentationCommentId</c>. Acts as
        /// the upsert primary key in storage so the symbol's integer id stays consistent across
        /// re-indexes.
        /// </summary>
        public string CanonicalKey { get; }

        /// <summary>Short name (e.g. <c>"Calculate"</c>).</summary>
        public string Name { get; }

        /// <summary>Fully-qualified name (e.g. <c>"Sample.Domain.Calculator.Calculate"</c>).</summary>
        public string Fqn { get; }

        /// <summary>Symbol kind as a kebab-case identifier. See <see cref="SymbolKinds"/> for built-in values.</summary>
        public string Kind { get; }

        /// <summary>1-based start line.</summary>
        public int StartLine { get; }

        /// <summary>1-based start column.</summary>
        public int StartColumn { get; }

        /// <summary>1-based end line.</summary>
        public int EndLine { get; }

        /// <summary>1-based end column.</summary>
        public int EndColumn { get; }

        /// <summary>Optional signature string (e.g. <c>"int Calculate(int)"</c>).</summary>
        public string? Signature { get; }

        /// <summary>Optional canonical key of the enclosing symbol, e.g. the type for a method.</summary>
        public string? ContainerCanonicalKey { get; }

        /// <summary>Optional space-joined modifiers (e.g. <c>"public static readonly"</c>).</summary>
        public string? Modifiers { get; }

        /// <summary>
        /// Integer mirror of <c>Microsoft.CodeAnalysis.Accessibility</c> (NotApplicable=0,
        /// Private=1, ProtectedAndInternal=2, Protected=3, Internal=4, ProtectedOrInternal=5,
        /// Public=6). Plugins for non-CLR languages should map their visibility model into the
        /// closest constant.
        /// </summary>
        public int Accessibility { get; }

        /// <summary>Optional one-line documentation summary.</summary>
        public string? XmlSummary { get; }
    }

    /// <summary>
    /// An edge between two symbols: <see cref="SourceCanonicalKey"/> -[<see cref="EdgeKindName"/>]-> <see cref="TargetCanonicalKey"/>.
    /// The host resolves the keys to symbol ids at flush time. <see cref="EdgeKindName"/> is a
    /// kebab-case identifier; use the <see cref="EdgeKinds"/> constants for well-known values
    /// or emit any plugin-defined kebab-case name.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Metadata"/> carries per-edge facts that the storage layer cannot capture
    /// in <c>(src, dst, kind)</c>. Examples: a binding's path string, an event handler's event
    /// name, a JSX prop list. Stored as JSON in the <c>edges.payload</c> column when non-null.</para>
    /// </remarks>
    public sealed record EdgeEmitted : IndexEvent
    {
        public EdgeEmitted(
            string sourceCanonicalKey,
            string targetCanonicalKey,
            string edgeKindName,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            CanonicalKeyValidator.Validate(sourceCanonicalKey, nameof(sourceCanonicalKey));
            CanonicalKeyValidator.Validate(targetCanonicalKey, nameof(targetCanonicalKey));
            KebabCaseValidator.Validate(edgeKindName, nameof(edgeKindName));

            SourceCanonicalKey = sourceCanonicalKey;
            TargetCanonicalKey = targetCanonicalKey;
            EdgeKindName = edgeKindName;
            Metadata = metadata;
        }

        public string SourceCanonicalKey { get; }
        public string TargetCanonicalKey { get; }
        public string EdgeKindName { get; }
        public IReadOnlyDictionary<string, string>? Metadata { get; }
    }

    /// <summary>
    /// A symbol carries an annotation (a .NET attribute, a TS decorator, a Vue directive, a
    /// Svelte action, …). The host turns this into a row in <c>annotations</c>; if the
    /// annotation's defining symbol is itself indexed (via a <see cref="SymbolDeclared"/> with a
    /// matching <see cref="TargetCanonicalKey"/>) the join is wired automatically.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Flavor"/> discriminates annotation patterns across languages
    /// (e.g. <c>"csharp-attribute"</c>, <c>"ts-decorator"</c>, <c>"vue-directive"</c>,
    /// <c>"svelte-action"</c>). MUST be a kebab-case identifier.</para>
    /// </remarks>
    public sealed record AnnotationAttached : IndexEvent
    {
        public AnnotationAttached(
            string symbolCanonicalKey,
            string annotationName,
            string flavor,
            string? fullName = null,
            string? argsJson = null,
            string? targetCanonicalKey = null)
        {
            CanonicalKeyValidator.Validate(symbolCanonicalKey, nameof(symbolCanonicalKey));
            KebabCaseValidator.Validate(flavor, nameof(flavor));
            if (targetCanonicalKey is not null)
            {
                CanonicalKeyValidator.Validate(targetCanonicalKey, nameof(targetCanonicalKey));
            }

            SymbolCanonicalKey = symbolCanonicalKey;
            AnnotationName = annotationName;
            Flavor = flavor;
            FullName = fullName;
            ArgsJson = argsJson;
            TargetCanonicalKey = targetCanonicalKey;
        }

        /// <summary>The symbol the annotation is attached to.</summary>
        public string SymbolCanonicalKey { get; }

        /// <summary>Short annotation name (e.g. <c>"HttpGet"</c>, <c>"Component"</c>, <c>"v-bind"</c>).</summary>
        public string AnnotationName { get; }

        /// <summary>
        /// Kebab-case discriminator (<c>"csharp-attribute"</c>, <c>"ts-decorator"</c>,
        /// <c>"vue-directive"</c>, <c>"svelte-action"</c>, …). Lets queries filter without
        /// conflating annotation patterns from different languages.
        /// </summary>
        public string Flavor { get; }

        /// <summary>Fully-qualified name when applicable (e.g. <c>"Microsoft.AspNetCore.Mvc.HttpGetAttribute"</c>).</summary>
        public string? FullName { get; }

        /// <summary>Optional JSON payload of constructor + named args, see <c>AttributeArgsJson</c>.</summary>
        public string? ArgsJson { get; }

        /// <summary>Optional canonical key of the annotation's defining type when it's a user-defined symbol the indexer also walked.</summary>
        public string? TargetCanonicalKey { get; }
    }

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
