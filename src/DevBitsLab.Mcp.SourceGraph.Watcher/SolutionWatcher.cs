using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Watcher;

/// <summary>
/// Watches a solution directory for .cs file changes and the git HEAD ref for branch switches.
/// Coalesces raw events with a debounce window and emits batched <see cref="FileChangeBatch"/>
/// values via <see cref="ReadAllAsync"/>.
/// </summary>
public sealed class SolutionWatcher : IAsyncDisposable
{
    private readonly string _root;
    private readonly TimeSpan _debounce;
    private readonly ILogger<SolutionWatcher> _logger;
    private readonly FileSystemWatcher _csWatcher;
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

    public SolutionWatcher(string solutionDirectory, TimeSpan? debounce = null, ILogger<SolutionWatcher>? logger = null)
    {
        _root = Path.GetFullPath(solutionDirectory);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(200);
        _logger = logger ?? NullLogger<SolutionWatcher>.Instance;

        _csWatcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = "*.cs",
        };
        _csWatcher.Changed += OnFileEvent;
        _csWatcher.Created += OnFileEvent;
        _csWatcher.Deleted += OnFileEvent;
        _csWatcher.Renamed += OnFileRenamed;
        _csWatcher.EnableRaisingEvents = true;

        var gitDir = Path.Combine(_root, ".git");
        if (Directory.Exists(gitDir))
        {
            _gitHeadWatcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                Filter = "HEAD",
            };
            _gitHeadWatcher.Changed += OnGitHeadEvent;
            _gitHeadWatcher.EnableRaisingEvents = true;
        }

        _processor = Task.Run(() => ProcessAsync(_cts.Token));
    }

    /// <summary>Returns an async stream of debounced change batches. Stops when disposed.</summary>
    public IAsyncEnumerable<FileChangeBatch> ReadAllAsync(CancellationToken ct = default) =>
        _batches.Reader.ReadAllAsync(ct);

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _raw.Writer.TryWrite(new RawEvent(e.FullPath, FileChangeReason.FileSystemEvent));
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!ShouldIgnore(e.OldFullPath)) _raw.Writer.TryWrite(new RawEvent(e.OldFullPath, FileChangeReason.FileSystemEvent));
        if (!ShouldIgnore(e.FullPath)) _raw.Writer.TryWrite(new RawEvent(e.FullPath, FileChangeReason.FileSystemEvent));
    }

    private void OnGitHeadEvent(object sender, FileSystemEventArgs e)
    {
        _raw.Writer.TryWrite(new RawEvent(string.Empty, FileChangeReason.GitHeadChanged));
    }

    private static bool ShouldIgnore(string path)
    {
        if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return true;
        if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return true;
        if (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return true;
        if (path.Contains($"{Path.DirectorySeparatorChar}.sourcegraph{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return true;
        return false;
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
        _csWatcher.EnableRaisingEvents = false;
        _csWatcher.Dispose();
        _gitHeadWatcher?.Dispose();
        _cts.Cancel();
        try { await _processor.ConfigureAwait(false); } catch { /* shutting down */ }
        _cts.Dispose();
    }

    private readonly record struct RawEvent(string Path, FileChangeReason Reason);
}
