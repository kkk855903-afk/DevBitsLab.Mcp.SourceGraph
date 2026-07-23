namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Plugin-supplied translation from tree-sitter node-type strings (e.g. <c>"function_declaration"</c>,
/// <c>"class_declaration"</c>, <c>"jsx_self_closing_element"</c>) to the SDK's kebab-case symbol /
/// edge / reference vocabularies. Consumed by <c>TreeSitterLanguageIndexer&lt;T&gt;</c> in the
/// host project; per-language plugins implement the interface once and let the abstract base
/// drive the AST walk.
///
/// <para>The three orthogonal methods mirror the <see cref="IndexEvent"/> family: a tree-sitter
/// node either declares a symbol, references a symbol, or carries an edge — the mapper says
/// which (or none, by returning <c>false</c>).</para>
/// </summary>
public interface INodeKindMapper
{
    /// <summary>
    /// True when the named node-type produces a <see cref="IndexEvent.SymbolDeclared"/>; in that
    /// case <paramref name="mapping"/> carries the kebab-case <see cref="SymbolKinds"/> value
    /// plus optional modifiers / accessibility. False means "skip this node".
    /// </summary>
    bool TryMapDeclaration(string nodeType, out NodeMapping mapping);

    /// <summary>
    /// True when the node-type produces a <see cref="IndexEvent.ReferenceFound"/>; in that case
    /// <paramref name="referenceKind"/> is one of the storage-level reference kinds
    /// (<c>"reference"</c>, <c>"call"</c>, <c>"read"</c>, <c>"write"</c>). False means "skip".
    /// </summary>
    bool TryMapReference(string nodeType, out string referenceKind);

    /// <summary>
    /// True when the node-type emits an <see cref="IndexEvent.EdgeEmitted"/>; in that case
    /// <paramref name="edgeKindName"/> is a kebab-case <see cref="EdgeKinds"/> value (or any
    /// plugin-defined kebab-case identifier). False means "skip".
    /// </summary>
    bool TryMapEdge(string nodeType, out string edgeKindName);
}

/// <summary>
/// Result of <see cref="INodeKindMapper.TryMapDeclaration"/>. Carries the kebab-case
/// <see cref="Kind"/> plus optional modifiers and accessibility. <see cref="Kind"/> MUST be
/// kebab-case; the host validates via <see cref="Validation.KebabCaseValidator"/> at emission
/// time and throws on violation, identifying the offending value.
/// </summary>
public readonly struct NodeMapping
{
    public NodeMapping(string kind, string? modifiers = null, int accessibility = 0)
    {
        Kind = kind;
        Modifiers = modifiers;
        Accessibility = accessibility;
    }

    /// <summary>Kebab-case symbol kind. See <see cref="SymbolKinds"/> for built-in values.</summary>
    public string Kind { get; }

    /// <summary>Optional space-joined modifier list (e.g. <c>"public static readonly"</c>).</summary>
    public string? Modifiers { get; }

    /// <summary>
    /// Integer mirror of <c>Microsoft.CodeAnalysis.Accessibility</c> (NotApplicable=0,
    /// Private=1, ProtectedAndInternal=2, Protected=3, Internal=4, ProtectedOrInternal=5,
    /// Public=6). Plugins for non-CLR languages map their visibility model into the closest
    /// constant.
    /// </summary>
    public int Accessibility { get; }
}
