using System.Collections.Concurrent;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class PriorityInferenceGateTests
{
    [Fact]
    public async Task WaitingQueryRunsBeforeQueuedBackgroundBatch()
    {
        using var gate = new PriorityInferenceGate();
        using var first = await gate.AcquireAsync(highPriority: false, CancellationToken.None);
        var order = new ConcurrentQueue<string>();

        var background = Task.Run(async () =>
        {
            using var lease = await gate.AcquireAsync(
                highPriority: false,
                CancellationToken.None);
            order.Enqueue("background");
        });
        var query = Task.Run(async () =>
        {
            using var lease = await gate.AcquireAsync(
                highPriority: true,
                CancellationToken.None);
            order.Enqueue("query");
        });

        await WaitUntilAsync(() => gate.QueryWaiters == 1);
        first.Dispose();
        await Task.WhenAll(query, background).WaitAsync(TimeSpan.FromSeconds(5));

        order.Should().Equal("query", "background");
        gate.Statistics.QueryCalls.Should().Be(1);
        gate.Statistics.BackgroundCalls.Should().Be(2);
        gate.Statistics.QueryWaitMs.Should().BeGreaterThan(0);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }
            await Task.Delay(5);
        }
    }
}
