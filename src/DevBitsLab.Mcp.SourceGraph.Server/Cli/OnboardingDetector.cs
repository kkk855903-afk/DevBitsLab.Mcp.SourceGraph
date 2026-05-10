using System.Diagnostics;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Read-only environment + repo detection used by <c>init</c>, <c>doctor</c>, and the README's
/// onboarding path. Never writes; results feed into per-client config writers and into the
/// interactive picker. See <see cref="OnboardingDetectionResult"/> for the shape returned.
/// </summary>
internal static class OnboardingDetector
{
    /// <summary>
    /// Detects state at <paramref name="root"/>: SDK version, git availability, the solution
    /// files at root and one level deep, the parse status of <c>.sourcegraph.json</c> (if any),
    /// and the per-client config files that already exist on disk.
    /// </summary>
    public static async Task<OnboardingDetectionResult> DetectAsync(string root, CancellationToken ct = default)
    {
        var rooted = Path.GetFullPath(root);

        var sdkTask = DetectDotnetSdkAsync(ct);
        var gitTask = DetectGitOnPathAsync(ct);

        var solutions = DiscoverSolutions(rooted);
        var (sgStatus, sgError) = DetectSourceGraphConfigStatus(rooted);
        var clients = DetectClientConfigs(rooted);

        var sdk = await sdkTask.ConfigureAwait(false);
        var git = await gitTask.ConfigureAwait(false);

        return new OnboardingDetectionResult(
            DotnetSdkVersion: sdk,
            GitOnPath: git,
            RepoRootPath: rooted,
            SolutionFiles: solutions,
            SourceGraphConfigStatus: sgStatus,
            SourceGraphConfigError: sgError,
            ClientConfigsDetected: clients);
    }

    private static IReadOnlyList<string> DiscoverSolutions(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<string>();
        // Root and one level deep — matching ScopeConfigLoader.DiscoverSolutionSiblings's range.
        // `seen.Add` is idempotent and side-effect-safe, so using it as the Where predicate
        // doubles as the dedup pass: only first-seen paths are added to `hits`.
        foreach (var pattern in new[] { "*.slnx", "*.sln" })
        {
            hits.AddRange(
                Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
                    .Where(seen.Add));
            foreach (var dir in SafeEnumerateTopDirs(root))
            {
                hits.AddRange(SafeEnumerateFiles(dir, pattern).Where(seen.Add));
            }
        }
        hits.Sort(StringComparer.Ordinal);
        return hits;
    }

    private static IReadOnlyList<string> SafeEnumerateTopDirs(string root)
    {
        // Materialise inside the try so exceptions thrown during enumeration (not just at
        // call time) are caught — `Directory.EnumerateDirectories` is lazy and can throw
        // UnauthorizedAccessException / IOException on the iterator's MoveNext, which would
        // bypass a return-only catch clause.
        try { return Directory.EnumerateDirectories(root).ToArray(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private static (SourceGraphConfigStatus, string?) DetectSourceGraphConfigStatus(string root)
    {
        var path = Path.Join(root, ScopeConfigLoader.FileName);
        if (!File.Exists(path)) return (SourceGraphConfigStatus.Missing, null);
        try
        {
            _ = ScopeConfigLoader.Load(root);
            return (SourceGraphConfigStatus.Valid, null);
        }
        catch (ScopeConfigException ex)
        {
            return (SourceGraphConfigStatus.Malformed, ex.Message);
        }
    }

    private static IReadOnlyList<DetectedClientConfig> DetectClientConfigs(string root)
    {
        var hits = new List<DetectedClientConfig>();
        foreach (var (client, projectPath) in ProjectConfigPaths(root))
        {
            var exists = File.Exists(projectPath);
            var hasEntry = exists && FileContainsSourcegraphEntry(client, projectPath);
            hits.Add(new DetectedClientConfig(client, projectPath, IsUserScope: false, exists, hasEntry));
        }
        foreach (var (client, userPath) in UserConfigPaths())
        {
            var exists = File.Exists(userPath);
            var hasEntry = exists && FileContainsSourcegraphEntry(client, userPath);
            hits.Add(new DetectedClientConfig(client, userPath, IsUserScope: true, exists, hasEntry));
        }
        return hits;
    }

    private static IEnumerable<(ClientId Client, string Path)> ProjectConfigPaths(string root)
    {
        yield return (ClientId.ClaudeCode, Path.Join(root, ".mcp.json"));
        yield return (ClientId.Copilot, Path.Join(root, ".vscode", "mcp.json"));
        yield return (ClientId.Cursor, Path.Join(root, ".cursor", "mcp.json"));
        yield return (ClientId.Continue, Path.Join(root, ".continue", "mcp", "sourcegraph.yaml"));
    }

    private static IEnumerable<(ClientId Client, string Path)> UserConfigPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(home)) yield break;
        yield return (ClientId.ClaudeCode, Path.Join(home, ".claude", ".mcp.json"));
        yield return (ClientId.Cursor, Path.Join(home, ".cursor", "mcp.json"));
        yield return (ClientId.Continue, Path.Join(home, ".continue", "mcp", "sourcegraph.yaml"));
        // Claude Desktop is user-scope only and platform-specific.
        var desktop = ClaudeDesktopUserPath(home);
        if (!string.IsNullOrEmpty(desktop)) yield return (ClientId.ClaudeDesktop, desktop);
    }

    /// <summary>
    /// Per-OS Claude Desktop config path: %APPDATA%\Claude\ on Windows,
    /// ~/Library/Application Support/Claude/ on macOS, ~/.config/Claude/ elsewhere.
    /// </summary>
    public static string ClaudeDesktopUserPath(string home)
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            return string.IsNullOrEmpty(appData)
                ? string.Empty
                : Path.Join(appData, "Claude", "claude_desktop_config.json");
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Join(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        }
        return Path.Join(home, ".config", "Claude", "claude_desktop_config.json");
    }

    private static bool FileContainsSourcegraphEntry(ClientId client, string path)
    {
        try
        {
            // Continue uses YAML; the rest are JSON. We only need a coarse "is the entry there?"
            // check here, not byte-exact equivalence. The writers do that at plan time.
            if (client == ClientId.Continue)
            {
                var text = File.ReadAllText(path);
                return text.Contains("name: sourcegraph", StringComparison.Ordinal)
                    || text.Contains("name: 'sourcegraph'", StringComparison.Ordinal)
                    || text.Contains("name: \"sourcegraph\"", StringComparison.Ordinal);
            }
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var key = client == ClientId.Copilot ? "servers" : "mcpServers";
            return root.TryGetProperty(key, out var servers)
                && servers.ValueKind == JsonValueKind.Object
                && servers.TryGetProperty("sourcegraph", out _);
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<string?> DetectDotnetSdkAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<bool> DetectGitOnPathAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>
/// Snapshot of detected state at a repo root. All collections are empty (never null) when nothing
/// matched. Path strings are absolute.
/// </summary>
internal sealed record OnboardingDetectionResult(
    string? DotnetSdkVersion,
    bool GitOnPath,
    string RepoRootPath,
    IReadOnlyList<string> SolutionFiles,
    SourceGraphConfigStatus SourceGraphConfigStatus,
    string? SourceGraphConfigError,
    IReadOnlyList<DetectedClientConfig> ClientConfigsDetected);

/// <summary>
/// Outcome of parsing the repo's <c>.sourcegraph.json</c>. <see cref="Missing"/> means no file at
/// all (single-solution synth path is fine); <see cref="Malformed"/> carries an error message in
/// <see cref="OnboardingDetectionResult.SourceGraphConfigError"/>.
/// </summary>
internal enum SourceGraphConfigStatus { Missing, Valid, Malformed }

/// <summary>
/// One observed MCP-client config slot. <see cref="IsUserScope"/> distinguishes a project-scoped
/// path under the repo root from a user-scoped path under the home directory. Multiple entries
/// for the same <see cref="ClientId"/> can appear when both project and user paths exist.
/// </summary>
internal sealed record DetectedClientConfig(
    ClientId Client,
    string Path,
    bool IsUserScope,
    bool Exists,
    bool ContainsSourcegraphEntry);

/// <summary>
/// First-class MCP clients the onboarding flow knows how to write configs for. Listed in the
/// order they appear in the picker.
/// </summary>
internal enum ClientId
{
    ClaudeCode,
    Copilot,
    Cursor,
    Continue,
    ClaudeDesktop,
}

internal static class ClientIdExtensions
{
    public static string ToSlug(this ClientId id) => id switch
    {
        ClientId.ClaudeCode => "claude-code",
        ClientId.Copilot => "copilot",
        ClientId.Cursor => "cursor",
        ClientId.Continue => "continue",
        ClientId.ClaudeDesktop => "claude-desktop",
        _ => id.ToString().ToLowerInvariant(),
    };

    public static bool TryParseSlug(string slug, out ClientId id)
    {
        switch (slug)
        {
            case "claude-code": id = ClientId.ClaudeCode; return true;
            case "copilot": id = ClientId.Copilot; return true;
            case "cursor": id = ClientId.Cursor; return true;
            case "continue": id = ClientId.Continue; return true;
            case "claude-desktop": id = ClientId.ClaudeDesktop; return true;
            default: id = default; return false;
        }
    }
}
