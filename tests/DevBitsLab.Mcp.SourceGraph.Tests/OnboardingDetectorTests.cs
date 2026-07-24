using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the read-only environment detector that powers <c>init</c> / <c>doctor</c>.
/// Uses temp dirs because the detector is intentionally not behind a filesystem abstraction —
/// it answers questions about the *real* repo on disk, and the wiring is thin enough that
/// real-fs tests are simpler than a mock seam.
/// </summary>
public sealed class OnboardingDetectorTests : IDisposable
{
    private readonly string _tempRoot;

    public OnboardingDetectorTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-onboarding-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task DetectsDotnetSdk_whenAvailable()
    {
        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        // We're running this inside a .NET test host, so the SDK has to be present.
        result.DotnetSdkVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task NoSolutions_returnsEmpty()
    {
        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        result.SolutionFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoversSlnxAtRoot()
    {
        var sln = Path.Join(_tempRoot, "MyApp.slnx");
        File.WriteAllText(sln, "<Solution/>");
        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        result.SolutionFiles.Should().ContainSingle().Which.Should().EndWith("MyApp.slnx");
    }

    [Fact]
    public async Task DiscoversSlnAtRoot()
    {
        var sln = Path.Join(_tempRoot, "Legacy.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File");
        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        result.SolutionFiles.Should().ContainSingle().Which.Should().EndWith("Legacy.sln");
    }

    [Fact]
    public async Task DiscoversSolutionsOneLevelDeep()
    {
        var sub = Path.Join(_tempRoot, "src");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Join(sub, "frontend.slnx"), "<Solution/>");
        File.WriteAllText(Path.Join(sub, "backend.slnx"), "<Solution/>");

        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        result.SolutionFiles.Should().HaveCount(2);
        result.SolutionFiles.Should().Contain(p => p.EndsWith("frontend.slnx"));
        result.SolutionFiles.Should().Contain(p => p.EndsWith("backend.slnx"));
    }

    [Fact]
    public async Task SourceGraphConfig_missing_whenAbsent()
    {
        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        result.SourceGraphConfigStatus.Should().Be(SourceGraphConfigStatus.Missing);
        result.SourceGraphConfigError.Should().BeNull();
    }

    [Fact]
    public async Task SourceGraphConfig_malformed_carriesError()
    {
        File.WriteAllText(Path.Join(_tempRoot, ".sourcegraph.json"), "{ this is not valid json");
        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        result.SourceGraphConfigStatus.Should().Be(SourceGraphConfigStatus.Malformed);
        result.SourceGraphConfigError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ClientConfig_detectedWhenPresent_claudeCode()
    {
        File.WriteAllText(Path.Join(_tempRoot, ".mcp.json"),
            "{ \"mcpServers\": { \"sourcegraph\": { \"command\": \"sourcegraph-mcp\" } } }");
        var result = await OnboardingDetector.DetectAsync(_tempRoot);

        var claudeCode = result.ClientConfigsDetected.FirstOrDefault(
            c => c.Client == ClientId.ClaudeCode && !c.IsUserScope);
        claudeCode.Should().NotBeNull();
        claudeCode!.Exists.Should().BeTrue();
        claudeCode.ContainsSourcegraphEntry.Should().BeTrue();
    }

    [Fact]
    public async Task ClientConfig_detectedWhenPresent_copilot_distinctSchema()
    {
        var vscodeDir = Path.Join(_tempRoot, ".vscode");
        Directory.CreateDirectory(vscodeDir);
        // Copilot uses `servers` (not `mcpServers`) and an explicit `type: "stdio"`.
        File.WriteAllText(Path.Join(vscodeDir, "mcp.json"),
            "{ \"servers\": { \"sourcegraph\": { \"type\": \"stdio\", \"command\": \"sourcegraph-mcp\" } } }");
        var result = await OnboardingDetector.DetectAsync(_tempRoot);

        var copilot = result.ClientConfigsDetected.FirstOrDefault(
            c => c.Client == ClientId.Copilot && !c.IsUserScope);
        copilot.Should().NotBeNull();
        copilot!.Exists.Should().BeTrue();
        copilot.ContainsSourcegraphEntry.Should().BeTrue();
    }

    [Fact]
    public async Task ClientConfig_existsButNoEntry_isFlagged()
    {
        File.WriteAllText(Path.Join(_tempRoot, ".mcp.json"),
            "{ \"mcpServers\": { \"otherServer\": { \"command\": \"x\" } } }");
        var result = await OnboardingDetector.DetectAsync(_tempRoot);

        var claudeCode = result.ClientConfigsDetected.First(
            c => c.Client == ClientId.ClaudeCode && !c.IsUserScope);
        claudeCode.Exists.Should().BeTrue();
        claudeCode.ContainsSourcegraphEntry.Should().BeFalse();
    }

    [Fact]
    public async Task ClientConfig_continueYaml_detectedByNameLine()
    {
        var continueDir = Path.Join(_tempRoot, ".continue", "mcp");
        Directory.CreateDirectory(continueDir);
        File.WriteAllText(Path.Join(continueDir, "sourcegraph.yaml"),
            "name: sourcegraph\ncommand: sourcegraph-mcp\nargs:\n  - serve\n");
        var result = await OnboardingDetector.DetectAsync(_tempRoot);

        var continueClient = result.ClientConfigsDetected.First(
            c => c.Client == ClientId.Continue && !c.IsUserScope);
        continueClient.ContainsSourcegraphEntry.Should().BeTrue();
    }

    [Fact]
    public async Task ClientConfig_codexToml_detectedBySemanticTable()
    {
        var codexDir = Path.Join(_tempRoot, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Join(codexDir, "config.toml"),
            "model = \"gpt-test\"\n[mcp_servers.\"sourcegraph\"]\ncommand = \"sourcegraph-mcp\"\n");

        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        var codex = result.ClientConfigsDetected.First(
            c => c.Client == ClientId.Codex && !c.IsUserScope);

        codex.Exists.Should().BeTrue();
        codex.ContainsSourcegraphEntry.Should().BeTrue();
    }

    [Fact]
    public async Task ClientConfig_codexCommentedOrStringHeader_isNotFalsePositive()
    {
        var codexDir = Path.Join(_tempRoot, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Join(codexDir, "config.toml"),
            "# [mcp_servers.sourcegraph]\nnote = '''\n[mcp_servers.sourcegraph]\n'''\n");

        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        var codex = result.ClientConfigsDetected.First(
            c => c.Client == ClientId.Codex && !c.IsUserScope);

        codex.Exists.Should().BeTrue();
        codex.ContainsSourcegraphEntry.Should().BeFalse();
    }

    [Theory]
    [InlineData("mcp_servers.sourcegraph = 1\n")]
    [InlineData("[[mcp_servers.sourcegraph]]\ncommand = \"sourcegraph-mcp\"\n")]
    public async Task ClientConfig_codexNonTableValue_isNotReportedAsWired(string config)
    {
        var codexDir = Path.Join(_tempRoot, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Join(codexDir, "config.toml"), config);

        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        var codex = result.ClientConfigsDetected.First(
            c => c.Client == ClientId.Codex && !c.IsUserScope);

        codex.Exists.Should().BeTrue();
        codex.ContainsSourcegraphEntry.Should().BeFalse();
    }

    [Theory]
    [InlineData("utf16")]
    [InlineData("invalid-utf8")]
    public async Task ClientConfig_codexNonUtf8_isNotReportedAsWired(string encodingCase)
    {
        var codexDir = Path.Join(_tempRoot, ".codex");
        Directory.CreateDirectory(codexDir);
        var path = Path.Join(codexDir, "config.toml");
        var text = "[mcp_servers.sourcegraph]\ncommand = \"sourcegraph-mcp\"\n";
        var bytes = encodingCase switch
        {
            "utf16" => Encoding.Unicode.GetPreamble()
                .Concat(Encoding.Unicode.GetBytes(text))
                .ToArray(),
            _ => new byte[] { 0xff, 0xfe, 0xfd },
        };
        File.WriteAllBytes(path, bytes);

        var result = await OnboardingDetector.DetectAsync(_tempRoot);
        var codex = result.ClientConfigsDetected.First(
            c => c.Client == ClientId.Codex && !c.IsUserScope);

        codex.Exists.Should().BeTrue();
        codex.ContainsSourcegraphEntry.Should().BeFalse();
    }

    [Fact]
    public void ClientId_slugs_roundTrip()
    {
        foreach (var id in Enum.GetValues<ClientId>())
        {
            var slug = id.ToSlug();
            ClientIdExtensions.TryParseSlug(slug, out var parsed).Should().BeTrue();
            parsed.Should().Be(id);
        }
    }

    [Fact]
    public void ClaudeDesktopUserPath_isNeverEmpty_onUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = OnboardingDetector.ClaudeDesktopUserPath("/home/test");
        path.Should().Contain("Claude");
        path.Should().EndWith("claude_desktop_config.json");
    }
}

/// <summary>
/// Contract-level coverage for <see cref="DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.IClientConfigWriter"/>:
/// the four <c>WriterAction</c> outcomes plus the comment-aware degraded path. Uses a tiny
/// in-test stub so the per-client writers in Group 2 can follow the same testing pattern.
/// </summary>
public sealed class WriterContractTests : IDisposable
{
    private readonly string _tempRoot;

    public WriterContractTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-writer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterContext MakeContext(
        string targetPath, byte[]? existingContent, bool force = false)
    {
        return new DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterContext(
            Root: _tempRoot,
            TargetPath: targetPath,
            UseUserScope: false,
            InstallMode: DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.InstallMode.Global,
            SolutionPath: "${workspaceFolder}/MyApp.slnx",
            ServerProjectPath: null,
            NoEmbeddings: false,
            NoHistory: false,
            Force: force,
            ExistingContent: existingContent);
    }

    [Fact]
    public void Plan_missingFile_isInsert()
    {
        var writer = new StubWriter();
        var ctx = MakeContext(Path.Join(_tempRoot, "absent.json"), existingContent: null);
        var plan = writer.Plan(ctx);
        plan.Action.Should().Be(DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.Insert);
        plan.ContentBytes.Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_existingNoEntry_isInsert()
    {
        var existing = Encoding.UTF8.GetBytes("{ \"mcpServers\": { \"otherServer\": { \"command\": \"x\" } } }");
        var writer = new StubWriter();
        var ctx = MakeContext(Path.Join(_tempRoot, "f.json"), existing);
        var plan = writer.Plan(ctx);
        plan.Action.Should().Be(DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.Insert);
    }

    [Fact]
    public void Plan_existingMatchesOurs_isNoOp()
    {
        var writer = new StubWriter();
        var ctx0 = MakeContext(Path.Join(_tempRoot, "f.json"), existingContent: null);
        var clean = writer.Plan(ctx0).ContentBytes;

        var ctx1 = MakeContext(Path.Join(_tempRoot, "f.json"), existingContent: clean);
        var plan = writer.Plan(ctx1);
        plan.Action.Should().Be(DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.NoOpAlreadyMatches);
    }

    [Fact]
    public void Plan_existingDiffersWithoutForce_isSkip()
    {
        var existing = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"sourcegraph\": { \"command\": \"different\" } } }");
        var writer = new StubWriter();
        var ctx = MakeContext(Path.Join(_tempRoot, "f.json"), existing, force: false);
        var plan = writer.Plan(ctx);
        plan.Action.Should().Be(DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.SkipExistingDiffers);
    }

    [Fact]
    public void Plan_existingDiffersWithForce_isReplace()
    {
        var existing = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"sourcegraph\": { \"command\": \"different\" } } }");
        var writer = new StubWriter();
        var ctx = MakeContext(Path.Join(_tempRoot, "f.json"), existing, force: true);
        var plan = writer.Plan(ctx);
        plan.Action.Should().Be(DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.ReplaceOurs);
    }

    [Fact]
    public void Apply_writesContentToTarget()
    {
        var path = Path.Join(_tempRoot, "subdir", "f.json");
        var writer = new StubWriter();
        var plan = writer.Plan(MakeContext(path, existingContent: null));
        writer.Apply(plan);
        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().BeEquivalentTo(plan.ContentBytes);
    }

    [Fact]
    public void Apply_isNoOp_whenActionIsNoOpOrSkip()
    {
        var path = Path.Join(_tempRoot, "f.json");
        var writer = new StubWriter();
        var plan = new DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterPlan(
            TargetPath: path,
            Action: DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.NoOpAlreadyMatches,
            ContentBytes: Encoding.UTF8.GetBytes("ignored"),
            Description: "no-op");
        writer.Apply(plan);
        File.Exists(path).Should().BeFalse();
    }

    /// <summary>
    /// Minimal in-test stub: emits a fixed JSON document and uses byte-for-byte equality plus the
    /// existence of an `mcpServers.sourcegraph` key as the merge predicate. Real per-client
    /// writers (Group 2) follow the same shape with their own schemas.
    /// </summary>
    private sealed class StubWriter : DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.IClientConfigWriter
    {
        public ClientId ClientId => ClientId.ClaudeCode;
        public string? DefaultProjectPath(string root) => Path.Join(root, ".mcp.json");
        public string? DefaultUserPath() => null;

        public DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterPlan Plan(
            DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterContext ctx)
        {
            byte[] ours = Encoding.UTF8.GetBytes(
                "{\n  \"mcpServers\": {\n    \"sourcegraph\": {\n      \"command\": \"sourcegraph-mcp\"\n    }\n  }\n}\n");

            if (ctx.ExistingContent is null)
            {
                return new(ctx.TargetPath,
                    DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.Insert, ours,
                    "would create new config");
            }
            if (ours.AsSpan().SequenceEqual(ctx.ExistingContent))
            {
                return new(ctx.TargetPath,
                    DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.NoOpAlreadyMatches, ours,
                    "already wired");
            }
            // Has an entry that differs?
            var existingText = Encoding.UTF8.GetString(ctx.ExistingContent);
            if (existingText.Contains("\"sourcegraph\"", StringComparison.Ordinal))
            {
                return new(ctx.TargetPath,
                    ctx.Force
                        ? DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.ReplaceOurs
                        : DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.SkipExistingDiffers,
                    ours,
                    ctx.Force ? "force-replacing existing entry" : "existing entry differs (--force to overwrite)");
            }
            // Has the file but no sourcegraph entry → insert.
            return new(ctx.TargetPath,
                DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.Insert, ours,
                "would insert into existing config");
        }

        public void Apply(DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterPlan plan)
        {
            if (plan.Action is DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.Insert
                or DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters.WriterAction.ReplaceOurs)
            {
                var dir = Path.GetDirectoryName(plan.TargetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(plan.TargetPath, plan.ContentBytes);
            }
        }
    }
}
