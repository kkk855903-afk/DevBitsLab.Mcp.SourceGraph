using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Pure helper that diffs the live scope set against a freshly-loaded <see cref="ScopeConfig"/>.
/// Used by <c>LiveIndexService</c> to route a single config save into the four delta kinds it
/// reacts to (add / remove / modify / default-scope) plus a separate flag for plugin changes.
///
/// Equality between two scopes is structural over the authored project-set, isolation, and
/// interop configuration. <c>Root</c> and <c>LastIndexedAt</c> are deliberately ignored:
/// <c>Root</c> is a process-wide constant and <c>LastIndexedAt</c> is a runtime artefact that's
/// never authored in <c>.sourcegraph.json</c>.
/// </summary>
public static class ScopeDiff
{
    public static ScopeDiffResult Compute(
        IReadOnlyList<Scope> currentScopes,
        IReadOnlyList<Scope> newScopes,
        string? currentDefaultScope,
        string? newDefaultScope,
        IReadOnlyList<PluginRef> currentPlugins,
        IReadOnlyList<PluginRef> newPlugins)
    {
        var currentById = currentScopes.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var newById = newScopes.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var added = new List<Scope>();
        var removed = new List<Scope>();
        var modified = new List<ScopeReplacement>();

        foreach (var (id, newScope) in newById)
        {
            if (!currentById.TryGetValue(id, out var oldScope))
            {
                added.Add(newScope);
            }
            else if (!ScopesStructurallyEqual(oldScope, newScope))
            {
                modified.Add(new ScopeReplacement(oldScope, newScope));
            }
        }

        foreach (var (id, oldScope) in currentById)
        {
            if (!newById.ContainsKey(id)) removed.Add(oldScope);
        }

        var defaultChanged = !string.Equals(currentDefaultScope ?? "", newDefaultScope ?? "", StringComparison.Ordinal);
        var pluginsChanged = !PluginsStructurallyEqual(currentPlugins, newPlugins);

        return new ScopeDiffResult(added, removed, modified, defaultChanged, pluginsChanged);
    }

    private static bool ScopesStructurallyEqual(Scope a, Scope b)
    {
        if (a.Isolated != b.Isolated) return false;
        return ProjectSetsEqual(a.ProjectSet, b.ProjectSet)
               && InteropConfigsEqual(a.Interop, b.Interop);
    }

    private static bool ProjectSetsEqual(ScopeProjectSet a, ScopeProjectSet b)
    {
        if (a.GetType() != b.GetType()) return false;
        if (!ListsEqual(a.Exclude, b.Exclude)) return false;
        return (a, b) switch
        {
            (ScopeProjectSet.Solutions x, ScopeProjectSet.Solutions y) => ListsEqual(x.Items, y.Items),
            (ScopeProjectSet.Projects x, ScopeProjectSet.Projects y) => ListsEqual(x.Items, y.Items),
            (ScopeProjectSet.Paths x, ScopeProjectSet.Paths y) => ListsEqual(x.Globs, y.Globs),
            _ => false,
        };
    }

    private static bool InteropConfigsEqual(ScopeInteropConfig? a, ScopeInteropConfig? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (!string.Equals(
                a.Target.RuntimeIdentifier,
                b.Target.RuntimeIdentifier,
                StringComparison.Ordinal)
            || a.Target.Architecture != b.Target.Architecture
            || a.Target.CompilerAbi != b.Target.CompilerAbi
            || a.Target.PointerSizeBytes != b.Target.PointerSizeBytes
            || a.Target.DefaultPack != b.Target.DefaultPack
            || a.TranslationUnits.Count != b.TranslationUnits.Count
            || a.VcxProjects.Count != b.VcxProjects.Count)
        {
            return false;
        }

        for (var i = 0; i < a.TranslationUnits.Count; i++)
        {
            var x = a.TranslationUnits[i];
            var y = b.TranslationUnits[i];
            if (!string.Equals(x.Path, y.Path, StringComparison.Ordinal)
                || !string.Equals(x.Library, y.Library, StringComparison.Ordinal)
                || !string.Equals(x.BinaryPath, y.BinaryPath, StringComparison.Ordinal)
                || !ListsEqual(x.Arguments, y.Arguments))
            {
                return false;
            }
        }

        for (var i = 0; i < a.VcxProjects.Count; i++)
        {
            var x = a.VcxProjects[i];
            var y = b.VcxProjects[i];
            if (!string.Equals(x.Path, y.Path, StringComparison.Ordinal)
                || !string.Equals(
                    x.Configuration,
                    y.Configuration,
                    StringComparison.Ordinal)
                || !string.Equals(
                    x.Platform,
                    y.Platform,
                    StringComparison.Ordinal)
                || !string.Equals(
                    x.Library,
                    y.Library,
                    StringComparison.Ordinal)
                || !string.Equals(
                    x.BinaryPath,
                    y.BinaryPath,
                    StringComparison.Ordinal)
                || !ListsEqual(x.SourceFiles, y.SourceFiles)
                || !ListsEqual(
                    x.AdditionalArguments,
                    y.AdditionalArguments))
            {
                return false;
            }
        }
        return true;
    }

    private static bool PluginsStructurallyEqual(IReadOnlyList<PluginRef> a, IReadOnlyList<PluginRef> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Package, b[i].Package, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].Version, b[i].Version, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].Path, b[i].Path, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool ListsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}

/// <summary>Outcome of <see cref="ScopeDiff.Compute"/>.</summary>
public sealed record ScopeDiffResult(
    IReadOnlyList<Scope> Added,
    IReadOnlyList<Scope> Removed,
    IReadOnlyList<ScopeReplacement> Modified,
    bool DefaultScopeChanged,
    bool PluginsChanged)
{
    public bool HasAny => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0
                          || DefaultScopeChanged || PluginsChanged;

    /// <summary>Concise human-readable summary suitable for a single info-level log line.</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Added.Count > 0) parts.Add("+" + string.Join(",", Added.Select(s => s.Id)));
        if (Removed.Count > 0) parts.Add("-" + string.Join(",", Removed.Select(s => s.Id)));
        if (Modified.Count > 0) parts.Add("~" + string.Join(",", Modified.Select(m => m.New.Id)));
        if (DefaultScopeChanged) parts.Add("default-scope");
        if (PluginsChanged) parts.Add("plugins (restart required)");
        return parts.Count == 0 ? "no-op" : string.Join(" ", parts);
    }
}

public sealed record ScopeReplacement(Scope Old, Scope New);
