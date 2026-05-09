using System;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit-level coverage of the <see cref="KebabCaseValidator"/> introduced by
/// open-language-contract. The validator is the single gatekeeper for edge / symbol / annotation
/// kind names emitted by plugins, so its definition of "valid" must match the spec exactly:
/// non-empty lowercase letters/digits with single hyphens between segments, no leading or
/// trailing hyphens, no double hyphens, no whitespace, no underscores, no uppercase.
/// </summary>
public sealed class KebabCaseValidatorTests
{
    [Theory]
    [InlineData("calls")]
    [InlineData("binds-path")]
    [InlineData("type-parameter")]
    [InlineData("a")]                 // single character — minimal valid case
    [InlineData("a-b")]               // single-hyphen separator
    [InlineData("aa1-bb2-cc3")]       // digits allowed inside segments
    [InlineData("renders-component")] // typical multi-word kind
    public void IsValid_returnsTrue_forValidKebabCaseIdentifiers(string value)
    {
        KebabCaseValidator.IsValid(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("RendersComponent")]   // PascalCase
    [InlineData("renders_component")]  // snake_case
    [InlineData("-leading")]            // leading hyphen
    [InlineData("trailing-")]           // trailing hyphen
    [InlineData("double--hyphen")]     // consecutive hyphens
    [InlineData("with space")]          // spaces inside
    [InlineData("UPPER")]               // uppercase
    [InlineData("Camel")]               // PascalCase
    [InlineData("has.dot")]             // dot is reserved as separator
    public void IsValid_returnsFalse_forInvalidIdentifiers(string value)
    {
        KebabCaseValidator.IsValid(value).Should().BeFalse();
    }

    [Fact]
    public void IsValid_returnsFalse_forNull()
    {
        KebabCaseValidator.IsValid(null).Should().BeFalse();
    }

    [Fact]
    public void Validate_throwsArgumentException_whenInvalid()
    {
        var act = () => KebabCaseValidator.Validate("Invalid", "kindParam");
        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("kindParam");
    }

    [Fact]
    public void Validate_doesNotThrow_whenValid()
    {
        var act = () => KebabCaseValidator.Validate("calls", "kindParam");
        act.Should().NotThrow();
    }
}

/// <summary>
/// Unit-level coverage of the <see cref="CanonicalKeyValidator"/> introduced by
/// open-language-contract. Asserts the URI-prefix shape <c>&lt;scheme&gt;:&lt;rest&gt;</c>,
/// the reserved-and-enforced scheme set ({csharp, xaml} at v1), and the rule that path
/// components in keys MUST use forward slashes regardless of OS.
/// </summary>
public sealed class CanonicalKeyValidatorTests
{
    [Theory]
    [InlineData("csharp:T:Foo")]
    [InlineData("csharp:M:Sample.Domain.Calculator.Add(System.Int32,System.Int32)")]
    [InlineData("xaml:element:Views/Main.xaml#X")]
    [InlineData("xaml:element:Views/Sub/Foo.xaml#binding")]
    public void IsValid_returnsTrue_forReservedSchemes(string key)
    {
        CanonicalKeyValidator.IsValid(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("python:M:foo")]                       // unknown scheme
    [InlineData("vbnet:T:Foo")]                        // reserved-but-not-enforced
    [InlineData("ts:T:Foo")]                           // reserved-but-not-enforced
    [InlineData("xaml:element:Views\\Main.xaml#X")]    // backslash forbidden
    [InlineData("CSharp:T:Foo")]                       // uppercase scheme
    [InlineData("Csharp:T:Foo")]                       // mixed case scheme
    [InlineData("csharp:")]                            // empty body
    [InlineData(":body")]                              // empty scheme
    [InlineData("nocolon")]                            // missing scheme separator
    [InlineData("")]
    public void IsValid_returnsFalse_forInvalidKeys(string key)
    {
        CanonicalKeyValidator.IsValid(key).Should().BeFalse();
    }

    [Fact]
    public void IsValid_returnsFalse_forNull()
    {
        CanonicalKeyValidator.IsValid(null).Should().BeFalse();
    }

    [Fact]
    public void Validate_throwsArgumentException_forUnknownScheme()
    {
        var act = () => CanonicalKeyValidator.Validate("unknownscheme:something", "key");
        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("key");
    }

    [Fact]
    public void Validate_messageHints_atReservedFutureUse()
    {
        // vbnet is in ReservedFutureSchemes (documented but not yet enforced). The validator's
        // error message must specifically call out the reserved-but-not-enforced status so the
        // plugin author knows to switch to an enforced scheme rather than reading the rejection
        // as a typo. Pair with `Validate_messageDoesNotHintFutureUse_forCompletelyUnknownScheme`
        // below, which asserts the inverse for a scheme that's not reserved at all.
        var act = () => CanonicalKeyValidator.Validate("vbnet:T:Foo", "key");
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("reserved for a future language");
    }

    [Fact]
    public void Validate_messageDoesNotHintFutureUse_forCompletelyUnknownScheme()
    {
        var act = () => CanonicalKeyValidator.Validate("python:M:foo", "key");
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().NotContain("reserved for a future language");
    }

    [Fact]
    public void Validate_throwsArgumentException_forBackslashInBody()
    {
        var act = () => CanonicalKeyValidator.Validate("xaml:element:Views\\Main.xaml#X", "key");
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("backslash");
    }

    [Fact]
    public void Validate_throwsArgumentException_forMissingScheme()
    {
        var act = () => CanonicalKeyValidator.Validate("nocolon", "key");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EnforcedSchemes_containsCsharpAndXaml()
    {
        // Surface property is documented; assert v1's two-scheme set. A future SDK release
        // expands this; a deliberate update of the test set is the trip-wire for that.
        CanonicalKeyValidator.EnforcedSchemes.Should().BeEquivalentTo(new[] { "csharp", "xaml" });
    }

    [Fact]
    public void ReservedFutureSchemes_isNonEmpty_andDoesNotIntersectEnforced()
    {
        CanonicalKeyValidator.ReservedFutureSchemes.Should().NotBeEmpty();
        // Reserved-future and enforced are disjoint sets — every future scheme is "not yet" enforced.
        foreach (var scheme in CanonicalKeyValidator.ReservedFutureSchemes)
        {
            CanonicalKeyValidator.EnforcedSchemes.Should().NotContain(scheme,
                $"reserved-future scheme '{scheme}' must not appear in the enforced set");
        }
    }
}
