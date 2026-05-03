using System.Text;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Helpers used by every scope-aware MCP tool. Centralises the resolution → fan-out → merge
/// pattern so each tool body stays focused on its own SQL shape.
/// </summary>
public static class ScopedExecution
{
    /// <summary>
    /// Resolve <paramref name="scope"/> via the router and short-circuit with an error string when
    /// the resolution fails. <paramref name="onResolved"/> is invoked once per matching scope and
    /// must return a per-scope rendering; the returned strings are concatenated into the response,
    /// each prefixed with a "scope: …" header when more than one host matched.
    /// </summary>
    public static async Task<string> RunAsync(
        ScopeRouter router,
        object? scope,
        Func<ScopeHost, Task<string>> onResolved,
        CancellationToken ct)
    {
        var resolution = router.Resolve(scope);
        if (resolution.IsError) return resolution.ErrorMessage!;

        // Skip degraded scopes silently when the user asked for a wildcard or an explicit list of
        // healthy scopes. When the user explicitly asks for a degraded scope, surface the message.
        var hosts = resolution.Hosts;
        if (hosts.Count == 0)
        {
            return "No scopes matched.";
        }

        // Common shortcut for single-host: no header dance, no merging.
        if (hosts.Count == 1)
        {
            var host = hosts[0];
            if (host.Status == "degraded")
            {
                return $"scope `{host.Scope.Id}` is degraded: {host.StatusMessage ?? "(no message)"}";
            }
            var body = await onResolved(host).ConfigureAwait(false);
            return resolution.IsImplicit
                ? $"_(scope: `{host.Scope.Id}`)_\n\n{body}"
                : body;
        }

        // Multi-host: render each scope's response in turn, prefixed with the scope id. Tools that
        // want a deeper merge (find_definition, search_symbols) use the typed `MergeAsync` helpers
        // below instead.
        var sb = new StringBuilder();
        var results = await Task.WhenAll(hosts.Select(async h =>
        {
            if (h.Status == "degraded")
            {
                return (h.Scope.Id, $"scope is degraded: {h.StatusMessage ?? "(no message)"}");
            }
            try
            {
                var body = await onResolved(h).ConfigureAwait(false);
                return (h.Scope.Id, body);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (h.Scope.Id, $"scope query failed: {ex.Message}");
            }
        })).ConfigureAwait(false);

        foreach (var (id, body) in results)
        {
            sb.AppendLine($"### scope: `{id}`");
            sb.AppendLine();
            sb.AppendLine(body.TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Fan-out the typed <c>SymbolHit</c> query <paramref name="probe"/> across every resolved
    /// scope, then dedup the merged result by <c>canonical_key</c>. Each surviving hit is paired
    /// with the sorted list of scope ids it came from so the renderer can annotate
    /// <c>scope: [...]</c>.
    /// </summary>
    public static async Task<MergedHits<SymbolHit>> FanOutSymbolsAsync(
        IReadOnlyList<ScopeHost> hosts,
        Func<ScopeHost, Task<IReadOnlyList<SymbolHit>>> probe,
        CancellationToken ct)
    {
        var perScope = await Task.WhenAll(hosts.Select(async h =>
        {
            if (h.Status == "degraded")
            {
                return (h, (IReadOnlyList<SymbolHit>)Array.Empty<SymbolHit>());
            }
            var hits = await probe(h).ConfigureAwait(false);
            return (h, hits);
        })).ConfigureAwait(false);

        // Group by canonical_key when present; fall back to (Fqn, Kind, FilePath) for storage rows
        // that didn't carry the key (defensive — the migration path should always populate it).
        var byKey = new Dictionary<string, MergedHit<SymbolHit>>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        foreach (var (host, hits) in perScope)
        {
            foreach (var hit in hits)
            {
                var key = hit.CanonicalKey ?? $"!fqn:{hit.Fqn}|{hit.Kind}|{hit.FilePath}|{hit.StartLine}";
                if (byKey.TryGetValue(key, out var existing))
                {
                    existing.Scopes.Add(host.Scope.Id);
                }
                else
                {
                    var merged = new MergedHit<SymbolHit>(hit, new SortedSet<string>(StringComparer.Ordinal) { host.Scope.Id });
                    byKey[key] = merged;
                    orderedKeys.Add(key);
                }
            }
        }

        return new MergedHits<SymbolHit>(orderedKeys.Select(k => byKey[k]).ToList());
    }

    /// <summary>
    /// Format the trailing scope annotation rendered next to a row when multiple scopes are
    /// queried. Returns <c>"scope: foo"</c> for single-scope rows, <c>"scope: [bar, foo]"</c> for
    /// the merged case, or the empty string when only one host was queried.
    /// </summary>
    public static string ScopeAnnotation(IReadOnlyCollection<string> scopes, int totalHostsQueried)
    {
        if (totalHostsQueried <= 1) return "";
        if (scopes.Count == 1) return $" — scope: `{scopes.First()}`";
        return $" — scope: [{string.Join(", ", scopes.Order().Select(s => $"`{s}`"))}]";
    }
}

public sealed record MergedHit<T>(T Hit, SortedSet<string> Scopes);
public sealed record MergedHits<T>(IReadOnlyList<MergedHit<T>> Rows);
