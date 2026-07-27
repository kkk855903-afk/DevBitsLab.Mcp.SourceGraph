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
/// cascades rooted at the project's <c>ApplicationDefinition</c> item and
/// <c>Themes/Generic.xaml</c>. When an <c>App.xaml</c> directly beside the project file has no
/// explicit, conflicting item identity, its filename is retained as a conservative SDK-default
/// fallback.
/// Project item expressions, conditions, imports, and removals that require MSBuild evaluation
/// make the resource snapshot incomplete instead of being guessed. Relative
/// <c>MergedDictionaries Source=</c> links are followed only when their physical target is an
/// allowed XAML file owned by the same project.
/// </summary>
public sealed class XamlLanguageProjectFactory : IExclusionAwareLanguageProjectFactory
{
    private readonly Func<Solution?>? _solutionProvider;
    private readonly Func<string, bool>? _semanticInputCompleteProvider;
    private readonly Func<string, bool>?
        _semanticPositiveResolutionSafeProvider;
    private readonly Action<XamlDiscoveryAccess, string>? _beforeAccess;

    public XamlLanguageProjectFactory()
    {
    }

    /// <summary>
    /// Creates a factory backed by the host's current privacy-sanitized Roslyn solution.
    /// The callback is intentionally lazy so live solution snapshots remain current. Because
    /// this overload has no raw-versus-sanitized completeness proof, semantic edges fail closed;
    /// production hosts should use the two-callback overload.
    /// </summary>
    public XamlLanguageProjectFactory(Func<Solution?> solutionProvider)
    {
        _solutionProvider = solutionProvider
            ?? throw new ArgumentNullException(nameof(solutionProvider));
        _semanticInputCompleteProvider = _ => false;
        _semanticPositiveResolutionSafeProvider = _ => false;
    }

    /// <summary>
    /// Creates a Roslyn-backed factory with an explicit completeness probe for the raw versus
    /// privacy-sanitized project inputs. The probe is evaluated lazily for every semantic pass.
    /// </summary>
    public XamlLanguageProjectFactory(
        Func<Solution?> solutionProvider,
        Func<string, bool> semanticInputCompleteProvider)
        : this(solutionProvider)
    {
        _semanticInputCompleteProvider = semanticInputCompleteProvider
            ?? throw new ArgumentNullException(nameof(semanticInputCompleteProvider));
        _semanticPositiveResolutionSafeProvider =
            semanticInputCompleteProvider;
    }

    /// <summary>
    /// Creates a Roslyn-backed factory with separate probes for authoritative completeness and
    /// positive-only binding safety. The latter may admit build-generated omissions, but never
    /// authorizes missing-member findings or inherited-member binding edges.
    /// </summary>
    public XamlLanguageProjectFactory(
        Func<Solution?> solutionProvider,
        Func<string, bool> semanticInputCompleteProvider,
        Func<string, bool> semanticPositiveResolutionSafeProvider)
        : this(solutionProvider, semanticInputCompleteProvider)
    {
        _semanticPositiveResolutionSafeProvider =
            semanticPositiveResolutionSafeProvider
            ?? throw new ArgumentNullException(
                nameof(semanticPositiveResolutionSafeProvider));
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
        var xamlDiscovery = EnumerateXamlFiles(
            csprojPath,
            projectDir,
            pathPolicy,
            ct);
        var xamlFiles = xamlDiscovery.FilePaths;
        if (xamlFiles.Count == 0) return null;

        ct.ThrowIfCancellationRequested();
        var resourceSnapshot = BuildResourceSnapshot(
            xamlDiscovery,
            pathPolicy,
            ct);
        ct.ThrowIfCancellationRequested();
        var fullProjectPath = Path.GetFullPath(csprojPath);
        Func<IReadOnlyList<Project>>? roslynProjectsProvider = _solutionProvider is null
            ? null
            : () => FindRoslynProjects(_solutionProvider(), fullProjectPath);
        Func<bool>? semanticInputCompleteProvider = _solutionProvider is null
            ? null
            : () => _semanticInputCompleteProvider?.Invoke(fullProjectPath) ?? false;
        Func<bool>? semanticPositiveResolutionSafeProvider =
            _solutionProvider is null
                ? null
                : () =>
                    _semanticPositiveResolutionSafeProvider?.Invoke(
                        fullProjectPath)
                    ?? false;
        return new XamlLanguageProject(
            fullProjectPath,
            xamlFiles,
            resourceSnapshot,
            () => BuildResourceSnapshot(
                xamlDiscovery,
                pathPolicy,
                CancellationToken.None),
            roslynProjectsProvider,
            semanticInputCompleteProvider,
            semanticPositiveResolutionSafeProvider);
    }

    private static IReadOnlyList<Project> FindRoslynProjects(
        Solution? solution,
        string projectFilePath)
    {
        if (solution is null) return Array.Empty<Project>();

        var matches = new List<Project>();
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp
                || string.IsNullOrEmpty(project.FilePath))
            {
                continue;
            }
            string candidate;
            try
            {
                candidate = Path.GetFullPath(project.FilePath);
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException)
            {
                continue;
            }

            if (string.Equals(candidate, projectFilePath, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(project);
            }
        }

        return matches;
    }

    /// <summary>
    /// Find the <c>.xaml</c> files that belong to the project. Explicit
    /// <c>&lt;Page&gt;</c> / <c>&lt;ApplicationDefinition&gt;</c> items are unioned with a complete
    /// policy-pruned recursive scan. An <c>Update</c> item only changes metadata for an SDK default
    /// item and therefore can never be treated as the complete project file set.
    /// </summary>
    private XamlFileDiscovery EnumerateXamlFiles(
        string csprojPath,
        string projectDir,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectMetadata = ReadXamlItemsFromCsproj(
            csprojPath,
            projectDir,
            pathPolicy,
            ct);
        var projectItems = projectMetadata.Items;
        var metadataUnknownReasons = new HashSet<string>(
            projectMetadata.UnknownReasons,
            StringComparer.Ordinal);
        foreach (var item in projectItems)
        {
            if (!File.Exists(item.Path))
            {
                var relativePath = Path.GetRelativePath(projectDir, item.Path)
                    .Replace('\\', '/');
                metadataUnknownReasons.Add(
                    "project-xaml-item-missing:" + relativePath);
                continue;
            }
            results.Add(item.Path);
        }

        // Always walk the project directory for *.xaml. Privacy-excluded directories are pruned
        // before enumeration, so markup outputs and medical data are never opened. A nested
        // project root is also a hard fallback-scan boundary; an explicit item above remains in
        // the result and is the only way for the parent to link a file across that boundary.
        var stack = new Stack<string>();
        var visitedPhysicalDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var fullProjectPath = Path.GetFullPath(csprojPath);
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

            var entries = EnumerateDirectoryEntries(
                dir,
                XamlDiscoveryAccess.EnumerateXamlEntries,
                ct);
            if (!string.Equals(
                    Path.GetFullPath(dir),
                    projectDir,
                    StringComparison.OrdinalIgnoreCase)
                && entries.Any(entry =>
                    string.Equals(
                        Path.GetExtension(entry),
                        ".csproj",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        Path.GetFullPath(entry),
                        fullProjectPath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var entry in entries)
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
        var filePaths = results
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var applicationDefinitionPaths = new HashSet<string>(
            projectItems
                .Where(item => item.IsApplicationDefinition)
                .Select(item => item.Path),
            StringComparer.OrdinalIgnoreCase);
        var nonApplicationDefinitionPaths = new HashSet<string>(
            projectItems
                .Where(item => !item.IsApplicationDefinition)
                .Select(item => item.Path),
            StringComparer.OrdinalIgnoreCase);
        foreach (var conflictingPath in applicationDefinitionPaths
                     .Where(nonApplicationDefinitionPaths.Contains))
        {
            metadataUnknownReasons.Add(
                "project-xaml-item-evaluation-unsupported:conflicting-identity:"
                + Path.GetFileName(conflictingPath));
        }
        return new XamlFileDiscovery(
            filePaths,
            projectDir,
            applicationDefinitionPaths,
            nonApplicationDefinitionPaths,
            metadataUnknownReasons
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray());
    }

    private XamlProjectMetadata ReadXamlItemsFromCsproj(
        string csprojPath,
        string projectDir,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        var results = new List<XamlProjectItem>();
        var unknownReasons = new HashSet<string>(StringComparer.Ordinal);
        var conditionalDepths = new Stack<int>();
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
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (conditionalDepths.Count > 0
                    && conditionalDepths.Peek() == reader.Depth)
                {
                    conditionalDepths.Pop();
                }
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;

            var condition = reader.GetAttribute("Condition");
            var hasOwnCondition = !string.IsNullOrWhiteSpace(condition);
            var hasInheritedCondition = conditionalDepths.Count > 0;
            if (hasOwnCondition && !reader.IsEmptyElement)
            {
                conditionalDepths.Push(reader.Depth);
            }

            if (string.Equals(
                    reader.LocalName,
                    "Import",
                    StringComparison.OrdinalIgnoreCase))
            {
                unknownReasons.Add(
                    "project-xaml-item-evaluation-unsupported:import");
                continue;
            }
            if (!IsXamlItemElement(reader.LocalName)) continue;

            var isApplicationDefinition = string.Equals(
                reader.LocalName,
                "ApplicationDefinition",
                StringComparison.OrdinalIgnoreCase);
            var include = reader.GetAttribute("Include");
            var update = reader.GetAttribute("Update");
            var remove = reader.GetAttribute("Remove");
            var itemSpec = include ?? update ?? remove;
            if (string.IsNullOrWhiteSpace(itemSpec)) continue;
            if (!IsPotentialXamlItemSpec(
                    reader.LocalName,
                    itemSpec))
            {
                continue;
            }

            // Update only changes metadata on an item that already exists. It neither creates
            // project membership nor changes the item's Page/ApplicationDefinition identity.
            if (string.IsNullOrWhiteSpace(include)
                && !string.IsNullOrWhiteSpace(update)
                && string.IsNullOrWhiteSpace(remove))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(remove))
            {
                unknownReasons.Add(
                    "project-xaml-item-evaluation-unsupported:remove");
                continue;
            }
            if (hasOwnCondition || hasInheritedCondition)
            {
                unknownReasons.Add(
                    "project-xaml-item-evaluation-unsupported:condition");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(reader.GetAttribute("Exclude")))
            {
                unknownReasons.Add(
                    "project-xaml-item-evaluation-unsupported:exclude");
                continue;
            }
            if (ContainsMsBuildExpression(itemSpec))
            {
                unknownReasons.Add(
                    "project-xaml-item-evaluation-unsupported:expression");
                continue;
            }
            if (itemSpec.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                unknownReasons.Add(
                    "project-xaml-item-evaluation-unsupported:glob");
                continue;
            }
            if (itemSpec.Contains(';'))
            {
                unknownReasons.Add(
                    "project-xaml-item-evaluation-unsupported:item-list");
                continue;
            }
            if (!itemSpec.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = itemSpec.Replace('\\', '/');
            var absolute = Path.GetFullPath(
                Path.IsPathRooted(relative)
                    ? relative
                    : Path.Join(projectDir, relative));
            ct.ThrowIfCancellationRequested();
            if (pathPolicy.IsExcludedForDiscovery(absolute))
            {
                if (isApplicationDefinition)
                {
                    // The project file proves that an application resource root exists, but the
                    // privacy policy forbids reading it. Silently dropping that root would turn
                    // every key it may contain into a false project-wide "missing" finding.
                    unknownReasons.Add(
                        "project-application-definition-excluded");
                }
            }
            else
            {
                results.Add(new XamlProjectItem(
                    absolute,
                    isApplicationDefinition));
            }
        }
        ct.ThrowIfCancellationRequested();
        return new XamlProjectMetadata(
            results,
            unknownReasons
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool IsPotentialXamlItemSpec(
        string itemType,
        string itemSpec) =>
        string.Equals(
            itemType,
            "Page",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            itemType,
            "ApplicationDefinition",
            StringComparison.OrdinalIgnoreCase)
        || itemSpec.Contains(".xaml", StringComparison.OrdinalIgnoreCase)
        || ContainsMsBuildExpression(itemSpec);

    private static bool ContainsMsBuildExpression(string itemSpec) =>
        itemSpec.Contains("$(", StringComparison.Ordinal)
        || itemSpec.Contains("@(", StringComparison.Ordinal)
        || itemSpec.Contains("%(", StringComparison.Ordinal);

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
    /// file exactly once. Pass two follows relative merged-dictionary links from the selected
    /// application-definition roots and Themes/Generic.xaml, collecting all reachable keyed
    /// declarations. Duplicate keys retain every declaration so lookup can report ambiguity
    /// rather than depending on enumeration order.
    /// </summary>
    private XamlResourceSnapshot BuildResourceSnapshot(
        XamlFileDiscovery xamlDiscovery,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var documents = new Dictionary<string, ResourceDocument>(
            StringComparer.OrdinalIgnoreCase);

        // Pass 1: parse all project-owned, policy-approved files into an in-memory catalog.
        foreach (var candidate in xamlDiscovery.FilePaths
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

            documents[candidate] = new ResourceDocument(
                document.Root,
                XamlResourceCanonicalKey.FindDeclarationDiscriminators(
                    document.Root));
        }

        ct.ThrowIfCancellationRequested();
        // Pass 2: walk only the project-global cascade roots and their same-project merges.
        var roots = documents.Keys
            .Where(path => IsProjectResourceRoot(path, xamlDiscovery))
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
        var unknownReasons = new HashSet<string>(
            xamlDiscovery.MetadataUnknownReasons,
            StringComparer.Ordinal);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            Visit(root);
        }

        var snapshotDefinitions = new ReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>(
            visible.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ResourceDefinition>)pair.Value
                    .OrderBy(definition => definition.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(definition => definition.Line)
                    .ThenBy(definition => definition.Column)
                    .ToArray(),
                StringComparer.Ordinal));
        return new XamlResourceSnapshot(
            snapshotDefinitions,
            visited
                .Where(documents.ContainsKey)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            isComplete: unknownReasons.Count == 0,
            unknownReasons
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray());

        void Visit(
            string path,
            bool requireResourceDictionaryRoot = false,
            string? mergeSource = null)
        {
            ct.ThrowIfCancellationRequested();
            if (!documents.TryGetValue(path, out var document)) return;
            if (requireResourceDictionaryRoot
                && !string.Equals(
                    document.Root.LocalName,
                    "ResourceDictionary",
                    StringComparison.Ordinal))
            {
                visited.Add(path);
                unknownReasons.Add(
                    "merged-dictionary-target-root-not-resource-dictionary:"
                    + (mergeSource ?? Path.GetFileName(path)));
                return;
            }
            if (!visited.Add(path)) return;

            VisitProjectRoot(document.Root, path);
        }

        void VisitProjectRoot(XamlElement root, string documentPath)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(
                    root.LocalName,
                    "ResourceDictionary",
                    StringComparison.Ordinal))
            {
                VisitDictionary(root, documentPath);
                return;
            }

            foreach (var child in root.Children.Where(child =>
                         child.LocalName.EndsWith(
                             ".Resources",
                             StringComparison.Ordinal)))
            {
                VisitResourceCollection(child, documentPath);
            }
        }

        void VisitResourceCollection(
            XamlElement collection,
            string documentPath)
        {
            foreach (var child in collection.Children)
            {
                ct.ThrowIfCancellationRequested();
                if (child.LocalName.EndsWith(
                        ".MergedDictionaries",
                        StringComparison.Ordinal))
                {
                    VisitMergedDictionaries(child, documentPath);
                    continue;
                }

                if (string.Equals(
                        child.LocalName,
                        "ResourceDictionary",
                        StringComparison.Ordinal)
                    && child.FindAttribute(
                        XamlReader.XamlNamespace,
                        "Key") is null)
                {
                    VisitDictionary(child, documentPath);
                    continue;
                }

                AddDefinition(child, documentPath);
            }
        }

        void VisitDictionary(XamlElement dictionary, string documentPath)
        {
            ct.ThrowIfCancellationRequested();
            var sourceAttribute = dictionary.FindAttributeByLocalName("Source");
            if (sourceAttribute is not null
                && !string.IsNullOrWhiteSpace(sourceAttribute.Value))
            {
                VisitMergedSource(
                    documentPath,
                    sourceAttribute.Value.Trim());
                if (dictionary.Children.Count > 0)
                {
                    unknownReasons.Add(
                        "resource-dictionary-source-with-inline-content:"
                        + sourceAttribute.Value.Trim());
                }
                return;
            }

            foreach (var child in dictionary.Children)
            {
                ct.ThrowIfCancellationRequested();
                if (child.LocalName.EndsWith(
                        ".MergedDictionaries",
                        StringComparison.Ordinal))
                {
                    VisitMergedDictionaries(child, documentPath);
                    continue;
                }
                AddDefinition(child, documentPath);
            }
        }

        void VisitMergedDictionaries(
            XamlElement mergedDictionaries,
            string documentPath)
        {
            foreach (var child in mergedDictionaries.Children)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(
                        child.LocalName,
                        "ResourceDictionary",
                        StringComparison.Ordinal))
                {
                    unknownReasons.Add(
                        "unsupported-inline-merged-dictionary:"
                        + child.LocalName);
                    continue;
                }

                var sourceAttribute = child.FindAttributeByLocalName("Source");
                if (sourceAttribute is not null
                    && !string.IsNullOrWhiteSpace(sourceAttribute.Value))
                {
                    VisitMergedSource(
                        documentPath,
                        sourceAttribute.Value.Trim());
                    continue;
                }

                VisitDictionary(child, documentPath);
            }
        }

        void VisitMergedSource(string containingPath, string source)
        {
            var merged = ResolveMergedDictionaryPath(
                containingPath,
                source,
                documents,
                pathPolicy,
                ct);
            if (merged.Path is not null)
            {
                Visit(
                    merged.Path,
                    requireResourceDictionaryRoot: true,
                    mergeSource: source);
            }
            else if (merged.UnknownReason is not null)
            {
                unknownReasons.Add(merged.UnknownReason);
            }
        }

        void AddDefinition(XamlElement element, string documentPath)
        {
            var keyAttribute = element.FindAttribute(
                XamlReader.XamlNamespace,
                "Key");
            if (keyAttribute is null) return;
            documents[documentPath].CanonicalDiscriminators.TryGetValue(
                element,
                out var canonicalDiscriminator);

            var definition = new ResourceDefinition(
                keyAttribute.Value,
                documentPath,
                element.Line,
                element.Column,
                element.LocalName,
                canonicalDiscriminator);
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
    }

    private static bool IsProjectResourceRoot(
        string path,
        XamlFileDiscovery xamlDiscovery)
    {
        if (IsThemeGenericDictionary(path))
        {
            return true;
        }

        if (xamlDiscovery.ApplicationDefinitionPaths.Contains(path))
        {
            return true;
        }

        // Explicit identity for this file is authoritative. In particular, a
        // <Page Include="App.xaml"> is an ordinary page even though its file name resembles the
        // conventional app root. Metadata for unrelated XAML files does not suppress the SDK
        // implicit App.xaml fallback, which is limited to the project directory itself.
        if (xamlDiscovery.NonApplicationDefinitionPaths.Contains(path))
        {
            return false;
        }

        return string.Equals(
                   Path.GetFileName(path),
                   "App.xaml",
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   Path.GetDirectoryName(Path.GetFullPath(path)),
                   xamlDiscovery.ProjectDirectory,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsThemeGenericDictionary(string path)
    {
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

    private static MergedDictionaryResolution ResolveMergedDictionaryPath(
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
            return new MergedDictionaryResolution(
                Path: null,
                UnknownReason: "unsupported-merged-dictionary-source:" + source);
        }

        try
        {
            clean = Uri.UnescapeDataString(clean)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var containingDirectory = Path.GetDirectoryName(containingFile);
            if (string.IsNullOrEmpty(containingDirectory))
            {
                return new MergedDictionaryResolution(
                    Path: null,
                    UnknownReason: "merged-dictionary-containing-directory-unavailable");
            }
            var candidate = Path.GetFullPath(Path.Combine(containingDirectory, clean));
            ct.ThrowIfCancellationRequested();
            if (pathPolicy.IsExcludedForDiscovery(candidate))
            {
                return new MergedDictionaryResolution(
                    Path: null,
                    UnknownReason: "merged-dictionary-target-excluded:" + source);
            }
            return documents.ContainsKey(candidate)
                ? new MergedDictionaryResolution(candidate, UnknownReason: null)
                : new MergedDictionaryResolution(
                    Path: null,
                    UnknownReason: "merged-dictionary-target-unavailable:" + source);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UriFormatException)
        {
            return new MergedDictionaryResolution(
                Path: null,
                UnknownReason: "unsupported-merged-dictionary-source:" + source);
        }
    }

    private sealed record ResourceDocument(
        XamlElement Root,
        IReadOnlyDictionary<XamlElement, string> CanonicalDiscriminators);

    private sealed record XamlProjectItem(
        string Path,
        bool IsApplicationDefinition);

    private sealed record XamlProjectMetadata(
        IReadOnlyList<XamlProjectItem> Items,
        IReadOnlyList<string> UnknownReasons);

    private sealed record XamlFileDiscovery(
        IReadOnlyList<string> FilePaths,
        string ProjectDirectory,
        IReadOnlySet<string> ApplicationDefinitionPaths,
        IReadOnlySet<string> NonApplicationDefinitionPaths,
        IReadOnlyList<string> MetadataUnknownReasons);

    private sealed record MergedDictionaryResolution(
        string? Path,
        string? UnknownReason);
}

internal enum XamlDiscoveryAccess
{
    EnumerateProjectEntries,
    ReadProjectFile,
    EnumerateXamlEntries,
    ReadXamlFile,
}
