using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// CLI flag + env-var coverage for the <c>query_graph</c> tunables
/// (<c>--query-timeout-seconds</c>, <c>--query-row-limit</c>) and the corresponding
/// <see cref="GraphQueryOptions.Resolve"/> precedence.
/// </summary>
public sealed class CommandLineQueryFlagsTests
{
    [Fact]
    public void Defaults_areNullOnCommandLine()
    {
        var cli = CommandLine.Parse(new[] { "serve" });
        cli.QueryTimeoutSeconds.Should().BeNull();
        cli.QueryRowLimit.Should().BeNull();
    }

    [Fact]
    public void TimeoutFlag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--query-timeout-seconds", "12" });
        cli.QueryTimeoutSeconds.Should().Be(12);
    }

    [Fact]
    public void RowLimitFlag_isParsed()
    {
        var cli = CommandLine.Parse(new[] { "serve", "--query-row-limit", "250" });
        cli.QueryRowLimit.Should().Be(250);
    }

    [Fact]
    public void Flags_compose()
    {
        var cli = CommandLine.Parse(new[]
        {
            "serve", "--query-timeout-seconds", "8", "--query-row-limit", "500", "--no-leaf",
        });
        cli.QueryTimeoutSeconds.Should().Be(8);
        cli.QueryRowLimit.Should().Be(500);
        cli.NoLeaf.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("notanumber")]
    public void TimeoutFlag_rejectsBadValues(string raw)
    {
        var act = () => CommandLine.Parse(new[] { "serve", "--query-timeout-seconds", raw });
        act.Should().Throw<System.ArgumentException>().WithMessage("*positive integer*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-50")]
    [InlineData("xyz")]
    public void RowLimitFlag_rejectsBadValues(string raw)
    {
        var act = () => CommandLine.Parse(new[] { "serve", "--query-row-limit", raw });
        act.Should().Throw<System.ArgumentException>().WithMessage("*positive integer*");
    }

    [Fact]
    public void HelpText_documentsBothFlags()
    {
        CommandLine.HelpText.Should().Contain("--query-timeout-seconds");
        CommandLine.HelpText.Should().Contain("--query-row-limit");
        CommandLine.HelpText.Should().Contain("SOURCEGRAPH_QUERY_TIMEOUT_SECONDS");
        CommandLine.HelpText.Should().Contain("SOURCEGRAPH_QUERY_ROW_LIMIT");
    }

    [Fact]
    public void Resolve_flagBeatsEnvBeatsDefault()
    {
        // Saved/restored to keep the test from leaking env state into other tests.
        var savedTimeout = Environment.GetEnvironmentVariable(GraphQueryOptions.TimeoutEnvVarName);
        var savedRowLimit = Environment.GetEnvironmentVariable(GraphQueryOptions.RowLimitEnvVarName);
        try
        {
            // Default (nothing set anywhere)
            Environment.SetEnvironmentVariable(GraphQueryOptions.TimeoutEnvVarName, null);
            Environment.SetEnvironmentVariable(GraphQueryOptions.RowLimitEnvVarName, null);
            var defaults = GraphQueryOptions.Resolve(null, null);
            defaults.TimeoutSeconds.Should().Be(GraphQueryOptions.DefaultTimeoutSeconds);
            defaults.RowLimit.Should().Be(GraphQueryOptions.DefaultRowLimit);

            // Env wins over default
            Environment.SetEnvironmentVariable(GraphQueryOptions.TimeoutEnvVarName, "9");
            Environment.SetEnvironmentVariable(GraphQueryOptions.RowLimitEnvVarName, "250");
            var envOnly = GraphQueryOptions.Resolve(null, null);
            envOnly.TimeoutSeconds.Should().Be(9);
            envOnly.RowLimit.Should().Be(250);

            // Flag wins over env
            var flag = GraphQueryOptions.Resolve(timeoutFlag: 30, rowLimitFlag: 1234);
            flag.TimeoutSeconds.Should().Be(30);
            flag.RowLimit.Should().Be(1234);

            // Bad env values fall back to default (don't crash startup)
            Environment.SetEnvironmentVariable(GraphQueryOptions.TimeoutEnvVarName, "garbage");
            Environment.SetEnvironmentVariable(GraphQueryOptions.RowLimitEnvVarName, "0");
            var badEnv = GraphQueryOptions.Resolve(null, null);
            badEnv.TimeoutSeconds.Should().Be(GraphQueryOptions.DefaultTimeoutSeconds);
            badEnv.RowLimit.Should().Be(GraphQueryOptions.DefaultRowLimit);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GraphQueryOptions.TimeoutEnvVarName, savedTimeout);
            Environment.SetEnvironmentVariable(GraphQueryOptions.RowLimitEnvVarName, savedRowLimit);
        }
    }
}
