using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;

/// <summary>
/// Uno profile. Detected by the presentation namespace plus a Uno-specific namespace declaration
/// (e.g. <c>xmlns:nventive="…"</c> or the Uno-Xaml controls namespace). Same markup-extension
/// dialect as WPF (runtime <c>{Binding}</c>) and uses the <c>using:</c> CLR-namespace form.
/// </summary>
public sealed class UnoDialect : IXamlDialect
{
    /// <summary>Uno's nventive prefix tells the detector the document is rendered by Uno even when the presentation namespace is the WPF one.</summary>
    public const string NventiveNamespace = "http://nventive.com";

    public string Name => "Uno";
    public string XClassNamespace => XamlReader.XamlNamespace;
    public MarkupExtensionDialect MarkupExtensionDialect => MarkupExtensionDialect.RuntimeBinding;
    public string ClrNamespacePrefix => "using:";
}
