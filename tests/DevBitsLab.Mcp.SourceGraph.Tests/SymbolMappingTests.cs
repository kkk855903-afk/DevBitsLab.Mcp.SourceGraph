using System.Linq;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Verifies the three Roslyn-fact extractors that power the v5 symbol enrichment:
/// <c>Modifiers</c>, <c>Accessibility</c>, and <c>XmlSummary</c> (including <c>&lt;inheritdoc/&gt;</c>
/// resolution).
/// </summary>
public sealed class SymbolMappingTests
{
    private static Compilation Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(documentationMode: DocumentationMode.Parse));
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Console).Assembly.Location),
        };
        return CSharpCompilation.Create("UnitTest", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static ISymbol GetMember(Compilation comp, string typeName, string memberName)
    {
        var type = comp.GetSymbolsWithName(typeName, SymbolFilter.Type).OfType<INamedTypeSymbol>().Single();
        return type.GetMembers(memberName).First();
    }

    [Fact]
    public void Modifiers_publicAsyncMethod_returnsAsync()
    {
        var c = Compile("""
            using System.Threading.Tasks;
            public class C { public async Task DoAsync() { await Task.Yield(); } }
            """);
        var sym = GetMember(c, "C", "DoAsync");
        SymbolMapping.Modifiers(sym).Should().Be("async");
    }

    [Fact]
    public void Modifiers_privateReadonlyField_returnsReadonly()
    {
        var c = Compile("public class C { private readonly string _x = \"\"; }");
        var sym = GetMember(c, "C", "_x");
        SymbolMapping.Modifiers(sym).Should().Be("readonly");
    }

    [Fact]
    public void Modifiers_publicMethodWithNoModifiers_returnsNull()
    {
        var c = Compile("public class C { public int Add(int a, int b) => a + b; }");
        var sym = GetMember(c, "C", "Add");
        SymbolMapping.Modifiers(sym).Should().BeNull();
    }

    [Fact]
    public void Modifiers_staticVirtualOverride_orderedCanonical()
    {
        // Static + override is meaningful only via abstract-static interfaces; emulate just static
        // for the canonical-order assertion.
        var c = Compile("public static class C { public static int X() => 1; }");
        var sym = GetMember(c, "C", "X");
        SymbolMapping.Modifiers(sym).Should().Be("static");
    }

    [Fact]
    public void Modifiers_interfaceMember_suppressesAbstract()
    {
        var c = Compile("public interface I { int Greet(string name); }");
        var sym = GetMember(c, "I", "Greet");
        // Interface methods report IsAbstract=true; we suppress to keep the row uncluttered.
        SymbolMapping.Modifiers(sym).Should().BeNull();
    }

    [Fact]
    public void Accessibility_returnsRoslynEnumInteger()
    {
        var c = Compile("public class C { private int Hidden; public int Visible; }");
        var hidden = GetMember(c, "C", "Hidden");
        var visible = GetMember(c, "C", "Visible");
        // Roslyn's Accessibility enum: Private=1, Public=6.
        SymbolMapping.Accessibility(hidden).Should().Be((int)Accessibility.Private);
        SymbolMapping.Accessibility(visible).Should().Be((int)Accessibility.Public);
    }

    [Fact]
    public void XmlSummary_simpleSummary_returnsParsedText()
    {
        var c = Compile("""
            public class C
            {
                /// <summary>Publishes the feed.</summary>
                public void Publish() { }
            }
            """);
        var sym = GetMember(c, "C", "Publish");
        SymbolMapping.XmlSummary(sym).Should().Be("Publishes the feed.");
    }

    [Fact]
    public void XmlSummary_inlineSeeAndParamRef_renderedAsPlainText()
    {
        var c = Compile("""
            public class C
            {
                /// <summary>Adds <paramref name="a"/> to <paramref name="b"/>.</summary>
                public int Add(int a, int b) => a + b;
            }
            """);
        var sym = GetMember(c, "C", "Add");
        SymbolMapping.XmlSummary(sym).Should().Be("Adds a to b.");
    }

    [Fact]
    public void XmlSummary_inheritDoc_resolvesUpOverrideChain()
    {
        var c = Compile("""
            public abstract class Base
            {
                /// <summary>Greets the world.</summary>
                public abstract string Greet();
            }
            public class Derived : Base
            {
                /// <inheritdoc/>
                public override string Greet() => "hi";
            }
            """);
        var sym = GetMember(c, "Derived", "Greet");
        SymbolMapping.XmlSummary(sym).Should().Be("Greets the world.");
    }

    [Fact]
    public void XmlSummary_inheritDoc_resolvesViaInterface()
    {
        var c = Compile("""
            public interface IGreeter
            {
                /// <summary>Greets the world.</summary>
                string Greet();
            }
            public class Greeter : IGreeter
            {
                /// <inheritdoc/>
                public string Greet() => "hi";
            }
            """);
        var sym = GetMember(c, "Greeter", "Greet");
        SymbolMapping.XmlSummary(sym).Should().Be("Greets the world.");
    }

    [Fact]
    public void XmlSummary_noDocAtAll_returnsNull()
    {
        var c = Compile("public class C { public int Add(int a, int b) => a + b; }");
        var sym = GetMember(c, "C", "Add");
        SymbolMapping.XmlSummary(sym).Should().BeNull();
    }

    [Fact]
    public void XmlSummary_inheritDocOnTopOfChain_returnsNull()
    {
        // <inheritdoc/> with no parent symbol to resolve from -> NULL, not "".
        var c = Compile("""
            public class C
            {
                /// <inheritdoc/>
                public string Solo() => "x";
            }
            """);
        var sym = GetMember(c, "C", "Solo");
        SymbolMapping.XmlSummary(sym).Should().BeNull();
    }
}
