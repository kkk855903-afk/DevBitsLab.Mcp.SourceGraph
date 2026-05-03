using System.Threading.Channels;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// One file's worth of history work: re-blame the file and update <c>symbol_history</c> for
/// every symbol whose span lives in it. The dispatcher (the indexer wrapper) drops these into
/// <see cref="HistoryQueue.Writer"/> on every (re)indexed file.
/// </summary>
public readonly record struct HistoryRequest(long FileId, string Path, byte[] ContentSha256);

/// <summary>
/// Singleton bag for the bounded async channel that ferries history work between the indexer
/// and the background <see cref="HistoryHostedService"/>. Drop the request from the producer side
/// (indexer) and consume on the host side. Bounded to keep memory finite during a cold-index
/// burst; full-channel writes block, which is fine because blame is fast and the indexer is
/// I/O-bound anyway.
/// </summary>
public sealed class HistoryQueue
{
    public Channel<HistoryRequest> Channel { get; } = System.Threading.Channels.Channel
        .CreateBounded<HistoryRequest>(new BoundedChannelOptions(capacity: 4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelWriter<HistoryRequest> Writer => Channel.Writer;
    public ChannelReader<HistoryRequest> Reader => Channel.Reader;
}

/// <summary>
/// Server-wide config knobs for the history pipeline. <see cref="Disabled"/> = true means the
/// pipeline never runs (CLI <c>--no-history</c>, or auto-detected when git isn't on PATH /
/// the indexed solution doesn't sit in a git working tree).
/// </summary>
public sealed record HistoryOptions(bool Disabled);

/// <summary>
/// Background consumer of <see cref="HistoryQueue"/>. For each request:
/// 1. Compare the file's <c>content_sha256</c> with the cached <c>blamed_content_sha</c> on every
///    symbol in that file. If they all match, skip the run entirely (cache hit).
/// 2. Otherwise run <c>git blame --line-porcelain</c> via <see cref="GitBlameRunner"/>.
/// 3. For each indexed symbol in the file, pick the most-recent commit in its
///    <c>start_line..end_line</c> span and persist as a <see cref="SymbolHistory"/> row keyed by
///    symbol id.
/// On any failure we log once and skip the file; <c>symbol_history</c> simply doesn't pick up the
/// row, which is a harmless degraded state for downstream tools.
/// </summary>
public sealed class HistoryHostedService : BackgroundService
{
    private readonly HistoryQueue _queue;
    private readonly IGraphStore _store;
    private readonly GitBlameRunner _blamer;
    private readonly HistoryOptions _options;
    private readonly ILogger<HistoryHostedService> _logger;
    private bool _warnedDisabled;

    public HistoryHostedService(
        HistoryQueue queue,
        IGraphStore store,
        GitBlameRunner blamer,
        HistoryOptions options,
        ILogger<HistoryHostedService> logger)
    {
        _queue = queue;
        _store = store;
        _blamer = blamer;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    /// <summary>
    /// Same loop as the hosted-service ExecuteAsync, exposed publicly so the one-shot
    /// <c>index</c> CLI can drive the pipeline without the BackgroundService.StopAsync race
    /// (which would cancel mid-drain). Pass <see cref="CancellationToken.None"/> for the
    /// natural-completion case; the loop exits when the channel writer is completed.
    /// </summary>
    public async Task ExecuteAsyncForOneShot(CancellationToken stoppingToken) => await RunAsync(stoppingToken).ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_options.Disabled)
        {
            _logger.LogInformation("History pipeline disabled (--no-history or git unavailable); symbol_history will remain empty.");
            // Still drain the channel so producers don't block on writes if disabled flips later.
            await foreach (var _ in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // discard
            }
            return;
        }

        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(request, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "History pipeline failed for {Path}; skipping", request.Path);
            }
        }
    }

    private async Task ProcessAsync(HistoryRequest req, CancellationToken ct)
    {
        var spans = await _store.GetSymbolSpansForFileAsync(req.FileId, ct).ConfigureAwait(false);
        if (spans.Count == 0) return;

        // Cache check: if every symbol in this file already has a blamed_content_sha matching the
        // file's current content_sha, there's nothing to do.
        var cached = await _store.GetBlamedShasForFileAsync(req.FileId, ct).ConfigureAwait(false);
        var allCached = spans.Count > 0 && spans.All(s =>
            cached.TryGetValue(s.SymbolId, out var sha) && sha is not null && sha.AsSpan().SequenceEqual(req.ContentSha256));
        if (allCached) return;

        var blame = await _blamer.BlameAsync(req.Path, ct).ConfigureAwait(false);
        if (!blame.IsSuccess)
        {
            if (!_warnedDisabled)
            {
                _logger.LogInformation("git blame unavailable ({Reason}); symbol_history rows will be sparse.", blame.FailureReason);
                _warnedDisabled = true;
            }
            return;
        }

        var lines = blame.Lines!;
        if (lines.Count == 0) return;

        foreach (var span in spans)
        {
            ct.ThrowIfCancellationRequested();
            // start_line/end_line are 1-based; line list is 0-based. Clamp to the parsed range
            // (mismatches happen when the file changed on disk between our SHA read and git's
            // blame walk — rare but harmless to handle).
            var start = Math.Max(1, span.StartLine);
            var end = Math.Min(lines.Count, span.EndLine);
            if (start > end) continue;

            BlameLine mostRecent = lines[start - 1];
            for (var i = start; i < end; i++)
            {
                var l = lines[i];
                if (l.AuthoredAtUnixMs > mostRecent.AuthoredAtUnixMs)
                {
                    mostRecent = l;
                }
            }

            var history = new SymbolHistory(
                SymbolId: span.SymbolId,
                LastCommitSha: mostRecent.CommitSha,
                LastAuthor: mostRecent.Author,
                LastAuthoredAt: mostRecent.AuthoredAtUnixMs == 0
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(mostRecent.AuthoredAtUnixMs),
                LineCount: end - start + 1,
                BlamedContentSha: req.ContentSha256);

            await _store.UpsertSymbolHistoryAsync(history, ct).ConfigureAwait(false);
        }
    }
}
