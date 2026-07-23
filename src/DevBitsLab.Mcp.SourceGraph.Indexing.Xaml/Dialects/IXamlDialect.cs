namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;

/// <summary>
/// Strategy interface for one of the five XAML framework profiles (WPF, WinUI 3, UWP, Avalonia,
/// Uno). Profile-specific behaviour — markup-extension dialect (<c>{Binding}</c> vs
/// <c>{x:Bind}</c>), <c>clr-namespace:</c> vs <c>using:</c> namespace mappings, special-case
/// elements (Avalonia <c>ControlTheme</c>) — lives behind this interface so the
/// <c>XamlLanguageIndexer</c> stays profile-agnostic.
/// </summary>
public interface IXamlDialect
{
    /// <summary>Stable name for diagnostics (<c>Wpf</c>, <c>WinUi</c>, <c>Uwp</c>, <c>Avalonia</c>, <c>Uno</c>).</summary>
    string Name { get; }

    /// <summary>
    /// The XAML namespace URI used by <c>x:Class</c> / <c>x:Key</c> / <c>x:Name</c> on this
    /// profile. Identical across every profile shipped today (the <c>http://schemas.microsoft.com/winfx/2006/xaml</c>
    /// constant) but exposed on the strategy in case a future profile diverges.
    /// </summary>
    string XClassNamespace { get; }

    /// <summary>The markup-extension dialect this profile uses (binding flavor, etc.).</summary>
    MarkupExtensionDialect MarkupExtensionDialect { get; }

    /// <summary>
    /// CLR-namespace declaration prefix used by this profile's <c>xmlns</c> declarations.
    /// WPF/WinUI/Avalonia/Uno use <c>clr-namespace:</c>; UWP uses <c>using:</c>. Lets the
    /// indexer recognise CLR-mapped namespaces without reimplementing the dispatch per profile.
    /// </summary>
    string ClrNamespacePrefix { get; }
}

/// <summary>
/// The markup-extension dialect a profile speaks. WPF/Avalonia/Uno use the runtime
/// <c>{Binding}</c> family; WinUI 3 / UWP also support the compiled-binding <c>{x:Bind}</c>
/// form. The indexer uses this to decide whether to look up an <c>x:Bind</c> path against
/// the codebehind's typed data context (deferred for v1) or to record it as a runtime path.
/// </summary>
public enum MarkupExtensionDialect
{
    /// <summary>Runtime <c>{Binding}</c> only.</summary>
    RuntimeBinding,

    /// <summary>Both <c>{Binding}</c> and compiled <c>{x:Bind}</c> are accepted (WinUI 3, UWP).</summary>
    CompiledOrRuntime,
}
