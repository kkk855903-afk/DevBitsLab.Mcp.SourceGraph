using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// <see cref="ILanguageProjectFactory"/> for the in-tree XAML indexer. Walks every
/// <c>.csproj</c> under the repo root, locates the <c>.xaml</c> files belonging to each project
/// (via <c>&lt;Page&gt;</c> / <c>&lt;ApplicationDefinition&gt;</c> / <c>&lt;EmbeddedResource&gt;</c>
/// / <c>&lt;Resource&gt;</c> items unioned with a complete policy-pruned directory scan), and
/// builds a two-pass per-project resource catalog. Pass one parses every allowed
/// project XAML document into declarations plus merged-dictionary links; pass two walks the
/// cascades rooted at <c>App.xaml</c> and <c>Themes/Generic.xaml</c>. Relative
/// <c>MergedDictionaries Source=</c> links are followed only when their physical target is an
/// allowed XAML file owned by the same project.
/// </summary>
public sealed class XamlLanguageProjectFactory : IExclusionAwareLanguageProjectFactory
{
    private readonly Func<Solution?>? _solutionProvider;
    private readonly Action<XamlDiscoveryAccess, string>? _beforeAccess;

    public XamlLanguageProjectFactory()
    {
    }

    /// <summary>
    /// Creates a factory backed by the host's current privacy-sanitized Roslyn solution.
    /// The callback is intentionally lazy so live solution snapshots remain current.
    /// </summary>
    public XamlLanguageProjectFactory(Func<Solution?> solutionProvider)
    {
        _solutionProvider = solutionProvider
            ?? throw new ArgumentNullException(nameof(solutionProvider));
    }

    internal XamlLanguageProjectFactory(
        Action<XamlDiscoveryAccess, string> beforeAccess)
    {
        _beforeAccess = beforeAccess
            ?? throw new ArgumentNullException(nameof(beforeAccess));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> ProjectMarkers { get; } = new[] { "*.csproj", "*.xaml" };

    /// <inheritdoc />
    public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
        string repoRoot,
        CancellationToken ct) =>
        DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
        string repoRoot,
        IReadOnlyList<string> excludePatterns,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(repoRoot)) throw new ArgumentException("repoRoot must be non-empty", nameof(repoRoot));

        var root = Path.GetFullPath(repoRoot);
        var pathPolicy = new ScopePathPolicy(root, excludePatterns);
        var projects = new List<ILanguageProject>();
        // Use an explicit directory walker so excluded subtrees are pruned before they are read.
        // Any non-excluded read failure must abort the discovery pass: returning a partial project
        // set would let the dispatcher replace a previously complete per-scope project map.
        foreach (var csproj in EnumerateCsprojFiles(root, pathPolicy, ct))
        {
            ct.ThrowIfCancellationRequested();
            var project = BuildProject(csproj, pathPolicy, ct);
            if (project is not null) projects.Add(project);
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ILanguageProject>>(projects);
    }

    /// <summary>
    /// Walk <paramref name="root"/> for <c>.csproj</c> files. Skips <c>bin/</c>, <c>obj/</c>,
    /// <c>node_modules/</c>, <c>.git/</c>, and <c>.sourcegraph/</c> wholesale (matching the
    /// dispatcher's own walker) so build outputs and vendored sources don't pollute project
    /// discovery. Enumeration failures propagate so callers cannot mistake an incomplete walk
    /// for a complete project map.
    /// </summary>
    private IEnumerable<string> EnumerateCsprojFiles(
        string root,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        var stack = new Stack<string>();
        var visitedPhysicalDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (pathPolicy.IsExcludedForDiscovery(dir, out var physicalDirectory))
            {
                continue;
            }
            if (physicalDirectory is null
                || !visitedPhysicalDirectories.Add(physicalDirectory))
            {
                continue;
            }

            foreach (var entry in EnumerateDirectoryEntries(
                         dir,
                         XamlDiscoveryAccess.EnumerateProjectEntries,
                         ct))
            {
                ct.ThrowIfCancellationRequested();
                if (pathPolicy.IsExcludedForDiscovery(entry)) continue;

                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                ct.ThrowIfCancellationRequested();
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                }
                else if (string.Equals(
                             Path.GetExtension(entry),
                             ".csproj",
                             StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    private IReadOnlyList<string> EnumerateDirectoryEntries(
        string directory,
        XamlDiscoveryAccess access,
        CancellationToken ct)
    {
        BeforeAccess(access, directory, ct);
        var entries = Directory
            .EnumerateFileSystemEntries(directory)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        ct.ThrowIfCancellationRequested();
        return entries;
    }

    private void BeforeAccess(
        XamlDiscoveryAccess access,
        string path,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _beforeAccess?.Invoke(access, path);
        ct.ThrowIfCancellationRequested();
    }

    private XamlLanguageProject? BuildProject(
        string csprojPath,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        var xamlFiles = EnumerateXamlFiles(
            csprojPath,
            projectDir,
            pathPolicy,
            ct);
        if (xamlFiles.Count == 0) return null;

        ct.ThrowIfCancellationRequested();
        var resourceCache = BuildResourceCache(xamlFiles, pathPolicy, ct);
        ct.ThrowIfCancellationRequested();
        var fullProjectPath = Path.GetFullPath(csprojPath);
        Func<Project?>? roslynProjectProvider = _solutionProvider is null
            ? null
            : () => FindRoslynProject(_solutionProvider(), fullProjectPath);
        return new XamlLanguageProject(
            fullProjectPath,
            xamlFiles,
            resourceCache,
            () => BuildResourceCache(
                xamlFiles,
                pathPolicy,
                CancellationToken.None),
            roslynProjectProvider);
    }

    private static Project? FindRoslynProject(Solution? solution, string projectFilePath)
    {
        if (solution is null) return null;

        foreach (var project in solution.Projects)
        {
            if (string.IsNullOrEmpty(project.FilePath)) continue;
            string candidate;
            try
            {
                candidate = Path.GetFullPath(project.FilePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (string.Equals(candidate, projectFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the <c>.xaml</c> files that belong to the project. Explicit
    /// <c>&lt;Page&gt;</c> / <c>&lt;ApplicationDefinition&gt;</c> items are unioned with a complete
    /// policy-pruned recursive scan. An <c>Update</c> item only changes metadata for an SDK default
    /// item and therefore can never be treated as the complete project file set.
    /// </summary>
    private IReadOnlyList<string> EnumerateXamlFiles(
        string csprojPath,
        string projectDir,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ReadXamlItemsFromCsproj(
                     csprojPath,
                     projectDir,
                     pathPolicy,
                     ct))
        {
            results.Add(item);
        }

        // Always walk the project directory for *.xaml. Privacy-excluded directories are pruned
        // before enumeration, so markup outputs and medical data are never opened.
        var stack = new Stack<string>();
        var visitedPhysicalDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        stack.Push(projectDir);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (pathPolicy.IsExcludedForDiscovery(dir, out var physicalDirectory))
            {
                continue;
            }
            if (physicalDirectory is null
                || !visitedPhysicalDirectories.Add(physicalDirectory))
            {
                continue;
            }

            foreach (var entry in EnumerateDirectoryEntries(
                         dir,
                         XamlDiscoveryAccess.EnumerateXamlEntries,
                         ct))
            {
                ct.ThrowIfCancellationRequested();
                if (pathPolicy.IsExcludedForDiscovery(entry)) continue;

                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                ct.ThrowIfCancellationRequested();
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                }
                else if (string.Equals(
                             Path.GetExtension(entry),
                             ".xaml",
                             StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(Path.GetFullPath(entry));
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        return results
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<string> ReadXamlItemsFromCsproj(
        string csprojPath,
        string projectDir,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        var results = new List<string>();
        BeforeAccess(XamlDiscoveryAccess.ReadProjectFile, csprojPath, ct);
        using var stream = File.OpenRead(csprojPath);
        ct.ThrowIfCancellationRequested();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(stream, settings);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!reader.Read()) break;
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (!IsXamlItemElement(reader.LocalName)) continue;
            var include = reader.GetAttribute("Include") ?? reader.GetAttribute("Update");
            if (string.IsNullOrEmpty(include)) continue;
            if (include.IndexOfAny(new[] { '*', '?' }) >= 0) continue; // globbed; defer to fs walk
            if (!include.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = include.Replace('\\', '/');
            var absolute = Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Join(projectDir, relative));
            ct.ThrowIfCancellationRequested();
            if (!pathPolicy.IsExcludedForDiscovery(absolute))
            {
                results.Add(absolute);
            }
        }
        ct.ThrowIfCancellationRequested();
        return results;
    }

    private static bool IsXamlItemElement(string localName) =>
        // <Page> + <ApplicationDefinition> are the WPF/WinUI markup-compiler item types; many
        // projects also include resource dictionaries via <Resource> or <EmbeddedResource> when
        // the file isn't compiled at build time but still belongs to the project. Match all
        // four so explicit item includes are honoured alongside the complete directory scan.
        string.Equals(localName, "Page", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(localName, "ApplicationDefinition", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(localName, "EmbeddedResource", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(localName, "Resource", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build an immutable project-level resource snapshot. Pass one parses every allowed XAML
    /// file exactly once. Pass two follows relative merged-dictionary links from App.xaml and
    /// Themes/Generic.xaml, collecting all reachable keyed declarations. Duplicate keys retain
    /// every declaration so lookup can report ambiguity rather than depending on enumeration
    /// order.
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> BuildResourceCache(
        IReadOnlyList<string> xamlFiles,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var documents = new Dictionary<string, ResourceDocument>(
            StringComparer.OrdinalIgnoreCase);

        // Pass 1: parse all project-owned, policy-approved files into an in-memory catalog.
        foreach (var candidate in xamlFiles
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (pathPolicy.IsExcludedForDiscovery(candidate)) continue;

            BeforeAccess(XamlDiscoveryAccess.ReadXamlFile, candidate, ct);
            var bytes = File.ReadAllBytes(candidate);
            ct.ThrowIfCancellationRequested();
            var document = XamlReader.Parse(bytes);
            ct.ThrowIfCancellationRequested();

            var definitions = new List<ResourceDefinition>();
            var mergedSources = new List<string>();
            XamlReader.Walk(document.Root, (element, ancestors) =>
            {
                ct.ThrowIfCancellationRequested();
                var keyAttribute = element.FindAttribute(
                    XamlReader.XamlNamespace,
                    "Key");
                if (keyAttribute is not null && IsInResourceScope(ancestors))
                {
                    definitions.Add(new ResourceDefinition(
                        keyAttribute.Value,
                        candidate,
                        element.Line,
                        element.Column,
                        element.LocalName));
                }

                if (!string.Equals(
                        element.LocalName,
                        "ResourceDictionary",
                        StringComparison.Ordinal))
                {
                    return;
                }
                var sourceAttribute = element.FindAttributeByLocalName("Source");
                if (sourceAttribute is not null
                    && !string.IsNullOrWhiteSpace(sourceAttribute.Value))
                {
                    mergedSources.Add(sourceAttribute.Value.Trim());
                }
            });
            documents[candidate] = new ResourceDocument(definitions, mergedSources);
        }

        ct.ThrowIfCancellationRequested();
        // Pass 2: walk only the project-global cascade roots and their same-project merges.
        var roots = documents.Keys
            .Where(IsProjectResourceRoot)
            .OrderBy(
                path => string.Equals(
                    Path.GetFileName(path),
                    "App.xaml",
                    StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var visible = new Dictionary<string, List<ResourceDefinition>>(
            StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            Visit(root);
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>(
            visible.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ResourceDefinition>)pair.Value
                    .OrderBy(definition => definition.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(definition => definition.Line)
                    .ThenBy(definition => definition.Column)
                    .ToArray(),
                StringComparer.Ordinal));

        void Visit(string path)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(path)) return;
            if (!documents.TryGetValue(path, out var document)) return;

            foreach (var definition in document.Definitions)
            {
                ct.ThrowIfCancellationRequested();
                if (!visible.TryGetValue(definition.Key, out var candidates))
                {
                    candidates = new List<ResourceDefinition>();
                    visible[definition.Key] = candidates;
                }
                if (!candidates.Any(existing =>
                        string.Equals(
                            existing.FilePath,
                            definition.FilePath,
                            StringComparison.OrdinalIgnoreCase)
                        && existing.Line == definition.Line
                        && existing.Column == definition.Column))
                {
                    candidates.Add(definition);
                }
            }

            foreach (var source in document.MergedSources)
            {
                ct.ThrowIfCancellationRequested();
                var mergedPath = ResolveMergedDictionaryPath(
                    path,
                    source,
                    documents,
                    pathPolicy,
                    ct);
                if (mergedPath is not null) Visit(mergedPath);
            }
        }
    }

    private static bool IsInResourceScope(IReadOnlyList<XamlElement> ancestors)
    {
        foreach (var ancestor in ancestors)
        {
            if (string.Equals(
                    ancestor.LocalName,
                    "ResourceDictionary",
                    StringComparison.Ordinal)
                || ancestor.LocalName.EndsWith(
                    ".Resources",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsProjectResourceRoot(string path)
    {
        if (string.Equals(
                Path.GetFileName(path),
                "App.xaml",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.Equals(
                Path.GetFileName(path),
                "Generic.xaml",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return string.Equals(
            Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
            "Themes",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveMergedDictionaryPath(
        string containingFile,
        string source,
        IReadOnlyDictionary<string, ResourceDocument> documents,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var clean = source;
        var suffix = clean.IndexOfAny(new[] { '?', '#' });
        if (suffix >= 0) clean = clean.Substring(0, suffix);
        if (string.IsNullOrWhiteSpace(clean)
            || clean.Contains("://", StringComparison.Ordinal)
            || clean.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)
            || clean.Contains(";component/", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(clean))
        {
            return null;
        }

        try
        {
            clean = Uri.UnescapeDataString(clean)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var containingDirectory = Path.GetDirectoryName(containingFile);
            if (string.IsNullOrEmpty(containingDirectory)) return null;
            var candidate = Path.GetFullPath(Path.Combine(containingDirectory, clean));
            ct.ThrowIfCancellationRequested();
            if (pathPolicy.IsExcludedForDiscovery(candidate)) return null;
            return documents.ContainsKey(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UriFormatException)
        {
            return null;
        }
    }

    private sealed record ResourceDocument(
        IReadOnlyList<ResourceDefinition> Definitions,
        IReadOnlyList<string> MergedSources);
}

internal enum XamlDiscoveryAccess
{
    EnumerateProjectEntries,
    ReadProjectFile,
    EnumerateXamlEntries,
    ReadXamlFile,
}
