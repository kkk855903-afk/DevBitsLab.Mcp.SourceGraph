using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Verifies the layered-tooling sentence (describe_schema → query_graph) lives in the published
/// <c>ServerInstructions</c> template and is suppressed alongside the rest of the payload by
/// <c>--no-instructions</c> / <c>SOURCEGRAPH_NO_INSTRUCTIONS</c>. Mirrors the existing
/// <see cref="CommandLineNoInstructionsTests"/> CLI-flag coverage.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class ServerInstructionsQueryGraphTests
{
    private const string LayeredSentenceFragment = "describe_schema";
    private const string LayeredSentenceFragment2 = "query_graph";

    [Fact]
    public void Template_includesTheLayeredQueryGraphSentence()
    {
        ServerInstructions.Template.Should().Contain(LayeredSentenceFragment);
        ServerInstructions.Template.Should().Contain(LayeredSentenceFragment2);
        ServerInstructions.Template.Should().Contain("stable contract");
        ServerInstructions.Template.Should().Contain("v_symbols");
    }

    [Fact]
    public void ResolvePublished_withoutLeafSuppression_keepsLeafAndLayeredSentence()
    {
        var saved = LeafFormatter.Suppressed;
        try
        {
            LeafFormatter.Suppressed = false;
            var published = ServerInstructions.ResolvePublished();
            published.Should().StartWith(LeafFormatter.Mark);
            published.Should().Contain(LayeredSentenceFragment);
            published.Should().Contain(LayeredSentenceFragment2);
        }
        finally
        {
            LeafFormatter.Suppressed = saved;
        }
    }

    [Fact]
    public void ResolvePublished_withLeafSuppression_stripsLeafButKeepsLayeredSentence()
    {
        var saved = LeafFormatter.Suppressed;
        try
        {
            LeafFormatter.Suppressed = true;
            var published = ServerInstructions.ResolvePublished();
            published.Should().NotStartWith(LeafFormatter.Mark);
            published.Should().Contain(LayeredSentenceFragment);
            published.Should().Contain(LayeredSentenceFragment2);
        }
        finally
        {
            LeafFormatter.Suppressed = saved;
        }
    }

    [Fact]
    public void ShouldSuppress_byCliFlag_returnsTrue_andCallSiteSkipsPublishing()
    {
        // The actual "skip publishing" branch lives in Program.cs and calls ShouldSuppress;
        // we verify the helper returns true for the flag, which is what Program.cs reads.
        ServerInstructions.ShouldSuppress(noInstructionsFlag: true, envValue: null).Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppress_byEnvVar_returnsTrue()
    {
        ServerInstructions.ShouldSuppress(noInstructionsFlag: false, envValue: "1").Should().BeTrue();
        ServerInstructions.ShouldSuppress(noInstructionsFlag: false, envValue: "true").Should().BeTrue();
        ServerInstructions.ShouldSuppress(noInstructionsFlag: false, envValue: "TRUE").Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppress_default_returnsFalse()
    {
        ServerInstructions.ShouldSuppress(noInstructionsFlag: false, envValue: null).Should().BeFalse();
        ServerInstructions.ShouldSuppress(noInstructionsFlag: false, envValue: "").Should().BeFalse();
        ServerInstructions.ShouldSuppress(noInstructionsFlag: false, envValue: "0").Should().BeFalse();
    }
}
