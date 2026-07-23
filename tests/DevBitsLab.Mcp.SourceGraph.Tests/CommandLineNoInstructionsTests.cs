using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// CLI flag coverage for <c>--no-instructions</c>.
/// </summary>
public sealed class CommandLineNoInstructionsTests
{
    [Fact]
    public void Default_isFalse()
    {
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoInstructions.Should().BeFalse();
    }

    [Fact]
    public void Flag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-instructions" });
        cli.NoInstructions.Should().BeTrue();
    }

    [Fact]
    public void Flag_composesWithOtherFlags()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-history", "--no-instructions", "--no-embeddings" });
        cli.NoInstructions.Should().BeTrue();
        cli.NoHistory.Should().BeTrue();
        cli.NoEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void HelpText_documentsTheFlag()
    {
        CommandLine.HelpText.Should().Contain("--no-instructions");
    }
}
