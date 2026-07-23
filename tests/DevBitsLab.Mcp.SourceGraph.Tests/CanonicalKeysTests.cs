using System;
using System.Linq;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit-level coverage of the <see cref="CanonicalKeys"/> SDK helpers. The contract is that the keys
/// they produce are byte-identical to <c>"csharp:" + ISymbol.GetDocumentationCommentId()</c> for the
/// symbol shapes covered, so cross-language plugins can construct C# canonical keys by name without
/// depending on Roslyn at runtime. The <see cref="Roslyn_round_trip_for_Sample_Domain_shapes"/>
/// test compiles a Sample.Domain stand-in inline and asserts each helper's output equals the
/// scheme-prefixed Roslyn output for the same symbol.
/// </summary>
public sealed class CanonicalKeysTests
{
    [Fact]
    public void ForType_simpleType_returnsCsharpTPrefix()
    {
        CanonicalKeys.ForType("MyApp.Foo").Should().Be("csharp:T:MyApp.Foo");
    }

    [Fact]
    public void ForType_nestedTypeWithPlus_normalisesToDot()
    {
        // Doc-comment-id always uses '.' for nested types regardless of whether the input source
        // used C#'s '+' separator (Reflection-style) or '.' (Roslyn-style). Both inputs converge.
        CanonicalKeys.ForType("MyApp.Outer+Inner").Should().Be("csharp:T:MyApp.Outer.Inner");
        CanonicalKeys.ForType("MyApp.Outer.Inner").Should().Be("csharp:T:MyApp.Outer.Inner");
    }

    [Fact]
    public void ForType_openGenericSingleArity_emitsBacktickOne()
    {
        CanonicalKeys.ForType("System.Collections.Generic.List<T>")
            .Should().Be("csharp:T:System.Collections.Generic.List`1");
    }

    [Fact]
    public void ForType_openGenericTwoArities_emitsBacktickTwo()
    {
        CanonicalKeys.ForType("System.Collections.Generic.Dictionary<TKey,TValue>")
            .Should().Be("csharp:T:System.Collections.Generic.Dictionary`2");
    }

    [Fact]
    public void ForType_openGenericTwoArities_alternateInputSpelling()
    {
        // Same arity-2 dictionary but with shorter type-parameter names and a space the SDK should
        // tolerate; arity counts top-level commas, not characters.
        CanonicalKeys.ForType("System.Collections.Generic.Dictionary<K,V>")
            .Should().Be("csharp:T:System.Collections.Generic.Dictionary`2");
    }

    [Fact]
    public void ForType_globalPrefix_isStripped()
    {
        CanonicalKeys.ForType("global::MyApp.Foo").Should().Be("csharp:T:MyApp.Foo");
    }

    [Fact]
    public void ForType_closedGenericTreatedAsOpenForm()
    {
        // Documented v1 simplification: closed generics emit the open-generic form. Roslyn's
        // GetDocumentationCommentId() also returns the open form for type definitions, so this
        // matches Roslyn for the type-definition case while side-stepping the curly-brace
        // substitution rules for cref attributes.
        CanonicalKeys.ForType("System.Collections.Generic.List<int>")
            .Should().Be("csharp:T:System.Collections.Generic.List`1");
    }

    [Fact]
    public void ForType_nestedGenericFlattensInnerArguments()
    {
        // Dictionary<int, List<string>> has arity 2 at the outer level. The inner List<...> is
        // consumed inside the outer angle-bracket block and does NOT inflate the outer arity.
        CanonicalKeys.ForType("System.Collections.Generic.Dictionary<int,System.Collections.Generic.List<string>>")
            .Should().Be("csharp:T:System.Collections.Generic.Dictionary`2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForType_invalidInputs_throwArgumentException(string? input)
    {
        var act = () => CanonicalKeys.ForType(input!);
        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("fullyQualifiedName");
    }

    [Fact]
    public void ForMethod_nullParamList_omitsParens()
    {
        // Spec: a method key with NO parameter list omits parens entirely; the resulting key
        // matches every overload by name.
        CanonicalKeys.ForMethod("MyApp.Calc", "Add", null)
            .Should().Be("csharp:M:MyApp.Calc.Add");
    }

    [Fact]
    public void ForMethod_emptyParamList_emitsEmptyParens()
    {
        // Empty list != null: this targets the parameterless overload specifically.
        CanonicalKeys.ForMethod("MyApp.Calc", "Add", Array.Empty<string>())
            .Should().Be("csharp:M:MyApp.Calc.Add()");
    }

    [Fact]
    public void ForMethod_paramList_commaSeparatedNoSpaces()
    {
        CanonicalKeys.ForMethod("MyApp.Calc", "Add", new[] { "System.Int32", "System.Int32" })
            .Should().Be("csharp:M:MyApp.Calc.Add(System.Int32,System.Int32)");
    }

    [Fact]
    public void ForMethod_paramList_paramTypesNormalised()
    {
        // global:: prefix on a parameter type is stripped just like it is on the declaring type.
        CanonicalKeys.ForMethod("MyApp.Calc", "Take", new[] { "global::System.String" })
            .Should().Be("csharp:M:MyApp.Calc.Take(System.String)");
    }

    [Theory]
    [InlineData(null, "Add")]
    [InlineData("", "Add")]
    [InlineData("  ", "Add")]
    public void ForMethod_invalidType_throws(string? typeName, string methodName)
    {
        var act = () => CanonicalKeys.ForMethod(typeName!, methodName, null);
        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("typeFullyQualifiedName");
    }

    [Theory]
    [InlineData("MyApp.Calc", null)]
    [InlineData("MyApp.Calc", "")]
    [InlineData("MyApp.Calc", "  ")]
    public void ForMethod_invalidMethodName_throws(string typeName, string? methodName)
    {
        var act = () => CanonicalKeys.ForMethod(typeName, methodName!, null);
        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("methodName");
    }

    [Fact]
    public void ForMethod_blankParamInList_throws()
    {
        var act = () => CanonicalKeys.ForMethod("MyApp.Calc", "Take", new[] { "System.Int32", "" });
        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("parameterTypeFullyQualifiedNames");
    }

    [Fact]
    public void ForField_simpleField_returnsCsharpFPrefix()
    {
        CanonicalKeys.ForField("MyApp.Calc", "_state").Should().Be("csharp:F:MyApp.Calc._state");
    }

    [Fact]
    public void ForField_typeKeyNormalisationApplies()
    {
        CanonicalKeys.ForField("global::MyApp.Outer+Inner", "_x")
            .Should().Be("csharp:F:MyApp.Outer.Inner._x");
    }

    [Theory]
    [InlineData(null, "f")]
    [InlineData("", "f")]
    [InlineData("Type", null)]
    [InlineData("Type", "")]
    public void ForField_invalidInputs_throw(string? type, string? field)
    {
        var act = () => CanonicalKeys.ForField(type!, field!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForProperty_simpleProperty_returnsCsharpPPrefix()
    {
        CanonicalKeys.ForProperty("MyApp.Calc", "Result").Should().Be("csharp:P:MyApp.Calc.Result");
    }

    [Theory]
    [InlineData(null, "P")]
    [InlineData("", "P")]
    [InlineData("Type", null)]
    [InlineData("Type", "")]
    public void ForProperty_invalidInputs_throw(string? type, string? prop)
    {
        var act = () => CanonicalKeys.ForProperty(type!, prop!);
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// End-to-end Roslyn round-trip: compile a Sample.Domain stand-in inline, look up a handful of
    /// symbols (a class, a method, a field, a property), and assert that each
    /// <see cref="CanonicalKeys"/> helper output equals <c>"csharp:" + symbol.GetDocumentationCommentId()</c>
    /// for the same shape. This is the load-bearing test: any drift between the SDK helper format
    /// and Roslyn's doc-comment-id format breaks cross-language joins on
    /// <c>symbols.canonical_key</c>, so it MUST stay green.
    /// <para>Compiles inline rather than loading the on-disk Sample.Domain via MSBuildWorkspace —
    /// the test only needs the symbol shapes (Calculator, Add, Subtract, _count, Result), and an
    /// inline compilation has no MSBuild prerequisites and runs in milliseconds.</para>
    /// </summary>
    [Fact]
    public void Roslyn_round_trip_for_Sample_Domain_shapes()
    {
        const string source = """
            namespace Sample.Domain;
            public class Calculator
            {
                private int _count;
                public int Result => _count;
                public int Add(int a, int b) => a + b;
                public int Subtract(int a, int b) => a - b;
                public int Parameterless() => 0;
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "RoslynRoundTrip",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var calculator = compilation.GetSymbolsWithName("Calculator", SymbolFilter.Type)
            .OfType<INamedTypeSymbol>().Single();

        // Type
        var typeKey = "csharp:" + calculator.GetDocumentationCommentId();
        CanonicalKeys.ForType("Sample.Domain.Calculator").Should().Be(typeKey,
            "ForType output must match the prefix-applied Roslyn doc-comment-id for the same type");

        // Method with two int parameters
        var add = calculator.GetMembers("Add").OfType<IMethodSymbol>().Single();
        var addKey = "csharp:" + add.GetDocumentationCommentId();
        CanonicalKeys
            .ForMethod("Sample.Domain.Calculator", "Add", new[] { "System.Int32", "System.Int32" })
            .Should().Be(addKey);

        var subtract = calculator.GetMembers("Subtract").OfType<IMethodSymbol>().Single();
        var subtractKey = "csharp:" + subtract.GetDocumentationCommentId();
        CanonicalKeys
            .ForMethod("Sample.Domain.Calculator", "Subtract", new[] { "System.Int32", "System.Int32" })
            .Should().Be(subtractKey);

        // Method with no parameters — Roslyn's GetDocumentationCommentId() for an actual
        // parameterless method emits NO parens at all (the doc-comment-id parameter list is
        // unspecified in this case). The SDK matches that with the null-paramList overload;
        // passing an empty array would emit "()" which Roslyn does not do for type-definition
        // doc-comment-ids. The empty-paren form is exercised by the unit-only
        // ForMethod_emptyParamList_emitsEmptyParens test.
        var parameterless = calculator.GetMembers("Parameterless").OfType<IMethodSymbol>().Single();
        var parameterlessKey = "csharp:" + parameterless.GetDocumentationCommentId();
        CanonicalKeys
            .ForMethod("Sample.Domain.Calculator", "Parameterless", null)
            .Should().Be(parameterlessKey);

        // Field
        var count = calculator.GetMembers("_count").OfType<IFieldSymbol>().Single();
        var countKey = "csharp:" + count.GetDocumentationCommentId();
        CanonicalKeys.ForField("Sample.Domain.Calculator", "_count").Should().Be(countKey);

        // Property
        var result = calculator.GetMembers("Result").OfType<IPropertySymbol>().Single();
        var resultKey = "csharp:" + result.GetDocumentationCommentId();
        CanonicalKeys.ForProperty("Sample.Domain.Calculator", "Result").Should().Be(resultKey);
    }

    /// <summary>
    /// Roslyn round-trip for a generic open type, asserting backtick-arity rendering matches
    /// <c>GetDocumentationCommentId()</c>.
    /// </summary>
    [Fact]
    public void Roslyn_round_trip_for_open_generic_type()
    {
        const string source = """
            namespace Sample.Domain;
            public class Box<T> { }
            public class Pair<TKey, TValue> { }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create(
            "RoslynRoundTripGeneric",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var box = compilation.GetSymbolsWithName("Box", SymbolFilter.Type)
            .OfType<INamedTypeSymbol>().Single();
        var boxKey = "csharp:" + box.GetDocumentationCommentId();
        CanonicalKeys.ForType("Sample.Domain.Box<T>").Should().Be(boxKey);

        var pair = compilation.GetSymbolsWithName("Pair", SymbolFilter.Type)
            .OfType<INamedTypeSymbol>().Single();
        var pairKey = "csharp:" + pair.GetDocumentationCommentId();
        CanonicalKeys.ForType("Sample.Domain.Pair<TKey,TValue>").Should().Be(pairKey);
    }
}
