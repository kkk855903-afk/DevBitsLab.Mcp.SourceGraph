using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Removes rows that were indexed under an older, wider scope boundary. This must run before
/// any source-file read so adding an exclude or tightening the privacy policy cannot leave old
/// symbols queryable from the per-scope database.
/// </summary>
internal static class ExcludedFilePurger
{
    public static async Task<int> PurgeAsync(ScopeHost host, CancellationToken ct)
    {
        var policy = new ScopePathPolicy(
            Path.GetFullPath(host.Scope.Root),
            host.Scope.ProjectSet.Exclude);
        var indexedFiles = await host.Store.GetAllFilesAsync(ct).ConfigureAwait(false);
        var deleted = 0;

        foreach (var file in indexedFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!policy.IsExcluded(file.Path)) continue;

            // Source-generated documents intentionally use the narrower generated-document
            // boundary: compiler output under obj/bin is allowed, while privacy directories,
            // sensitive extensions, out-of-root paths, and configured excludes still fail
            // closed. Mirror the indexer's policy so a cold start does not erase valid generated
            // symbols merely because their synthetic path lives below obj/.
            if (await host.Store.IsGeneratedFileAsync(file.Id, ct).ConfigureAwait(false)
                && !policy.IsGeneratedDocumentExcluded(file.Path))
            {
                continue;
            }

            if (await host.Store.DeleteFileAsync(file.Id, ct).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return deleted;
    }
}
