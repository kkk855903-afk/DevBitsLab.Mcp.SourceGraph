using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Parser-level coverage for the <c>embeddings</c> subcommand group: positional verb capture,
/// the new <c>--all</c> flag, and that <c>--model</c> still applies as a top-level flag.
/// </summary>
public sealed class EmbeddingsCliParserTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("pull")]
    [InlineData("remove")]
    [InlineData("verify")]
    public void EmbeddingsVerb_isCapturedAsPositional(string verb)
    {
        var cli = CommandLine.Parse(new[] { "embeddings", verb });
        cli.Subcommand.Should().Be("embeddings");
        cli.Positional.Should().HaveCount(1);
        cli.Positional[0].Should().Be(verb);
    }

    [Fact]
    public void All_flag_default_isFalse()
    {
        var cli = CommandLine.Parse(new[] { "embeddings", "remove" });
        cli.All.Should().BeFalse();
    }

    [Fact]
    public void All_flag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "embeddings", "remove", "--all" });
        cli.All.Should().BeTrue();
    }

    [Fact]
    public void Model_andAll_areBothParsed_butCombinationIsRejectedAtVerbLayer()
    {
        // Parser is permissive — it captures both. The conflict is rejected by EmbeddingsCli's
        // remove handler so the error message can be specific to the verb. See
        // EmbeddingsCli.RunRemoveAsync.
        var cli = CommandLine.Parse(new[] { "embeddings", "remove", "--model", "vendor/x", "--all" });
        cli.Model.Should().Be("vendor/x");
        cli.All.Should().BeTrue();
    }

    [Fact]
    public void HelpText_documentsAllFourEmbeddingsVerbs()
    {
        CommandLine.HelpText.Should().Contain("embeddings status");
        CommandLine.HelpText.Should().Contain("embeddings pull");
        CommandLine.HelpText.Should().Contain("embeddings remove");
        CommandLine.HelpText.Should().Contain("embeddings verify");
        CommandLine.HelpText.Should().Contain("--all");
    }
}
