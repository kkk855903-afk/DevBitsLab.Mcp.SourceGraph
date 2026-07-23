using System;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// CLI flag coverage for <c>--no-model-download</c> and its <c>SOURCEGRAPH_NO_MODEL_DOWNLOAD</c>
/// env-var equivalent.
/// </summary>
public sealed class CommandLineNoModelDownloadTests : IDisposable
{
    private readonly string? _previousEnv;

    public CommandLineNoModelDownloadTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD");
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", _previousEnv);
    }

    [Fact]
    public void Default_isFalse()
    {
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoModelDownload.Should().BeFalse();
    }

    [Fact]
    public void Flag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-model-download" });
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void EnvVar_isHonoured_whenFlagAbsent()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", "1");
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void EnvVar_isComposable_withFlag()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", "1");
        var cli = CommandLine.Parse(new[] { "serve", "--no-model-download" });
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void Flag_doesNotConflictWithNoEmbeddings()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-embeddings", "--no-model-download" });
        cli.NoEmbeddings.Should().BeTrue();
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void HelpText_documentsTheFlag()
    {
        CommandLine.HelpText.Should().Contain("--no-model-download");
        CommandLine.HelpText.Should().Contain("SOURCEGRAPH_NO_MODEL_DOWNLOAD");
    }
}
