using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Process-wide registry of <see cref="ScopeHost"/>s plus the resolution rules that turn an MCP
/// tool's optional <c>scope</c> argument into a concrete list of hosts to fan out to.
///
/// Resolution:
/// <list type="bullet">
/// <item>Argument omitted → <c>default_scope</c> if configured; otherwise the only registered
///     scope; otherwise an error pointing at <c>list_scopes</c>.</item>
/// <item>Argument is the literal <c>"*"</c> → every non-isolated registered scope.</item>
/// <item>Argument is a string id → the matching scope (or an error when missing).</item>
/// <item>Argument is a string array → those scopes (or an error when one is missing).</item>
/// </list>
///
/// Isolated scopes only enter the result set when they appear in an explicit list; <c>"*"</c>
/// excludes them by design (the v1 isolation hint, not a security boundary).
/// </summary>
public sealed class ScopeRouter
{
    private readonly Dictionary<string, ScopeHost> _hosts = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private string? _defaultScope;

    /// <summary>The configured <c>default_scope</c> from <c>.sourcegraph.json</c>, if any.</summary>
    public string? DefaultScope
    {
        get { lock (_lock) return _defaultScope; }
    }

    public void SetDefaultScope(string? id)
    {
        lock (_lock) _defaultScope = id;
    }

    public void Register(ScopeHost host)
    {
        lock (_lock) _hosts[host.Scope.Id] = host;
    }

    /// <summary>
    /// Remove the host registered under <paramref name="id"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a host was removed; <c>false</c> when no host was registered under
    /// <paramref name="id"/>. Note that the removed host instance is <em>not</em> returned: the
    /// caller is expected to already hold a reference to it (typically from the diff or
    /// <see cref="TryGet"/> call that prompted the unregister) and is responsible for disposal.
    /// For in-place atomic swaps where the displaced host needs to be captured, use
    /// <see cref="Replace"/> instead, which does return the previous mapping.
    /// </returns>
    public bool Unregister(string id)
    {
        lock (_lock) return _hosts.Remove(id);
    }

    /// <summary>
    /// Atomically replace the host registered under <paramref name="id"/> with
    /// <paramref name="newHost"/>. Returns the displaced host (or <c>null</c> if no host was
    /// previously registered under that id). Performs the swap under a single <c>_lock</c>
    /// acquisition, so any concurrent <see cref="TryGet"/> sees either the old host or the new
    /// host — never <c>null</c> and never two distinct old hosts in succession.
    /// </summary>
    public ScopeHost? Replace(string id, ScopeHost newHost)
    {
        lock (_lock)
        {
            _hosts.TryGetValue(id, out var displaced);
            _hosts[id] = newHost;
            return displaced;
        }
    }

    public bool TryGet(string id, out ScopeHost host)
    {
        lock (_lock) return _hosts.TryGetValue(id, out host!);
    }

    public IReadOnlyList<ScopeHost> All()
    {
        lock (_lock) return _hosts.Values.OrderBy(h => h.Scope.Id, StringComparer.Ordinal).ToList();
    }

    public int Count
    {
        get { lock (_lock) return _hosts.Count; }
    }

    /// <summary>
    /// Resolve the optional <c>scope</c> argument to a concrete list of hosts. The argument may
    /// arrive as a string, a JSON-array of strings, or null (the MCP SDK serialises it that way
    /// when tools accept <c>object?</c>). Errors are returned as the second tuple element so the
    /// tool layer can short-circuit with a well-formed message rather than throw.
    /// </summary>
    public ScopeResolution Resolve(object? scopeArg)
    {
        var ids = ParseScopeArgument(scopeArg);

        lock (_lock)
        {
            if (_hosts.Count == 0)
            {
                return ScopeResolution.Error("No scopes are registered. Run `sourcegraph-mcp init-scopes` to scaffold .sourcegraph.json, or pass --solution to register a single-scope default.");
            }

            // ids null = nothing supplied; pick the default. ids = ["*"] = wildcard. ids ≠ null = explicit.
            if (ids is null)
            {
                if (_defaultScope is not null && _hosts.TryGetValue(_defaultScope, out var def))
                {
                    return ScopeResolution.Ok(new[] { def }, isImplicit: true);
                }
                if (_hosts.Count == 1)
                {
                    return ScopeResolution.Ok(_hosts.Values.ToList(), isImplicit: true);
                }
                return ScopeResolution.Error(
                    $"Multiple scopes are registered ({string.Join(", ", _hosts.Keys.OrderBy(x => x))}) but no `default_scope` is configured. Pass `scope: \"<id>\"` or call `list_scopes` to discover the options.");
            }

            // Wildcard: every non-isolated scope.
            if (ids.Count == 1 && ids[0] == "*")
            {
                var nonIsolated = _hosts.Values.Where(h => !h.Scope.Isolated).ToList();
                if (nonIsolated.Count == 0)
                {
                    return ScopeResolution.Error("`scope = \"*\"` matched no scopes (every registered scope is `isolated: true`). Pass an explicit scope id.");
                }
                return ScopeResolution.Ok(nonIsolated, isImplicit: false);
            }

            // Explicit list: every id must match a registered scope.
            var matched = new List<ScopeHost>(ids.Count);
            var missing = new List<string>();
            foreach (var id in ids)
            {
                if (id == "*")
                {
                    return ScopeResolution.Error("`scope` array cannot mix `\"*\"` with explicit ids.");
                }
                if (_hosts.TryGetValue(id, out var host))
                {
                    matched.Add(host);
                }
                else
                {
                    missing.Add(id);
                }
            }
            if (missing.Count > 0)
            {
                return ScopeResolution.Error(
                    $"Unknown scope(s): {string.Join(", ", missing)}. Registered: {string.Join(", ", _hosts.Keys.OrderBy(x => x))}.");
            }
            return ScopeResolution.Ok(matched, isImplicit: false);
        }
    }

    /// <summary>
    /// Best-effort parse of the MCP tool's <c>scope</c> argument. The MCP SDK delivers JSON values
    /// as <see cref="JsonElement"/>; we also accept plain <see cref="string"/> (single id, the
    /// literal <c>"*"</c>, or a comma-separated list like <c>"frontend,backend"</c>) and string
    /// arrays (defensive — different SDK versions deserialise differently).
    /// </summary>
    /// <returns>
    /// <c>null</c> when no argument was supplied; otherwise the list of ids (which may contain a
    /// single <c>"*"</c> marker for the wildcard case).
    /// </returns>
    private static IReadOnlyList<string>? ParseScopeArgument(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case string s when string.IsNullOrWhiteSpace(s):
                return null;
            case string s:
                return ParseStringForm(s);
            case IEnumerable<string> en:
                {
                    var list = en.Where(x => !string.IsNullOrEmpty(x)).ToList();
                    return list.Count == 0 ? null : list;
                }
            case JsonElement je:
                return ParseJsonElement(je);
            default:
                throw new ArgumentException(
                    $"`scope` argument must be a string, a string array, or `\"*\"`. Got {raw.GetType().Name}.");
        }
    }

    /// <summary>
    /// Parse a string form: single id, the wildcard <c>"*"</c>, or a comma-separated list. Spaces
    /// around the commas are tolerated so an agent can pass <c>"frontend, backend"</c> verbatim.
    /// </summary>
    private static IReadOnlyList<string>? ParseStringForm(string s)
    {
        var trimmed = s.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (!trimmed.Contains(',', StringComparison.Ordinal)) return new[] { trimmed };
        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static IReadOnlyList<string>? ParseJsonElement(JsonElement je)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                {
                    var s = je.GetString();
                    return string.IsNullOrEmpty(s) ? null : new[] { s };
                }
            case JsonValueKind.Array:
                {
                    var ids = new List<string>(je.GetArrayLength());
                    foreach (var item in je.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            throw new ArgumentException("`scope` array entries must be strings.");
                        }
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s)) ids.Add(s);
                    }
                    return ids.Count == 0 ? null : ids;
                }
            default:
                throw new ArgumentException($"`scope` must be string, string[], or null. Got {je.ValueKind}.");
        }
    }
}

/// <summary>
/// Outcome of <see cref="ScopeRouter.Resolve"/>. <see cref="ErrorMessage"/> non-null means the
/// caller should short-circuit with that message; otherwise <see cref="Hosts"/> is the list of
/// scopes to fan out to. <see cref="IsImplicit"/> = true when the scope was inferred from the
/// default rather than an explicit argument; tools surface this in their response to remind the
/// agent which scope they queried.
/// </summary>
public sealed record ScopeResolution(IReadOnlyList<ScopeHost> Hosts, bool IsImplicit, string? ErrorMessage)
{
    public static ScopeResolution Ok(IReadOnlyList<ScopeHost> hosts, bool isImplicit) => new(hosts, isImplicit, null);
    public static ScopeResolution Error(string message) => new(Array.Empty<ScopeHost>(), false, message);

    public bool IsError => ErrorMessage is not null;
}
