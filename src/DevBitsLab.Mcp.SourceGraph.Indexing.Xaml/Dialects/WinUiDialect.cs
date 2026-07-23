using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;

/// <summary>
/// WinUI 3 profile. Detected by the WinUI controls namespace
/// (<c>using:Microsoft.UI.Xaml.Controls</c> mapping or
/// <c>http://schemas.microsoft.com/winfx/2006/xaml/presentation</c> + a WinUI mapping). Supports
/// both runtime <c>{Binding}</c> and compiled <c>{x:Bind}</c>. Uses the UWP-style
/// <c>using:</c> CLR-namespace prefix.
/// </summary>
public sealed class WinUiDialect : IXamlDialect
{
    /// <summary>The WinUI 3 controls namespace declared in <c>Microsoft.UI.Xaml.Controls</c>.</summary>
    public const string WinUiControlsNamespace = "using:Microsoft.UI.Xaml.Controls";

    public string Name => "WinUi";
    public string XClassNamespace => XamlReader.XamlNamespace;
    public MarkupExtensionDialect MarkupExtensionDialect => MarkupExtensionDialect.CompiledOrRuntime;
    public string ClrNamespacePrefix => "using:";
}
