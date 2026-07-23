using System.IO;
using System.Linq;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the <c>scopes info &lt;name&gt;</c> CLI subcommand. Captures stdout via the
/// shared <see cref="InternalCli"/> helper (which handles the reflection dance to reach the
/// internal CLI types) and asserts both the markdown and <c>--json</c> shapes surface the
/// new <c>language</c> and <c>enrichment</c> fields.
///
/// <para>Lives in the <c>CliConsole</c> collection so xUnit serialises every Console.Out
/// redirection with the other CLI test classes. Without that, parallel test runs racing on
/// <see cref="Console.SetOut"/> can interleave or restore the wrong writer.</para>
/// </summary>
[Collection("CliConsole")]
public sealed class ScopesInfoCliTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "sg-scopes-info-tests-" + Guid.NewGuid().ToString("N"));

    public ScopesInfoCliTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteConfig(string json) => File.WriteAllText(Path.Join(_tempDir, ScopeConfigLoader.FileName), json);

    [Fact]
    public async Task Markdown_renders_unset_fields_when_language_and_enrichment_absent()
    {
        WriteConfig("""
            { "scopes": [ { "name": "backend", "solutions": ["backend.slnx"] } ] }
            """);

        var output = await CaptureStdoutAsync(new[] { "scopes", "info", "backend", "--root", _tempDir });

        output.Should().Contain("## Identity");
        output.Should().Contain("id:   backend");
        output.Should().Contain("## Language");
        output.Should().Contain("(unset)");
        output.Should().Contain("## Enrichment");
    }

    [Fact]
    public async Task Markdown_renders_language_and_inert_lsp_when_set()
    {
        WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/web/**/*.ts"],
                  "language": "typescript",
                  "enrichment": {
                    "lsp": { "command": "typescript-language-server", "args": ["--stdio"] }
                  }
                }
              ]
            }
            """);

        var output = await CaptureStdoutAsync(new[] { "scopes", "info", "frontend", "--root", _tempDir });

        output.Should().Contain("Language");
        output.Should().Contain("typescript");
        output.Should().Contain("lsp.command: typescript-language-server");
        output.Should().Contain("--stdio");
        output.Should().Contain("no consumer at this version");
    }

    [Fact]
    public async Task Json_emits_stable_shape_with_language_and_enrichment()
    {
        WriteConfig("""
            {
              "scopes": [
                {
                  "name": "frontend",
                  "paths": ["src/web/**/*.ts"],
                  "language": "typescript",
                  "enrichment": { "lsp": { "command": "tsserver" } }
                }
              ]
            }
            """);

        var output = await CaptureStdoutAsync(new[] { "scopes", "info", "frontend", "--root", _tempDir, "--json" });
        var json = JsonDocument.Parse(output).RootElement;

        json.GetProperty("id").GetString().Should().Be("frontend");
        json.GetProperty("language").GetString().Should().Be("typescript");
        var enrichment = json.GetProperty("enrichment");
        enrichment.GetProperty("lsp").GetProperty("command").GetString().Should().Be("tsserver");
        enrichment.GetProperty("consumed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Returns_nonzero_exit_for_unknown_scope_name()
    {
        WriteConfig("""
            { "scopes": [ { "name": "backend", "solutions": ["backend.slnx"] } ] }
            """);

        var (_, exitCode) = await CaptureStdoutWithExitAsync(new[] { "scopes", "info", "doesnotexist", "--root", _tempDir });

        exitCode.Should().NotBe(0);
    }

    private static async Task<string> CaptureStdoutAsync(string[] args)
    {
        var (output, _) = await InternalCli.RunSubcommandAsync("ScopesCli", args);
        return output;
    }

    private static Task<(string Output, int ExitCode)> CaptureStdoutWithExitAsync(string[] args) =>
        InternalCli.RunSubcommandAsync("ScopesCli", args);
}
