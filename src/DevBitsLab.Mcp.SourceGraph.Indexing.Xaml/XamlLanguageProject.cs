using System.Collections.Generic;
using DevBitsLab.Mcp.SourceGraph.Sdk;

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

    public XamlLanguageProject(
        string projectFilePath,
        IReadOnlyList<string> xamlFilePaths,
        Dictionary<string, ResourceDefinition> resourceCache)
    {
        Id = projectFilePath;
        FilePaths = xamlFilePaths;
        _resourceCache = resourceCache;
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
}
