using System;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

/// <summary>
/// Classifies a <see cref="XamlAttribute"/> into one of the categories the indexer treats
/// differently (well-known XAML directive, namespace declaration, attached property,
/// markup-extension value, regular value). Centralised here so every indexer code path uses the
/// same rules — and so the rules are unit-testable in isolation from the indexer state machine.
/// </summary>
public enum XamlAttributeKind
{
    /// <summary>
    /// A well-known directive on the XAML namespace: <c>x:Class</c>, <c>x:Key</c>, <c>x:Name</c>,
    /// or the unprefixed <c>Name</c> (which behaves like <c>x:Name</c> for most controls).
    /// </summary>
    XamlDirective,

    /// <summary>
    /// A namespace declaration (<c>xmlns:foo="…"</c> or the default <c>xmlns="…"</c>). Captured
    /// here so the indexer can ignore them when walking attribute streams that are NOT the root
    /// element's mappings.
    /// </summary>
    NamespaceDeclaration,

    /// <summary>
    /// An attached property — local name contains a <c>.</c> (e.g. <c>Grid.Row</c>).
    /// </summary>
    AttachedProperty,

    /// <summary>
    /// A markup-extension value (<c>{Binding …}</c>, <c>{StaticResource …}</c>, etc.).
    /// </summary>
    MarkupExtension,

    /// <summary>Anything else: a regular string-typed property.</summary>
    Value,
}

public static class XamlAttributeClassifier
{
    /// <summary>Identify the category of <paramref name="attribute"/> for indexer routing.</summary>
    public static XamlAttributeKind Classify(XamlAttribute attribute)
    {
        if (attribute is null) throw new ArgumentNullException(nameof(attribute));

        if (IsNamespaceDeclaration(attribute)) return XamlAttributeKind.NamespaceDeclaration;
        if (IsXamlDirective(attribute)) return XamlAttributeKind.XamlDirective;
        if (attribute.IsAttachedProperty) return XamlAttributeKind.AttachedProperty;
        if (attribute.IsMarkupExtension) return XamlAttributeKind.MarkupExtension;
        return XamlAttributeKind.Value;
    }

    private static bool IsNamespaceDeclaration(XamlAttribute a)
    {
        // `xmlns="..."` arrives as Prefix="" / LocalName="xmlns"; `xmlns:x="..."` arrives as
        // Prefix="xmlns" / LocalName="x". XmlReader normalises the namespace URI to the W3C
        // xmlns namespace constant; comparing prefixes is more readable than namespace URIs.
        if (a.Prefix == "xmlns") return true;
        if (string.IsNullOrEmpty(a.Prefix) && a.LocalName == "xmlns") return true;
        return false;
    }

    private static bool IsXamlDirective(XamlAttribute a)
    {
        if (a.Namespace == XamlReader.XamlNamespace)
        {
            // x:Class, x:Key, x:Name, x:DataType — all directives in the XAML namespace.
            return true;
        }
        // Unprefixed `Name="…"` is conventionally treated as <c>x:Name</c>'s sibling: every WPF /
        // Avalonia / WinUI control type expresses an x:Name through the implicit Name property,
        // so the indexer treats both shapes identically when materialising xaml-element symbols.
        return string.IsNullOrEmpty(a.Prefix) && a.LocalName == "Name";
    }
}
