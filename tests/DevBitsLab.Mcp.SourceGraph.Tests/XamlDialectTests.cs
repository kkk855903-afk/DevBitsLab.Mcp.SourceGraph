using System.Collections.Generic;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Tests <see cref="XamlProfileDetector"/> against one synthetic root-mappings dictionary per
/// supported profile. Each scenario mirrors a common <c>xmlns</c> stanza found at the top of a
/// real WPF / WinUI / UWP / Avalonia / Uno file.
/// </summary>
public sealed class XamlDialectTests
{
    [Fact]
    public void Detect_wpfFromPresentationNamespace()
    {
        var mappings = new Dictionary<string, string>
        {
            [""] = WpfDialect.PresentationNamespace,
            ["x"] = "http://schemas.microsoft.com/winfx/2006/xaml",
        };
        XamlProfileDetector.Detect(mappings).Should().BeOfType<WpfDialect>();
    }

    [Fact]
    public void Detect_avaloniaFromAvaloniaNamespace()
    {
        var mappings = new Dictionary<string, string>
        {
            [""] = AvaloniaDialect.AvaloniaNamespace,
            ["x"] = "http://schemas.microsoft.com/winfx/2006/xaml",
        };
        XamlProfileDetector.Detect(mappings).Should().BeOfType<AvaloniaDialect>();
    }

    [Fact]
    public void Detect_winUiFromMicrosoftUiXamlMapping()
    {
        var mappings = new Dictionary<string, string>
        {
            [""] = WpfDialect.PresentationNamespace,
            ["controls"] = "using:Microsoft.UI.Xaml.Controls",
        };
        XamlProfileDetector.Detect(mappings).Should().BeOfType<WinUiDialect>();
    }

    [Fact]
    public void Detect_uwpFromWindowsUiXamlMapping()
    {
        var mappings = new Dictionary<string, string>
        {
            [""] = WpfDialect.PresentationNamespace,
            ["controls"] = "using:Windows.UI.Xaml.Controls",
        };
        XamlProfileDetector.Detect(mappings).Should().BeOfType<UwpDialect>();
    }

    [Fact]
    public void Detect_unoFromNventiveNamespace()
    {
        var mappings = new Dictionary<string, string>
        {
            [""] = WpfDialect.PresentationNamespace,
            ["nventive"] = UnoDialect.NventiveNamespace,
        };
        XamlProfileDetector.Detect(mappings).Should().BeOfType<UnoDialect>();
    }

    [Fact]
    public void Detect_emptyDefaultsToWpf()
    {
        var mappings = new Dictionary<string, string>();
        XamlProfileDetector.Detect(mappings).Should().BeOfType<WpfDialect>();
    }

    [Fact]
    public void Dialects_advertiseExpectedClrPrefixes()
    {
        new WpfDialect().ClrNamespacePrefix.Should().Be("clr-namespace:");
        new AvaloniaDialect().ClrNamespacePrefix.Should().Be("clr-namespace:");
        new WinUiDialect().ClrNamespacePrefix.Should().Be("using:");
        new UwpDialect().ClrNamespacePrefix.Should().Be("using:");
        new UnoDialect().ClrNamespacePrefix.Should().Be("using:");
    }

    [Fact]
    public void Dialects_advertiseExpectedMarkupExtensionDialect()
    {
        new WpfDialect().MarkupExtensionDialect.Should().Be(MarkupExtensionDialect.RuntimeBinding);
        new AvaloniaDialect().MarkupExtensionDialect.Should().Be(MarkupExtensionDialect.RuntimeBinding);
        new WinUiDialect().MarkupExtensionDialect.Should().Be(MarkupExtensionDialect.CompiledOrRuntime);
        new UwpDialect().MarkupExtensionDialect.Should().Be(MarkupExtensionDialect.CompiledOrRuntime);
        new UnoDialect().MarkupExtensionDialect.Should().Be(MarkupExtensionDialect.RuntimeBinding);
    }
}
