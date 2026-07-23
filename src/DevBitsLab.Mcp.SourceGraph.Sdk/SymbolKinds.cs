namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Well-known symbol-kind identifiers emitted by the built-in C# Roslyn indexer. Plugins MAY emit
/// additional kebab-case identifiers — the host stores them as TEXT and does not reject unknown
/// kebab-case kinds. Use <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.Validation.KebabCaseValidator"/>
/// to validate plugin-supplied kinds.
/// </summary>
public static class SymbolKinds
{
    public const string Other = "other";
    public const string Namespace = "namespace";
    public const string Class = "class";
    public const string Interface = "interface";
    public const string Struct = "struct";
    public const string Enum = "enum";
    public const string Delegate = "delegate";
    public const string Method = "method";
    public const string Constructor = "constructor";
    public const string Property = "property";
    public const string Field = "field";
    public const string Event = "event";
    public const string EnumMember = "enum-member";
    public const string Operator = "operator";
    public const string Record = "record";
    /// <summary>Method-local variable. Not emitted by the built-in indexer today.</summary>
    public const string Local = "local";
    /// <summary>Method/constructor parameter. Not emitted by the built-in indexer today.</summary>
    public const string Parameter = "parameter";
    /// <summary>Generic type parameter. Not emitted by the built-in indexer today.</summary>
    public const string TypeParameter = "type-parameter";
}
