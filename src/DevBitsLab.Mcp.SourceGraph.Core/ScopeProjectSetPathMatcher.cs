namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// Applies the positive path boundary declared by a scope's project set.
/// </summary>
/// <remarks>
/// <see cref="ScopePathPolicy"/> remains the mandatory privacy/physical/exclude boundary.
/// This matcher only narrows that allowed set:
/// <list type="bullet">
/// <item><c>solutions</c> scopes retain repository-wide cross-language discovery.</item>
/// <item><c>projects</c> scopes include files below each configured project anchor.</item>
/// <item>
/// <c>paths</c> globs select project anchors; files below those matched projects are included.
/// </item>
/// </list>
/// Project-anchor globs use the exact parser and matching rules used by
/// <see cref="ScopePathPolicy"/> excludes.
/// </remarks>
public sealed class ScopeProjectSetPathMatcher
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly string _root;
    private readonly ScopeProjectSet _projectSet;
    private readonly IReadOnlyList<ProjectRoot>? _projectRoots;
    private readonly IReadOnlyList<ScopePathPolicy.GlobPattern> _pathPatterns;
    private readonly IReadOnlySet<string> _explicitProjectAnchors;

    public ScopeProjectSetPathMatcher(string repoRoot, ScopeProjectSet projectSet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(projectSet);

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        _projectSet = projectSet;
        _pathPatterns = projectSet is ScopeProjectSet.Paths configuredPaths
            ? configuredPaths.Globs.Select(ScopePathPolicy.GlobPattern.Create).ToArray()
            : Array.Empty<ScopePathPolicy.GlobPattern>();
        _explicitProjectAnchors = projectSet is ScopeProjectSet.Projects configuredProjects
            ? configuredProjects.Items
                .Select(TryResolveConfiguredAnchor)
                .Where(path => path is not null)
                .Select(path => path!)
                .ToHashSet(PathComparer)
            : new HashSet<string>(PathComparer);
        switch (projectSet)
        {
            case ScopeProjectSet.Paths paths:
                _projectRoots = ResolveGlobbedProjectRoots(paths);
                break;

            case ScopeProjectSet.Projects projects:
                _projectRoots = projects.Items
                    .Select(path => ResolveProjectRoot(path, projects.Exclude))
                    .Where(path => path is not null)
                    .Select(path => path!)
                    .DistinctBy(path => path.Lexical, PathComparer)
                    .ToArray();
                break;
        }
    }

    /// <summary>
    /// Roots that may be handed to project factories without exposing an unselected sibling
    /// project. Solution scopes intentionally retain the repository root.
    /// </summary>
    public IReadOnlyList<string> DiscoveryRoots =>
        _projectRoots is null
            ? [_root]
            : _projectRoots
                .Select(root => root.Lexical)
                .OrderBy(path => path.Length)
                .ThenBy(path => path, PathComparer)
                .Where((path, index) =>
                {
                    var earlier = _projectRoots
                        .Select(root => root.Lexical)
                        .OrderBy(candidate => candidate.Length)
                        .ThenBy(candidate => candidate, PathComparer)
                        .Take(index);
                    return !earlier.Any(parent => IsSameOrDescendant(parent, path));
                })
                .ToArray();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> belongs to the scope's
    /// positive project-set selection. Invalid and out-of-root paths fail closed.
    /// </summary>
    public bool Includes(string? path)
    {
        if (!TryGetFullPath(path, includeRoot: false, out var fullPath))
        {
            return false;
        }

        if (_projectRoots is not null)
        {
            if (!ScopePathPolicy.TryResolvePhysicalPath(fullPath, out var physicalPath))
            {
                return false;
            }
            // Lexical containment enforces the configured project boundary, while physical
            // containment prevents a symlink below that boundary from importing a sibling or
            // external project.
            return _projectRoots.Any(projectRoot =>
                IsSameOrDescendant(projectRoot.Lexical, fullPath)
                && IsSameOrDescendant(projectRoot.Physical, physicalPath));
        }

        // A solutions scope deliberately permits registered cross-language sources anywhere
        // below the scope root; the privacy/physical/exclude policy still narrows this set.
        return true;
    }

    /// <summary>
    /// Returns whether a directory can lead to or live below an eligible project root. Cold
    /// walkers use this to avoid descending into unrelated directories and into in-project
    /// links whose physical target escapes the selected project.
    /// </summary>
    public bool ShouldTraverseDirectory(string? path)
    {
        if (!TryGetFullPath(path, includeRoot: true, out var fullPath)) return false;
        if (_projectRoots is null) return true;

        foreach (var projectRoot in _projectRoots)
        {
            if (IsSameOrDescendant(fullPath, projectRoot.Lexical))
            {
                return true;
            }
            if (!IsSameOrDescendant(projectRoot.Lexical, fullPath)) continue;
            if (!ScopePathPolicy.TryResolvePhysicalPath(fullPath, out var physicalPath))
            {
                return false;
            }
            if (IsSameOrDescendant(projectRoot.Physical, physicalPath))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Identifies project-control paths that must trigger a project-map/matcher refresh even
    /// when no language indexer claims <c>.csproj</c>.
    /// </summary>
    public bool IsProjectAnchorCandidate(string? path)
    {
        if (!TryGetFullPath(path, includeRoot: false, out var fullPath)
            || !string.Equals(
                Path.GetExtension(fullPath),
                ".csproj",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _projectSet switch
        {
            ScopeProjectSet.Solutions => true,
            ScopeProjectSet.Projects => _explicitProjectAnchors.Contains(fullPath),
            ScopeProjectSet.Paths =>
                _pathPatterns.Any(pattern => pattern.IsMatch(
                    Path.GetRelativePath(_root, fullPath).Replace('\\', '/'))),
            _ => false,
        };
    }

    /// <summary>
    /// Rebase repository-relative excludes for a selected child discovery root. Factories see
    /// only that root, so their policy must receive the residual form of each global pattern.
    /// </summary>
    public IReadOnlyList<string> ExcludesForDiscoveryRoot(string discoveryRoot)
    {
        if (!TryGetFullPath(discoveryRoot, includeRoot: true, out var fullRoot)
            || !IsSameOrDescendant(_root, fullRoot))
        {
            return ["**"];
        }
        var relative = Path.GetRelativePath(_root, fullRoot).Replace('\\', '/');
        if (relative == ".") return _projectSet.Exclude;

        return _projectSet.Exclude
            .SelectMany(pattern => ScopePathPolicy.GlobPattern.Create(pattern).Rebase(relative))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(pattern => pattern, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pattern => pattern, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<ProjectRoot> ResolveGlobbedProjectRoots(ScopeProjectSet.Paths paths)
    {
        if (_pathPatterns.Count == 0 || !Directory.Exists(_root))
        {
            return Array.Empty<ProjectRoot>();
        }

        var pathPolicy = new ScopePathPolicy(_root, paths.Exclude);
        var projectRoots = new Dictionary<string, ProjectRoot>(PathComparer);
        var visitedPhysicalDirectories = new HashSet<string>(PathComparer);
        var stack = new Stack<string>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            if (pathPolicy.IsExcluded(directory)) continue;
            // A project-root glob must not make traversal follow a link into another tree. The
            // physical-directory set additionally breaks cycles formed by distinct link paths.
            if (!ScopePathPolicy.TryResolvePhysicalPath(directory, out var physicalDirectory)
                || !visitedPhysicalDirectories.Add(physicalDirectory))
            {
                continue;
            }

            IReadOnlyList<string> entries;
            try
            {
                entries = Directory
                    .EnumerateFileSystemEntries(directory)
                    .OrderBy(path => path, PathComparer)
                    .ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (pathPolicy.IsExcluded(entry)) continue;
                if (Directory.Exists(entry))
                {
                    if (IsDirectoryReparsePoint(entry))
                    {
                        continue;
                    }
                    stack.Push(entry);
                    continue;
                }
                if (!string.Equals(
                        Path.GetExtension(entry),
                        ".csproj",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(_root, entry).Replace('\\', '/');
                if (!_pathPatterns.Any(pattern => pattern.IsMatch(relativePath))) continue;
                var projectRoot = Path.GetDirectoryName(entry);
                if (!string.IsNullOrEmpty(projectRoot)
                    && !ContainsReparseDirectory(projectRoot)
                    && ScopePathPolicy.TryResolvePhysicalPath(
                        projectRoot,
                        out var physicalProjectRoot))
                {
                    var lexicalProjectRoot =
                        Path.TrimEndingDirectorySeparator(projectRoot);
                    projectRoots[lexicalProjectRoot] = new ProjectRoot(
                        lexicalProjectRoot,
                        Path.TrimEndingDirectorySeparator(physicalProjectRoot));
                }
            }
        }

        return projectRoots.Values.ToArray();
    }

    private bool ContainsReparseDirectory(string directory)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(_root, directory);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return true;
        }

        var current = _root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            if (IsDirectoryReparsePoint(current)) return true;
        }
        return false;
    }

    private static bool IsDirectoryReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                   or DirectoryNotFoundException
                                   or UnauthorizedAccessException
                                   or IOException
                                   or System.Security.SecurityException)
        {
            return true;
        }
    }

    private ProjectRoot? ResolveProjectRoot(
        string configuredPath,
        IReadOnlyList<string> excludePatterns)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException("Configured project paths cannot be blank.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(configuredPath)
                    ? configuredPath
                    : Path.Join(_root, configuredPath));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return null;
        }
        if (!IsSameOrDescendant(_root, fullPath)
            || !string.Equals(
                Path.GetExtension(fullPath),
                ".csproj",
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || new ScopePathPolicy(_root, excludePatterns).IsExcluded(fullPath))
        {
            return null;
        }

        var lexicalRoot = Path.TrimEndingDirectorySeparator(
            Path.GetDirectoryName(fullPath)
            ?? _root);
        if (!ScopePathPolicy.TryResolvePhysicalPath(lexicalRoot, out var physicalRoot))
        {
            return null;
        }
        return new ProjectRoot(
            lexicalRoot,
            Path.TrimEndingDirectorySeparator(physicalRoot));
    }

    private string? TryResolveConfiguredAnchor(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;
        try
        {
            var fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(configuredPath)
                    ? configuredPath
                    : Path.Join(_root, configuredPath));
            return IsSameOrDescendant(_root, fullPath)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return null;
        }
    }

    private bool TryGetFullPath(string? path, bool includeRoot, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(path)
                    ? path
                    : Path.Join(_root, path));
            return IsSameOrDescendant(_root, fullPath)
                && (includeRoot || !string.Equals(_root, fullPath, _pathComparison));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        if (string.Equals(parent, candidate, _pathComparison)) return true;
        if (!candidate.StartsWith(parent, _pathComparison)) return false;
        if (candidate.Length <= parent.Length) return false;

        var separator = candidate[parent.Length];
        return separator == Path.DirectorySeparatorChar
            || separator == Path.AltDirectorySeparatorChar;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record ProjectRoot(string Lexical, string Physical);
}
