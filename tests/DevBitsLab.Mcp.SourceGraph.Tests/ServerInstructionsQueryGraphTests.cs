using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Resources;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Verifies the layered-tooling sentence (describe_schema → query_graph) lives in the on-demand
/// <c>graph://help</c> body (<see cref="ServerHelp.Template"/>) and that the trimmed
/// <see cref="ServerInstructions.Template"/> still points agents at the help resource.
/// Suppression of the published preamble by <c>--no-instructions</c> /
/// <c>SOURCEGRAPH_NO_INSTRUCTIONS</c> remains unchanged.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class ServerInstructionsQueryGraphTests
{
    private const string LayeredSentenceFragment = "describe_schema";
    private const string LayeredSentenceFragment2 = "query_graph";

    [Fact]
    public void HelpResource_includesTheLayeredQueryGraphSentence()
    {
        ServerHelp.Template.Should().Contain(LayeredSentenceFragment);
        ServerHelp.Template.Should().Contain(LayeredSentenceFragment2);
        ServerHelp.Template.Should().Contain("stable contract");
        ServerHelp.Template.Should().Contain("v_symbols");
    }

    [Fact]
    public void Template_pointsAtHelpResource()
    {
        // The verbose body moved to graph://help to cut upfront tokens; the preamble must still
        // tell agents where to fetch it, otherwise they'll never discover the SQL escape hatch.
        ServerInstructions.Template.Should().Contain(GraphResourceUris.Help);
    }

    [Fact]
    public void ResolvePublished_withoutLeafSuppression_keepsLeafAndHelpPointer()
    {
        var saved = LeafFormatter.Suppressed;
        try
        {
            LeafFormatter.Suppressed = false;
            var published = ServerInstructions.ResolvePublished();
            published.Should().StartWith(LeafFormatter.Mark);
            published.Should().Contain(GraphResourceUris.Help);
        }
        finally
        {
            LeafFormatter.Suppressed = saved;
        }
    }

    [Fact]
    public void ResolvePublished_withLeafSuppression_stripsLeafButKeepsHelpPointer()
    {
        var saved = LeafFormatter.Suppressed;
        try
        {
            LeafFormatter.Suppressed = true;
            var published = ServerInstructions.ResolvePublished();
            published.Should().NotStartWith(LeafFormatter.Mark);
            published.Should().Contain(GraphResourceUris.Help);
        }
        finally
        {
            LeafFormatter.Suppressed = saved;
        }
    }

    [Fact]
    public void GraphResources_GetHelp_returnsServerHelpTemplate()
    {
        // Wiring smoke-test: the [McpServerResource] handler must return the same body the help
        // resource is documented to serve. If someone refactors GraphResources.GetHelp, this
        // catches accidental drift between the Template constant and the served payload.
        DevBitsLab.Mcp.SourceGraph.Server.Resources.GraphResources.GetHelp()
            .Should().Be(ServerHelp.Template);
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
