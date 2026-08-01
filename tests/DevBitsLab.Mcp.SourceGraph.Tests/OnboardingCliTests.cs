using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the three new CLI subcommands: <c>init</c>, <c>doctor</c>, <c>demo</c>.
/// Tests run serially in the <c>CliConsole</c> collection because each captures Console.Out;
/// xUnit's default parallelism would race two redirections against each other.
/// </summary>
[Collection("CliConsole")]
public sealed class OnboardingCliTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TextWriter _originalStdout;
    private readonly TextWriter _originalStderr;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public OnboardingCliTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-init-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _originalStdout = Console.Out;
        _originalStderr = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalStdout);
        Console.SetError(_originalStderr);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private CommandLine ParseInit(params string[] extra)
    {
        var args = new List<string> { "init", "--yes", "--root", _tempRoot };
        args.AddRange(extra);
        return CommandLine.Parse(args.ToArray());
    }

    [Fact]
    public async Task Init_yes_printOnly_emitsClaudeCodeSnippet()
    {
        var cli = ParseInit("--print-only", "--client", "claude-code");
        await InitCli.RunAsync(cli);
        var output = _stdout.ToString();
        output.Should().Contain("# would write to:");
        output.Should().Contain(".mcp.json");
        output.Should().Contain("\"mcpServers\"");
        output.Should().Contain("\"sourcegraph\"");
    }

    [Fact]
    public async Task Init_yes_printOnly_copilotEmitsDistinctSchema()
    {
        var cli = ParseInit("--print-only", "--client", "copilot");
        await InitCli.RunAsync(cli);
        var output = _stdout.ToString();
        output.Should().Contain("\"servers\"");
        output.Should().Contain("\"type\": \"stdio\"");
        output.Should().NotContain("\"mcpServers\"");
    }

    [Fact]
    public async Task Init_yes_printOnly_codexEmitsAbsoluteCompatibleToml()
    {
        var cli = ParseInit("--print-only", "--client", "codex");
        var rc = await InitCli.RunAsync(cli);
        var output = _stdout.ToString();

        rc.Should().Be(0);
        output.Should().Contain(Path.Join(".codex", "config.toml"));
        output.Should().Contain("[mcp_servers.sourcegraph]");
        output.Should().Contain($"cwd = \"{_tempRoot.Replace("\\", "\\\\", StringComparison.Ordinal)}\"");
        output.Should().Contain(
            Path.Join(_tempRoot, "MySolution.slnx").Replace("\\", "\\\\", StringComparison.Ordinal));
        output.Should().Contain("\"--root\"");
        output.Should().Contain("\"--codex-compat\"");
        output.Should().NotContain("${workspaceFolder}");
        _stderr.ToString().Should().NotContain("unknown --client");
    }

    [Fact]
    public async Task Init_defaultSelection_includesCodexProjectConfig()
    {
        var cli = ParseInit("--print-only");
        await InitCli.RunAsync(cli);

        _stdout.ToString().Should().Contain(Path.Join(".codex", "config.toml"));
    }

    [Fact]
    public async Task Init_noCodex_skipsCodex()
    {
        var cli = ParseInit("--print-only", "--no-codex");
        await InitCli.RunAsync(cli);

        _stdout.ToString().Should().NotContain(Path.Join(".codex", "config.toml"));
    }

    [Fact]
    public void Init_userCodex_isRejectedBecauseCodexOnboardingIsProjectScoped()
    {
        var act = () => ParseInit("--client", "codex", "--user-codex");

        act.Should().Throw<ArgumentException>()
            .WithMessage("Unrecognised argument: --user-codex");
    }

    [Fact]
    public async Task Init_codex_writesAndThenReportsNoChange()
    {
        var cli = ParseInit("--client", "codex");
        var firstRc = await InitCli.RunAsync(cli);
        var path = Path.Join(_tempRoot, ".codex", "config.toml");

        firstRc.Should().Be(0);
        File.ReadAllText(path).Should().Contain("[mcp_servers.sourcegraph]");

        using var freshOut = new StringWriter();
        Console.SetOut(freshOut);
        var secondRc = await InitCli.RunAsync(ParseInit("--client", "codex"));
        secondRc.Should().Be(0);
        freshOut.ToString().Should().Contain("no change");
    }

    [Fact]
    public async Task Init_yes_printOnly_multipleClients_emitsAll()
    {
        var cli = ParseInit("--print-only", "--client", "claude-code,copilot,cursor");
        await InitCli.RunAsync(cli);
        var output = _stdout.ToString();
        // Paths render with the platform's native separator — `.vscode/mcp.json` on Linux/macOS,
        // `.vscode\mcp.json` on Windows. Use `Path.Join` so the assertion matches both. (The
        // previous hardcoded `/` form failed the Windows CI run.)
        output.Should().Contain(".mcp.json");
        output.Should().Contain(Path.Join(".vscode", "mcp.json"));
        output.Should().Contain(Path.Join(".cursor", "mcp.json"));
    }

    [Fact]
    public async Task Init_yes_writesFile_whenNotPrintOnly()
    {
        var cli = ParseInit("--client", "claude-code");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(0);
        var path = Path.Join(_tempRoot, ".mcp.json");
        File.Exists(path).Should().BeTrue();
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
    }

    [Fact]
    public async Task Init_yes_mergesIntoExistingConfig()
    {
        var path = Path.Join(_tempRoot, ".mcp.json");
        File.WriteAllText(path,
            "{ \"mcpServers\": { \"otherServer\": { \"command\": \"x\", \"args\": [\"y\"] } } }");

        var cli = ParseInit("--client", "claude-code");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(0);
        var merged = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        merged["mcpServers"]!["otherServer"]!["command"]!.GetValue<string>().Should().Be("x");
        merged["mcpServers"]!["sourcegraph"]!.Should().NotBeNull();
    }

    [Fact]
    public async Task Init_skipsExistingDifferingEntry_withoutForce()
    {
        var path = Path.Join(_tempRoot, ".mcp.json");
        File.WriteAllText(path,
            "{ \"mcpServers\": { \"sourcegraph\": { \"command\": \"different\", \"args\": [] } } }");

        var cli = ParseInit("--client", "claude-code");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(2);
        var unchanged = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        unchanged["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("different");
    }

    [Fact]
    public async Task Init_force_overwritesExistingDifferingEntry()
    {
        var path = Path.Join(_tempRoot, ".mcp.json");
        File.WriteAllText(path,
            "{ \"mcpServers\": { \"sourcegraph\": { \"command\": \"different\", \"args\": [] } } }");

        var cli = ParseInit("--client", "claude-code", "--force");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(0);
        var updated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        updated["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
    }

    [Fact]
    public async Task Init_noCursor_skipsCursor()
    {
        var cli = ParseInit("--print-only", "--no-cursor");
        await InitCli.RunAsync(cli);
        var output = _stdout.ToString();
        output.Should().NotContain(".cursor/mcp.json");
    }

    [Fact]
    public async Task Init_claudeDesktopOptIn_isRequired()
    {
        var cli = ParseInit("--print-only");
        await InitCli.RunAsync(cli);
        var output = _stdout.ToString();
        // Claude Desktop's user-scope path is platform specific but always contains "Claude".
        // Without --claude-desktop, it should be absent.
        output.Should().NotContain("claude_desktop_config.json");
    }

    [Fact]
    public async Task Init_existingMatching_reportsNoChange()
    {
        // Write the file once, then run init again — second run should be no-op.
        var firstCli = ParseInit("--client", "claude-code");
        await InitCli.RunAsync(firstCli);

        // Reset stdout for the second run.
        var marker = _stdout.ToString();
        marker.Should().Contain("wrote");

        using var freshOut = new StringWriter();
        Console.SetOut(freshOut);
        var secondCli = ParseInit("--client", "claude-code");
        await InitCli.RunAsync(secondCli);
        var secondOutput = freshOut.ToString();
        secondOutput.Should().Contain("no change");
    }

    [Fact]
    public async Task Init_userCopilot_exitsZero_withInformationalSkip()
    {
        // Copilot has no user-scope writer (settings.json edits aren't supported in v1).
        // The combo should be a documented skip, NOT a CI-failure exit-2.
        var cli = ParseInit("--client", "copilot", "--user-copilot");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(0);
        _stderr.ToString().Should().Contain("user-scope Copilot");
        _stdout.ToString().Should().Contain("skipped (unsupported)");
    }

    [Fact]
    public async Task Init_multiScope_emitsRootMode_notSolutionPlaceholder()
    {
        // Multi-solution detected (no .sourcegraph.json) → init should scaffold scope config and
        // the emitted server args should use `--root ${workspaceFolder}`, not the
        // `--solution ${workspaceFolder}/MySolution.slnx` placeholder. Otherwise the resulting
        // config silently overrides the user's multi-scope intent.
        File.WriteAllText(Path.Join(_tempRoot, "frontend.slnx"), "<Solution/>");
        File.WriteAllText(Path.Join(_tempRoot, "backend.slnx"), "<Solution/>");

        var cli = ParseInit("--print-only", "--client", "claude-code");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(0);

        var output = _stdout.ToString();
        output.Should().Contain("\"--root\"");
        output.Should().Contain("\"${workspaceFolder}\"");
        output.Should().NotContain("MySolution.slnx");
    }

    [Fact]
    public async Task Init_existingSourceGraphConfig_emitsRootMode_andPreservesScopeMetadata()
    {
        // A configured scope may carry interop/enrichment metadata that the implicit
        // --solution scope cannot represent. Init must keep the server in root mode so the
        // runtime loads that metadata from .sourcegraph.json.
        File.WriteAllText(Path.Join(_tempRoot, "MySolution.slnx"), "<Solution/>");
        File.WriteAllText(
            Path.Join(_tempRoot, ".sourcegraph.json"),
            "{\"scopes\":[{\"name\":\"default\",\"solutions\":[\"MySolution.slnx\"]}]}");

        var cli = ParseInit("--print-only", "--client", "codex");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(0);

        var output = _stdout.ToString();
        output.Should().Contain("\"--root\"");
        output.Should().NotContain("\"--solution\"");
        output.Should().NotContain("MySolution.slnx");
    }

    [Fact]
    public async Task Init_malformedSourcegraphJson_failsFast()
    {
        File.WriteAllText(Path.Join(_tempRoot, ".sourcegraph.json"), "{ broken");
        var cli = ParseInit("--client", "claude-code");
        var rc = await InitCli.RunAsync(cli);
        rc.Should().Be(1);
        _stderr.ToString().Should().Contain("malformed");
    }
}

/// <summary>
/// Coverage for <c>sourcegraph-mcp doctor</c>. Same console-redirection harness as
/// <see cref="OnboardingCliTests"/>.
/// </summary>
[Collection("CliConsole")]
public sealed class DoctorCliTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TextWriter _originalStdout;
    private readonly TextWriter _originalStderr;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public DoctorCliTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-doctor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _originalStdout = Console.Out;
        _originalStderr = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalStdout);
        Console.SetError(_originalStderr);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Doctor_emptyRepo_returnsAtLeastWarn()
    {
        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot });
        var rc = await DoctorCli.RunAsync(cli);
        rc.Should().BeOneOf(0, 2);
        _stdout.ToString().Should().Contain("SourceGraph doctor");
        _stdout.ToString().Should().Contain("dotnet-sdk");
        _stdout.ToString().Should().Contain("solutions");
    }

    [Fact]
    public async Task Doctor_repoWithSolution_passesSolutionsCheck()
    {
        File.WriteAllText(Path.Join(_tempRoot, "Test.slnx"), "<Solution/>");
        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot });
        await DoctorCli.RunAsync(cli);
        var output = _stdout.ToString();
        output.Should().Contain("[OK]").And.Contain("solutions");
        output.Should().Contain("Test.slnx");
    }

    [Fact]
    public async Task Doctor_malformedSourcegraphJson_failsCheck()
    {
        File.WriteAllText(Path.Join(_tempRoot, ".sourcegraph.json"), "broken{json");
        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot });
        var rc = await DoctorCli.RunAsync(cli);
        rc.Should().Be(1);
        _stdout.ToString().Should().Contain("[FAIL]");
        _stdout.ToString().Should().Contain("malformed");
    }

    [Fact]
    public async Task Doctor_jsonMode_emitsStructuredDocument()
    {
        File.WriteAllText(Path.Join(_tempRoot, "Test.slnx"), "<Solution/>");
        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot, "--json" });
        await DoctorCli.RunAsync(cli);

        var json = _stdout.ToString();
        json.Should().StartWith("{");
        var doc = JsonNode.Parse(json)!.AsObject();
        doc["checks"].Should().NotBeNull();
        doc["exit_code"].Should().NotBeNull();
        var checks = doc["checks"]!.AsArray();
        checks.Should().NotBeEmpty();
        var first = checks[0]!.AsObject();
        first["name"].Should().NotBeNull();
        first["status"].Should().NotBeNull();
        first["message"].Should().NotBeNull();
    }

    [Fact]
    public async Task Doctor_reportsCodexConfigPresence()
    {
        var codexDir = Path.Join(_tempRoot, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Join(codexDir, "config.toml"),
            "[mcp_servers.sourcegraph]\ncommand = \"sourcegraph-mcp\"\n");

        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot });
        await DoctorCli.RunAsync(cli);

        _stdout.ToString().Should().Contain("client-codex");
        _stdout.ToString().Should().Contain("codex config wired");
    }

    [Fact]
    public async Task Doctor_warnsWhenCodexConfigHasNoSourcegraphEntry()
    {
        var codexDir = Path.Join(_tempRoot, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Join(codexDir, "config.toml"), "model = \"gpt-test\"\n");

        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot });
        await DoctorCli.RunAsync(cli);

        _stdout.ToString().Should().Contain("client-codex");
        _stdout.ToString().Should().Contain("has no sourcegraph entry");
    }
}

/// <summary>
/// Coverage for <c>sourcegraph-mcp demo</c>. Builds a minimal scope DB programmatically so the
/// tests don't depend on having a real solution indexed.
/// </summary>
[Collection("CliConsole")]
public sealed class DemoCliTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TextWriter _originalStdout;
    private readonly TextWriter _originalStderr;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public DemoCliTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-demo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _originalStdout = Console.Out;
        _originalStderr = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalStdout);
        Console.SetError(_originalStderr);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Demo_noScopeDb_returnsTwo_withHint()
    {
        var cli = CommandLine.Parse(new[] { "demo", "--root", _tempRoot });
        var rc = await DemoCli.RunAsync(cli);
        rc.Should().Be(2);
        _stderr.ToString().Should().Contain("no graph database");
        _stderr.ToString().Should().Contain("sourcegraph-mcp");
    }

    [Fact]
    public async Task Demo_emptyGraph_returnsTwo_afterPrintingPingAndStats()
    {
        // Stand up an empty (schema-only) DB at the expected path.
        var dbPath = ScopeLayout.ScopeDbPath(_tempRoot, "default");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.EnsureSchemaAsync();
        }

        var cli = CommandLine.Parse(new[] { "demo", "--root", _tempRoot });
        var rc = await DemoCli.RunAsync(cli);
        rc.Should().Be(2);
        var output = _stdout.ToString();
        output.Should().Contain("ping");
        output.Should().Contain("graph_stats");
        output.Should().Contain("symbols=0");
        _stderr.ToString().Should().Contain("0 symbols indexed");
    }

    [Fact]
    public async Task Demo_populatedGraph_emitsFourLeafSections()
    {
        // Build a tiny graph with one file + one symbol so the four canned operations succeed.
        var dbPath = ScopeLayout.ScopeDbPath(_tempRoot, "default");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.EnsureSchemaAsync();
            var fileId = await store.UpsertFileAsync(
                Path.Join(_tempRoot, "Calculator.cs"),
                contentSha256: new byte[32],
                indexedAt: DateTimeOffset.UtcNow);
            await store.UpsertSymbolAsync(
                canonicalKey: "csharp:T:Sample.Calculator",
                new Symbol(
                    Id: 0, Name: "Calculator", Fqn: "Sample.Calculator", Kind: "class",
                    FileId: fileId, StartLine: 1, StartCol: 1, EndLine: 1, EndCol: 1,
                    Signature: "public class Calculator", ContainerId: null));
        }

        var cli = CommandLine.Parse(new[] { "demo", "--root", _tempRoot });
        var rc = await DemoCli.RunAsync(cli);
        rc.Should().Be(0);
        var output = _stdout.ToString();
        output.Should().Contain("ping");
        output.Should().Contain("graph_stats");
        output.Should().Contain("search_symbols");
        output.Should().Contain("find_definition");
        output.Should().Contain("Sample.Calculator");
        output.Should().Contain("🌿");
    }

    [Fact]
    public async Task Demo_noColor_suppressesLeaf()
    {
        var dbPath = ScopeLayout.ScopeDbPath(_tempRoot, "default");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.EnsureSchemaAsync();
            var fileId = await store.UpsertFileAsync(
                Path.Join(_tempRoot, "x.cs"),
                contentSha256: new byte[32],
                indexedAt: DateTimeOffset.UtcNow);
            await store.UpsertSymbolAsync(
                canonicalKey: "csharp:T:X",
                new Symbol(
                    Id: 0, Name: "X", Fqn: "X", Kind: "class",
                    FileId: fileId, StartLine: 1, StartCol: 1, EndLine: 1, EndCol: 1,
                    Signature: null, ContainerId: null));
        }

        // Pass --no-color through the positional list. It's not a known CommandLine flag so
        // it lands in Positional via the existing fall-through. DemoCli reads it from there.
        // Build args with --no-color appended after the positional slot.
        var args = new[] { "demo", "--root", _tempRoot };
        var cli = CommandLine.Parse(args);

        // The `--no-color` flag is not parsed by CommandLine (it's not a registered flag and
        // would throw). Instead we set NO_COLOR in the env to take the same path.
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
        try
        {
            await DemoCli.RunAsync(cli);
            // With NO_COLOR set, the leaf glyph is NOT prefixed onto the lines.
            var output = _stdout.ToString();
            // The bordered section header still uses the ── glyphs but the per-line leaf is gone.
            output.Should().Contain("ping");
            output.Should().NotContain("🌿 pong");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
        }
    }
}
