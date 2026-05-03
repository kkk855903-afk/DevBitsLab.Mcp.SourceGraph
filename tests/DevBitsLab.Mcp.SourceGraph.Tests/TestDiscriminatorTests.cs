using System.Linq;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit tests for <see cref="TestDiscriminator"/>, the attribute-based test framework
/// detector that powers the indexer's <c>test_framework</c> column.
/// </summary>
public sealed class TestDiscriminatorTests
{
    private static Compilation Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Console).Assembly.Location),
        };
        return CSharpCompilation.Create("UnitTest", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IMethodSymbol GetMethod(Compilation comp, string typeName, string methodName)
    {
        var type = comp.GetSymbolsWithName(typeName, SymbolFilter.Type).OfType<INamedTypeSymbol>().Single();
        return (IMethodSymbol)type.GetMembers(methodName).First();
    }

    [Fact]
    public void Detect_xUnitFact_returnsXunit()
    {
        var c = Compile("""
            namespace Xunit { public sealed class FactAttribute : System.Attribute { } }
            public class C
            {
                [Xunit.Fact] public void T1() { }
            }
            """);
        TestDiscriminator.Detect(GetMethod(c, "C", "T1")).Should().Be("xunit");
    }

    [Fact]
    public void Detect_xUnitTheory_returnsXunit()
    {
        var c = Compile("""
            namespace Xunit { public sealed class TheoryAttribute : System.Attribute { } }
            public class C
            {
                [Xunit.Theory] public void T2(int n) { }
            }
            """);
        TestDiscriminator.Detect(GetMethod(c, "C", "T2")).Should().Be("xunit");
    }

    [Fact]
    public void Detect_NUnitTestInsideTestFixture_returnsNunit()
    {
        var c = Compile("""
            namespace NUnit.Framework
            {
                public sealed class TestAttribute : System.Attribute { }
                public sealed class TestFixtureAttribute : System.Attribute { }
            }
            [NUnit.Framework.TestFixture]
            public class C
            {
                [NUnit.Framework.Test] public void T1() { }
            }
            """);
        TestDiscriminator.Detect(GetMethod(c, "C", "T1")).Should().Be("nunit");
    }

    [Fact]
    public void Detect_NUnitTestWithoutTestFixture_returnsNull()
    {
        // [Test] alone (without [TestFixture] on the containing class) is rejected to avoid
        // false positives on user-defined "Test" attributes.
        var c = Compile("""
            namespace NUnit.Framework
            {
                public sealed class TestAttribute : System.Attribute { }
            }
            public class C
            {
                [NUnit.Framework.Test] public void T1() { }
            }
            """);
        TestDiscriminator.Detect(GetMethod(c, "C", "T1")).Should().BeNull();
    }

    [Fact]
    public void Detect_MsTestTestMethod_returnsMstest()
    {
        var c = Compile("""
            namespace Microsoft.VisualStudio.TestTools.UnitTesting
            {
                public sealed class TestMethodAttribute : System.Attribute { }
            }
            public class C
            {
                [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod] public void T1() { }
            }
            """);
        TestDiscriminator.Detect(GetMethod(c, "C", "T1")).Should().Be("mstest");
    }

    [Fact]
    public void Detect_methodWithNoTestAttribute_returnsNull()
    {
        var c = Compile("""
            public class C
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        TestDiscriminator.Detect(GetMethod(c, "C", "Add")).Should().BeNull();
    }

    [Fact]
    public void IsTestFixture_classWithTestFixtureAttribute_returnsTrue()
    {
        var c = Compile("""
            namespace NUnit.Framework
            {
                public sealed class TestFixtureAttribute : System.Attribute { }
            }
            [NUnit.Framework.TestFixture]
            public class C { public void M() { } }
            """);
        var type = c.GetSymbolsWithName("C", SymbolFilter.Type).OfType<INamedTypeSymbol>().Single();
        TestDiscriminator.IsTestFixture(type).Should().BeTrue();
    }

    [Fact]
    public void IsTestFixture_classWithFactMember_returnsTrue()
    {
        // xUnit doesn't require a class marker — having a [Fact] method is enough.
        var c = Compile("""
            namespace Xunit { public sealed class FactAttribute : System.Attribute { } }
            public class C
            {
                [Xunit.Fact] public void T1() { }
            }
            """);
        var type = c.GetSymbolsWithName("C", SymbolFilter.Type).OfType<INamedTypeSymbol>().Single();
        TestDiscriminator.IsTestFixture(type).Should().BeTrue();
    }

    [Fact]
    public void IsTestFixture_plainClass_returnsFalse()
    {
        var c = Compile("public class C { public int Add(int a, int b) => a + b; }");
        var type = c.GetSymbolsWithName("C", SymbolFilter.Type).OfType<INamedTypeSymbol>().Single();
        TestDiscriminator.IsTestFixture(type).Should().BeFalse();
    }
}
