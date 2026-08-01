using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Lookup of <c>extension -&gt; <see cref="ILanguageIndexer"/></c>. The built-in
/// <c>RoslynLanguageIndexer</c> is registered automatically at server start; plugin-supplied
/// indexers register here too via <see cref="Register"/>.
///
/// <para>
/// Used by the analyzer pipeline (after the language indexer has produced events for a file the
/// host wants to dispatch via the SDK contract). The .cs path keeps the existing workspace-aware
/// solution walk; non-.cs paths route through this registry to the matching plugin indexer.
/// </para>
/// </summary>
public sealed class LanguageIndexerRegistry
{
    private readonly Dictionary<string, (ILanguageIndexer Indexer, PluginRecord? Owner)> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Register an indexer for every extension it claims. Returns the list of extensions that
    /// could NOT be registered because a previous indexer already claimed them; the caller can
    /// surface this as a warning. Dual-claim is a soft failure here — first-registered wins.
    /// </summary>
    public IReadOnlyList<string> Register(ILanguageIndexer indexer, PluginRecord? owner = null)
    {
        var rejected = new List<string>();
        lock (_lock)
        {
            foreach (var ext in indexer.FileExtensions ?? Array.Empty<string>())
            {
                var key = ext.StartsWith(".", StringComparison.Ordinal) ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
                if (_byExtension.ContainsKey(key))
                {
                    rejected.Add(key);
                    continue;
                }
                _byExtension[key] = (indexer, owner);
            }
        }
        return rejected;
    }

    /// <summary>True when <paramref name="extension"/> has a registered indexer.</summary>
    public bool HasIndexerFor(string extension)
    {
        lock (_lock)
        {
            var key = extension.StartsWith(".", StringComparison.Ordinal) ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
            return _byExtension.ContainsKey(key);
        }
    }

    /// <summary>Look up the indexer + owner record (if any) for <paramref name="extension"/>.</summary>
    public (ILanguageIndexer Indexer, PluginRecord? Owner)? TryGet(string extension)
    {
        lock (_lock)
        {
            var key = extension.StartsWith(".", StringComparison.Ordinal) ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
            return _byExtension.TryGetValue(key, out var hit) ? hit : null;
        }
    }

    /// <summary>
    /// Snapshot of every registered (extension, indexer) pair for diagnostics / CLI. Materialize
    /// it under the lock so plugin registration cannot mutate the dictionary while a caller is
    /// enumerating the diagnostic result.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, ILanguageIndexer>> All()
    {
        lock (_lock)
        {
            return _byExtension.Select(kv => new KeyValuePair<string, ILanguageIndexer>(kv.Key, kv.Value.Indexer)).ToList();
        }
    }

    /// <summary>Total registered extensions (including built-in).</summary>
    public int Count
    {
        get { lock (_lock) return _byExtension.Count; }
    }
}
