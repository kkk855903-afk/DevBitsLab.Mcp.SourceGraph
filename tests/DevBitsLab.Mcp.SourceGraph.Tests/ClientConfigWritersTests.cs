using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end coverage for each first-class writer: schema-correctness, merge-into-existing,
/// per-platform paths. Uses temp dirs and the writer contract from
/// <see cref="WriterContractTests"/>.
/// </summary>
public sealed class ClientConfigWritersTests : IDisposable
{
    private readonly string _tempRoot;

    public ClientConfigWritersTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-writers-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private WriterContext MakeContext(IClientConfigWriter writer, byte[]? existingContent, bool force = false, bool userScope = false)
    {
        var path = userScope
            ? writer.DefaultUserPath() ?? throw new InvalidOperationException("no user path")
            : writer.DefaultProjectPath(_tempRoot) ?? throw new InvalidOperationException("no project path");
        // Re-root user paths under temp so the test doesn't write to the real home dir.
        if (userScope) path = Path.Join(_tempRoot, "home", Path.GetFileName(path));
        return new WriterContext(
            Root: _tempRoot,
            TargetPath: path,
            UseUserScope: userScope,
            InstallMode: InstallMode.Global,
            SolutionPath: "${workspaceFolder}/MyApp.slnx",
            ServerProjectPath: null,
            EmbeddingsEnabled: false,
            AllowModelDownload: false,
            NoHistory: false,
            Force: force,
            ExistingContent: existingContent);
    }

    // ────────────────────────── ClaudeCodeWriter ──────────────────────────

    [Fact]
    public void ClaudeCode_cleanWrite_emitsCanonicalShape()
    {
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        plan.Action.Should().Be(WriterAction.Insert);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
        json["mcpServers"]!["sourcegraph"]!["args"]!.AsArray()[0]!.GetValue<string>().Should().Be("serve");
    }

    [Fact]
    public void ClaudeCode_mergeIntoExisting_preservesOtherServers()
    {
        var existing = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"otherServer\": { \"command\": \"x\", \"args\": [\"y\"] } } }");
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing));
        plan.Action.Should().Be(WriterAction.Insert);
        var merged = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        merged["mcpServers"]!["otherServer"]!["command"]!.GetValue<string>().Should().Be("x");
        merged["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
    }

    [Fact]
    public void ClaudeCode_existingMatchesOurs_isNoOp()
    {
        var w = new ClaudeCodeWriter();
        var firstPlan = w.Plan(MakeContext(w, existingContent: null));
        var secondPlan = w.Plan(MakeContext(w, existingContent: firstPlan.ContentBytes));
        secondPlan.Action.Should().Be(WriterAction.NoOpAlreadyMatches);
    }

    [Fact]
    public void ClaudeCode_existingDiffersWithoutForce_isSkip()
    {
        var existing = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"sourcegraph\": { \"command\": \"different\", \"args\": [] } } }");
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing, force: false));
        plan.Action.Should().Be(WriterAction.SkipExistingDiffers);
    }

    [Fact]
    public void ClaudeCode_existingDiffersWithForce_isReplace()
    {
        var existing = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"sourcegraph\": { \"command\": \"different\", \"args\": [] } } }");
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing, force: true));
        plan.Action.Should().Be(WriterAction.ReplaceOurs);
        var merged = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        merged["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
    }

    [Fact]
    public void ClaudeCode_apply_writesFile()
    {
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        w.Apply(plan);
        File.Exists(plan.TargetPath).Should().BeTrue();
    }

    // ────────────────────────── CopilotWriter ──────────────────────────

    [Fact]
    public void Copilot_cleanWrite_usesServersKey_notMcpServers()
    {
        var w = new CopilotWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["servers"].Should().NotBeNull();
        json["mcpServers"].Should().BeNull();
    }

    [Fact]
    public void Copilot_emitsTypeStdio_field()
    {
        var w = new CopilotWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["servers"]!["sourcegraph"]!["type"]!.GetValue<string>().Should().Be("stdio");
    }

    [Fact]
    public void Copilot_targetPath_isVscodeSubdir()
    {
        var w = new CopilotWriter();
        w.DefaultProjectPath(_tempRoot).Should().Be(Path.Join(_tempRoot, ".vscode", "mcp.json"));
    }

    [Fact]
    public void Copilot_mergeIntoExisting_preservesOtherServers()
    {
        var existing = Encoding.UTF8.GetBytes(
            "{ \"servers\": { \"otherServer\": { \"type\": \"stdio\", \"command\": \"x\" } } }");
        var w = new CopilotWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing));
        plan.Action.Should().Be(WriterAction.Insert);
        var merged = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        merged["servers"]!["otherServer"]!["command"]!.GetValue<string>().Should().Be("x");
        merged["servers"]!["sourcegraph"]!["type"]!.GetValue<string>().Should().Be("stdio");
    }

    // ────────────────────────── CursorWriter ──────────────────────────

    [Fact]
    public void Cursor_targetPath_isCursorSubdir()
    {
        var w = new CursorWriter();
        w.DefaultProjectPath(_tempRoot).Should().Be(Path.Join(_tempRoot, ".cursor", "mcp.json"));
    }

    [Fact]
    public void Cursor_emitsClaudeCodeShape_mcpServers()
    {
        var w = new CursorWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
        // No `type` field for non-Copilot writers.
        json["mcpServers"]!["sourcegraph"]!["type"].Should().BeNull();
    }

    [Fact]
    public void Cursor_mergeIntoExisting()
    {
        var existing = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"context7\": { \"command\": \"npx\", \"args\": [\"context7\"] } } }");
        var w = new CursorWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing));
        plan.Action.Should().Be(WriterAction.Insert);
        var merged = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        merged["mcpServers"]!["context7"]!["command"]!.GetValue<string>().Should().Be("npx");
        merged["mcpServers"]!["sourcegraph"].Should().NotBeNull();
    }

    // ────────────────────────── CodexWriter ──────────────────────────

    [Fact]
    public void Codex_cleanWrite_emitsAbsoluteRootedCompatibilityConfig()
    {
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        var toml = Encoding.UTF8.GetString(plan.ContentBytes);

        plan.Action.Should().Be(WriterAction.Insert);
        w.DefaultProjectPath(_tempRoot).Should().Be(
            Path.Join(_tempRoot, ".codex", "config.toml"));
        w.DefaultUserPath().Should().BeNull();
        toml.Should().Contain("[mcp_servers.sourcegraph]");
        toml.Should().Contain("command = \"sourcegraph-mcp\"");
        toml.Should().Contain(BasicTomlPath(Path.Join(_tempRoot, "MyApp.slnx")));
        toml.Should().Contain(BasicTomlPath(_tempRoot));
        toml.Should().Contain("\"--codex-compat\"");
        toml.Should().Contain($"cwd = {BasicTomlPath(_tempRoot)}");
        toml.Should().NotContain("${workspaceFolder}");
        CodexWriter.ContainsSourcegraphEntry(toml).Should().BeTrue(
            "the fresh write candidate must pass strict TOML parsing and contain a semantic table");
    }

    [Fact]
    public void Codex_merge_preservesUnrelatedTomlAndComments()
    {
        var existingText = """
            # personal defaults stay byte-for-byte
            model = "gpt-test"

            [mcp_servers.other]
            command = "other"

            """;
        var existing = Encoding.UTF8.GetBytes(existingText);
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existing));
        var merged = Encoding.UTF8.GetString(plan.ContentBytes);

        plan.Action.Should().Be(WriterAction.Insert);
        merged.Should().StartWith(existingText);
        merged.Should().Contain("[mcp_servers.other]");
        merged.Should().Contain("[mcp_servers.sourcegraph]");
    }

    [Fact]
    public void Codex_multilineStringContainingHeaderText_isNotMisdetected()
    {
        var existing = Encoding.UTF8.GetBytes(
            "note = '''\n[mcp_servers.sourcegraph]\ncommand = \"not a table\"\n'''\n");
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existing));
        var merged = Encoding.UTF8.GetString(plan.ContentBytes);

        plan.Action.Should().Be(WriterAction.Insert);
        merged.Should().Contain("note = '''");
        merged.Split("[mcp_servers.sourcegraph]").Should().HaveCount(3);
    }

    [Fact]
    public void Codex_semanticallyMatchingQuotedTable_isByteExactNoOp()
    {
        var existingText = """
            [mcp_servers."sourcegraph"]
            args = [
              'serve',
              '--solution',
              '__SOLUTION__',
              '--root',
              '__ROOT__',
              '--codex-compat',
            ]
            enabled = true # user-owned option
            cwd = '__ROOT__'
            command = 'sourcegraph-mcp'
            """;
        existingText = existingText
            .Replace("__SOLUTION__", Path.Join(_tempRoot, "MyApp.slnx"), StringComparison.Ordinal)
            .Replace("__ROOT__", _tempRoot, StringComparison.Ordinal);
        var existing = Encoding.UTF8.GetBytes(existingText);
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existing));

        plan.Action.Should().Be(WriterAction.NoOpAlreadyMatches);
        plan.ContentBytes.Should().Equal(existing);
    }

    [Fact]
    public void Codex_existingDiffersWithoutForce_isSkip()
    {
        var existing = Encoding.UTF8.GetBytes(
            "[mcp_servers.sourcegraph]\ncommand = \"old\"\nargs = []\ncwd = \"..\"\n");
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existing, force: false));

        plan.Action.Should().Be(WriterAction.SkipExistingDiffers);
    }

    [Fact]
    public void Codex_force_updatesOnlyOwnedValues()
    {
        var existingText = """
            # keep me
            [mcp_servers.sourcegraph]
            command = "old" # command note
            args = ["old"]
            cwd = "."
            enabled = false
            startup_timeout_sec = 120

            [mcp_servers.sourcegraph.env]
            TOKEN = "keep"

            [mcp_servers.other]
            command = "other"
            """;
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(
            w,
            Encoding.UTF8.GetBytes(existingText),
            force: true));
        var updated = Encoding.UTF8.GetString(plan.ContentBytes);

        plan.Action.Should().Be(WriterAction.ReplaceOurs);
        updated.Should().Contain("command = \"sourcegraph-mcp\" # command note");
        updated.Should().Contain("enabled = false");
        updated.Should().Contain("startup_timeout_sec = 120");
        updated.Should().Contain("[mcp_servers.sourcegraph.env]");
        updated.Should().Contain("TOKEN = \"keep\"");
        updated.Should().Contain("[mcp_servers.other]");
    }

    [Fact]
    public void Codex_forceDoesNotDiscardCommentsNestedInsideOwnedArray()
    {
        var existingText = """
            [mcp_servers.sourcegraph]
            command = "old"
            args = [
              "serve", # keep serve comment
              "--solution", # keep solution comment
              "Old.slnx",
            ]
            cwd = ".."
            """;
        var existing = Encoding.UTF8.GetBytes(existingText);
        var w = new CodexWriter();

        var plan = w.Plan(MakeContext(w, existing, force: true));

        plan.Action.Should().Be(WriterAction.SkipExistingDiffers);
        plan.Description.Should().Contain("cannot be patched safely");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetPath)!);
        File.WriteAllBytes(plan.TargetPath, existing);
        w.Apply(plan);
        File.ReadAllBytes(plan.TargetPath).Should().Equal(existing);
    }

    [Fact]
    public void Codex_forceAddsMissingOwnedFields_withoutRemovingExtras()
    {
        var existing = Encoding.UTF8.GetBytes(
            "[mcp_servers.sourcegraph]\nenabled = true\n");
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existing, force: true));
        var updated = Encoding.UTF8.GetString(plan.ContentBytes);

        plan.Action.Should().Be(WriterAction.ReplaceOurs);
        updated.Should().Contain("enabled = true");
        updated.Should().Contain("command = \"sourcegraph-mcp\"");
        updated.Should().Contain("\"--codex-compat\"");
        updated.Should().Contain($"cwd = {BasicTomlPath(_tempRoot)}");
    }

    [Fact]
    public void Codex_forceAddsMissingFieldsAfterHeaderTrailingComment()
    {
        var existingText = "[mcp_servers.sourcegraph] # keep with header";
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(
            w,
            Encoding.UTF8.GetBytes(existingText),
            force: true));
        var updated = Encoding.UTF8.GetString(plan.ContentBytes);

        plan.Action.Should().Be(WriterAction.ReplaceOurs);
        updated.Should().StartWith(existingText + "\n");
        updated.Should().Contain("\ncommand = \"sourcegraph-mcp\"");
    }

    [Fact]
    public void Codex_invalidUnicodeArgument_isRejectedWithoutThrowing()
    {
        var w = new CodexWriter();
        var context = MakeContext(w, null) with
        {
            SolutionPath = "${workspaceFolder}/\ud800.slnx",
        };

        var act = () => w.Plan(context);

        var plan = act.Should().NotThrow().Which;
        plan.Action.Should().Be(WriterAction.SkipExistingDiffers);
        plan.Description.Should().Contain("invalid Unicode scalar");
        plan.ContentBytes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("this = [not valid")]
    [InlineData("mcp_servers = 1")]
    [InlineData("mcp_servers = { sourcegraph = { command = \"old\" } }")]
    public void Codex_unsafeTomlShape_isNeverOverwrittenByForce(string existingText)
    {
        var existing = Encoding.UTF8.GetBytes(existingText);
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existing, force: true));

        plan.Action.Should().Be(WriterAction.SkipExistingDiffers);
        (plan.Description.Contains("refusing", StringComparison.Ordinal)
            || plan.Description.Contains("unsupported", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public void Codex_merge_preservesUtf8BomAndCrLf()
    {
        var payload = Encoding.UTF8.GetBytes("model = \"gpt-test\"\r\n");
        var existing = Encoding.UTF8.GetPreamble().Concat(payload).ToArray();
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, existing));

        plan.Action.Should().Be(WriterAction.Insert);
        plan.ContentBytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var text = Encoding.UTF8.GetString(plan.ContentBytes);
        text.Replace("\r\n", string.Empty).Should().NotContain("\n");
    }

    [Fact]
    public void Codex_rootModeAndInRepoMode_expandWorkspacePlaceholder()
    {
        var w = new CodexWriter();
        var rootPlan = w.Plan(MakeContext(w, null) with { SolutionPath = null });
        var rootToml = Encoding.UTF8.GetString(rootPlan.ContentBytes);
        rootToml.Should().Contain("\"--root\"");
        rootToml.Should().Contain(BasicTomlPath(_tempRoot));
        rootToml.Should().Contain("\"--codex-compat\"");

        var inRepoPlan = w.Plan(MakeContext(w, null) with
        {
            InstallMode = InstallMode.InRepo,
        });
        var inRepoToml = Encoding.UTF8.GetString(inRepoPlan.ContentBytes);
        inRepoToml.Should().Contain(
            BasicTomlPath(Path.Join(_tempRoot, "src", "DevBitsLab.Mcp.SourceGraph.Server")));
        inRepoToml.Should().NotContain("${workspaceFolder}");
    }

    [Fact]
    public void Codex_relativeSolution_isResolvedAgainstRepositoryRoot()
    {
        var writer = new CodexWriter();
        var plan = writer.Plan(MakeContext(writer, null) with
        {
            SolutionPath = Path.Join(".", "GestureHub.slnx"),
        });
        var toml = Encoding.UTF8.GetString(plan.ContentBytes);

        toml.Should().Contain(BasicTomlPath(Path.Join(_tempRoot, "GestureHub.slnx")));
    }

    [Fact]
    public void Codex_apply_writesConfig()
    {
        var w = new CodexWriter();
        var plan = w.Plan(MakeContext(w, null));
        w.Apply(plan);

        File.Exists(plan.TargetPath).Should().BeTrue();
        File.ReadAllText(plan.TargetPath).Should().Contain("[mcp_servers.sourcegraph]");
    }

    private static string BasicTomlPath(string path) =>
        "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";

    // ────────────────────────── ContinueWriter ──────────────────────────

    [Fact]
    public void Continue_cleanWrite_emitsValidYaml()
    {
        var w = new ContinueWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        var yaml = Encoding.UTF8.GetString(plan.ContentBytes);
        yaml.Should().StartWith("name: sourcegraph\n");
        yaml.Should().Contain("command: sourcegraph-mcp");
        yaml.Should().Contain("args:");
        yaml.Should().Contain("  - serve");
    }

    [Fact]
    public void Continue_targetPath_isContinueSubdir()
    {
        var w = new ContinueWriter();
        w.DefaultProjectPath(_tempRoot).Should().Be(
            Path.Join(_tempRoot, ".continue", "mcp", "sourcegraph.yaml"));
    }

    [Fact]
    public void Continue_existingMatches_isNoOp()
    {
        var w = new ContinueWriter();
        var firstPlan = w.Plan(MakeContext(w, existingContent: null));
        var secondPlan = w.Plan(MakeContext(w, existingContent: firstPlan.ContentBytes));
        secondPlan.Action.Should().Be(WriterAction.NoOpAlreadyMatches);
    }

    [Fact]
    public void Continue_existingDiffers_skipsWithoutForce()
    {
        var existing = Encoding.UTF8.GetBytes("name: sourcegraph\ncommand: different\nargs: []\n");
        var w = new ContinueWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing, force: false));
        plan.Action.Should().Be(WriterAction.SkipExistingDiffers);
    }

    [Fact]
    public void Continue_yamlContainsWorkspaceFolderToken()
    {
        var w = new ContinueWriter();
        var plan = w.Plan(MakeContext(w, existingContent: null));
        var yaml = Encoding.UTF8.GetString(plan.ContentBytes);
        // ${workspaceFolder} contains '$' and '{' / '}' which our quoter wraps in double quotes.
        yaml.Should().Contain("${workspaceFolder}");
    }

    // ────────────────────────── ClaudeDesktopWriter ──────────────────────────

    [Fact]
    public void ClaudeDesktop_hasNoProjectPath()
    {
        var w = new ClaudeDesktopWriter();
        w.DefaultProjectPath(_tempRoot).Should().BeNull();
    }

    [Fact]
    public void ClaudeDesktop_userPath_isPlatformSpecific()
    {
        var w = new ClaudeDesktopWriter();
        var path = w.DefaultUserPath();
        path.Should().NotBeNull();
        if (OperatingSystem.IsMacOS())
        {
            path.Should().Contain("Library/Application Support/Claude");
        }
        else if (OperatingSystem.IsWindows())
        {
            path.Should().Contain("Claude").And.EndWith("claude_desktop_config.json");
        }
        else
        {
            path.Should().Contain(".config/Claude");
        }
    }

    [Fact]
    public void ClaudeDesktop_emitsMcpServersShape()
    {
        var w = new ClaudeDesktopWriter();
        // Use a temp-rooted target so we don't write to the real home dir during tests.
        var ctx = new WriterContext(
            Root: _tempRoot,
            TargetPath: Path.Join(_tempRoot, "claude_desktop_config.json"),
            UseUserScope: true,
            InstallMode: InstallMode.Global,
            SolutionPath: "${workspaceFolder}/MyApp.slnx",
            ServerProjectPath: null,
            EmbeddingsEnabled: false,
            AllowModelDownload: false,
            NoHistory: false,
            Force: false,
            ExistingContent: null);
        var plan = w.Plan(ctx);
        plan.Action.Should().Be(WriterAction.Insert);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
    }

    // ────────────────────────── CommentDetector ──────────────────────────

    [Fact]
    public void CommentDetector_detectsLineComment()
    {
        CommentDetector.HasJsonComments("{ // hi\n  \"x\": 1 }").Should().BeTrue();
    }

    [Fact]
    public void CommentDetector_detectsBlockComment()
    {
        CommentDetector.HasJsonComments("/* a */ {\"x\": 1}").Should().BeTrue();
    }

    [Fact]
    public void CommentDetector_ignoresSlashSlashInsideString()
    {
        CommentDetector.HasJsonComments("{ \"url\": \"https://example.com\" }").Should().BeFalse();
    }

    [Fact]
    public void CommentDetector_ignoresEscapedQuoteAndComment()
    {
        // A backslash-escaped quote inside a string MUST keep us in-string. The `//` after still
        // counts as code, not a comment, but we test the predicate honours escape.
        var s = "{ \"label\": \"a\\\"//b\" }";
        CommentDetector.HasJsonComments(s).Should().BeFalse();
    }

    [Fact]
    public void CommentDetector_returnsFalseOnPlainJson()
    {
        CommentDetector.HasJsonComments("{\"a\":1,\"b\":[1,2,3]}").Should().BeFalse();
    }

    // ────────────────────────── Malformed-JSON degraded paths ──────────────────────────

    [Fact]
    public void ClaudeCode_existingMalformedJson_isSkip_withoutForce()
    {
        var existing = Encoding.UTF8.GetBytes("{ this is not json");
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing, force: false));
        plan.Action.Should().Be(WriterAction.SkipExistingDiffers);
        plan.Description.Should().Contain("not valid JSON");
    }

    [Fact]
    public void ClaudeCode_existingMalformedJson_withForce_isReplace()
    {
        var existing = Encoding.UTF8.GetBytes("{ this is not json");
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing, force: true));
        plan.Action.Should().Be(WriterAction.ReplaceOurs);
        // The fresh document should be valid JSON with our entry.
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["mcpServers"]!["sourcegraph"]!.Should().NotBeNull();
    }

    // ────────────────────────── Comment-aware degraded path ──────────────────────────

    [Fact]
    public void ClaudeCode_existingHasComments_returnsSkipHasComments()
    {
        var existing = Encoding.UTF8.GetBytes(
            "// I hand-edited this\n{ \"mcpServers\": { } }");
        var w = new ClaudeCodeWriter();
        var plan = w.Plan(MakeContext(w, existingContent: existing));
        plan.Action.Should().Be(WriterAction.SkipHasComments);
        plan.Description.Should().Contain("config has comments");
    }

    // ────────────────────────── Install-mode shaping ──────────────────────────

    [Fact]
    public void InstallMode_global_producesSourcegraphMcpCommand()
    {
        var w = new ClaudeCodeWriter();
        var ctx = MakeContext(w, existingContent: null) with { InstallMode = InstallMode.Global };
        var plan = w.Plan(ctx);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("sourcegraph-mcp");
    }

    [Fact]
    public void InstallMode_localTool_producesDotnetCommand()
    {
        var w = new ClaudeCodeWriter();
        var ctx = MakeContext(w, existingContent: null) with { InstallMode = InstallMode.LocalTool };
        var plan = w.Plan(ctx);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("dotnet");
        json["mcpServers"]!["sourcegraph"]!["args"]!.AsArray()[0]!.GetValue<string>().Should().Be("sourcegraph-mcp");
    }

    [Fact]
    public void InstallMode_inRepo_pointsAtServerProject()
    {
        var w = new ClaudeCodeWriter();
        var ctx = MakeContext(w, existingContent: null) with { InstallMode = InstallMode.InRepo };
        var plan = w.Plan(ctx);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        json["mcpServers"]!["sourcegraph"]!["command"]!.GetValue<string>().Should().Be("dotnet");
        var args = json["mcpServers"]!["sourcegraph"]!["args"]!.AsArray()
            .Select(a => a!.GetValue<string>()).ToArray();
        args[0].Should().Be("run");
        args.Should().Contain("--no-build");
        args.Should().Contain("serve");
    }

    [Fact]
    public void EmbeddingsEnabled_isPropagatedToArgs()
    {
        var w = new ClaudeCodeWriter();
        var ctx = MakeContext(w, existingContent: null) with { EmbeddingsEnabled = true };
        var plan = w.Plan(ctx);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        var args = json["mcpServers"]!["sourcegraph"]!["args"]!.AsArray()
            .Select(a => a!.GetValue<string>()).ToArray();
        args.Should().Contain("--enable-embeddings");
    }

    [Fact]
    public void AllowModelDownload_isPropagatedToArgs()
    {
        var w = new ClaudeCodeWriter();
        var ctx = MakeContext(w, existingContent: null) with
        {
            EmbeddingsEnabled = true,
            AllowModelDownload = true,
        };
        var plan = w.Plan(ctx);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        var args = json["mcpServers"]!["sourcegraph"]!["args"]!.AsArray()
            .Select(a => a!.GetValue<string>()).ToArray();
        args.Should().Contain("--enable-embeddings");
        args.Should().Contain("--allow-model-download");
    }
}
