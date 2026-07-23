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
            NoEmbeddings: false,
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
            NoEmbeddings: false,
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
    public void NoEmbeddings_isPropagatedToArgs()
    {
        var w = new ClaudeCodeWriter();
        var ctx = MakeContext(w, existingContent: null) with { NoEmbeddings = true };
        var plan = w.Plan(ctx);
        var json = JsonNode.Parse(plan.ContentBytes)!.AsObject();
        var args = json["mcpServers"]!["sourcegraph"]!["args"]!.AsArray()
            .Select(a => a!.GetValue<string>()).ToArray();
        args.Should().Contain("--no-embeddings");
    }
}
