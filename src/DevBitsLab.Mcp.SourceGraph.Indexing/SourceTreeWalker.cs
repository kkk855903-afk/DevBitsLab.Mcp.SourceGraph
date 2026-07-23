using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Walks a directory tree and yields <c>(absolutePath, content_sha256)</c> tuples for every
/// non-ignored file under it. Used by the <c>reconcile_drift</c> tool to compute a comparison
/// set against the per-scope <c>files</c> table without re-implementing path filtering.
///
/// <see cref="ScopePathPolicy"/> is applied before a directory is enumerated or a file is
/// opened, so medical images, patient data, databases, logs, and build output never enter the
/// drift comparison set. Reads each allowed file as bytes and computes SHA-256 in-process; an I/O
/// failure on a single file is swallowed (the file is silently skipped) so a permission hiccup on
/// one entry doesn't poison the whole walk.
/// </summary>
public static class SourceTreeWalker
{
    /// <summary>
    /// Walk <paramref name="root"/> recursively and return up to <paramref name="maxFiles"/>
    /// <c>(path, sha)</c> entries. The walk is stack-based with a per-directory try/catch around
    /// <see cref="Directory.EnumerateFileSystemEntries(string)"/>, so an unreadable subtree
    /// (UnauthorizedAccessException, IOException) is silently skipped rather than aborting the
    /// whole traversal. Per-file read failures are swallowed too — see the type-level remarks.
    ///
    /// <see cref="WalkOutcome.HitLimit"/> distinguishes "tree had exactly maxFiles entries"
    /// (<c>HitLimit = false</c>) from "tree had more than maxFiles entries" (<c>HitLimit = true</c>):
    /// the walker probes one step beyond the cap to detect whether the cap actually truncated the
    /// result. This matters for the <c>reconcile_drift</c> tool, which surfaces a
    /// <c>partial</c> flag in its structured response.
    /// </summary>
    public static async Task<WalkOutcome> WalkAsync(
        string root,
        int maxFiles,
        CancellationToken ct = default) =>
        await WalkAsync(root, maxFiles, Array.Empty<string>(), ct).ConfigureAwait(false);

    /// <summary>
    /// Scope-aware walk that applies <paramref name="excludePatterns"/> in addition to the
    /// mandatory privacy boundary.
    /// </summary>
    public static async Task<WalkOutcome> WalkAsync(
        string root,
        int maxFiles,
        IReadOnlyList<string> excludePatterns,
        CancellationToken ct)
    {
        var entries = new List<FileShaEntry>();
        if (!Directory.Exists(root))
        {
            return new WalkOutcome(entries, HitLimit: false);
        }

        var normalizedRoot = Path.GetFullPath(root);
        var pathPolicy = new ScopePathPolicy(normalizedRoot, excludePatterns);

        // Stack-based DFS so an unreadable subtree fails locally (per-directory catch) instead
        // of aborting the whole traversal — `Directory.EnumerateFiles(SearchOption.AllDirectories)`
        // throws on the offending directory and there's no clean recovery point inside the
        // outer foreach. Stack-based version isolates the failure to the directory that owns it.
        var stack = new Stack<string>();
        stack.Push(normalizedRoot);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (pathPolicy.IsExcluded(dir)) continue;

            IEnumerable<string> children;
            try
            {
                // Materialise inside the try so the enumeration itself runs under the catch.
                children = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();

                bool isDir;
                try { isDir = Directory.Exists(child); }
                catch (IOException) { continue; }

                if (isDir)
                {
                    if (!pathPolicy.IsExcluded(child)) stack.Push(child);
                    continue;
                }

                if (pathPolicy.IsExcluded(child)) continue;

                // Cap probe: we've encountered a non-ignored file we WOULD yield. If we've
                // already produced maxFiles entries, this proves the tree has more than the cap
                // and the result is truncated.
                if (entries.Count >= maxFiles)
                {
                    return new WalkOutcome(entries, HitLimit: true);
                }

                byte[] sha;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(child, ct).ConfigureAwait(false);
                    sha = SHA256.HashData(bytes);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                entries.Add(new FileShaEntry(child, sha));
            }
        }

        return new WalkOutcome(entries, HitLimit: false);
    }
}

/// <summary>One walk entry: the absolute path and the SHA-256 of the file's bytes.</summary>
public sealed record FileShaEntry(string Path, byte[] Sha256);

/// <summary>
/// Result of <see cref="SourceTreeWalker.WalkAsync"/>: the walked entries plus a flag
/// distinguishing "tree fit within the cap" from "tree exceeded the cap and we truncated".
/// </summary>
public sealed record WalkOutcome(IReadOnlyList<FileShaEntry> Entries, bool HitLimit);
