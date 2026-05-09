namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Well-known edge-kind identifiers emitted by the built-in C# Roslyn indexer. Plugins MAY emit
/// additional kebab-case identifiers — the host stores them as TEXT and does not reject unknown
/// kebab-case kinds. Use <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.Validation.KebabCaseValidator"/>
/// to validate plugin-supplied kinds.
/// </summary>
public static class EdgeKinds
{
    public const string Calls = "calls";
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    public const string UsesType = "uses-type";
    public const string OverridesMember = "overrides-member";
    public const string ImplementsMember = "implements-member";
    public const string Instantiates = "instantiates";
    public const string Throws = "throws";
    /// <summary>Test method exercises a piece of production code.</summary>
    public const string Tests = "tests";
}
