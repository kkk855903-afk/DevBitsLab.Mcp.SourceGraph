using System;
using System.Collections.Generic;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Per-scope-registry pool of <see cref="ILanguageProjectFactory"/> instances. The host invokes
/// every registered factory's <c>DiscoverAsync</c> once per scope at startup; the resulting
/// <see cref="ILanguageProject"/> instances feed the per-scope file → project lookup map that the
/// dispatcher uses to populate <see cref="IndexContext.Project"/>.
///
/// <para>Factories are added in order of declaration (built-ins first, then plugin-supplied);
/// duplicate registrations are accepted (each factory contributes its own projects). The only
/// rejection is <c>null</c> — <see cref="Register(ILanguageProjectFactory, PluginRecord?)"/>
/// throws <see cref="ArgumentNullException"/> on a null factory rather than silently dropping it,
/// since a null at the call site usually signals a wiring bug worth surfacing fast.</para>
/// </summary>
public sealed class LanguageProjectFactoryRegistry
{
    private readonly List<(ILanguageProjectFactory Factory, PluginRecord? Owner)> _factories = new();
    private readonly object _lock = new();

    /// <summary>
    /// Add <paramref name="factory"/> to the per-scope pool. Throws
    /// <see cref="ArgumentNullException"/> when <paramref name="factory"/> is <c>null</c>.
    /// </summary>
    public void Register(ILanguageProjectFactory factory, PluginRecord? owner = null)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        lock (_lock)
        {
            _factories.Add((factory, owner));
        }
    }

    public IReadOnlyList<ILanguageProjectFactory> All()
    {
        lock (_lock)
        {
            var result = new List<ILanguageProjectFactory>(_factories.Count);
            foreach (var entry in _factories) result.Add(entry.Factory);
            return result;
        }
    }

    public int Count
    {
        get { lock (_lock) return _factories.Count; }
    }
}
