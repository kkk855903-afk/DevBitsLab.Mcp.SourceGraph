using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Translates a method's attached attribute set into one of <c>xunit | nunit | mstest | null</c>.
/// We intentionally only inspect the simple attribute name (with the trailing <c>Attribute</c>
/// stripped) — pulling in containing-namespace checks would require fully-resolved attribute
/// types and adds complexity for little benefit; the names below are unique enough in the .NET
/// ecosystem to discriminate cleanly.
/// </summary>
internal static class TestDiscriminator
{
    /// <summary>
    /// Returns <c>"xunit" | "nunit" | "mstest"</c> when <paramref name="symbol"/> is decorated
    /// with one of the framework's discriminator attributes. NUnit's <c>[Test]</c> /
    /// <c>[TestCase]</c> only counts when the containing type carries <c>[TestFixture]</c> —
    /// otherwise we'd false-positive on any random method called <c>Test</c>.
    /// </summary>
    public static string? Detect(IMethodSymbol method)
    {
        var attrs = method.GetAttributes();
        if (attrs.IsDefaultOrEmpty) return null;

        var hasXUnit = false;
        var hasNUnitTest = false;
        var hasMsTest = false;

        foreach (var a in attrs)
        {
            var cls = a.AttributeClass;
            if (cls is null) continue;
            var name = StripAttributeSuffix(cls.Name);
            switch (name)
            {
                case "Fact":
                case "Theory":
                    hasXUnit = true;
                    break;
                case "Test":
                case "TestCase":
                case "TestCaseSource":
                    hasNUnitTest = true;
                    break;
                case "TestMethod":
                case "DataTestMethod":
                    hasMsTest = true;
                    break;
            }
        }

        if (hasXUnit) return "xunit";
        if (hasMsTest) return "mstest";
        if (hasNUnitTest && IsNUnitFixture(method.ContainingType)) return "nunit";
        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is a test fixture: marked <c>[TestFixture]</c>
    /// (NUnit), <c>[TestClass]</c> (MSTest), or contains at least one method decorated with an
    /// xUnit discriminator (xUnit doesn't require a class-level marker).
    /// </summary>
    public static bool IsTestFixture(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        var attrs = type.GetAttributes();
        foreach (var a in attrs)
        {
            var cls = a.AttributeClass;
            if (cls is null) continue;
            var name = StripAttributeSuffix(cls.Name);
            if (name is "TestFixture" or "TestClass") return true;
        }
        // xUnit has no class marker — infer from any contained test method.
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol m && Detect(m) is not null) return true;
        }
        return false;
    }

    /// <summary>True when the containing type is annotated <c>[TestFixture]</c>.</summary>
    private static bool IsNUnitFixture(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        foreach (var a in type.GetAttributes())
        {
            var cls = a.AttributeClass;
            if (cls is null) continue;
            if (StripAttributeSuffix(cls.Name) == "TestFixture") return true;
        }
        return false;
    }

    private static string StripAttributeSuffix(string name)
    {
        const string suffix = "Attribute";
        return name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length]
            : name;
    }
}
