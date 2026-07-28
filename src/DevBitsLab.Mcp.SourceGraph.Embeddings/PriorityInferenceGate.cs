using System.Diagnostics;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Single-slot async gate that lets interactive query work pass queued background work.
/// An in-flight ONNX call is never preempted; priority is applied when selecting the next call.
/// </summary>
internal sealed class PriorityInferenceGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private int _queryWaiters;
    private long _queryCalls;
    private long _queryWaitTicks;
    private long _backgroundCalls;
    private long _backgroundWaitTicks;

    internal int QueryWaiters => Volatile.Read(ref _queryWaiters);

    internal EmbeddingInferenceStatistics Statistics =>
        new(
            Interlocked.Read(ref _queryCalls),
            TicksToMilliseconds(Interlocked.Read(ref _queryWaitTicks)),
            Interlocked.Read(ref _backgroundCalls),
            TicksToMilliseconds(Interlocked.Read(ref _backgroundWaitTicks)));

    internal async ValueTask<IDisposable> AcquireAsync(
        bool highPriority,
        CancellationToken ct)
    {
        var waitStarted = Stopwatch.GetTimestamp();
        if (highPriority)
        {
            Interlocked.Increment(ref _queryWaiters);
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _queryWaiters);
            }
        }
        else
        {
            while (true)
            {
                while (Volatile.Read(ref _queryWaiters) > 0)
                {
                    await Task.Yield();
                    ct.ThrowIfCancellationRequested();
                }
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                if (Volatile.Read(ref _queryWaiters) == 0) break;
                _gate.Release();
                await Task.Yield();
            }
        }

        var waited = Stopwatch.GetTimestamp() - waitStarted;
        if (highPriority)
        {
            Interlocked.Increment(ref _queryCalls);
            Interlocked.Add(ref _queryWaitTicks, waited);
        }
        else
        {
            Interlocked.Increment(ref _backgroundCalls);
            Interlocked.Add(ref _backgroundWaitTicks, waited);
        }
        return new Releaser(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() =>
            Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
