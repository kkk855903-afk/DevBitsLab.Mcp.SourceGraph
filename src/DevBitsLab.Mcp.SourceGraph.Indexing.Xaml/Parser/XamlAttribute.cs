namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

/// <summary>
/// One attribute on a XAML element. Position is captured from the underlying
/// <see cref="System.Xml.IXmlLineInfo"/> at the moment XmlReader sat on the attribute value, so the
/// indexer can report file:line:col for every event handler / binding / resource use site.
/// </summary>
public sealed class XamlAttribute
{
    public XamlAttribute(string @namespace, string prefix, string localName, string value, int line, int column)
    {
        Namespace = @namespace;
        Prefix = prefix;
        LocalName = localName;
        Value = value;
        Line = line;
        Column = column;
    }

    /// <summary>Resolved XML namespace URI for the attribute (empty string when not prefixed).</summary>
    public string Namespace { get; }

    /// <summary>Prefix exactly as written in the source (empty when unprefixed).</summary>
    public string Prefix { get; }

    /// <summary>Local attribute name. For attached properties this is the full <c>Type.Property</c>.</summary>
    public string LocalName { get; }

    /// <summary>Raw attribute value (XmlReader has already decoded the XML entities).</summary>
    public string Value { get; }

    /// <summary>1-based line of the attribute.</summary>
    public int Line { get; }

    /// <summary>1-based column of the attribute.</summary>
    public int Column { get; }

    /// <summary>True when <see cref="LocalName"/> contains a dot (the well-known XAML attached-property shape).</summary>
    public bool IsAttachedProperty => LocalName.IndexOf('.') > 0;

    /// <summary>True when <see cref="Value"/> looks like a markup extension (<c>{Foo …}</c>).</summary>
    public bool IsMarkupExtension
    {
        get
        {
            var v = Value;
            if (v.Length < 2) return false;
            if (v[0] != '{') return false;
            // Escaped opening brace `{}…` is the canonical "treat the rest as a literal string"
            // sentinel; treat that as a regular value, not a markup extension.
            if (v[1] == '}') return false;
            return v[v.Length - 1] == '}';
        }
    }
}
