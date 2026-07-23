using System.Diagnostics;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Boot-time scope-state reconciliation: cross-references the configured scopes, registry rows,
/// and on-disk DB files; archives orphan DBs to <c>orphans/</c>, marks scopes whose DB file is
/// missing as <c>degraded</c>, and demotes stuck <c>indexing</c> rows from prior crashed
/// processes to <c>degraded</c>. Detection-only — no autonomous rebuild.
///
/// Pulled out of <see cref="LiveIndexService"/> as a standalone helper so the three branches can
/// be exercised in unit tests without standing up the full hosted-service DI graph.
/// </summary>
internal static class BootReconciler
{
    /// <summary>
    /// Run the boot-time reconciliation pass once. Idempotent: re-running against an
    /// already-reconciled tree is a no-op (no orphan files, no missing DBs, no stuck rows).
    /// </summary>
    public static async Task ReconcileAsync(
        IScopeRegistry registry,
        string repoRoot,
        DateTimeOffset processStart,
        ILogger logger,
        CancellationToken ct)
    {
        var registryRows = await registry.ListAsync(ct).ConfigureAwait(false);
        var registryIds = new HashSet<string>(registryRows.Select(r => r.Id), StringComparer.Ordinal);

        // Discover DB files on disk. Missing scopes/ dir is a no-op.
        var scopesDir = ScopeLayout.ScopesDirectory(repoRoot);
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(scopesDir))
        {
            foreach (var dbFile in Directory.EnumerateFiles(scopesDir, "*.db"))
            {
                var id = Path.GetFileNameWithoutExtension(dbFile);
                if (!string.IsNullOrEmpty(id)) fileIds.Add(id);
            }
        }

        // Branch 1 — orphan DB files: in fileIds but not in registry. Move to orphans/<id>-<ts>.db.
        foreach (var id in fileIds.Except(registryIds, StringComparer.Ordinal).ToArray())
        {
            await ArchiveOrphanDbAsync(repoRoot, id, logger).ConfigureAwait(false);
        }

        // Branch 2 — missing DB files: registry has the row but no file on disk. Mark degraded.
        foreach (var row in registryRows.Where(r => !fileIds.Contains(r.Id)).ToArray())
        {
            await MarkScopeDegradedAsync(registry, row,
                "scope DB file missing — call repair_scope or restart",
                healKind: "missing-db-detected",
                logger,
                ct).ConfigureAwait(false);
        }

        // Branch 3 — stuck `indexing`: status="indexing" with last_indexed_at older than this
        // process's start. Always also has a DB file on disk (otherwise branch 2 caught it).
        foreach (var row in registryRows.Where(r =>
            string.Equals(r.Status, "indexing", StringComparison.Ordinal) &&
            r.LastIndexedAt < processStart &&
            fileIds.Contains(r.Id)).ToArray())
        {
            await MarkScopeDegradedAsync(registry, row,
                "previous index interrupted — call repair_scope or restart",
                healKind: "stuck-indexing-detected",
                logger,
                ct).ConfigureAwait(false);
        }
    }

    private static Task ArchiveOrphanDbAsync(string repoRoot, string id, ILogger logger)
    {
        var sw = Stopwatch.StartNew();
        var src = ScopeLayout.ScopeDbPath(repoRoot, id);
        try
        {
            // Re-stat: a delete-during-listing race shouldn't trigger a heal event.
            if (!File.Exists(src)) return Task.CompletedTask;

            var orphansDir = ScopeLayout.OrphansDirectory(repoRoot);
            Directory.CreateDirectory(orphansDir);
            var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
            var dest = Path.Join(orphansDir, $"{id}-{ts}.db");
            File.Move(src, dest);
            // Best-effort: move sidecar -wal and -shm files too if present.
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var s = src + suffix;
                var d = dest + suffix;
                if (File.Exists(s) && !File.Exists(d))
                {
                    try { File.Move(s, d); } catch (IOException) { /* best-effort */ }
                }
            }
            sw.Stop();
            logger.LogInformation("Archived orphan scope DB `{Id}` to {Dest}", id, dest);
            HealLog.Append(kind: "orphan-db-archived", scope: id, ok: true, ms: sw.Elapsed.TotalMilliseconds,
                details: $"moved to orphans/{Path.GetFileName(dest)}");
        }
        catch (FileNotFoundException) { /* delete-during-listing race; silent */ }
        catch (IOException ex) { LogAndEmitFailure(sw, id, ex, logger); }
        catch (UnauthorizedAccessException ex) { LogAndEmitFailure(sw, id, ex, logger); }
        return Task.CompletedTask;
    }

    private static void LogAndEmitFailure(Stopwatch sw, string id, Exception ex, ILogger logger)
    {
        sw.Stop();
        logger.LogWarning(ex, "Failed to archive orphan scope DB `{Id}`", id);
        HealLog.Append(kind: "orphan-db-archived", scope: id, ok: false, ms: sw.Elapsed.TotalMilliseconds,
            details: ex.Message);
    }

    private static async Task MarkScopeDegradedAsync(
        IScopeRegistry registry,
        ScopeRow row,
        string statusMessage,
        string healKind,
        ILogger logger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var degraded = row with { Status = "degraded", StatusMessage = statusMessage };
        try
        {
            await registry.UpsertAsync(degraded, ct).ConfigureAwait(false);
            sw.Stop();
            logger.LogWarning("Boot reconciliation: scope `{Id}` marked degraded — {Reason}", row.Id, statusMessage);
            HealLog.Append(kind: healKind, scope: row.Id, ok: true, ms: sw.Elapsed.TotalMilliseconds, details: statusMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex, "Boot reconciliation: failed to persist degraded row for `{Id}`", row.Id);
            HealLog.Append(kind: healKind, scope: row.Id, ok: false, ms: sw.Elapsed.TotalMilliseconds, details: ex.Message);
        }
    }
}
