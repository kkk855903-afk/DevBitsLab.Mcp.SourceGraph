using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

/// <summary>
/// Position-tracking XAML parser built on <see cref="XmlReader"/>. Returns a <see cref="XamlElement"/>
/// tree with line/column captured for every element open and every attribute, plus the namespace
/// mappings declared on the root element so the dialect detector can pick the right profile.
///
/// <para>The reader is forward-only and constructs the tree in a single pass. It does not preserve
/// text nodes (the indexer doesn't care about textual content for v1) and does not validate against
/// any schema — XAML schemas are profile-specific and the indexer is profile-agnostic at parse
/// time. Anything malformed enough that <see cref="XmlReader"/> throws is surfaced through the
/// thrown <see cref="XmlException"/>.</para>
/// </summary>
public static class XamlReader
{
    /// <summary>The well-known <c>x</c> XAML namespace URI used by every dialect for <c>x:Class</c>, <c>x:Key</c>, <c>x:Name</c>.</summary>
    public const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Parse the XAML document from <paramref name="contents"/>. The caller is expected to pass the
    /// raw file bytes (UTF-8 or any other XML-recognised encoding); <see cref="XmlReader"/> handles
    /// the BOM / declaration sniff itself. Returns a <see cref="XamlDocument"/> with the root
    /// element and the root namespace mappings.
    /// </summary>
    public static XamlDocument Parse(byte[] contents)
    {
        if (contents is null) throw new ArgumentNullException(nameof(contents));
        return Parse(new MemoryStream(contents, writable: false));
    }

    /// <summary>
    /// Parse the XAML document from the supplied stream. The stream is consumed but not disposed
    /// — callers own the lifetime. <see cref="XmlReader"/> is configured with line-info enabled
    /// and DTD processing prohibited (no XAML profile uses DTDs and ignoring them dodges an entire
    /// class of XXE vectors).
    /// </summary>
    public static XamlDocument Parse(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(stream, settings);
        var lineInfo = (IXmlLineInfo)reader;

        XamlElement? root = null;
        var rootMappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new Stack<XamlElement>();

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var ns = reader.NamespaceURI ?? string.Empty;
                    var local = reader.LocalName;
                    var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
                    var col = lineInfo.HasLineInfo() ? lineInfo.LinePosition : 0;
                    var elem = new XamlElement(ns, local, line, col);

                    // Walk attributes BEFORE any nested reads — XmlReader exposes them off the
                    // element start node directly, and the line-info accessor reflects the
                    // element-relative position when MoveToAttribute is invoked.
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            var attrPrefix = reader.Prefix ?? string.Empty;
                            var attrLocal = reader.LocalName;
                            var attrNs = reader.NamespaceURI ?? string.Empty;
                            var attrValue = reader.Value ?? string.Empty;
                            var attrLine = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
                            var attrCol = lineInfo.HasLineInfo() ? lineInfo.LinePosition : 0;

                            // The xmlns* attributes are namespace declarations rather than data
                            // attributes, but we keep them in the model so the dialect detector
                            // can read them off the root element after the parse completes.
                            elem.Attributes.Add(new XamlAttribute(attrNs, attrPrefix, attrLocal, attrValue, attrLine, attrCol));

                            // Capture root-element namespace declarations into a flat map so the
                            // detector doesn't re-walk attributes. `xmlns="..."` lives at LocalName
                            // = "xmlns" with empty prefix; `xmlns:x="..."` lives at LocalName = "x"
                            // with prefix = "xmlns".
                            if (root is null)
                            {
                                if (attrPrefix == string.Empty && attrLocal == "xmlns")
                                {
                                    rootMappings[string.Empty] = attrValue;
                                }
                                else if (attrPrefix == "xmlns")
                                {
                                    rootMappings[attrLocal] = attrValue;
                                }
                            }
                        }
                        while (reader.MoveToNextAttribute());

                        // Move back to the element so subsequent reader.Read() advances correctly.
                        reader.MoveToElement();
                    }

                    if (root is null)
                    {
                        root = elem;
                    }
                    else
                    {
                        var parent = stack.Peek();
                        elem.Parent = parent;
                        parent.Children.Add(elem);
                    }

                    if (!reader.IsEmptyElement)
                    {
                        stack.Push(elem);
                    }
                    break;
                }
                case XmlNodeType.EndElement:
                {
                    if (stack.Count > 0) stack.Pop();
                    break;
                }
                default:
                    // Text / whitespace / CDATA / etc. — we don't model them today; the indexer
                    // only cares about element + attribute structure for v1.
                    break;
            }
        }

        if (root is null)
        {
            throw new XmlException("XAML document contained no elements.");
        }

        return new XamlDocument(root, rootMappings);
    }

    /// <summary>
    /// Visit every element in <paramref name="root"/> in pre-order (parent before children). The
    /// callback receives both the element and the parent stack at that point so the indexer can
    /// reason about ancestry without re-walking the tree.
    /// </summary>
    public static void Walk(XamlElement root, Action<XamlElement, IReadOnlyList<XamlElement>> visitor)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));

        var ancestors = new List<XamlElement>();
        WalkInner(root, ancestors, visitor);
    }

    private static void WalkInner(XamlElement element, List<XamlElement> ancestors, Action<XamlElement, IReadOnlyList<XamlElement>> visitor)
    {
        visitor(element, ancestors);
        ancestors.Add(element);
        try
        {
            foreach (var child in element.Children)
            {
                WalkInner(child, ancestors, visitor);
            }
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    /// <summary>UTF-8 encoder shared with tests so callers can build a byte payload from a string.</summary>
    public static byte[] EncodeUtf8(string source) => Encoding.UTF8.GetBytes(source);
}

/// <summary>
/// Result of <see cref="XamlReader.Parse(byte[])"/>: the root element plus the namespace mappings
/// declared on it. The mappings dictionary is keyed by the XML prefix (or the empty string for the
/// default <c>xmlns=…</c>) and values are the resolved namespace URIs.
/// </summary>
public sealed class XamlDocument
{
    public XamlDocument(XamlElement root, IReadOnlyDictionary<string, string> rootNamespaceMappings)
    {
        Root = root;
        RootNamespaceMappings = rootNamespaceMappings;
    }

    public XamlElement Root { get; }
    public IReadOnlyDictionary<string, string> RootNamespaceMappings { get; }
}
