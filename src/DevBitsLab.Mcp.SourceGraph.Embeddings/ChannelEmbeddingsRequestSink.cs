using System.Threading.Channels;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Channel-backed embed-request queue. The indexer (producer) calls <see cref="Enqueue"/>
/// per (re)indexed symbol; <c>EmbeddingsHostedService</c> (consumer) drains in batches of
/// 16-32, calls the generator, and persists via <c>IEmbeddingsStore</c>. The channel is
/// unbounded so a cold index never silently discards symbols when the ONNX worker falls
/// behind the producer.
/// </summary>
public sealed class ChannelEmbeddingsRequestSink : IEmbeddingsRequestSink
{
    private readonly Channel<EmbedRequest> _channel;
    private long _enqueued;
    private long _completed;
    private long _dropped;

    public ChannelEmbeddingsRequestSink()
    {
        _channel = Channel.CreateUnbounded<EmbedRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public bool IsEnabled => true;

    public void Enqueue(EmbedRequest request)
    {
        Interlocked.Increment(ref _enqueued);
        if (!_channel.Writer.TryWrite(request))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public ChannelReader<EmbedRequest> Reader => _channel.Reader;

    public EmbeddingQueueStatistics Statistics
    {
        get
        {
            var enqueued = Interlocked.Read(ref _enqueued);
            var completed = Interlocked.Read(ref _completed);
            var dropped = Interlocked.Read(ref _dropped);
            return new EmbeddingQueueStatistics(
                Pending: Math.Max(0, enqueued - completed - dropped),
                Completed: completed,
                Dropped: dropped);
        }
    }

    public void MarkCompleted(int count = 1)
    {
        if (count > 0) Interlocked.Add(ref _completed, count);
    }

    public void MarkDropped(int count = 1)
    {
        if (count > 0) Interlocked.Add(ref _dropped, count);
    }

    public void Complete() => _channel.Writer.TryComplete();
}
