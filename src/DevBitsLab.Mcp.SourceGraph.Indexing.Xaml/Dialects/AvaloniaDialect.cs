using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;

/// <summary>
/// Avalonia profile. Detected by the Avalonia presentation namespace
/// <c>https://github.com/avaloniaui</c> on the root element. Uses runtime <c>{Binding}</c>; CLR
/// namespace mappings use the <c>clr-namespace:</c> prefix exactly as in WPF.
/// </summary>
public sealed class AvaloniaDialect : IXamlDialect
{
    /// <summary>The Avalonia presentation namespace URI.</summary>
    public const string AvaloniaNamespace = "https://github.com/avaloniaui";

    public string Name => "Avalonia";
    public string XClassNamespace => XamlReader.XamlNamespace;
    public MarkupExtensionDialect MarkupExtensionDialect => MarkupExtensionDialect.RuntimeBinding;
    public string ClrNamespacePrefix => "clr-namespace:";
}
