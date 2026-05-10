using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// CLI flag coverage for <c>--no-tool-triggers</c>. Mirrors <see cref="CommandLineNoLeafTests"/>.
/// </summary>
public sealed class CommandLineNoToolTriggersTests
{
    [Fact]
    public void Default_isFalse()
    {
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoToolTriggers.Should().BeFalse();
    }

    [Fact]
    public void Flag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-tool-triggers" });
        cli.NoToolTriggers.Should().BeTrue();
    }

    [Fact]
    public void Flag_composesWithOtherFlags()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-instructions", "--no-leaf", "--no-tool-triggers" });
        cli.NoToolTriggers.Should().BeTrue();
        cli.NoInstructions.Should().BeTrue();
        cli.NoLeaf.Should().BeTrue();
    }

    [Fact]
    public void HelpText_documentsTheFlag()
    {
        CommandLine.HelpText.Should().Contain("--no-tool-triggers");
        CommandLine.HelpText.Should().Contain("SOURCEGRAPH_NO_TOOL_TRIGGERS");
    }
}
