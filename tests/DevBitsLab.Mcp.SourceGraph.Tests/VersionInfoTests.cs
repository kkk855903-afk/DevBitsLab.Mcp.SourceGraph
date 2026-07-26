using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class VersionInfoTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version_flags_select_read_only_version_output(string flag)
    {
        var commandLine = CommandLine.Parse([flag]);

        commandLine.ShowVersion.Should().BeTrue();
        commandLine.ShowHelp.Should().BeFalse();
    }

    [Fact]
    public void Version_output_reports_effective_build_runtime_and_os()
    {
        var output = VersionInfo.Render();

        output.Should().StartWith("sourcegraph-mcp ");
        output.Should().Contain("assembly: ");
        output.Should().Contain("runtime: .NET ");
        output.Should().Contain("os: ");
        output.Should().NotContain("unknown");
    }

    [Fact]
    public void Version_flag_rejects_ambiguous_extra_arguments()
    {
        var act = () => CommandLine.Parse(["--version", "serve"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*does not accept additional arguments*");
    }
}
