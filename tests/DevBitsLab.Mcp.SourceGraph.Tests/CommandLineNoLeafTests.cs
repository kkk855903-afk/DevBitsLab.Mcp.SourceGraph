using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// CLI flag coverage for <c>--no-leaf</c>. Mirrors <see cref="CommandLineNoInstructionsTests"/>.
/// </summary>
public sealed class CommandLineNoLeafTests
{
    [Fact]
    public void Default_isFalse()
    {
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoLeaf.Should().BeFalse();
    }

    [Fact]
    public void Flag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-leaf" });
        cli.NoLeaf.Should().BeTrue();
    }

    [Fact]
    public void Flag_composesWithOtherFlags()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-instructions", "--no-leaf", "--no-history" });
        cli.NoLeaf.Should().BeTrue();
        cli.NoInstructions.Should().BeTrue();
        cli.NoHistory.Should().BeTrue();
    }

    [Fact]
    public void HelpText_documentsTheFlag()
    {
        CommandLine.HelpText.Should().Contain("--no-leaf");
        CommandLine.HelpText.Should().Contain("SOURCEGRAPH_NO_LEAF");
    }
}
