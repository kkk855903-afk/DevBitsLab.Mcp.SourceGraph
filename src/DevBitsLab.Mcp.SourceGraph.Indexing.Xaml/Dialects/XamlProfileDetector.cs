using System;
using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;

/// <summary>
/// Inspects a root element's <c>xmlns</c> mappings and returns the matching
/// <see cref="IXamlDialect"/>. Detection is intentionally conservative — when the input doesn't
/// uniquely identify a profile we return <see cref="WpfDialect"/> as the safe default since its
/// runtime-binding semantics are the strict subset every dialect supports.
///
/// <para>The rules (evaluated in order):</para>
/// <list type="number">
/// <item><description><c>https://github.com/avaloniaui</c> → <see cref="AvaloniaDialect"/></description></item>
/// <item><description>WPF presentation NS + a Uno hint (<c>nventive</c> / <c>uno</c> namespace value) → <see cref="UnoDialect"/></description></item>
/// <item><description>WPF presentation NS + a WinUI mapping (<c>Microsoft.UI.Xaml</c>) → <see cref="WinUiDialect"/></description></item>
/// <item><description>WPF presentation NS + a UWP mapping (<c>Windows.UI.Xaml</c>) → <see cref="UwpDialect"/></description></item>
/// <item><description>WPF presentation NS otherwise → <see cref="WpfDialect"/></description></item>
/// <item><description>No presentation NS at all → <see cref="WpfDialect"/> (defensive default)</description></item>
/// </list>
/// </summary>
public static class XamlProfileDetector
{
    /// <summary>
    /// Pick the dialect that best matches <paramref name="rootNamespaceMappings"/> (the dictionary
    /// produced by <c>XamlReader.Parse(...).RootNamespaceMappings</c>).
    /// </summary>
    public static IXamlDialect Detect(IReadOnlyDictionary<string, string> rootNamespaceMappings)
    {
        if (rootNamespaceMappings is null) throw new ArgumentNullException(nameof(rootNamespaceMappings));

        // Collect the set of namespace VALUES so we can scan them once.
        var values = new List<string>(rootNamespaceMappings.Count);
        foreach (var kv in rootNamespaceMappings) values.Add(kv.Value);

        if (HasNamespace(values, AvaloniaDialect.AvaloniaNamespace))
        {
            return new AvaloniaDialect();
        }

        var hasWpfPresentation = HasNamespace(values, WpfDialect.PresentationNamespace);
        if (hasWpfPresentation)
        {
            // Uno declares its identity via the nventive / Uno.UI prefixes alongside the
            // presentation namespace it shares with WPF. Catch this before the WinUI / UWP checks
            // so a Uno-WinUI app (which inherits the Microsoft.UI.Xaml namespaces) still resolves
            // to Uno.
            if (HasNamespaceContaining(values, "nventive") || HasNamespaceContaining(values, "uno"))
            {
                return new UnoDialect();
            }
            if (HasNamespaceContaining(values, "Microsoft.UI.Xaml"))
            {
                return new WinUiDialect();
            }
            if (HasNamespaceContaining(values, "Windows.UI.Xaml"))
            {
                return new UwpDialect();
            }
            return new WpfDialect();
        }

        // WinUI 3 / UWP files sometimes declare the framework directly without the presentation
        // alias when the root is `<Page xmlns="using:Microsoft.UI.Xaml.Controls">`.
        if (HasNamespace(values, WinUiDialect.WinUiControlsNamespace))
        {
            return new WinUiDialect();
        }
        if (HasNamespace(values, UwpDialect.UwpControlsNamespace))
        {
            return new UwpDialect();
        }

        return new WpfDialect();
    }

    private static bool HasNamespace(IReadOnlyList<string> values, string target)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], target, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool HasNamespaceContaining(IReadOnlyList<string> values, string substring)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }
}
