using System.Threading.Channels;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Channel-backed embed-request queue. The indexer (producer) calls <see cref="Enqueue"/>
/// per (re)indexed symbol; <c>EmbeddingsHostedService</c> (consumer) drains in batches of
/// 16-32, calls the generator, and persists via <c>IEmbeddingsStore</c>. The channel is
/// bounded with <c>DropOldest</c> full-mode so the indexer never blocks even if the worker
/// momentarily falls behind during a big cold-index pass.
/// </summary>
public sealed class ChannelEmbeddingsRequestSink : IEmbeddingsRequestSink
{
    private readonly Channel<EmbedRequest> _channel;

    public ChannelEmbeddingsRequestSink(int capacity = 4096)
    {
        _channel = Channel.CreateBounded<EmbedRequest>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    public bool IsEnabled => true;

    public void Enqueue(EmbedRequest request)
    {
        // Bounded + DropOldest: TryWrite never blocks and never returns false for our config,
        // but we honour the boolean anyway in case the channel is shut down on host stop.
        _ = _channel.Writer.TryWrite(request);
    }

    public ChannelReader<EmbedRequest> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();
}
