using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class CommandLineEmbeddingDefaultsTests
{
    [Fact]
    public void Embeddings_areDisabledByDefault()
    {
        CommandLine.Parse(Array.Empty<string>()).EmbeddingsEnabled.Should().BeFalse();
        CommandLine.Parse(["serve"]).EmbeddingsEnabled.Should().BeFalse();
        CommandLine.Parse(["index", "sample.sln"]).EmbeddingsEnabled.Should().BeFalse();
        CommandLine.Parse(["benchmark"]).EmbeddingsEnabled.Should().BeFalse();
        CommandLine.Parse(["init", "--yes"]).EmbeddingsEnabled.Should().BeFalse();
    }

    [Fact]
    public void EnableFlag_isExplicitOptIn()
    {
        CommandLine.Parse(["serve", "--enable-embeddings"])
            .EmbeddingsEnabled.Should().BeTrue();
    }

    [Fact]
    public void LegacyDisableFlag_remainsAccepted()
    {
        CommandLine.Parse(["serve", "--no-embeddings"])
            .EmbeddingsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ConflictingEmbeddingFlags_areRejected()
    {
        var act = () => CommandLine.Parse(
            ["serve", "--enable-embeddings", "--no-embeddings"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*cannot be used together*");
    }

    [Fact]
    public void DownloadOptIn_requiresEmbeddingOptIn()
    {
        var act = () => CommandLine.Parse(["serve", "--allow-model-download"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*requires --enable-embeddings*");
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("12", 12)]
    public void IdleTimeout_acceptsNonNegativeMinutes(string value, int expected)
    {
        CommandLine.Parse(["serve", "--idle-timeout-minutes", value])
            .IdleTimeoutMinutes.Should().Be(expected);
    }

    [Fact]
    public void IdleTimeout_defaultsToThirtyMinutes() =>
        CommandLine.Parse(["serve"]).IdleTimeoutMinutes.Should().Be(30);

    [Theory]
    [InlineData("-1")]
    [InlineData("later")]
    public void IdleTimeout_rejectsInvalidValues(string value)
    {
        var act = () => CommandLine.Parse(["serve", "--idle-timeout-minutes", value]);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*non-negative integer*");
    }

    [Fact]
    public void Help_documentsEmbeddingOptInAndIdleExit()
    {
        CommandLine.HelpText.Should().Contain("--enable-embeddings");
        CommandLine.HelpText.Should().Contain("--idle-timeout-minutes");
        CommandLine.ChineseHelpText.Should().Contain("--enable-embeddings");
        CommandLine.ChineseHelpText.Should().Contain("--idle-timeout-minutes");
    }
}
