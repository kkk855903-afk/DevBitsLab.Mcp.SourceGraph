using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Plugin-private <see cref="ILanguageProject"/> for one <c>.csproj</c> that contains XAML files.
/// Holds the absolute project file path as <see cref="Id"/>, every <c>.xaml</c> file the project
/// owns, and an immutable <see cref="ResourceCache"/> snapshot populated from <c>App.xaml</c>,
/// same-project <c>MergedDictionaries</c>, and theme <c>Generic.xaml</c>. Per-document indexer
/// calls reuse the snapshot for resource-resolution lookups
/// (<c>{StaticResource AccentBrush}</c> → declaration site); an explicit rebuild atomically
/// refreshes it after an incremental edit.
/// </summary>
public sealed class XamlLanguageProject : IDeclarationFirstLanguageProject
{
    private IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> _resourceCache;
    private readonly Func<IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>>? _resourceCacheBuilder;
    private readonly Func<Project?>? _roslynProjectProvider;

    public XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        Dictionary<string, ResourceDefinition> resourceCache)
        : this(
            projectFilePath,
            xamlFilePaths,
            new ReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>(
                resourceCache.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<ResourceDefinition>)new[] { pair.Value },
                    StringComparer.Ordinal)),
            resourceCacheBuilder: null,
            roslynProjectProvider: null)
    {
    }

    internal XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> resourceCache,
        Func<IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>>? resourceCacheBuilder,
        Func<Project?>? roslynProjectProvider)
    {
        Id = projectFilePath;
        FilePaths = xamlFilePaths;
        _resourceCache = resourceCache;
        _resourceCacheBuilder = resourceCacheBuilder;
        _roslynProjectProvider = roslynProjectProvider;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> FilePaths { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> DeclarationFilePaths
    {
        get
        {
            var cache = Volatile.Read(ref _resourceCache);
            var ownedPaths = new HashSet<string>(
                FilePaths,
                StringComparer.OrdinalIgnoreCase);
            return cache.Values
                .SelectMany(candidates => candidates)
                .Select(definition => definition.FilePath)
                .Where(ownedPaths.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Resource cascade keyed by <c>x:Key</c>. Each value retains every visible declaration;
    /// callers use <see cref="ResolveResource"/> so duplicate declarations remain ambiguous.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> ResourceCache =>
        Volatile.Read(ref _resourceCache);

    /// <summary>
    /// Resolves <paramref name="key"/> against the current project snapshot. Duplicate visible
    /// declarations are reported as ambiguous; discovery order never silently selects a winner.
    /// </summary>
    public ResourceResolution ResolveResource(string key)
    {
        if (string.IsNullOrEmpty(key)) return ResourceResolution.Missing;
        var cache = Volatile.Read(ref _resourceCache);
        if (!cache.TryGetValue(key, out var candidates) || candidates.Count == 0)
        {
            return ResourceResolution.Missing;
        }
        return candidates.Count == 1
            ? new ResourceResolution(ResourceResolutionStatus.Resolved, candidates)
            : new ResourceResolution(ResourceResolutionStatus.Ambiguous, candidates);
    }

    /// <summary>
    /// Atomically rebuilds the project resource snapshot from the same scope-filtered XAML file
    /// set used during discovery. Hosts SHALL call this after an in-scope XAML resource file
    /// changes; callers already resolving against the old immutable snapshot complete safely.
    /// </summary>
    public void RebuildResourceCache()
    {
        if (_resourceCacheBuilder is null) return;
        var rebuilt = _resourceCacheBuilder();
        Interlocked.Exchange(ref _resourceCache, rebuilt);
    }

    /// <summary>
    /// Returns the compilation from the host's current privacy-sanitized Roslyn solution.
    /// The provider is evaluated per call so live C# edits are not pinned to the project
    /// snapshot that happened to exist when XAML discovery first ran.
    /// </summary>
    internal async Task<Compilation?> GetCompilationAsync(CancellationToken ct)
    {
        var project = _roslynProjectProvider?.Invoke();
        return project is null
            ? null
            : await project.GetCompilationAsync(ct).ConfigureAwait(false);
    }
}
