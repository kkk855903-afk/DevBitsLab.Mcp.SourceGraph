using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

/// <summary>
/// One parsed XAML element. Carries the namespace-resolved name, the element's open-tag position,
/// the attributes attached to it (in source order), and its child elements (also in source order).
/// </summary>
public sealed class XamlElement
{
    public XamlElement(string @namespace, string localName, int line, int column)
    {
        Namespace = @namespace;
        LocalName = localName;
        Line = line;
        Column = column;
    }

    /// <summary>Resolved XML namespace URI for the element (the empty string if none).</summary>
    public string Namespace { get; }

    /// <summary>Local element name (without prefix).</summary>
    public string LocalName { get; }

    /// <summary>1-based line of the element open tag.</summary>
    public int Line { get; }

    /// <summary>1-based column of the element open tag.</summary>
    public int Column { get; }

    /// <summary>Attributes declared on the element, in source order.</summary>
    public List<XamlAttribute> Attributes { get; } = new();

    /// <summary>Child elements, in source order. Text/whitespace nodes are not modelled.</summary>
    public List<XamlElement> Children { get; } = new();

    /// <summary>The element's parent, or null for the root.</summary>
    public XamlElement? Parent { get; internal set; }

    /// <summary>
    /// Convenience: look up the first attribute matching <paramref name="namespaceUri"/> and
    /// <paramref name="localName"/>. Returns null when absent.
    /// </summary>
    public XamlAttribute? FindAttribute(string namespaceUri, string localName)
    {
        foreach (var a in Attributes)
        {
            if (a.LocalName == localName && a.Namespace == namespaceUri) return a;
        }
        return null;
    }

    /// <summary>
    /// Convenience: look up the first attribute by local name only (any namespace). Useful for
    /// markup-only attribute names like <c>Name</c> that may appear with no prefix.
    /// </summary>
    public XamlAttribute? FindAttributeByLocalName(string localName)
    {
        foreach (var a in Attributes)
        {
            if (a.LocalName == localName) return a;
        }
        return null;
    }
}
