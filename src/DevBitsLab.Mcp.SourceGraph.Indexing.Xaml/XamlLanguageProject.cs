using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Plugin-private <see cref="ILanguageProject"/> for one <c>.csproj</c> that contains XAML files.
/// Holds the absolute project file path as <see cref="Id"/>, every <c>.xaml</c> file the project
/// owns, and a precomputed <see cref="ResourceCache"/> populated from <c>App.xaml</c>,
/// <c>MergedDictionaries</c>, and theme <c>Generic.xaml</c>. The cache is populated once at
/// project discovery; per-document indexer calls reuse it for resource-resolution lookups
/// (<c>{StaticResource AccentBrush}</c> → declaration site) without re-walking the cascade per
/// file.
/// </summary>
public sealed class XamlLanguageProject : ILanguageProject
{
    private readonly Dictionary<string, ResourceDefinition> _resourceCache;
    private readonly Func<Project?>? _roslynProjectProvider;

    public XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        Dictionary<string, ResourceDefinition> resourceCache)
        : this(projectFilePath, xamlFilePaths, resourceCache, roslynProjectProvider: null)
    {
    }

    internal XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        Dictionary<string, ResourceDefinition> resourceCache,
        Func<Project?>? roslynProjectProvider)
    {
        Id = projectFilePath;
        FilePaths = xamlFilePaths;
        _resourceCache = resourceCache;
        _roslynProjectProvider = roslynProjectProvider;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> FilePaths { get; }

    /// <summary>
    /// Resource cascade keyed by <c>x:Key</c>. A static-resource lookup against this dictionary
    /// returns the declaration site or null when the resource isn't visible from this project's
    /// cascade (cross-project references are an open question — see design.md).
    /// </summary>
    public IReadOnlyDictionary<string, ResourceDefinition> ResourceCache => _resourceCache;

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
