using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript;

/// <summary>
/// Translates tree-sitter <c>tree-sitter-typescript</c> / <c>tree-sitter-tsx</c> /
/// <c>tree-sitter-javascript</c> node-type strings into the SDK's kebab-case kind / edge /
/// reference vocabularies.
///
/// <para>Strategy: the mapper recognises the *container* nodes that wrap declarations and
/// references. Bare <c>identifier</c> nodes are NOT mapped — when the indexer's walk descends
/// into a <c>function_declaration</c> the inner identifier is part of the declaration and
/// emitting a separate ref for it would double-count. The trade-off is that some
/// non-container reference sites (a lone <c>identifier;</c> read statement) won't surface as
/// refs — documented as a known limit. Cross-file refs that matter (call sites, type uses,
/// JSX components) all live inside container nodes that the mapper recognises.</para>
/// </summary>
public sealed class TypeScriptNodeKindMapper : INodeKindMapper
{
    /// <inheritdoc />
    public bool TryMapDeclaration(string nodeType, out NodeMapping mapping)
    {
        switch (nodeType)
        {
            case "function_declaration":
            case "function_signature":
            case "generator_function_declaration":
                mapping = new NodeMapping(SymbolKinds.Method);
                return true;
            case "method_definition":
            case "method_signature":
                mapping = new NodeMapping(SymbolKinds.Method);
                return true;
            case "class_declaration":
            case "abstract_class_declaration":
                mapping = new NodeMapping(SymbolKinds.Class);
                return true;
            case "interface_declaration":
                mapping = new NodeMapping(SymbolKinds.Interface);
                return true;
            case "type_alias_declaration":
                mapping = new NodeMapping("type-alias");
                return true;
            case "enum_declaration":
                mapping = new NodeMapping(SymbolKinds.Enum);
                return true;
            case "public_field_definition":
            case "property_signature":
                mapping = new NodeMapping(SymbolKinds.Field);
                return true;
            case "lexical_declaration":
                // `const x = ...` / `let x = ...`. Subclass picks `constant` vs `variable` from
                // the node's first-token text (`const` keyword) — beyond what the mapper sees.
                // Default to variable; subclass can refine.
                mapping = new NodeMapping("variable");
                return true;
            case "variable_declaration":
                mapping = new NodeMapping("variable");
                return true;
            case "internal_module":
            case "module":
            case "namespace_declaration":
                mapping = new NodeMapping(SymbolKinds.Namespace);
                return true;
            case "enum_assignment":
                mapping = new NodeMapping(SymbolKinds.EnumMember);
                return true;
            default:
                mapping = default;
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryMapReference(string nodeType, out string referenceKind)
    {
        switch (nodeType)
        {
            case "call_expression":
                referenceKind = "call";
                return true;
            case "type_identifier":
                referenceKind = "reference";
                return true;
            default:
                referenceKind = string.Empty;
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryMapEdge(string nodeType, out string edgeKindName)
    {
        switch (nodeType)
        {
            case "new_expression":
                edgeKindName = EdgeKinds.Instantiates;
                return true;
            case "jsx_self_closing_element":
            case "jsx_opening_element":
                edgeKindName = EdgeKinds.Instantiates;
                return true;
            default:
                edgeKindName = string.Empty;
                return false;
        }
    }
}
