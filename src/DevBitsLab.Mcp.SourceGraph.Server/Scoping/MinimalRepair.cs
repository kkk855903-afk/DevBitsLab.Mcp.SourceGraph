using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Minimal-mode repair logic. Pulled out of <see cref="LiveIndexService"/> as a standalone
/// helper so the integrity-check refusal path and the prune-then-reload sequence can be
/// exercised in unit tests without standing up the hosted-service DI graph.
/// </summary>
internal static class MinimalRepair
{
    /// <summary>
    /// Run the minimal-repair sequence against an existing host. Mutates <paramref name="host"/>
    /// status fields and the registry on success/failure; emits <c>embeddings-pruned</c> heal
    /// events on a non-zero prune count. Does NOT emit the <c>repair-scope-invoked</c> heal
    /// event — that's the caller's responsibility (which knows the call origin).
    /// </summary>
    public static async Task<MinimalRepairResult> RunAsync(
        ScopeHost host,
        IScopeRegistry registry,
        IReadOnlyList<TimeSpan> reloadBackoffs,
        ILogger logger,
        CancellationToken ct,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        // Phase 1 (0.0): integrity check. Reported BEFORE the call so the caller's chat panel
        // shows motion during the slow integrity-check pass. Corrupt DB short-circuits to
        // "use rebuild" so we never run prune / reload against unreliable btree pages.
        progress?.Report(Format.Progress(0.0, "running integrity_check"));
        var integrity = await host.Store.IntegrityCheckAsync(ct).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            return new MinimalRepairResult(Refused: true, IntegrityCheck: integrity, PrunedEmbeddings: 0,
                Reindexed: false,
                Message: $"integrity_check failed: {integrity}; call repair_scope mode=rebuild");
        }

        // Phase 2 (0.5): prune orphan embeddings. Cheap (one DELETE) but reported so the
        // checkpoint sequence stays monotonically increasing.
        progress?.Report(Format.Progress(0.5, "pruning orphan embeddings"));
        var pruned = await host.EmbeddingsStore.PruneOrphanedAsync(ct).ConfigureAwait(false);
        if (pruned > 0)
        {
            HealLog.Append(kind: "embeddings-pruned", scope: host.Scope.Id, ok: true, ms: 0,
                details: $"removed {pruned} orphan rows");
        }

        // Phase 3 (0.8): workspace reload + index_all. Re-attempt under bounded retry. If the
        // scope didn't open during initial bring-up (no SolutionPath), skip — there's nothing
        // to reload.
        progress?.Report(Format.Progress(0.8, "reopening workspace"));
        var reindexed = false;
        if (!string.IsNullOrEmpty(host.SolutionPath))
        {
            try
            {
                await WorkspaceOpenRetry.RunAsync(
                    host.Scope.Id,
                    _ => Task.CompletedTask, // workspace already open; skip the open phase
                    tk => host.Indexer.ReloadAndIndexAllAsync(tk),
                    reloadBackoffs,
                    logger,
                    ct).ConfigureAwait(false);
                reindexed = true;
                host.LastIndexedAt = DateTimeOffset.UtcNow;
                host.Status = "ok";
                await registry.UpsertAsync(ToRow(host, "ok", null), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scope `{Id}`: minimal repair reindex failed after retries", host.Scope.Id);
                host.Status = "degraded";
                host.StatusMessage = ex.Message;
                await registry.UpsertAsync(ToRow(host, "degraded", ex.Message), ct).ConfigureAwait(false);
                return new MinimalRepairResult(Refused: false, IntegrityCheck: integrity, PrunedEmbeddings: pruned,
                    Reindexed: false, Message: $"workspace reload failed after retries: {ex.Message}");
            }
        }

        return new MinimalRepairResult(Refused: false, IntegrityCheck: integrity, PrunedEmbeddings: pruned,
            Reindexed: reindexed,
            Message: reindexed
                ? $"ok; pruned {pruned} orphan embeddings; reopened workspace"
                : $"ok; pruned {pruned} orphan embeddings; no solution to reload");
    }

    private static ScopeRow ToRow(ScopeHost host, string status, string? statusMessage) =>
        new(
            Id: host.Scope.Id,
            Name: host.Scope.Name,
            Root: host.Scope.Root,
            ProjectSetJson: ScopeProjectSetSerialiser.Serialise(host.Scope.ProjectSet),
            Isolated: host.Scope.Isolated,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: status,
            StatusMessage: statusMessage);
}
