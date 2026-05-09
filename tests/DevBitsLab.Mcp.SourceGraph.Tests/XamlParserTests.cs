using System.IO;
using System.Linq;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit tests for the XAML parser core: <see cref="XamlReader"/> position tracking,
/// <see cref="XamlAttributeClassifier"/> categorisation, and <see cref="MarkupExtensionParser"/>
/// tokenisation. Written against synthetic XAML strings so the rules are exercised in isolation
/// from any indexer state.
/// </summary>
public sealed class XamlParserTests
{
    [Fact]
    public void XamlReader_capturesElementPosition()
    {
        var src = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="A.B">
            <Grid>
                <Button Content="OK" />
            </Grid>
        </Window>
        """;
        var doc = XamlReader.Parse(XamlReader.EncodeUtf8(src));
        doc.Root.LocalName.Should().Be("Window");
        doc.Root.Line.Should().BeGreaterThan(0);
        var button = doc.Root.Children[0].Children[0];
        button.LocalName.Should().Be("Button");
        button.Line.Should().BeGreaterThan(doc.Root.Line);
    }

    [Fact]
    public void XamlReader_collectsRootNamespaceMappings()
    {
        var src = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Foo" />
        """;
        var doc = XamlReader.Parse(XamlReader.EncodeUtf8(src));
        doc.RootNamespaceMappings[""].Should().Be("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        doc.RootNamespaceMappings["x"].Should().Be("http://schemas.microsoft.com/winfx/2006/xaml");
        doc.RootNamespaceMappings["vm"].Should().Be("clr-namespace:Foo");
    }

    [Fact]
    public void XamlReader_walkInPreOrderWithAncestors()
    {
        var src = "<R xmlns=\"u\"><A><B/></A><C/></R>";
        var doc = XamlReader.Parse(XamlReader.EncodeUtf8(src));
        var seen = new System.Collections.Generic.List<string>();
        XamlReader.Walk(doc.Root, (e, ancestors) =>
            seen.Add($"{e.LocalName}({ancestors.Count})"));
        seen.Should().Equal("R(0)", "A(1)", "B(2)", "C(1)");
    }

    [Theory]
    [InlineData("xmlns", "", XamlAttributeKind.NamespaceDeclaration)]
    [InlineData("x", "xmlns", XamlAttributeKind.NamespaceDeclaration)]
    public void XamlAttributeClassifier_namespaceDeclarations(string local, string prefix, XamlAttributeKind expected)
    {
        var attr = new XamlAttribute(@namespace: "", prefix: prefix, localName: local, value: "v", line: 1, column: 1);
        XamlAttributeClassifier.Classify(attr).Should().Be(expected);
    }

    [Fact]
    public void XamlAttributeClassifier_xClassIsDirective()
    {
        var attr = new XamlAttribute(XamlReader.XamlNamespace, "x", "Class", "Foo.Bar", 1, 1);
        XamlAttributeClassifier.Classify(attr).Should().Be(XamlAttributeKind.XamlDirective);
    }

    [Fact]
    public void XamlAttributeClassifier_attachedProperty()
    {
        var attr = new XamlAttribute("u", "", "Grid.Row", "2", 1, 1);
        XamlAttributeClassifier.Classify(attr).Should().Be(XamlAttributeKind.AttachedProperty);
    }

    [Fact]
    public void XamlAttributeClassifier_markupExtension()
    {
        var attr = new XamlAttribute("u", "", "Text", "{Binding User.Name}", 1, 1);
        XamlAttributeClassifier.Classify(attr).Should().Be(XamlAttributeKind.MarkupExtension);
    }

    [Fact]
    public void XamlAttributeClassifier_value()
    {
        var attr = new XamlAttribute("u", "", "Text", "Hello", 1, 1);
        XamlAttributeClassifier.Classify(attr).Should().Be(XamlAttributeKind.Value);
    }

    [Fact]
    public void XamlAttributeClassifier_escapedBraceIsLiteral()
    {
        // The `{}` prefix is the canonical "treat the rest as a literal string" sentinel.
        var attr = new XamlAttribute("u", "", "Text", "{}{Not a binding}", 1, 1);
        XamlAttributeClassifier.Classify(attr).Should().Be(XamlAttributeKind.Value);
    }

    [Fact]
    public void MarkupExtensionParser_simpleNamedArgs()
    {
        var v = MarkupExtensionParser.Parse("{Binding Path=User.Name, Mode=TwoWay}");
        v.Name.Should().Be("Binding");
        v.NamedArgs["Path"].Literal.Should().Be("User.Name");
        v.NamedArgs["Mode"].Literal.Should().Be("TwoWay");
    }

    [Fact]
    public void MarkupExtensionParser_positionalArg()
    {
        var v = MarkupExtensionParser.Parse("{Binding User.Name}");
        v.Name.Should().Be("Binding");
        v.PositionalArgs.Should().HaveCount(1);
        v.PositionalArgs[0].Literal.Should().Be("User.Name");
    }

    [Fact]
    public void MarkupExtensionParser_explicitPathStillNamed()
    {
        var v = MarkupExtensionParser.Parse("{Binding Path=Foo}");
        v.NamedArgs["Path"].Literal.Should().Be("Foo");
    }

    [Fact]
    public void MarkupExtensionParser_staticResource()
    {
        var v = MarkupExtensionParser.Parse("{StaticResource MyKey}");
        v.Name.Should().Be("StaticResource");
        v.PositionalArgs[0].Literal.Should().Be("MyKey");
    }

    [Fact]
    public void MarkupExtensionParser_elementNameAndPath()
    {
        var v = MarkupExtensionParser.Parse("{Binding ElementName=Other, Path=Value}");
        v.NamedArgs["ElementName"].Literal.Should().Be("Other");
        v.NamedArgs["Path"].Literal.Should().Be("Value");
    }

    [Fact]
    public void MarkupExtensionParser_nestedExtension()
    {
        var v = MarkupExtensionParser.Parse("{Binding Path=Foo, Converter={StaticResource b2v}}");
        v.NamedArgs["Path"].Literal.Should().Be("Foo");
        var conv = v.NamedArgs["Converter"];
        conv.IsLiteral.Should().BeFalse();
        conv.Name.Should().Be("StaticResource");
        conv.PositionalArgs[0].Literal.Should().Be("b2v");
    }

    [Fact]
    public void MarkupExtensionParser_quotedValueWithComma()
    {
        var v = MarkupExtensionParser.Parse("{Binding Path='User, Inc.', Mode=OneWay}");
        v.NamedArgs["Path"].Literal.Should().Be("User, Inc.");
        v.NamedArgs["Mode"].Literal.Should().Be("OneWay");
    }

    [Fact]
    public void MarkupExtensionParser_escapedBrace()
    {
        // Backslash-escaped braces are unescaped into the bare value rather than terminating it.
        var v = MarkupExtensionParser.Parse(@"{Binding Path=foo\{bar\}baz}");
        v.NamedArgs["Path"].Literal.Should().Be("foo{bar}baz");
    }

    [Fact]
    public void MarkupExtensionParser_emptyExtensionParses()
    {
        var v = MarkupExtensionParser.Parse("{Binding}");
        v.Name.Should().Be("Binding");
        v.PositionalArgs.Should().BeEmpty();
        v.NamedArgs.Should().BeEmpty();
    }
}
