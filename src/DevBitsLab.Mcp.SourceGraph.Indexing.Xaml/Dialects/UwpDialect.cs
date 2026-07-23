using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;

/// <summary>
/// UWP profile. Detected by the presentation namespace plus a UWP-only namespace mapping such as
/// <c>using:Windows.UI.Xaml</c>. Like WinUI 3, UWP supports both runtime <c>{Binding}</c> and
/// compiled <c>{x:Bind}</c>; the difference from WinUI 3 is the <c>Windows.UI.Xaml</c> root vs
/// <c>Microsoft.UI.Xaml</c> root, which the detector inspects on the root element.
/// </summary>
public sealed class UwpDialect : IXamlDialect
{
    /// <summary>The UWP controls namespace mapping.</summary>
    public const string UwpControlsNamespace = "using:Windows.UI.Xaml.Controls";

    public string Name => "Uwp";
    public string XClassNamespace => XamlReader.XamlNamespace;
    public MarkupExtensionDialect MarkupExtensionDialect => MarkupExtensionDialect.CompiledOrRuntime;
    public string ClrNamespacePrefix => "using:";
}
