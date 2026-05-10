namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Process-wide handle to the in-flight model-download task. Components that need the embedding
/// model to be present on disk before they probe <see cref="ICodeEmbeddingGenerator.IsAvailable"/>
/// (which is sticky — it caches the answer of its first cache check) await <see cref="Ready"/>
/// at startup.
///
/// <para>
/// Usage shape:
/// <list type="bullet">
///   <item>Server startup spawns <c>ModelStore.EnsureAsync(...)</c> as a fire-and-don't-await
///         task and wraps the resulting <see cref="Task"/> in a <see cref="ModelDownloadGate"/>
///         singleton.</item>
///   <item>The indexer's per-scope bring-up <c>await</c>s <see cref="Ready"/> before checking
///         <see cref="ICodeEmbeddingGenerator.IsAvailable"/>, closing the race where the
///         generator would otherwise see a missing file and disable itself permanently.</item>
///   <item>The hosted embed-drain worker also awaits <see cref="Ready"/> before its
///         <c>IsAvailable</c> probe — defense in depth for paths that bypass the indexer
///         (one-shot <c>index</c> CLI, future direct-embed callers).</item>
/// </list>
/// </para>
///
/// <para>
/// When the download is bypassed entirely (<c>--no-embeddings</c>, or <c>--no-model-download</c>
/// against an empty cache), use <see cref="Open"/> to construct an already-completed gate so
/// awaiting code unblocks immediately.
/// </para>
/// </summary>
public sealed class ModelDownloadGate
{
    /// <summary>Task that completes when the auto-download has finished (success or failure)
    /// or has been bypassed.
    /// <para>
    /// Awaiters can rely on this transitioning to <see cref="TaskStatus.RanToCompletion"/> for
    /// every non-cancellation outcome — the producer (the gate factory) catches all download
    /// failures (<see cref="ModelDownloadException"/>, IO/permission errors, unexpected runtime
    /// faults) and logs them at warning before the task settles. The only way this task ends in
    /// <see cref="TaskStatus.Canceled"/> or <see cref="TaskStatus.Faulted"/> is if cooperative
    /// cancellation propagates through the producer; the awaiting code paths
    /// (<c>LiveIndexService.OpenScopeAsync</c>, <c>EmbeddingsHostedService.ExecuteAsync</c>)
    /// honour the same <c>CancellationToken</c> so a cancelled gate is the expected shape during
    /// shutdown.
    /// </para></summary>
    public Task Ready { get; }

    public ModelDownloadGate(Task ready)
    {
        Ready = ready;
    }

    /// <summary>Pre-completed gate used when no download is in flight (--no-embeddings or
    /// --no-model-download with empty cache). Awaiting <see cref="Ready"/> returns synchronously.</summary>
    public static ModelDownloadGate Open() => new(Task.CompletedTask);
}
