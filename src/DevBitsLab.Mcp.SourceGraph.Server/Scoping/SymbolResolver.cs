using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

internal sealed record SymbolResolution(
    string Status,
    string Match,
    IReadOnlyList<SymbolHit> Candidates,
    SymbolHit? Selected,
    string? Error = null);

/// <summary>One strict symbol-identity resolver shared by tools that select a single endpoint.</summary>
internal static class SymbolResolver
{
    internal static async Task<SymbolResolution> ResolveAsync(
        IGraphStore store,
        string? query,
        long? symbolId,
        string? fileHint,
        int candidateLimit,
        CancellationToken ct)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var hasId = symbolId is not null;
        if (hasQuery == hasId)
        {
            return new SymbolResolution(
                "invalid",
                "none",
                [],
                null,
                "Provide exactly one of symbol text/canonical key or symbol_id.");
        }
        if (symbolId is <= 0)
        {
            return new SymbolResolution(
                "invalid",
                "none",
                [],
                null,
                "symbol_id must be positive.");
        }
        if (symbolId is { } id)
        {
            var byId = await store.GetSymbolByIdAsync(id, ct).ConfigureAwait(false);
            return byId is null
                ? new SymbolResolution("not_found", "symbol_id", [], null)
                : new SymbolResolution("ok", "symbol_id", [byId], byId);
        }

        var selection = query!.Trim();
        if (CanonicalKeyValidator.IsValid(selection))
        {
            var byKey = await store.GetSymbolByCanonicalKeyAsync(selection, ct)
                .ConfigureAwait(false);
            return byKey is null
                ? new SymbolResolution("not_found", "canonical_key", [], null)
                : new SymbolResolution("ok", "canonical_key", [byKey], byKey);
        }

        var candidates = await store.FindSymbolsAsync(
            selection,
            fileHint,
            candidateLimit,
            ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new SymbolResolution("not_found", "text", candidates, null);
        }
        if (candidates.Count == 1)
        {
            return new SymbolResolution("ok", "unique_text", candidates, candidates[0]);
        }

        var exactFqn = candidates
            .Where(candidate => string.Equals(candidate.Fqn, selection, StringComparison.Ordinal))
            .ToList();
        return exactFqn.Count == 1
            ? new SymbolResolution("ok", "exact_fqn", candidates, exactFqn[0])
            : new SymbolResolution("ambiguous", "text", candidates, null);
    }
}
