using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Implementation of the <c>sourcegraph-mcp doctor</c> subcommand: read-only environment
/// diagnostic. Reports SDK / git / solution / config / per-client-wiring status. Exit codes
/// follow the <c>vocabulary list --strict</c> precedent: 0 on all-pass, 2 on any warn,
/// 1 on any hard fail.
/// </summary>
internal static class DoctorCli
{
    public static async Task<int> RunAsync(CommandLine cli)
    {
        var jsonMode = cli.Json;
        var root = cli.ResolvedRepoRoot();
        var detection = await OnboardingDetector.DetectAsync(root).ConfigureAwait(false);

        var checks = new List<DoctorCheck>();

        // 1. .NET SDK.
        if (string.IsNullOrEmpty(detection.DotnetSdkVersion))
        {
            checks.Add(new("dotnet-sdk", DoctorStatus.Fail,
                "no .NET SDK on PATH (>= 10.0 required); see https://dotnet.microsoft.com/download"));
        }
        else
        {
            var ok = SdkVersionMeetsMin(detection.DotnetSdkVersion, major: 10);
            checks.Add(new("dotnet-sdk", ok ? DoctorStatus.Pass : DoctorStatus.Fail,
                ok ? $".NET SDK {detection.DotnetSdkVersion}"
                   : $".NET SDK {detection.DotnetSdkVersion} is below the required 10.0"));
        }

        // 2. git.
        checks.Add(new("git", detection.GitOnPath ? DoctorStatus.Pass : DoctorStatus.Warn,
            detection.GitOnPath
                ? "git on PATH"
                : "git not on PATH — `who_authored` and `recent_changes` will return empty; pass --no-history to silence"));

        // 3. Repo root readable.
        checks.Add(new("repo-root", Directory.Exists(detection.RepoRootPath) ? DoctorStatus.Pass : DoctorStatus.Fail,
            $"repo root: {detection.RepoRootPath}"));

        // 4. Solution discoverable.
        checks.Add(detection.SolutionFiles.Count > 0
            ? new DoctorCheck("solutions", DoctorStatus.Pass,
                $"discovered {detection.SolutionFiles.Count} solution(s): {string.Join(", ", detection.SolutionFiles.Select(Path.GetFileName))}")
            : new DoctorCheck("solutions", DoctorStatus.Warn,
                "no .slnx/.sln files at repo root — run `sourcegraph-mcp init --solution <path>` if you want to scaffold a config explicitly"));

        // 5. .sourcegraph.json status.
        switch (detection.SourceGraphConfigStatus)
        {
            case SourceGraphConfigStatus.Valid:
                checks.Add(new("sourcegraph-config", DoctorStatus.Pass, ".sourcegraph.json parses cleanly"));
                break;
            case SourceGraphConfigStatus.Missing:
                checks.Add(new("sourcegraph-config", DoctorStatus.Pass, "no .sourcegraph.json (single-scope synth path)"));
                break;
            case SourceGraphConfigStatus.Malformed:
                checks.Add(new("sourcegraph-config", DoctorStatus.Fail,
                    $".sourcegraph.json malformed: {detection.SourceGraphConfigError}"));
                break;
        }

        // 6. Embedding model cache.
        var modelCachePath = ResolveModelCachePath();
        if (Directory.Exists(modelCachePath))
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(modelCachePath, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; }
                    catch (IOException) { /* best-effort: skip unreadable files */ }
                    catch (UnauthorizedAccessException) { /* best-effort */ }
                }
            }
            catch (IOException) { /* best-effort: cache dir disappeared mid-walk */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
            checks.Add(new("embedding-cache", DoctorStatus.Pass,
                $"embedding model cache present at {modelCachePath} ({total / 1024 / 1024} MB)"));
        }
        else
        {
            checks.Add(new("embedding-cache", DoctorStatus.Warn,
                $"embedding model cache absent at {modelCachePath} — `semantic_search` will return its disabled-message until model files are placed there (or pass --no-embeddings to silence)"));
        }

        // 7. Per-scope DB writability.
        var scopeDir = Path.Join(detection.RepoRootPath, ".sourcegraph", "scopes");
        var dbWritable = TestWritability(scopeDir);
        checks.Add(new("db-writable", dbWritable ? DoctorStatus.Pass : DoctorStatus.Fail,
            dbWritable ? $"per-scope DB dir writable: {scopeDir}"
                       : $"per-scope DB dir not writable: {scopeDir}"));

        // 8. Per-client config files. Only existing config files are reported; an absent
        // optional config slot is not a finding.
        foreach (var c in detection.ClientConfigsDetected.Where(x => x.Exists))
        {
            var status = c.ContainsSourcegraphEntry ? DoctorStatus.Pass : DoctorStatus.Warn;
            var msg = c.ContainsSourcegraphEntry
                ? $"{c.Client.ToSlug()} config wired ({(c.IsUserScope ? "user" : "project")}: {c.Path})"
                : $"{c.Client.ToSlug()} config exists but has no sourcegraph entry — run `sourcegraph-mcp init --client {c.Client.ToSlug()}` ({(c.IsUserScope ? "user" : "project")}: {c.Path})";
            checks.Add(new($"client-{c.Client.ToSlug()}", status, msg));
        }

        // Output.
        if (jsonMode)
        {
            EmitJson(checks);
        }
        else
        {
            EmitHuman(checks);
        }

        // Exit code.
        if (checks.Any(c => c.Status == DoctorStatus.Fail)) return 1;
        if (checks.Any(c => c.Status == DoctorStatus.Warn)) return 2;
        return 0;
    }

    private static void EmitHuman(List<DoctorCheck> checks)
    {
        var color = !Environment.GetEnvironmentVariables().Contains("NO_COLOR")
            && !Console.IsOutputRedirected;

        Console.WriteLine("🌿 SourceGraph doctor");
        Console.WriteLine();
        foreach (var c in checks)
        {
            string glyph = c.Status switch
            {
                DoctorStatus.Pass => color ? "✓" : "[OK]",
                DoctorStatus.Warn => color ? "⚠" : "[WARN]",
                DoctorStatus.Fail => color ? "✗" : "[FAIL]",
                _ => "?",
            };
            Console.WriteLine($"  {glyph} {c.Name,-22} {c.Message}");
        }
        Console.WriteLine();
        var passed = checks.Count(c => c.Status == DoctorStatus.Pass);
        var warned = checks.Count(c => c.Status == DoctorStatus.Warn);
        var failed = checks.Count(c => c.Status == DoctorStatus.Fail);
        Console.WriteLine($"summary: {passed} pass, {warned} warn, {failed} fail");
    }

    private static void EmitJson(List<DoctorCheck> checks)
    {
        var exit = checks.Any(c => c.Status == DoctorStatus.Fail) ? 1
            : checks.Any(c => c.Status == DoctorStatus.Warn) ? 2
            : 0;
        var doc = new
        {
            checks = checks.Select(c => new
            {
                name = c.Name,
                status = c.Status switch
                {
                    DoctorStatus.Pass => "pass",
                    DoctorStatus.Warn => "warn",
                    DoctorStatus.Fail => "fail",
                    _ => "unknown",
                },
                message = c.Message,
            }).ToArray(),
            exit_code = exit,
        };
        Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 2,
        }));
    }

    private static bool SdkVersionMeetsMin(string version, int major)
    {
        var firstDot = version.IndexOf('.');
        if (firstDot <= 0) return false;
        return int.TryParse(version.AsSpan(0, firstDot), out var ver) && ver >= major;
    }

    private static string ResolveModelCachePath()
    {
        // Single source of truth: ModelStore.DefaultCacheDir() is a pure calculation that the
        // generator's path resolution also goes through. Calling it directly keeps doctor and
        // the live path in lockstep — duplicating the resolution let an earlier prefix mismatch
        // (`sourcegraph-mcp/models` vs `devbitslab.sourcegraph/models`) make doctor warn even
        // when the cache was actually present.
        return DevBitsLab.Mcp.SourceGraph.Embeddings.ModelStore.DefaultCacheDir();
    }

    private static bool TestWritability(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Join(dir, ".sg-doctor-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record DoctorCheck(string Name, DoctorStatus Status, string Message);
    private enum DoctorStatus { Pass, Warn, Fail }
}
