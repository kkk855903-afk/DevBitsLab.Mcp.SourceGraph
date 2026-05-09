using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the markdown payload sub-line emitted by <c>list_callers</c> /
/// <c>list_callees</c> / <c>neighborhood</c> when a row's originating <c>edges.payload</c>
/// column is non-null. Validates the documented shape from harden-sdk-pre-xaml §8.1–8.3:
/// 4-space indent, <c>payload: { key: "value", ... }</c>, capped at 5 keys with a
/// <c>(N more)</c> suffix when more keys are present.
///
/// <para>The renderer is exercised directly via <see cref="Format.PayloadSubLine"/> so the test
/// stays in-process and doesn't need a fake <c>ScopeRouter</c>; the round-trip from real edge
/// inserts through <c>ListCallersAsync</c> is covered by <see cref="EdgeMetadataRoundTripTests"/>.
/// </para>
/// </summary>
public sealed class PayloadRenderingTests
{
    [Fact]
    public void PayloadSubLine_rendersBindsPathExample_perDesignDoc()
    {
        // The exact shape design.md §5 calls out for a binds-path edge.
        const string payloadJson = """{"path":"User.Name","mode":"two-way"}""";

        var line = Format.PayloadSubLine(payloadJson);

        line.Should().NotBeNull();
        line.Should().StartWith("    payload: {");
        line.Should().Contain("path: \"User.Name\"");
        line.Should().Contain("mode: \"two-way\"");
        line.Should().EndWith("}");
        line.Should().NotContain("(more)", "no overflow when payload has fewer than the cap of 5 keys");
    }

    [Fact]
    public void PayloadSubLine_returnsNull_whenJsonIsNull()
    {
        // Default for a non-edge query path or an edge with no metadata; renderer must skip.
        Format.PayloadSubLine(null).Should().BeNull();
    }

    [Fact]
    public void PayloadSubLine_returnsNull_whenJsonIsBlank()
    {
        Format.PayloadSubLine("").Should().BeNull();
        Format.PayloadSubLine("   ").Should().BeNull();
    }

    [Fact]
    public void PayloadSubLine_returnsNull_whenJsonIsEmptyObject()
    {
        // Defensive: BulkInsertEdgesAsync writes NULL for an empty Metadata dictionary, but if a
        // future producer ever inserts "{}" the renderer should still skip the sub-line so the
        // agent isn't shown a vacuous "payload: {  }".
        Format.PayloadSubLine("{}").Should().BeNull();
    }

    [Fact]
    public void PayloadSubLine_returnsNull_whenJsonIsMalformed()
    {
        // Storage is opaque to payload shape — a malformed row must NOT crash the renderer.
        Format.PayloadSubLine("not-json").Should().BeNull();
        Format.PayloadSubLine("[1, 2, 3]").Should().BeNull();      // array, not object
    }

    [Fact]
    public void PayloadSubLine_capsAtFiveKeys_andAppendsMoreSuffix()
    {
        // §8.2: cap rendered payload to first 5 keys; append " (N more)" when overflow.
        const string sevenKeys = """
            {"a":"1","b":"2","c":"3","d":"4","e":"5","f":"6","g":"7"}
            """;

        var line = Format.PayloadSubLine(sevenKeys);

        line.Should().NotBeNull();
        line.Should().Contain("a: \"1\"");
        line.Should().Contain("e: \"5\"");
        line.Should().NotContain("f: \"6\"", "the 6th key falls past the 5-key render cap");
        line.Should().Contain("(2 more)");
    }

    [Fact]
    public void PayloadSubLine_capsAtFiveKeys_withoutSuffix_whenExactlyFive()
    {
        const string fiveKeys = """{"a":"1","b":"2","c":"3","d":"4","e":"5"}""";

        var line = Format.PayloadSubLine(fiveKeys);

        line.Should().NotBeNull();
        line.Should().Contain("a: \"1\"");
        line.Should().Contain("e: \"5\"");
        line.Should().NotContain("more)", "exactly five keys is at the cap, not over it");
    }

    [Fact]
    public void PayloadSubLine_rendersNonStringValuesAsRawJson()
    {
        // Strings render with quotes for readability; numbers, bools, nulls round-trip via
        // GetRawText so a producer that emits {"count": 42, "enabled": true} still surfaces.
        const string mixedTypes = """{"count":42,"enabled":true,"label":"production"}""";

        var line = Format.PayloadSubLine(mixedTypes);

        line.Should().NotBeNull();
        line.Should().Contain("count: 42");
        line.Should().Contain("enabled: true");
        line.Should().Contain("label: \"production\"");
    }

    [Fact]
    public void PayloadSubLine_usesFourSpaceIndent_perTaskSpec()
    {
        // §8.1: "Suggested format: `    payload: { ... }` (4 leading spaces for indent)".
        var line = Format.PayloadSubLine("""{"k":"v"}""");

        line.Should().NotBeNull();
        line!.Substring(0, 4).Should().Be("    ");
        line.Substring(4).Should().StartWith("payload:");
    }
}
