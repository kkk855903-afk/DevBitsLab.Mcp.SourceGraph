using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class CodexCompatibilityTests
{
    [Fact]
    public void CommandLine_acceptsCodexCompatibilityFlag()
    {
        var commandLine = CommandLine.Parse(["serve", "--codex-compat"]);

        commandLine.CodexCompat.Should().BeTrue();
    }

    [Fact]
    public void NormalizeToolResult_keepsOnePlainTextBlock_andPreservesErrorState()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = "visible result",
                    Annotations = new Annotations { Audience = [Role.User] },
                },
                new ResourceLinkBlock { Uri = "file:///repo/Foo.cs", Name = "Foo.cs" },
                new TextContentBlock
                {
                    Text = "scope=default",
                    Annotations = new Annotations { Audience = [Role.Assistant] },
                },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new { count = 1 }),
            IsError = true,
        };

        var normalized = CodexCompatibility.NormalizeToolResult(result);

        normalized.Should().BeSameAs(result);
        normalized.IsError.Should().BeTrue();
        normalized.StructuredContent.Should().BeNull();
        normalized.Meta.Should().BeNull();
        normalized.Content.Should().ContainSingle();
        var text = normalized.Content![0].Should().BeOfType<TextContentBlock>().Subject;
        text.Text.Should().Be("visible result");
        text.Annotations.Should().BeNull();
        text.Meta.Should().BeNull();
    }

    [Fact]
    public async Task RunPrewarmAttempts_continuesAfterStartedProcessReturnsNonZero()
    {
        var attempts = new[]
        {
            new PrewarmAttempt("first", ["index", "repo.slnx"]),
            new PrewarmAttempt("second", ["index", "repo.slnx"]),
        };
        var invoked = new List<string>();

        var outcome = await PrewarmLauncher.RunAttemptsAsync(
            attempts,
            attempt =>
            {
                invoked.Add(attempt.FileName);
                return Task.FromResult<int?>(attempt.FileName == "first" ? 1 : 0);
            });

        outcome.Should().Be(0);
        invoked.Should().Equal("first", "second");
    }

    [Fact]
    public void BuildPrewarmAttempts_whenHostedByDotnet_reinvokesServerDllFirst()
    {
        var dotnetHost = Path.Join(
            Path.GetTempPath(),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var serverAssembly = Path.Join(Path.GetTempPath(), "sourcegraph-mcp.dll");
        var root = Path.Join(Path.GetTempPath(), "repo");
        var solution = Path.Join(root, "src", "App.slnx");
        var database = Path.Join(root, ".sourcegraph", "scopes", "default.db");
        var attempts = PrewarmLauncher.BuildAttempts(
            solution,
            root,
            database,
            processPath: dotnetHost,
            serverAssemblyPath: serverAssembly);

        attempts[0].FileName.Should().Be(dotnetHost);
        attempts[0].Arguments.Should().Equal(
            serverAssembly,
            "index",
            solution,
            "--root",
            root,
            "--db",
            database);
        attempts.Should().Contain(attempt =>
            attempt.FileName == "sourcegraph-mcp"
            && attempt.Arguments.SequenceEqual(new[]
            {
                "index",
                solution,
                "--root",
                root,
                "--db",
                database,
            }));
    }
}
