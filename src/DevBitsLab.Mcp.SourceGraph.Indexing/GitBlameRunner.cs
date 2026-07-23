using System.Diagnostics;
using System.Globalization;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// One blame line in <c>git blame --line-porcelain</c> output, parsed into the three fields the
/// history pipeline needs: commit sha, author name, authored Unix-millisecond timestamp.
/// </summary>
public readonly record struct BlameLine(string CommitSha, string Author, long AuthoredAtUnixMs);

/// <summary>
/// Spawns <c>git blame --line-porcelain --no-progress &lt;path&gt;</c> in the file's directory and
/// turns the output into a list of <see cref="BlameLine"/>. The line-porcelain format repeats a
/// header for every line:
/// <code>
/// &lt;sha&gt; &lt;orig-line&gt; &lt;final-line&gt; [&lt;count&gt;]
/// author Foo Bar
/// author-mail &lt;foo@bar&gt;
/// author-time 1700000000
/// author-tz +0000
/// committer ...
/// summary ...
/// previous ...
/// filename ...
/// \t&lt;raw line content&gt;
/// </code>
/// We index by final-line so each entry in the returned list corresponds to one source line.
/// On any failure we return <see cref="GitBlameResult.Disabled"/> with a reason; the caller is
/// expected to log it once and continue without history. On a successful run the returned list
/// is exactly <c>file_line_count</c> entries long, indexed 0-based.
/// </summary>
public class GitBlameRunner
{
    private readonly ILogger<GitBlameRunner> _logger;
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public GitBlameRunner(ILogger<GitBlameRunner>? logger = null, TimeSpan? timeout = null)
        : this(logger, timeout, Process.Start)
    {
    }

    internal GitBlameRunner(
        ILogger<GitBlameRunner>? logger,
        TimeSpan? timeout,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        _logger = logger ?? NullLogger<GitBlameRunner>.Instance;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    /// <summary>
    /// Run blame against <paramref name="path"/>. The git CWD is forced to the file's directory
    /// so blame runs against the right working tree even when the host process's CWD is
    /// different. Cancellation kills the subprocess.
    /// </summary>
    public virtual Task<GitBlameResult> BlameAsync(string path, CancellationToken ct = default) =>
        BlameCoreAsync(path, repositoryRoot: null, ct);

    /// <summary>
    /// Run blame with an explicit repository privacy boundary. Paths outside
    /// <paramref name="repositoryRoot"/> or matching a mandatory privacy exclusion fail closed
    /// before the file system or a git subprocess is touched.
    /// </summary>
    public virtual Task<GitBlameResult> BlameAsync(
        string path,
        string repositoryRoot,
        CancellationToken ct = default) =>
        BlameCoreAsync(path, repositoryRoot, ct);

    private async Task<GitBlameResult> BlameCoreAsync(
        string path,
        string? repositoryRoot,
        CancellationToken ct)
    {
        if (IsExcluded(path, repositoryRoot))
        {
            return GitBlameResult.Failure("path excluded by privacy policy");
        }

        if (!File.Exists(path))
        {
            return GitBlameResult.Failure($"file not found: {path}");
        }

        var workDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
        var startInfo = new ProcessStartInfo("git", $"blame --line-porcelain --no-progress -- \"{Path.GetFileName(path)}\"")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process process;
        try
        {
            process = _startProcess(startInfo) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "git blame failed to launch (git not on PATH?) for {Path}", path);
            return GitBlameResult.Failure($"git not on PATH or unavailable: {ex.Message}");
        }

        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return GitBlameResult.Failure("git blame timed out");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                // Most common failures: "fatal: not a git repository", "fatal: no such path",
                // both of which we treat as a soft "no history" signal — not an error.
                _logger.LogDebug("git blame exit={Code} stderr={Err}", process.ExitCode, stderr.Trim());
                return GitBlameResult.Failure(stderr.Trim());
            }

            try
            {
                var lines = ParseLinePorcelain(stdout);
                return GitBlameResult.Ok(lines);
            }
            catch (FormatException ex)
            {
                _logger.LogDebug(ex, "git blame output malformed for {Path}", path);
                return GitBlameResult.Failure($"git blame output malformed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Probe whether git is on PATH and the directory is a git working tree. Used at startup to
    /// decide whether to enable the history pipeline at all. Cheap (one <c>rev-parse</c>) and
    /// silent: we never log the user-visible "git not available" — that's the caller's job.
    /// </summary>
    public virtual async Task<bool> IsGitWorkingTreeAsync(string directory, CancellationToken ct = default)
    {
        if (IsExcluded(directory, repositoryRoot: null)) return false;
        if (!Directory.Exists(directory)) return false;
        var startInfo = new ProcessStartInfo("git", "rev-parse --is-inside-work-tree")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process? process;
        try
        {
            process = _startProcess(startInfo);
        }
        catch
        {
            return false;
        }
        if (process is null) return false;
        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return false;
            }
            return process.ExitCode == 0;
        }
    }

    private static bool IsExcluded(string? path, string? repositoryRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            var fullPath = Path.GetFullPath(path);
            var privacyRoot = string.IsNullOrWhiteSpace(repositoryRoot)
                ? Path.GetPathRoot(fullPath)
                : Path.GetFullPath(repositoryRoot);
            return string.IsNullOrWhiteSpace(privacyRoot)
                || new PrivacyPathPolicy(privacyRoot).IsExcluded(fullPath);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
        catch (PathTooLongException)
        {
            return true;
        }
    }

    private static IReadOnlyList<BlameLine> ParseLinePorcelain(string output)
    {
        var result = new List<BlameLine>();
        // git blame --line-porcelain emits one header per line followed by a "\t<source>" payload.
        // We only keep the metadata; the payload is consumed and discarded.
        // Header shape:
        //   <40-hex-sha> <orig-line> <final-line> [<group-count>]
        //   author <name>
        //   author-mail <addr>
        //   author-time <unix-seconds>
        //   author-tz <±zone>
        //   committer <name>
        //   committer-mail ...
        //   committer-time ...
        //   committer-tz ...
        //   summary ...
        //   [previous ...]
        //   filename ...
        //   \t<source>
        // git de-duplicates author/etc. lines for repeat-from-same-blob runs, so once a sha is
        // seen we may only get the first header line + filename + payload for subsequent lines.
        // We track last-seen author/time per sha and reuse them.
        var commitMeta = new Dictionary<string, (string Author, long AuthorUnixMs)>(StringComparer.Ordinal);

        using var reader = new StringReader(output);
        string? sha = null;
        string? author = null;
        long authorUnixMs = 0;
        var pendingMeta = false;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t')
            {
                // Source payload — emit one BlameLine and reset.
                if (sha is null) throw new FormatException("source payload before header");
                if (author is null && commitMeta.TryGetValue(sha, out var cached))
                {
                    author = cached.Author;
                    authorUnixMs = cached.AuthorUnixMs;
                }
                result.Add(new BlameLine(sha, author ?? "(unknown)", authorUnixMs));
                if (pendingMeta && author is not null)
                {
                    commitMeta[sha] = (author, authorUnixMs);
                }
                sha = null;
                author = null;
                authorUnixMs = 0;
                pendingMeta = false;
                continue;
            }

            // Header lines.
            // Sha header has the form: "<40-hex> <orig> <final> [<count>]". We detect by the
            // presence of a space after a 40-char (or 7+, defensive) hex string.
            if (sha is null && IsCommitShaHeader(line))
            {
                var space = line.IndexOf(' ');
                sha = space < 0 ? line : line[..space];
                pendingMeta = true;
                continue;
            }

            if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                author = line["author ".Length..];
            }
            else if (line.StartsWith("author-time ", StringComparison.Ordinal))
            {
                var s = line["author-time ".Length..];
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                {
                    authorUnixMs = seconds * 1000L;
                }
            }
            // Other header fields (author-mail, committer*, summary, previous, filename) are ignored.
        }

        return result;
    }

    private static bool IsCommitShaHeader(string line)
    {
        // First token must be all hex (>= 7 chars; full git sha is 40, but be defensive); then a space.
        var space = line.IndexOf(' ');
        if (space < 7) return false;
        for (var i = 0; i < space; i++)
        {
            var c = line[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }
}

/// <summary>
/// Tagged-union result of a blame run: either a list of per-line records (one per source line),
/// or a failure with a free-text reason. The pipeline treats failure as "this file has no
/// history right now" rather than "the index is broken".
/// </summary>
public sealed record GitBlameResult(IReadOnlyList<BlameLine>? Lines, string? FailureReason)
{
    public static GitBlameResult Ok(IReadOnlyList<BlameLine> lines) => new(lines, null);
    public static GitBlameResult Failure(string reason) => new(null, reason);
    public bool IsSuccess => Lines is not null;
}
