using DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Implementation of the <c>sourcegraph-mcp init</c> subcommand: detects environment, picks
/// MCP clients (interactive or flag-driven), runs each client's writer, optionally pre-warms
/// the index, and prints a closing report. Project-scoped writes are the default; user-scope
/// writes require an explicit per-client flag.
/// </summary>
internal static class InitCli
{
    /// <summary>Banner printed at the top of an interactive <c>init</c> session, immediately
    /// before the detection summary.</summary>
    private const string Heading = "🌿 SourceGraph init";

    public static async Task<int> RunAsync(CommandLine cli)
    {
        var root = cli.ResolvedRepoRoot();
        var detection = await OnboardingDetector.DetectAsync(root).ConfigureAwait(false);
        var interactive = !cli.Yes && !cli.PrintOnly && IsStdinInteractive();

        if (!cli.PrintOnly)
        {
            Console.WriteLine(Heading);
            Console.WriteLine();
            PrintDetectionSummary(detection);
            Console.WriteLine();
        }

        if (detection.SourceGraphConfigStatus == SourceGraphConfigStatus.Malformed)
        {
            await Console.Error.WriteLineAsync(
                $"error: .sourcegraph.json is malformed: {detection.SourceGraphConfigError}")
                .ConfigureAwait(false);
            return 1;
        }

        // Resolve solution + scope mode.
        var (solutionPath, useRootMode) = ResolveSolutionMode(cli, detection, interactive);

        // If multi-solution and no .sourcegraph.json yet, scaffold one (delegates to existing
        // init-scopes core logic). Skipped under --print-only because it would write to disk.
        if (!cli.PrintOnly && useRootMode &&
            detection.SourceGraphConfigStatus == SourceGraphConfigStatus.Missing &&
            detection.SolutionFiles.Count > 1)
        {
            var ok = await ScaffoldSourceGraphConfigAsync(root, detection, interactive).ConfigureAwait(false);
            if (!ok) return 1;
        }

        // Resolve enabled clients (slug → user-scope?).
        var enabledClients = ResolveEnabledClients(cli, detection, interactive);
        if (enabledClients.Count == 0)
        {
            Console.WriteLine("No clients selected. Nothing to do.");
            return 0;
        }

        var installMode = ParseInstallMode(cli.InstallMode);

        // Run each writer. Collect results for the closing report.
        var results = new List<WriterRunResult>();
        foreach (var (clientId, useUserScope) in enabledClients)
        {
            var writer = MakeWriter(clientId);
            var targetPath = useUserScope
                ? writer.DefaultUserPath()
                : writer.DefaultProjectPath(root);
            if (string.IsNullOrEmpty(targetPath))
            {
                // Specific guidance per known combo so the user knows why the scope was skipped.
                var msg = (clientId, useUserScope) switch
                {
                    (ClientId.Copilot, true) =>
                        "user-scope Copilot wiring (chat.mcp.servers in settings.json) is not " +
                        "supported by `init` in v1; use the project-scope `.vscode/mcp.json` " +
                        "(re-run without --user-copilot) or paste the snippet from `--print-only` " +
                        "into your VS Code user settings manually",
                    _ => $"client `{clientId.ToSlug()}` has no {(useUserScope ? "user" : "project")}-scope target path",
                };
                Console.Error.WriteLine($"warn: skipping {clientId.ToSlug()} ({(useUserScope ? "user" : "project")}): {msg}");
                results.Add(new WriterRunResult(clientId, useUserScope, "(no target path)",
                    WriterAction.SkipUnsupported, msg));
                continue;
            }
            byte[]? existing = null;
            try
            {
                if (File.Exists(targetPath)) existing = File.ReadAllBytes(targetPath);
            }
            catch (IOException ex)
            {
                results.Add(new WriterRunResult(clientId, useUserScope, targetPath,
                    WriterAction.SkipExistingDiffers, $"could not read existing file: {ex.Message}"));
                continue;
            }
            // Force `--no-history` into the emitted args when git isn't on PATH. Without git the
            // server's history pipeline can't function, and the detection summary already told
            // the user this would happen — making the implication explicit avoids a confusing
            // half-broken first run.
            var noHistory = cli.NoHistory || !detection.GitOnPath;
            var ctx = new WriterContext(
                Root: root,
                TargetPath: targetPath,
                UseUserScope: useUserScope,
                InstallMode: installMode,
                SolutionPath: useRootMode ? null : solutionPath,
                ServerProjectPath: null,
                NoEmbeddings: cli.NoEmbeddings,
                NoHistory: noHistory,
                Force: cli.Force,
                ExistingContent: existing);
            var plan = writer.Plan(ctx);

            if (cli.PrintOnly)
            {
                PrintPlanToStdout(plan);
                results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                    plan.Action, plan.Description));
                continue;
            }

            try
            {
                writer.Apply(plan);
            }
            catch (IOException ex)
            {
                results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                    WriterAction.SkipExistingDiffers, $"apply failed: {ex.Message}"));
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                    WriterAction.SkipExistingDiffers, $"apply failed: {ex.Message}"));
                continue;
            }

            // Comment-aware degraded path: even when not --print-only, a SkipHasComments outcome
            // emits the snippet to stdout so the user can paste manually.
            if (plan.Action == WriterAction.SkipHasComments)
            {
                PrintPlanToStdout(plan);
            }

            results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                plan.Action, plan.Description));
        }

        // Pre-warm — interactive default is ON; --yes default is OFF.
        var prewarmDefault = interactive && !cli.PrintOnly;
        var prewarm = cli.Prewarm ?? prewarmDefault;
        if (prewarm && !cli.PrintOnly && !string.IsNullOrEmpty(solutionPath))
        {
            await PrewarmAsync(solutionPath, root).ConfigureAwait(false);
        }

        if (!cli.PrintOnly)
        {
            Console.WriteLine();
            PrintClosingReport(results);
        }

        // Exit code: 0 unless any writer skipped because of a conflict; 2 in that case (matches
        // the doctor / vocabulary --strict precedent for "warning-as-failure" CI signals).
        return results.Any(r => r.Action == WriterAction.SkipExistingDiffers) ? 2 : 0;
    }

    /// <summary>
    /// True if stdin is a terminal (we can prompt), false if it's a pipe / redirected. Used to
    /// silently fall back to <c>--yes</c>-style defaults in non-tty contexts even when the user
    /// didn't pass <c>--yes</c> explicitly.
    /// </summary>
    private static bool IsStdinInteractive()
    {
        try { return !Console.IsInputRedirected; }
        catch (IOException) { return false; }
    }

    private static void PrintDetectionSummary(OnboardingDetectionResult d)
    {
        Console.WriteLine($"  ✓ .NET SDK            : {d.DotnetSdkVersion ?? "(not detected)"}");
        Console.WriteLine($"  {(d.GitOnPath ? "✓" : "⚠")} git on PATH         : {(d.GitOnPath ? "yes" : "no (--no-history will be implied)")}");
        Console.WriteLine($"  ✓ repo root           : {d.RepoRootPath}");
        Console.WriteLine($"  ✓ solutions detected  : {(d.SolutionFiles.Count == 0 ? "(none)" : string.Join(", ", d.SolutionFiles.Select(Path.GetFileName)))}");
        var sgState = d.SourceGraphConfigStatus switch
        {
            SourceGraphConfigStatus.Valid => "valid",
            SourceGraphConfigStatus.Missing => "missing (single-scope synth path)",
            SourceGraphConfigStatus.Malformed => $"MALFORMED — {d.SourceGraphConfigError}",
            _ => "?",
        };
        Console.WriteLine($"  ✓ .sourcegraph.json   : {sgState}");
    }

    /// <summary>
    /// Decide whether to use <c>--solution &lt;path&gt;</c> mode (single solution, args carry the
    /// resolved path) or <c>--root</c> mode (multi-scope, args carry the workspace folder).
    /// In single-solution and explicit-flag cases we use the path; otherwise we use root mode.
    /// </summary>
    private static (string? SolutionPath, bool UseRootMode) ResolveSolutionMode(
        CommandLine cli, OnboardingDetectionResult d, bool interactive)
    {
        // Explicit --solution wins.
        if (cli.Solutions.Count == 1)
        {
            return (cli.Solutions[0], UseRootMode: false);
        }
        if (cli.Solutions.Count > 1)
        {
            return (SolutionPath: null, UseRootMode: true);
        }
        // Existing .sourcegraph.json → root mode (multi-scope is configured).
        if (d.SourceGraphConfigStatus == SourceGraphConfigStatus.Valid)
        {
            return (SolutionPath: null, UseRootMode: true);
        }
        // No solutions detected → use the workspace-folder placeholder (user fills in later).
        if (d.SolutionFiles.Count == 0)
        {
            return ("${workspaceFolder}/MySolution.slnx", UseRootMode: false);
        }
        // Single solution → use it.
        if (d.SolutionFiles.Count == 1)
        {
            // Encode as ${workspaceFolder}-relative when possible so the resulting config is
            // portable across machines.
            var rel = TryRelativeWorkspacePath(d.RepoRootPath, d.SolutionFiles[0]);
            return (rel, UseRootMode: false);
        }
        // Multiple solutions, no .sourcegraph.json → root mode (and we'll scaffold the config
        // inside RunAsync).
        return (SolutionPath: null, UseRootMode: true);
    }

    private static string TryRelativeWorkspacePath(string root, string absolute)
    {
        var rel = Path.GetRelativePath(root, absolute);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return absolute;
        }
        return "${workspaceFolder}/" + rel.Replace('\\', '/');
    }

    /// <summary>
    /// Decide which (clientId, useUserScope) pairs to wire. Honours <c>--client &lt;id&gt;</c>,
    /// <c>--no-&lt;client&gt;</c>, <c>--user-&lt;client&gt;</c>, and <c>--claude-desktop</c>; falls back
    /// to "every client whose project-scope path is sensible" when no explicit flags are set.
    /// </summary>
    private static List<(ClientId Id, bool UseUserScope)> ResolveEnabledClients(
        CommandLine cli, OnboardingDetectionResult d, bool interactive)
    {
        // Build the candidate set from --client (when set), or from auto-detected defaults.
        HashSet<ClientId> candidates;
        if (cli.Clients.Count > 0)
        {
            candidates = new HashSet<ClientId>();
            foreach (var raw in cli.Clients)
            {
                foreach (var slug in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ClientIdExtensions.TryParseSlug(slug, out var id))
                    {
                        candidates.Add(id);
                    }
                    else
                    {
                        Console.Error.WriteLine($"warn: unknown --client value: {slug}");
                    }
                }
            }
        }
        else
        {
            // Default candidates: every project-scoped client. Claude Desktop is opt-in only.
            candidates = new HashSet<ClientId>
            {
                ClientId.ClaudeCode,
                ClientId.Codex,
                ClientId.Copilot,
                ClientId.Cursor,
                ClientId.Continue,
            };
        }
        if (cli.ClaudeDesktop) candidates.Add(ClientId.ClaudeDesktop);

        // Apply --no-<client> drops. Slugs that don't parse to a known ClientId are silently
        // ignored — the parser already rejected them with a warn earlier in this method.
        foreach (var id in cli.NoClients.Select(TryParseClientIdOrNull).Where(x => x.HasValue))
        {
            candidates.Remove(id!.Value);
        }

        // Interactive picker (only when no explicit --client was given).
        if (interactive && cli.Clients.Count == 0)
        {
            candidates = InteractiveClientPicker(candidates, d, cli.ClaudeDesktop);
        }

        // Map to (id, useUserScope). User-scope is requested via --user-<client>.
        var results = new List<(ClientId, bool)>();
        foreach (var id in candidates.OrderBy(c => (int)c))
        {
            var slug = id.ToSlug();
            // Claude Desktop is always user-scope.
            var useUser = id == ClientId.ClaudeDesktop || cli.UserClients.Contains(slug);
            results.Add((id, useUser));
        }
        return results;
    }

    private static HashSet<ClientId> InteractiveClientPicker(
        HashSet<ClientId> autoSelected, OnboardingDetectionResult d, bool claudeDesktopOptedIn)
    {
        Console.WriteLine("Which clients should I wire up? (Enter to accept, type 'n' to skip a client)");
        var picked = new HashSet<ClientId>();
        var visible = Enum.GetValues<ClientId>()
            .Where(id => id != ClientId.ClaudeDesktop || claudeDesktopOptedIn);
        foreach (var id in visible)
        {
            var defaultYes = autoSelected.Contains(id);
            var marker = defaultYes ? "[Y/n]" : "[y/N]";
            Console.Write($"  {id.ToSlug(),-15} {marker} ");
            var line = Console.ReadLine();
            var answer = NormaliseYesNo(line, defaultYes);
            if (answer) picked.Add(id);
        }
        return picked;
    }

    private static ClientId? TryParseClientIdOrNull(string slug) =>
        ClientIdExtensions.TryParseSlug(slug, out var id) ? id : null;

    private static bool NormaliseYesNo(string? line, bool defaultYes)
    {
        if (string.IsNullOrWhiteSpace(line)) return defaultYes;
        var c = char.ToLowerInvariant(line.Trim()[0]);
        return c switch
        {
            'y' => true,
            'n' => false,
            _ => defaultYes,
        };
    }

    private static InstallMode ParseInstallMode(string? raw) => raw?.ToLowerInvariant() switch
    {
        null => ClientConfigWriters.InstallMode.Global,
        "global" => ClientConfigWriters.InstallMode.Global,
        "local-tool" => ClientConfigWriters.InstallMode.LocalTool,
        "in-repo" => ClientConfigWriters.InstallMode.InRepo,
        _ => ClientConfigWriters.InstallMode.Global,
    };

    private static IClientConfigWriter MakeWriter(ClientId id) => id switch
    {
        ClientId.ClaudeCode => new ClaudeCodeWriter(),
        ClientId.Codex => new CodexWriter(),
        ClientId.Copilot => new CopilotWriter(),
        ClientId.Cursor => new CursorWriter(),
        ClientId.Continue => new ContinueWriter(),
        ClientId.ClaudeDesktop => new ClaudeDesktopWriter(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "no writer for this ClientId"),
    };

    private static async Task<bool> ScaffoldSourceGraphConfigAsync(
        string root, OnboardingDetectionResult d, bool interactive)
    {
        if (interactive)
        {
            Console.WriteLine($"Detected {d.SolutionFiles.Count} solutions; will scaffold .sourcegraph.json (one scope per solution).");
            Console.Write("Proceed? [Y/n] ");
            if (!NormaliseYesNo(Console.ReadLine(), defaultYes: true))
            {
                Console.WriteLine("Skipping .sourcegraph.json scaffolding.");
                return true;
            }
        }
        var fakeCli = new CommandLine().WithRoot(root);
        var rc = await ScopesCli.RunInitAsync(fakeCli).ConfigureAwait(false);
        return rc == 0;
    }

    /// <summary>
    /// Print the snippet a writer would emit, prefixed by a comment line naming the target path
    /// and (when relevant) the writer's <see cref="WriterPlan.Description"/>. Used both for
    /// <c>--print-only</c> and the comment-aware degraded path; in the latter case the
    /// description carries the user-facing reason ("config has comments — paste manually") so
    /// surfacing it is what tells the user why nothing got written.
    /// </summary>
    private static void PrintPlanToStdout(WriterPlan plan)
    {
        Console.WriteLine($"# would write to: {plan.TargetPath}");
        if (plan.Action == WriterAction.SkipHasComments && !string.IsNullOrEmpty(plan.Description))
        {
            Console.WriteLine($"# {plan.Description}");
        }
        Console.Write(System.Text.Encoding.UTF8.GetString(plan.ContentBytes));
        Console.WriteLine();
    }

    private static void PrintClosingReport(List<WriterRunResult> results)
    {
        Console.WriteLine("Summary:");
        foreach (var r in results)
        {
            var glyph = r.Action switch
            {
                WriterAction.Insert => "✓ wrote",
                WriterAction.ReplaceOurs => "✓ replaced",
                WriterAction.NoOpAlreadyMatches => "= no change",
                WriterAction.SkipExistingDiffers => "⚠ skipped (conflict)",
                WriterAction.SkipHasComments => "⚠ skipped (comments)",
                WriterAction.SkipUnsupported => "ⓘ skipped (unsupported)",
                _ => "? ",
            };
            var scope = r.UserScope ? "user" : "project";
            Console.WriteLine($"  {glyph,-22} {r.ClientId.ToSlug(),-15} ({scope}) → {r.TargetPath}");
            if (r.Action is WriterAction.SkipExistingDiffers or WriterAction.SkipUnsupported)
            {
                Console.WriteLine($"      {r.Description}");
            }
        }
        Console.WriteLine();
        Console.WriteLine("Next:");
        Console.WriteLine("  • Open this repo in your MCP client.");
        Console.WriteLine("  • Verify with `sourcegraph-mcp demo`.");
    }

    private static async Task PrewarmAsync(string solutionPath, string root)
    {
        // Resolve to an absolute path. ${workspaceFolder} expansion handled by ExpandTokens.
        var expanded = CommandLine.ExpandTokens(solutionPath)
            .Replace("${workspaceFolder}", root, StringComparison.Ordinal);
        var abs = Path.IsPathRooted(expanded) ? expanded : Path.Join(root, expanded);
        if (!File.Exists(abs))
        {
            Console.Error.WriteLine($"warn: --prewarm requested but solution not found at {abs}");
            return;
        }
        Console.WriteLine();
        Console.WriteLine($"Pre-warming index against {Path.GetFileName(abs)}…");
        var startedAt = DateTimeOffset.UtcNow;
        var exitCode = await PrewarmLauncher.RunAttemptsAsync(
            PrewarmLauncher.BuildAttempts(abs),
            PrewarmLauncher.RunProcessAsync).ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (exitCode == 0)
        {
            Console.WriteLine($"  pre-warm: complete in {elapsed.TotalSeconds:F1}s");
            return;
        }

        var detail = exitCode.HasValue ? $"last exit {exitCode.Value}" : "no process started";
        Console.Error.WriteLine(
            $"warn: pre-warm failed ({detail}). Run `sourcegraph-mcp index \"{abs}\"` manually if needed.");
    }

    private sealed record WriterRunResult(
        ClientId ClientId,
        bool UserScope,
        string TargetPath,
        WriterAction Action,
        string Description);
}

/// <summary>
/// Internal helper to construct a <see cref="CommandLine"/> with just <c>RepoRoot</c> set, used
/// when init delegates to <see cref="ScopesCli.RunInitAsync"/> for multi-solution scaffolding.
/// </summary>
internal static class CommandLineFactoryExtensions
{
    public static CommandLine WithRoot(this CommandLine _, string root)
    {
        // CommandLine's setters are init-only; the cleanest way to "construct with a root" is to
        // round-trip through Parse with a synthetic args array.
        return CommandLine.Parse(new[] { "init-scopes", "--root", root });
    }
}
