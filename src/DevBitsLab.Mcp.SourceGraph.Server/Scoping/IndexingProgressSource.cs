using ModelContextProtocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Per-scope cold-start progress broadcaster. <see cref="LiveIndexService"/> creates one per
/// scope, emits at coarse phase boundaries during the initial index, and tools that block on
/// <see cref="ScopeHost.Ready"/> while indexing is in flight subscribe to forward each event
/// as a wire-level <c>notifications/progress</c> message tagged with the originating
/// <c>tools/call</c> request's <c>progressToken</c>.
/// </summary>
internal interface IIndexingProgressSource
{
    /// <summary>
    /// Fires per phase checkpoint. The broadcaster takes a snapshot of the subscriber list under
    /// its lock and then invokes handlers OUTSIDE the lock, so a slow handler does not block other
    /// subscribers from running for the same emission. Handlers SHOULD still avoid blocking work
    /// because subsequent <c>Emit</c> calls share the same dispatch thread (the indexer's pump).
    /// </summary>
    event Action<ProgressNotificationValue> Reported;

    /// <summary>True once the source has emitted its <c>"ready"</c> terminal event. Subscribers
    /// can gate their subscription off this flag instead of re-checking per emission.</summary>
    bool IsReady { get; }
}

/// <summary>
/// Default broadcasting implementation. Thread-safe subscribe/unsubscribe; <see cref="Emit"/>
/// fires every subscriber's handler in the order they registered. Once <see cref="MarkReady"/>
/// has been invoked, the source becomes a no-op for any subsequent <see cref="Emit"/> call —
/// matches the spec contract that the source emits during initial indexing only and stops
/// after <c>ready</c>.
/// </summary>
internal sealed class IndexingProgressSource : IIndexingProgressSource
{
    private readonly object _lock = new();
    private Action<ProgressNotificationValue>? _handlers;
    private volatile bool _isReady;

    public bool IsReady => _isReady;

    public event Action<ProgressNotificationValue> Reported
    {
        add { lock (_lock) _handlers += value; }
        remove { lock (_lock) _handlers -= value; }
    }

    /// <summary>Emit a progress event to every subscriber. No-op once <see cref="MarkReady"/>
    /// has flipped <see cref="IsReady"/> (terminal-state contract).</summary>
    public void Emit(ProgressNotificationValue value)
    {
        Action<ProgressNotificationValue>? snapshot;
        lock (_lock)
        {
            // Re-check inside the lock so we don't race with `MarkReady` flipping the flag.
            // If MarkReady has already won the lock and set `_isReady = true`, this Emit drops.
            if (_isReady) return;
            snapshot = _handlers;
        }
        snapshot?.Invoke(value);
    }

    /// <summary>Emit the terminal <c>ready</c> event and flip the IsReady flag. Idempotent and
    /// thread-safe: the flag is set inside the same lock that takes the subscriber snapshot, so
    /// any subsequent <see cref="Emit"/> call observes the flag and short-circuits.</summary>
    public void MarkReady()
    {
        Action<ProgressNotificationValue>? snapshot;
        lock (_lock)
        {
            if (_isReady) return;
            // Flip the flag under the lock BEFORE invoking handlers. Any concurrent `Emit`
            // entering the lock after this point sees `_isReady = true` and drops; an Emit
            // already past the flag check (snapshot in hand) may still complete in parallel
            // with our own dispatch — that's an inherent property of out-of-lock invocation.
            _isReady = true;
            snapshot = _handlers;
        }
        snapshot?.Invoke(new ProgressNotificationValue { Progress = 1.0f, Total = 1.0f, Message = "ready" });
    }
}
