using System.Diagnostics;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

internal sealed record PrewarmAttempt(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Re-invokes the current server for <c>init --prewarm</c>. A process that starts but exits
/// non-zero is a failed strategy, so later launch strategies are still attempted.
/// </summary>
internal static class PrewarmLauncher
{
    public static IReadOnlyList<PrewarmAttempt> BuildAttempts(
        string solutionPath,
        string? processPath = null,
        string? serverAssemblyPath = null)
    {
        processPath ??= Environment.ProcessPath;
        serverAssemblyPath ??= typeof(InitCli).Assembly.Location;

        var attempts = new List<PrewarmAttempt>();
        if (!string.IsNullOrWhiteSpace(processPath) && !IsDotnetHost(processPath))
        {
            attempts.Add(new PrewarmAttempt(processPath, ["index", solutionPath]));
        }
        else if (!string.IsNullOrWhiteSpace(processPath)
            && !string.IsNullOrWhiteSpace(serverAssemblyPath))
        {
            attempts.Add(new PrewarmAttempt(
                processPath,
                [serverAssemblyPath, "index", solutionPath]));
        }

        attempts.Add(new PrewarmAttempt("sourcegraph-mcp", ["index", solutionPath]));
        attempts.Add(new PrewarmAttempt(
            "dotnet",
            ["tool", "run", "sourcegraph-mcp", "--", "index", solutionPath]));

        return attempts
            .DistinctBy(
                attempt => $"{attempt.FileName}\0{string.Join('\0', attempt.Arguments)}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<int?> RunAttemptsAsync(
        IEnumerable<PrewarmAttempt> attempts,
        Func<PrewarmAttempt, Task<int?>> runner)
    {
        int? lastExitCode = null;
        foreach (var attempt in attempts)
        {
            var exitCode = await runner(attempt).ConfigureAwait(false);
            if (exitCode == 0) return 0;
            if (exitCode.HasValue) lastExitCode = exitCode;
        }
        return lastExitCode;
    }

    public static async Task<int?> RunProcessAsync(PrewarmAttempt attempt)
    {
        var startInfo = new ProcessStartInfo(attempt.FileName)
        {
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        foreach (var argument in attempt.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsDotnetHost(string path) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(path),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
}
