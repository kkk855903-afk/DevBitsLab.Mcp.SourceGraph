using System;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// CLI coverage for the offline-by-default model download policy and its explicit opt-in.
/// </summary>
public sealed class CommandLineNoModelDownloadTests : IDisposable
{
    private readonly string? _previousNoDownloadEnv;
    private readonly string? _previousAllowDownloadEnv;

    public CommandLineNoModelDownloadTests()
    {
        _previousNoDownloadEnv = Environment.GetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD");
        _previousAllowDownloadEnv = Environment.GetEnvironmentVariable("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD");
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", null);
        Environment.SetEnvironmentVariable("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", _previousNoDownloadEnv);
        Environment.SetEnvironmentVariable("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD", _previousAllowDownloadEnv);
    }

    [Fact]
    public void Default_isOffline()
    {
        CommandLine.Parse(Array.Empty<string>()).NoModelDownload.Should().BeTrue();
        CommandLine.Parse(new[] { "serve" }).NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void Flag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-model-download" });
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void AllowFlag_explicitlyEnablesAutomaticDownload()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--enable-embeddings", "--allow-model-download" });
        cli.NoModelDownload.Should().BeFalse();
    }

    [Fact]
    public void AllowEnvVar_explicitlyEnablesAutomaticDownload()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD", "1");
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoModelDownload.Should().BeFalse();
    }

    [Fact]
    public void AllowEnvVar_appliesToImplicitServe()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD", "1");
        var cli = CommandLine.Parse(Array.Empty<string>());
        cli.NoModelDownload.Should().BeFalse();
    }

    [Fact]
    public void LegacyNoDownloadEnvVar_isHonoured()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", "1");
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void LegacyNoDownloadEnvVar_winsOverAllowEnvVar()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", "1");
        Environment.SetEnvironmentVariable("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD", "1");
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void LegacyNoDownloadEnvVar_winsOverAllowFlag()
    {
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD", "1");
        var cli = CommandLine.Parse(new[] { "serve", "--enable-embeddings", "--allow-model-download" });
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void Flag_doesNotConflictWithNoEmbeddings()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--no-embeddings", "--no-model-download" });
        cli.EmbeddingsEnabled.Should().BeFalse();
        cli.NoModelDownload.Should().BeTrue();
    }

    [Fact]
    public void ConflictingFlags_areRejected()
    {
        var act = () => CommandLine.Parse(
            new[] { "serve", "--allow-model-download", "--no-model-download" });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*cannot be used together*");
    }

    [Fact]
    public void HelpText_documentsOfflineDefaultAndOptIn()
    {
        CommandLine.HelpText.Should().Contain("--allow-model-download");
        CommandLine.HelpText.Should().Contain("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD");
        CommandLine.HelpText.Should().Contain("--no-model-download");
        CommandLine.HelpText.Should().Contain("SOURCEGRAPH_NO_MODEL_DOWNLOAD");
        CommandLine.HelpText.Should().Contain("disabled by default");
    }
}
