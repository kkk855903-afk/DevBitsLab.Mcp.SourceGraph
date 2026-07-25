using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

internal static class SymbolQueryResolver
{
    public static async Task<IReadOnlyList<SymbolHit>> ResolveAsync(
        IGraphStore store,
        string query,
        int limit,
        CancellationToken ct,
        string? filePathHint = null)
    {
        var selection = query.Trim();
        if (CanonicalKeyValidator.IsValid(selection))
        {
            var exact = await store.GetSymbolByCanonicalKeyAsync(
                selection,
                ct).ConfigureAwait(false);
            if (exact is null)
            {
                return [];
            }
            if (!string.IsNullOrWhiteSpace(filePathHint)
                && !exact.FilePath.Contains(
                    filePathHint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }
            return [exact];
        }

        return await store.FindSymbolsAsync(
            selection,
            filePathHint,
            limit,
            ct).ConfigureAwait(false);
    }
}
