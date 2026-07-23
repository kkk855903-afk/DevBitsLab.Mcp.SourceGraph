using System.Diagnostics;
using System.Threading.Channels;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Watcher;

/// <summary>
/// Watches a solution directory for source file changes and the git HEAD ref for branch switches.
/// Coalesces raw events with a debounce window and emits batched <see cref="FileChangeBatch"/>
/// values via <see cref="ReadAllAsync"/>.
/// </summary>
public sealed class SolutionWatcher : IAsyncDisposable
{
    private static readonly string[] _defaultSourceExtensions = [".cs", ".xaml"];

    private readonly string _root;
    private readonly TimeSpan _debounce;
    private readonly ILogger<SolutionWatcher> _logger;
    private readonly ScopePathPolicy _pathPolicy;
    private readonly string _policyRoot;
    private readonly ScopeProjectSet? _projectSet;
    private readonly object _projectSetMatcherLock = new();
    private ScopeProjectSetPathMatcher? _projectSetMatcher;
    private readonly HashSet<string> _sourceExtensions;
    private readonly FileSystemWatcher _sourceWatcher;
    private readonly FileSystemWatcher? _gitHeadWatcher;
    private readonly Channel<RawEvent> _raw = Channel.CreateUnbounded<RawEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Channel<FileChangeBatch> _batches = Channel.CreateUnbounded<FileChangeBatch>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processor;

    public SolutionWatcher(
        string solutionDirectory,
        TimeSpan? debounce = null,
        ILogger<SolutionWatcher>? logger = null,
        IEnumerable<string>? sourceExtensions = null)
        : this(
            solutionDirectory,
            debounce,
            logger,
            sourceExtensions,
            Array.Empty<string>())
    {
    }

    public SolutionWatcher(
        string solutionDirectory,
        TimeSpan? debounce,
        ILogger<SolutionWatcher>? logger,
        IEnumerable<string>? sourceExtensions,
        IReadOnlyList<string>? excludePatterns)
        : this(
            solutionDirectory,
            debounce,
            logger,
            sourceExtensions,
            excludePatterns,
            solutionDirectory)
    {
    }

    public SolutionWatcher(
        string solutionDirectory,
        TimeSpan? debounce,
        ILogger<SolutionWatcher>? logger,
        IEnumerable<string>? sourceExtensions,
        IReadOnlyList<string>? excludePatterns,
        string policyRoot,
        ScopeProjectSet? projectSet = null)
    {
        _root = Path.GetFullPath(solutionDirectory);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(200);
        _logger = logger ?? NullLogger<SolutionWatcher>.Instance;
        var normalizedPolicyRoot = Path.GetFullPath(policyRoot);
        _policyRoot = normalizedPolicyRoot;
        _projectSet = projectSet;
        _pathPolicy = new ScopePathPolicy(normalizedPolicyRoot, excludePatterns);
        _projectSetMatcher = projectSet is null
            ? null
            : new ScopeProjectSetPathMatcher(normalizedPolicyRoot, projectSet);
        var privacyPathPolicy = new PrivacyPathPolicy(normalizedPolicyRoot);
        _sourceExtensions = NormalizeSourceExtensions(sourceExtensions);
        _sourceExtensions.RemoveWhere(extension =>
            privacyPathPolicy.IsExcluded(Path.Join(_root, $"source{extension}")));
        if (projectSet is not null)
        {
            // Project files are control-plane events, not language-indexer input. Keeping the
            // extension in the watcher filter ensures create/delete/change can refresh the
            // positive matcher even when no indexer claims .csproj.
            _sourceExtensions.Add(".csproj");
        }
        if (_sourceExtensions.Count == 0)
        {
            throw new ArgumentException(
                "At least one source extension not excluded by the privacy policy is required.",
                nameof(sourceExtensions));
        }

        _sourceWatcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _sourceWatcher.Filters.Clear();
        foreach (var extension in _sourceExtensions)
        {
            _sourceWatcher.Filters.Add($"*{extension}");
        }
        _sourceWatcher.Changed += OnFileEvent;
        _sourceWatcher.Created += OnFileEvent;
        _sourceWatcher.Deleted += OnFileEvent;
        _sourceWatcher.Renamed += OnFileRenamed;
        _sourceWatcher.EnableRaisingEvents = true;

        var gitHeadDir = ResolveGitHeadDir(_root, _logger);
        if (gitHeadDir is not null)
        {
            _gitHeadWatcher = new FileSystemWatcher(gitHeadDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                Filter = "HEAD",
            };
            _gitHeadWatcher.Changed += OnGitHeadEvent;
            _gitHeadWatcher.Created += OnGitHeadEvent;
            _gitHeadWatcher.Renamed += (s, e) => OnGitHeadEvent(s, e);
            _gitHeadWatcher.EnableRaisingEvents = true;
            _logger.LogInformation("Watching git HEAD at {Path}", Path.Join(gitHeadDir, "HEAD"));
        }

        _processor = Task.Run(() => ProcessAsync(_cts.Token));
    }

    /// <summary>
    /// Resolve the directory whose <c>HEAD</c> file represents the current branch for the given
    /// solution root. In a normal checkout this is <c>&lt;root&gt;/.git</c>. In a git worktree,
    /// <c>&lt;root&gt;/.git</c> is a file containing <c>gitdir: &lt;path&gt;</c> that points at
    /// <c>&lt;main-repo&gt;/.git/worktrees/&lt;name&gt;</c>; that's where the worktree's HEAD lives.
    /// Returns <c>null</c> if the path is neither a git directory nor a worktree pointer.
    /// </summary>
    internal static string? ResolveGitHeadDir(string solutionRoot, ILogger logger)
    {
        var dotGit = Path.Join(solutionRoot, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;

        try
        {
            var content = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (!content.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var dir = content[prefix.Length..].Trim();
            if (!Path.IsPathRooted(dir))
            {
                dir = Path.GetFullPath(Path.Join(solutionRoot, dir));
            }
            return Directory.Exists(dir) ? dir : null;
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not resolve git worktree HEAD location for {Root}", solutionRoot);
            return null;
        }
    }

    /// <summary>Returns an async stream of debounced change batches. Stops when disposed.</summary>
    public IAsyncEnumerable<FileChangeBatch> ReadAllAsync(CancellationToken ct = default) =>
        _batches.Reader.ReadAllAsync(ct);

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsTrackedSourcePath(e.FullPath)) return;
        _raw.Writer.TryWrite(new RawEvent(e.FullPath, FileChangeReason.FileSystemEvent));
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsTrackedSourcePath(e.OldFullPath))
        {
            _raw.Writer.TryWrite(new RawEvent(e.OldFullPath, FileChangeReason.FileSystemEvent));
        }
        if (IsTrackedSourcePath(e.FullPath))
        {
            _raw.Writer.TryWrite(new RawEvent(e.FullPath, FileChangeReason.FileSystemEvent));
        }
    }

    private void OnGitHeadEvent(object sender, FileSystemEventArgs e)
    {
        _raw.Writer.TryWrite(new RawEvent(string.Empty, FileChangeReason.GitHeadChanged));
    }

    private bool IsTrackedSourcePath(string path)
    {
        if (_pathPolicy.IsExcluded(path)) return false;

        var matcher = Volatile.Read(ref _projectSetMatcher);
        if (string.Equals(
                Path.GetExtension(path),
                ".csproj",
                StringComparison.OrdinalIgnoreCase))
        {
            if (matcher?.IsProjectAnchorCandidate(path) != true) return false;

            // The matcher snapshots existing project anchors. Refresh it synchronously before
            // queueing the control event so a subsequent source event observes the new boundary.
            RefreshProjectSetMatcher();
            return true;
        }

        return _sourceExtensions.Contains(Path.GetExtension(path))
            && (matcher?.Includes(path) ?? true);
    }

    private void RefreshProjectSetMatcher()
    {
        if (_projectSet is null) return;
        lock (_projectSetMatcherLock)
        {
            Volatile.Write(
                ref _projectSetMatcher,
                new ScopeProjectSetPathMatcher(_policyRoot, _projectSet));
        }
    }

    private static HashSet<string> NormalizeSourceExtensions(IEnumerable<string>? sourceExtensions)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in sourceExtensions ?? _defaultSourceExtensions)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Source extensions cannot contain blank values.", nameof(sourceExtensions));
            }

            var extension = value.Trim();
            if (!extension.StartsWith('.'))
            {
                extension = $".{extension}";
            }
            if (extension.IndexOfAny(['*', '?', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new ArgumentException(
                    $"Source extension '{value}' must be an extension such as '.cs'.",
                    nameof(sourceExtensions));
            }

            extensions.Add(extension);
        }

        if (extensions.Count == 0)
        {
            throw new ArgumentException("At least one source extension is required.", nameof(sourceExtensions));
        }

        return extensions;
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        var pendingFs = new HashSet<string>(StringComparer.Ordinal);
        var pendingGit = false;
        var lastEventTime = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            // wait for first event
            RawEvent first;
            try
            {
                first = await _raw.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Apply(first);
            lastEventTime.Restart();

            // collect more until quiet for _debounce
            while (lastEventTime.Elapsed < _debounce)
            {
                var remaining = _debounce - lastEventTime.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(remaining);
                try
                {
                    var ev = await _raw.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                    Apply(ev);
                    lastEventTime.Restart();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (pendingGit)
            {
                _logger.LogDebug("Emitting GitHeadChanged batch");
                await _batches.Writer.WriteAsync(
                    new FileChangeBatch(Array.Empty<string>(), FileChangeReason.GitHeadChanged), ct).ConfigureAwait(false);
                pendingGit = false;
            }
            if (pendingFs.Count > 0)
            {
                _logger.LogDebug("Emitting FileSystemEvent batch ({Count} paths)", pendingFs.Count);
                await _batches.Writer.WriteAsync(
                    new FileChangeBatch(pendingFs.ToArray(), FileChangeReason.FileSystemEvent), ct).ConfigureAwait(false);
                pendingFs.Clear();
            }
        }

        _batches.Writer.TryComplete();

        void Apply(RawEvent ev)
        {
            if (ev.Reason == FileChangeReason.GitHeadChanged) pendingGit = true;
            else if (!string.IsNullOrEmpty(ev.Path)) pendingFs.Add(ev.Path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sourceWatcher.EnableRaisingEvents = false;
        _sourceWatcher.Dispose();
        _gitHeadWatcher?.Dispose();
        _cts.Cancel();
        try { await _processor.ConfigureAwait(false); } catch { /* shutting down */ }
        _cts.Dispose();
    }

    private readonly record struct RawEvent(string Path, FileChangeReason Reason);
}
