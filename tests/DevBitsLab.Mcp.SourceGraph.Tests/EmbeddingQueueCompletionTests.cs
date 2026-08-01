using DevBitsLab.Mcp.SourceGraph.Embeddings;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class EmbeddingQueueCompletionTests
{
    [Fact]
    public async Task Queue_doesNotDropRequests_beyondFormerCapacity()
    {
        const int count = 5000;
        var sink = new ChannelEmbeddingsRequestSink();
        for (var i = 1; i <= count; i++)
        {
            sink.Enqueue(new EmbedRequest(i, $"symbol {i}", new byte[32]));
        }

        var drained = 0;
        while (sink.Reader.TryRead(out _)) drained++;
        sink.MarkProcessed(drained);

        drained.Should().Be(count);
        (await sink.WaitForDrainAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Drain_reportsFailure_and_doesNotPublishFalseCompletion()
    {
        var sink = new ChannelEmbeddingsRequestSink();
        sink.Enqueue(new EmbedRequest(1, "symbol", new byte[32]));
        sink.Reader.TryRead(out _).Should().BeTrue();
        sink.MarkProcessed(count: 1, failed: 1);

        (await sink.WaitForDrainAsync()).Should().BeFalse();
    }
}
