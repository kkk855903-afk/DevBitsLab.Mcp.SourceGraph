using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Self-validation tests for the <see cref="PayloadKeys"/> SDK constants. Plugins are free to
/// emit their own kebab-case keys, but the well-known constants the SDK ships MUST themselves
/// satisfy the kebab-case format (lowercase letters/digits, single hyphens between segments).
/// Reflection enumerates every public string constant so a future addition is automatically
/// covered without test maintenance — adding a non-conforming constant fails the suite.
/// </summary>
public sealed class PayloadKeysTests
{
    private static readonly Regex KebabCasePattern = new(
        "^[a-z][a-z0-9]*(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TheoryData<string, string> AllConstants()
    {
        var data = new TheoryData<string, string>();
        var stringConstants = typeof(PayloadKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));
        foreach (var field in stringConstants)
        {
            var value = (string?)field.GetRawConstantValue();
            data.Add(field.Name, value ?? string.Empty);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllConstants))]
    public void EveryConstant_matchesKebabCaseFormat(string name, string value)
    {
        // Trip-wire for any future constant that drifts from the documented kebab-case shape.
        // The spec format is `^[a-z][a-z0-9]*(-[a-z0-9]+)*$` — start with a lowercase letter,
        // optionally followed by lowercase letters / digits, with single-hyphen separators.
        KebabCasePattern.IsMatch(value).Should().BeTrue(
            $"PayloadKeys.{name} = \"{value}\" must be a valid kebab-case key");
    }

    [Fact]
    public void Reflection_findsAtLeastTheDocumentedFifteenConstants()
    {
        // Sanity check that the reflection enumeration is actually picking up constants — without
        // this guard, an empty TheoryData would let the kebab-case test pass vacuously.
        var count = typeof(PayloadKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Count(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));
        count.Should().BeGreaterThanOrEqualTo(15);
    }
}
