using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;

/// <summary>
/// WPF profile. Detected by the presentation namespace
/// <c>http://schemas.microsoft.com/winfx/2006/xaml/presentation</c> on the root element WITHOUT
/// any of the WinUI / UWP / Uno opt-in namespaces. Uses runtime <c>{Binding}</c> and the
/// <c>clr-namespace:</c> form for CLR namespace mappings.
/// </summary>
public sealed class WpfDialect : IXamlDialect
{
    public const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public string Name => "Wpf";
    public string XClassNamespace => XamlReader.XamlNamespace;
    public MarkupExtensionDialect MarkupExtensionDialect => MarkupExtensionDialect.RuntimeBinding;
    public string ClrNamespacePrefix => "clr-namespace:";
}
