namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Resolves the on-disk paths used by the scope-based storage layout. Centralised so the indexer,
/// the registry, and the migrator never disagree about where a file should live.
///
/// Layout:
/// <code>
///   &lt;repo&gt;/.sourcegraph/
///     ├─ _meta.db                  (scope registry, see SqliteScopeRegistry)
///     ├─ scopes/
///     │    ├─ default.db           (per-scope graph data)
///     │    ├─ frontend.db
///     │    └─ ...
///     └─ usage.jsonl               (tool-call log)
/// </code>
///
/// Pre-scoping layout: <c>&lt;repo&gt;/.sourcegraph/graph.db</c>. The migrator detects the legacy
/// file at startup and renames it to <c>scopes/default.db</c>; see
/// <see cref="ScopeLayout.MigrateLegacyDb"/>.
/// </summary>
public static class ScopeLayout
{
    public const string DotDir = ".sourcegraph";
    public const string ScopesDir = "scopes";
    public const string OrphansDir = "orphans";
    public const string MetaDbName = "_meta.db";
    public const string LegacyDbName = "graph.db";

    /// <summary>The <c>.sourcegraph</c> directory under the given repo root.</summary>
    public static string SourcegraphDir(string repoRoot) => Path.Join(repoRoot, DotDir);

    /// <summary>The scope registry DB (<c>_meta.db</c>).</summary>
    public static string MetaDbPath(string repoRoot) => Path.Join(SourcegraphDir(repoRoot), MetaDbName);

    /// <summary>The directory holding per-scope DBs.</summary>
    public static string ScopesDirectory(string repoRoot) => Path.Join(SourcegraphDir(repoRoot), ScopesDir);

    /// <summary>
    /// The directory where archived (orphaned, rebuilt, or corrupt) scope DBs are preserved for
    /// inspection. Created lazily on first archive; never auto-pruned by the server.
    /// </summary>
    public static string OrphansDirectory(string repoRoot) => Path.Join(SourcegraphDir(repoRoot), OrphansDir);

    /// <summary>The DB path for a specific scope id. Caller must validate the id beforehand.</summary>
    public static string ScopeDbPath(string repoRoot, string scopeId) =>
        Path.Join(ScopesDirectory(repoRoot), scopeId + ".db");

    /// <summary>The legacy single-scope graph DB (pre-scoping layout).</summary>
    public static string LegacyDbPath(string repoRoot) => Path.Join(SourcegraphDir(repoRoot), LegacyDbName);

    /// <summary>
    /// One-shot migrator: if the legacy <c>&lt;repo&gt;/.sourcegraph/graph.db</c> exists and
    /// <c>&lt;repo&gt;/.sourcegraph/scopes/default.db</c> does not, atomically move the file to
    /// the new location. No-op when the legacy file is missing or the destination already exists
    /// (i.e. this has already run, or the user started fresh on the new layout).
    ///
    /// Returns <c>true</c> when migration happened, <c>false</c> otherwise. The caller logs.
    /// </summary>
    public static bool MigrateLegacyDb(string repoRoot)
    {
        var legacy = LegacyDbPath(repoRoot);
        if (!File.Exists(legacy)) return false;
        var dest = ScopeDbPath(repoRoot, "default");
        if (File.Exists(dest)) return false;
        Directory.CreateDirectory(ScopesDirectory(repoRoot));
        File.Move(legacy, dest);
        // SQLite WAL/SHM sidecar files: move them too if present so the moved DB stays consistent.
        // sqlite-net leaves -shm and -wal files next to the DB while a writer is open; on the path
        // we land here, the previous server process is no longer holding them open, so a stale pair
        // can be safely renamed alongside the main file.
        foreach (var suffix in new[] { "-shm", "-wal" })
        {
            var src = legacy + suffix;
            var dst = dest + suffix;
            if (File.Exists(src) && !File.Exists(dst))
            {
                try { File.Move(src, dst); }
                catch (IOException) { /* best-effort; sqlite recreates these on next open */ }
            }
        }
        return true;
    }
}
