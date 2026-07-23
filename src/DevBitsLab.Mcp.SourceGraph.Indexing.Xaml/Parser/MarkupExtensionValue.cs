using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

/// <summary>
/// Structured representation of a parsed markup extension. Either a literal string (the leaf form
/// most binding properties end up as) or a nested extension with name + positional + named args.
/// </summary>
public sealed class MarkupExtensionValue
{
    /// <summary>Literal string form: the value is a plain text fragment, no extension parse.</summary>
    public string? Literal { get; }

    /// <summary>Extension name (e.g. <c>Binding</c>, <c>StaticResource</c>). Null when <see cref="Literal"/> is set.</summary>
    public string? Name { get; }

    /// <summary>Positional arguments in source order. Each element may itself be a nested extension.</summary>
    public IReadOnlyList<MarkupExtensionValue> PositionalArgs { get; } = System.Array.Empty<MarkupExtensionValue>();

    /// <summary>Named arguments. Keys are the property names exactly as written; values may nest.</summary>
    public IReadOnlyDictionary<string, MarkupExtensionValue> NamedArgs { get; } = new Dictionary<string, MarkupExtensionValue>();

    public bool IsLiteral => Literal is not null;

    private MarkupExtensionValue(string literal)
    {
        Literal = literal;
    }

    private MarkupExtensionValue(
        string name,
        IReadOnlyList<MarkupExtensionValue> positional,
        IReadOnlyDictionary<string, MarkupExtensionValue> named)
    {
        Name = name;
        PositionalArgs = positional;
        NamedArgs = named;
    }

    public static MarkupExtensionValue ForLiteral(string text) => new(text);

    public static MarkupExtensionValue ForExtension(
        string name,
        IReadOnlyList<MarkupExtensionValue> positional,
        IReadOnlyDictionary<string, MarkupExtensionValue> named) =>
        new(name, positional, named);
}
