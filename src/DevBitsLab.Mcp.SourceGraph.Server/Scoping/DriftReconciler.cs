using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Drift-reconciliation logic. Walks a scope's source tree, compares each file's on-disk SHA-256
/// to the DB's stored value, and applies the symmetric difference (reindex changed + index added
/// + remove vanished). Pulled out as a standalone helper so the comparison logic is unit-testable
/// without standing up a real <see cref="LiveIndexService"/>.
/// </summary>
internal static class DriftReconciler
{
    /// <summary>
    /// Compute the diff between disk and the per-scope DB. Returns the four sets and the total
    /// scanned + a partial flag (true when the walk hit <paramref name="maxFiles"/>).
    /// </summary>
    public static async Task<DriftDiff> ComputeAsync(
        ScopeHost host,
        int maxFiles,
        CancellationToken ct,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        // Phase 1 (0.0): walk the source tree under the scope's root. SourceTreeWalker uses the
        // same exclusion rules as SolutionWatcher (obj/, bin/, .git/, .sourcegraph/) so the file
        // set matches. The walker's HitLimit flag is the authoritative "did the cap actually
        // truncate the result" signal — distinguishes "tree had exactly maxFiles" from "tree had
        // more"; a simple `scanned >= maxFiles` test would incorrectly report partial in the
        // first case. Reported BEFORE the walk so callers see motion during the disk pass.
        progress?.Report(Format.Progress(0.0, "walking source tree"));
        var outcome = await SourceTreeWalker.WalkAsync(
            host.Scope.Root,
            maxFiles,
            host.Scope.ProjectSet.Exclude,
            ct).ConfigureAwait(false);
        var disk = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in outcome.Entries)
        {
            disk[entry.Path] = entry.Sha256;
        }
        var scanned = outcome.Entries.Count;
        var partial = outcome.HitLimit;

        // Phase 2 (0.3): read every (path, sha) row from the DB and compute the diff. Reported
        // BEFORE the DB pass + comparison loop so the checkpoint sequence stays meaningful.
        // Comparing case-insensitive paths to mirror SqliteGraphStore's path-matching elsewhere.
        progress?.Report(Format.Progress(0.3, "comparing hashes"));
        var dbRows = await host.Store.GetAllFileShasAsync(ct).ConfigureAwait(false);
        var db = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dbRows) db[row.Path] = row.ContentSha256;

        var changed = new List<string>();
        var added = new List<string>();
        var removed = new List<string>();
        var unchanged = 0;

        foreach (var (path, sha) in disk)
        {
            if (db.TryGetValue(path, out var dbSha))
            {
                if (ByteArrayEquals(sha, dbSha)) unchanged++;
                else changed.Add(path);
            }
            else
            {
                added.Add(path);
            }
        }
        // Removal detection requires a COMPLETE on-disk file set: if a DB row's path is missing
        // from `disk`, we infer the file was deleted. When `partial = true` the walker stopped
        // before traversing the whole tree, so DB paths beyond the cap are absent from `disk`
        // not because they were deleted but because we never looked. Applying that inferred
        // `removed` set would tell `RoslynIndexer.IndexChangedFilesAsync` to wipe symbols for
        // files that still exist on disk — large parts of the index would silently disappear.
        // Skip removal detection entirely on partial walks; the agent / user can re-run with a
        // larger `max_files` to cover the whole tree.
        if (!partial)
        {
            foreach (var path in db.Keys)
            {
                if (!disk.ContainsKey(path)) removed.Add(path);
            }
        }

        return new DriftDiff(scanned, changed, added, removed, unchanged, partial);
    }

    /// <summary>
    /// Apply <paramref name="diff"/> by feeding the union of changed + added + removed paths to
    /// <see cref="RoslynIndexer.IndexChangedFilesAsync"/> — the same path the watcher uses for
    /// incremental updates. The indexer handles each kind correctly internally: changed files are
    /// re-indexed, removed files (File.Exists == false) trigger the per-file delete path, added
    /// files (if part of the workspace) are indexed.
    /// </summary>
    public static async Task ApplyAsync(ScopeHost host, DriftDiff diff, CancellationToken ct)
    {
        var union = new List<string>(diff.Changed.Count + diff.Added.Count + diff.Removed.Count);
        union.AddRange(diff.Changed);
        union.AddRange(diff.Added);
        union.AddRange(diff.Removed);
        if (union.Count == 0) return;
        await host.Indexer.IndexChangedFilesAsync(union, ct).ConfigureAwait(false);
    }

    private static bool ByteArrayEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>
/// Output of <see cref="DriftReconciler.ComputeAsync"/>. <see cref="Scanned"/> is the total file
/// count from the on-disk walk; <see cref="Partial"/> is true when the walk hit max_files.
/// </summary>
internal sealed record DriftDiff(
    int Scanned,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    int Unchanged,
    bool Partial);
