using System.Threading.Channels;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Channel-backed embed-request queue. The indexer (producer) calls <see cref="Enqueue"/>
/// per (re)indexed symbol; <c>EmbeddingsHostedService</c> (consumer) drains in batches of
/// 16-32, calls the generator, and persists via <c>IEmbeddingsStore</c>. The channel is
/// unbounded so the non-blocking producer never silently loses an early symbol during a large
/// cold-index pass. A processed-work watermark lets the host publish the producer checkpoint
/// only after every accepted request has settled.
/// </summary>
public sealed class ChannelEmbeddingsRequestSink : IEmbeddingsRequestSink
{
    private readonly Channel<EmbedRequest> _channel;
    private readonly object _progressGate = new();
    private TaskCompletionSource<bool> _progress =
        NewProgressSource();
    private long _accepted;
    private long _processed;
    private long _failed;

    public ChannelEmbeddingsRequestSink(bool requiresFullRefresh = false)
    {
        _channel = Channel.CreateUnbounded<EmbedRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        RequiresFullRefresh = requiresFullRefresh;
    }

    public bool IsEnabled => true;
    public bool RequiresFullRefresh { get; }

    public void Enqueue(EmbedRequest request)
    {
        Interlocked.Increment(ref _accepted);
        if (!_channel.Writer.TryWrite(request))
        {
            MarkProcessed(1, 1);
        }
    }

    public ChannelReader<EmbedRequest> Reader => _channel.Reader;

    /// <summary>Marks one settled consumer batch and wakes any producer-checkpoint waiter.</summary>
    public void MarkProcessed(int count, int failed = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(failed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(failed, count);
        if (failed > 0) Interlocked.Add(ref _failed, failed);
        Interlocked.Add(ref _processed, count);
        lock (_progressGate)
        {
            var completed = _progress;
            _progress = NewProgressSource();
            completed.TrySetResult(true);
        }
    }

    /// <summary>
    /// Waits until every request accepted before this call has settled. Returns false when any
    /// request in this sink lifetime failed, so callers do not publish a false-complete marker.
    /// </summary>
    public async Task<bool> WaitForDrainAsync(CancellationToken ct = default)
    {
        var target = Interlocked.Read(ref _accepted);
        while (Interlocked.Read(ref _processed) < target)
        {
            Task pulse;
            lock (_progressGate)
            {
                if (Interlocked.Read(ref _processed) >= target) break;
                pulse = _progress.Task;
            }
            await pulse.WaitAsync(ct).ConfigureAwait(false);
        }
        return Interlocked.Read(ref _failed) == 0;
    }

    public void Complete() => _channel.Writer.TryComplete();

    private static TaskCompletionSource<bool> NewProgressSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
