using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Per-scope dispatcher for non-C# language indexers. The C# bulk pathway lives in
/// <see cref="DevBitsLab.Mcp.SourceGraph.Indexing.RoslynIndexer"/> and is exempt — it walks the
/// MSBuildWorkspace solution directly. This dispatcher covers every OTHER extension that an
/// in-tree indexer or plugin claims through <see cref="LanguageIndexerRegistry"/>: it walks the
/// scope's repo root for files matching those extensions, populates
/// <see cref="IndexContext.Project"/> from the per-scope project lookup map (built up-front from
/// every registered <see cref="ILanguageProjectFactory"/>), invokes the indexer's
/// <see cref="ILanguageIndexer.IndexAsync"/>, compiles the resulting events, and atomically
/// replaces the file's stored graph facts.
/// </summary>
public sealed class LanguageIndexerDispatcher
{
    /// <summary>Extensions exempt from dispatch — handled by the workspace-aware C# bulk path.</summary>
    private static readonly HashSet<string> _csharpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs" };

    private readonly LanguageIndexerRegistry _indexers;
    private readonly LanguageProjectFactoryRegistry _factories;
    private readonly AnalyzerPipeline? _analyzerPipeline;
    private readonly ILogger<LanguageIndexerDispatcher> _logger;

    public LanguageIndexerDispatcher(
        LanguageIndexerRegistry indexers,
        LanguageProjectFactoryRegistry factories,
        ILogger<LanguageIndexerDispatcher>? logger = null,
        AnalyzerPipeline? analyzerPipeline = null)
    {
        _indexers = indexers;
        _factories = factories;
        _analyzerPipeline = analyzerPipeline;
        _logger = logger ?? NullLogger<LanguageIndexerDispatcher>.Instance;
    }

    /// <summary>
    /// The registry the dispatcher reads to look up indexers. Exposed so callers (e.g.
    /// per-scope dispatcher subclassing in <c>LiveIndexService</c>) can build a sibling
    /// dispatcher that shares the indexer set but has a different factory pool.
    /// </summary>
    internal LanguageIndexerRegistry Indexers => _indexers;
    internal AnalyzerPipeline? AnalyzerPipeline => _analyzerPipeline;

    /// <summary>
    /// Snapshot of every extension currently claimed by a registered language indexer. The live
    /// watcher consumes this list instead of maintaining its own hard-coded language set, so a
    /// future native/protobuf/plugin indexer becomes watchable as soon as it is registered.
    /// </summary>
    public IReadOnlyCollection<string> RegisteredSourceExtensions =>
        _indexers.All()
            .Select(pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// True when there is at least one non-C# indexer registered. Cheap pre-check so callers can
    /// skip the file-tree walk on a single-language scope.
    /// </summary>
    public bool HasNonCSharpIndexers
    {
        get
        {
            foreach (var pair in _indexers.All())
            {
                if (!_csharpExtensions.Contains(pair.Key)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Build the per-scope file → project map into a temporary dictionary. Factories receive
    /// only the discovery roots selected by the positive project set. The live map is swapped
    /// only when every factory/root succeeds, so a transient discovery failure cannot erase the
    /// last usable semantic project state.
    /// </summary>
    public async Task<ProjectMapBuildResult> BuildProjectMapAsync(
        ScopeHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var result = await DiscoverProjectMapAsync(
            host.Scope.Root,
            host.Scope.ProjectSet,
            host.Scope.Id,
            ct).ConfigureAwait(false);
        if (result.FailedProjects.Count > 0)
        {
            host.ProjectMapReady = false;
            return result;
        }

        host.ProjectByFilePath.Clear();
        foreach (var pair in result.ProjectByFilePath)
        {
            host.ProjectByFilePath[pair.Key] = pair.Value;
        }
        host.LanguageProjects = result.Projects;
        host.ProjectMapReady = true;
        return result;
    }

    /// <summary>
    /// ScopeHost-free project discovery used by the one-shot CLI. A non-empty failure list means
    /// <see cref="ProjectMapBuildResult.ProjectByFilePath"/> is incomplete and must not be used.
    /// </summary>
    internal async Task<ProjectMapBuildResult> DiscoverProjectMapAsync(
        string repoRoot,
        ScopeProjectSet projectSet,
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedRoot = Path.GetFullPath(repoRoot);
        var pathPolicy = new ScopePathPolicy(normalizedRoot, projectSet.Exclude);
        var projectSetMatcher = new ScopeProjectSetPathMatcher(normalizedRoot, projectSet);
        var temporary = new Dictionary<string, ILanguageProject>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var discoveredProjects = new Dictionary<string, ILanguageProject>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var failures = new List<ProjectFailure>();

        foreach (var factory in _factories.All())
        {
            foreach (var discoveryRoot in projectSetMatcher.DiscoveryRoots)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<ILanguageProject> projects;
                try
                {
                    var discoveryExcludes = projectSetMatcher
                        .ExcludesForDiscoveryRoot(discoveryRoot)
                        .Concat(PrivacyPathPolicy.MandatoryExcludePatterns)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(pattern => pattern, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(pattern => pattern, StringComparer.Ordinal)
                        .ToArray();
                    if (factory is not IExclusionAwareLanguageProjectFactory exclusionAware)
                    {
                        throw new InvalidOperationException(
                            "Factory must implement IExclusionAwareLanguageProjectFactory; "
                            + "project discovery is not invoked without a before-read privacy "
                            + "boundary.");
                    }
                    projects = await exclusionAware.DiscoverAsync(
                        discoveryRoot,
                        discoveryExcludes,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var relativeRoot = Path.GetRelativePath(normalizedRoot, discoveryRoot)
                        .Replace('\\', '/');
                    var factoryName = factory.GetType().FullName
                        ?? factory.GetType().Name;
                    failures.Add(new ProjectFailure(
                        $"{factoryName}@{relativeRoot}",
                        FailureMessage.Truncate(ex.Message)));
                    _logger.LogWarning(
                        ex,
                        "Factory `{Type}` failed DiscoverAsync at {Root} for scope `{Scope}`",
                        factoryName,
                        discoveryRoot,
                        scopeId);
                    continue;
                }

                foreach (var project in projects)
                {
                    var projectIdentity =
                        (project.GetType().AssemblyQualifiedName
                         ?? project.GetType().FullName
                         ?? project.GetType().Name)
                        + "\0"
                        + project.Id;
                    discoveredProjects.TryAdd(projectIdentity, project);
                    foreach (var path in project.FilePaths)
                    {
                        if (string.IsNullOrWhiteSpace(path)
                            || pathPolicy.IsExcluded(path)
                            || !projectSetMatcher.Includes(path))
                        {
                            continue;
                        }
                        if (!temporary.ContainsKey(path))
                        {
                            temporary[path] = project;
                        }
                    }
                }
            }
        }

        return new ProjectMapBuildResult(temporary, failures)
        {
            Projects = discoveredProjects.Values.ToArray(),
        };
    }

    /// <summary>
    /// ScopeHost-free overload: dispatch every non-C# file under <paramref name="repoRoot"/>
    /// directly against an <see cref="IGraphStore"/> + an externally-built project map. Used by
    /// the XAML indexer's smoke tests AND by the one-shot <c>index</c> CLI path — both want
    /// dispatcher behaviour but neither owns a <see cref="ScopeHost"/> (the test path doesn't
    /// need one; the CLI path keeps its own <c>await using</c> ownership of the store + indexer
    /// + embeddings to avoid double-dispose with ScopeHost.DisposeAsync).
    /// </summary>
    public async Task<LanguageDispatchResult> DispatchAllForTestAsync(
        IGraphStore store,
        string scopeId,
        string repoRoot,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        CancellationToken ct = default) =>
        await DispatchAllForTestAsync(
            store,
            scopeId,
            repoRoot,
            projectMap,
            Array.Empty<string>(),
            ct).ConfigureAwait(false);

    /// <summary>
    /// Scope-exclude-aware variant used by host-level tests and one-shot callers that already
    /// resolved a scope configuration. Supplying <paramref name="projectSet"/> also applies its
    /// positive project boundary.
    /// </summary>
    public async Task<LanguageDispatchResult> DispatchAllForTestAsync(
        IGraphStore store,
        string scopeId,
        string repoRoot,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        IReadOnlyList<string> excludePatterns,
        CancellationToken ct,
        ScopeProjectSet? projectSet = null,
        IReadOnlyList<ILanguageProject>? projects = null)
    {
        if (!HasNonCSharpIndexers) return LanguageDispatchResult.Empty;
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
        {
            return LanguageDispatchResult.Empty;
        }
        var pathPolicy = new ScopePathPolicy(Path.GetFullPath(repoRoot), excludePatterns);
        var projectSetMatcher = projectSet is null
            ? null
            : new ScopeProjectSetPathMatcher(repoRoot, projectSet);

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _indexers.All())
        {
            if (_csharpExtensions.Contains(pair.Key)) continue;
            extensions.Add(pair.Key);
        }
        if (extensions.Count == 0) return LanguageDispatchResult.Empty;

        var staleDeletion = await DeleteStaleRegisteredFilesAsync(
            store,
            extensions,
            pathPolicy,
            projectSetMatcher,
            ct).ConfigureAwait(false);

        var symbolIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var symbolFileIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var fileIdByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await PopulateSymbolKeyMapAsync(
            store,
            symbolIdByKey,
            symbolFileIdByKey,
            fileIdByPath,
            ct).ConfigureAwait(false);

        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usableOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = staleDeletion.FailedFiles.Count;
        var failedFiles = new List<FileFailure>(staleDeletion.FailedFiles);
        foreach (var file in OrderDispatchFiles(
                     EnumerateFiles(repoRoot, extensions, pathPolicy, projectSetMatcher),
                     projectMap,
                     projects))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            var indexerHit = _indexers.TryGet(ext);
            if (indexerHit is null) continue;
            try
            {
                var outcome = await DispatchOneCoreAsync(
                    store,
                    scopeId,
                    repoRoot,
                    projectMap,
                    projects,
                    file,
                    indexerHit.Value.Indexer,
                    symbolIdByKey,
                    symbolFileIdByKey,
                    fileIdByPath,
                    pathPolicy,
                    ct).ConfigureAwait(false);
                if (outcome.Replaced) indexedPaths.Add(file);
                if (outcome.HasUsableOutput) usableOutputPaths.Add(file);
                if (outcome.WasSkipped) skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                skipped++;
                failedFiles.Add(new FileFailure(file, FailureMessage.Truncate(ex.Message)));
                _logger.LogWarning(ex, "Indexer threw on {File}", file);
                if (indexerHit.Value.Owner is { } record)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage = $"IndexAsync threw on `{file}`: {ex.Message}";
                }
            }
        }
        return new LanguageDispatchResult(
            indexedPaths.Count,
            usableOutputPaths.Count,
            staleDeletion.DeletedFiles,
            skipped,
            failedFiles);
    }

    /// <summary>
    /// Walk the scope's repo root for every file whose extension has a registered non-C# indexer
    /// and dispatch each through that indexer. Events are persisted to <see cref="ScopeHost.Store"/>.
    /// Errors are isolated per file: a throwing indexer logs and skips the file but the rest of
    /// the pass continues.
    /// </summary>
    public async Task<LanguageDispatchResult> DispatchAllAsync(
        ScopeHost host,
        CancellationToken ct = default)
    {
        if (!HasNonCSharpIndexers) return LanguageDispatchResult.Empty;
        if (string.IsNullOrEmpty(host.Scope.Root) || !Directory.Exists(host.Scope.Root))
        {
            return LanguageDispatchResult.Empty;
        }

        // Snapshot the registered extensions so the file walk doesn't lock the registry on every
        // traversal step.
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _indexers.All())
        {
            if (_csharpExtensions.Contains(pair.Key)) continue;
            extensions.Add(pair.Key);
        }
        if (extensions.Count == 0) return LanguageDispatchResult.Empty;
        var pathPolicy = new ScopePathPolicy(
            Path.GetFullPath(host.Scope.Root),
            host.Scope.ProjectSet.Exclude);
        var projectSetMatcher = new ScopeProjectSetPathMatcher(
            host.Scope.Root,
            host.Scope.ProjectSet);

        var staleDeletion = await DeleteStaleRegisteredFilesAsync(
            host.Store,
            extensions,
            pathPolicy,
            projectSetMatcher,
            ct).ConfigureAwait(false);
        if (staleDeletion.DeletedFiles > 0)
        {
            _logger.LogInformation(
                "Scope `{Scope}` removed {Count} vanished registered-language files during full dispatch",
                host.Scope.Id,
                staleDeletion.DeletedFiles);
        }

        // Cache canonical-key → symbol-id across files in this pass so cross-file edges (e.g. a
        // XAML view binding to a viewmodel symbol the C# indexer wrote) resolve correctly.
        var symbolIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var symbolFileIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var fileIdByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await PopulateSymbolKeyMapAsync(
            host.Store,
            symbolIdByKey,
            symbolFileIdByKey,
            fileIdByPath,
            ct).ConfigureAwait(false);

        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usableOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = staleDeletion.FailedFiles.Count;
        var failedFiles = new List<FileFailure>(staleDeletion.FailedFiles);
        foreach (var file in OrderDispatchFiles(
                     EnumerateFiles(
                         host.Scope.Root,
                         extensions,
                         pathPolicy,
                         projectSetMatcher),
                     host.ProjectByFilePath,
                     host.LanguageProjects))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            var indexerHit = _indexers.TryGet(ext);
            if (indexerHit is null) continue;

            try
            {
                var outcome = await DispatchOneAsync(
                    host,
                    file,
                    indexerHit.Value.Indexer,
                    indexerHit.Value.Owner,
                    symbolIdByKey,
                    symbolFileIdByKey,
                    fileIdByPath,
                    pathPolicy,
                    ct).ConfigureAwait(false);
                if (outcome.Replaced) indexedPaths.Add(file);
                if (outcome.HasUsableOutput) usableOutputPaths.Add(file);
                if (outcome.WasSkipped) skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                skipped++;
                failedFiles.Add(new FileFailure(file, FailureMessage.Truncate(ex.Message)));
                _logger.LogWarning(ex, "Indexer `{Indexer}` threw on {File}; skipping",
                    indexerHit.Value.Indexer.GetType().FullName, file);
                if (indexerHit.Value.Owner is { } record)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage = $"IndexAsync threw on `{file}`: {ex.Message}";
                }
            }
        }
        return new LanguageDispatchResult(
            indexedPaths.Count,
            usableOutputPaths.Count,
            staleDeletion.DeletedFiles,
            skipped,
            failedFiles);
    }

    /// <summary>
    /// Apply one live watcher batch for every registered non-C# extension. Missing paths are
    /// deleted transactionally before existing files are re-indexed, which gives create/change/
    /// delete/rename the same replacement semantics as a cold dispatch. Paths outside the scope,
    /// excluded paths, privacy-sensitive paths, and unresolved physical paths fail closed before
    /// any file read or graph mutation.
    /// </summary>
    public async Task<LanguageDispatchResult> DispatchChangedFilesAsync(
        ScopeHost host,
        IReadOnlyCollection<string> paths,
        CancellationToken ct = default,
        bool csharpSemanticUpdateSucceeded = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(paths);
        if (!HasNonCSharpIndexers || paths.Count == 0)
        {
            return LanguageDispatchResult.Empty;
        }

        var root = Path.GetFullPath(host.Scope.Root);
        var pathPolicy = new ScopePathPolicy(root, host.Scope.ProjectSet.Exclude);
        var projectSetMatcher = new ScopeProjectSetPathMatcher(root, host.Scope.ProjectSet);
        var candidates = new List<DispatchCandidate>();
        var skipped = 0;
        var failedFiles = new List<FileFailure>();
        var projectAnchorChanged = false;
        var projectMapRebuilt = false;

        var suppliedPaths = csharpSemanticUpdateSucceeded
            && paths.Any(path => string.Equals(
                Path.GetExtension(path),
                ".cs",
                StringComparison.OrdinalIgnoreCase))
            ? paths
                .Concat(GetXamlProjects(host).SelectMany(project => project.FilePaths))
                .Distinct(StringComparer.OrdinalIgnoreCase)
            : paths.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var suppliedPath in suppliedPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryNormalizePath(root, suppliedPath, out var fullPath)
                || pathPolicy.IsExcluded(fullPath))
            {
                skipped++;
                continue;
            }

            if (projectSetMatcher.IsProjectAnchorCandidate(fullPath))
            {
                projectAnchorChanged = true;
                continue;
            }

            var extension = Path.GetExtension(fullPath);
            if (_csharpExtensions.Contains(extension))
            {
                skipped++;
                continue;
            }

            var indexerHit = _indexers.TryGet(extension);
            if (indexerHit is null)
            {
                skipped++;
                continue;
            }

            var state = GetSourcePathState(fullPath);
            if (!projectSetMatcher.Includes(fullPath))
            {
                // A source can become ineligible because its selected .csproj disappeared.
                // Missing files are still safe to purge from the graph; existing out-of-scope
                // files remain unread and untouched.
                if (state == SourcePathState.Missing)
                {
                    candidates.Add(new DispatchCandidate(
                        fullPath,
                        indexerHit.Value.Indexer,
                        indexerHit.Value.Owner,
                        state));
                }
                else
                {
                    skipped++;
                }
                continue;
            }
            if (state == SourcePathState.Rejected)
            {
                skipped++;
                continue;
            }

            candidates.Add(new DispatchCandidate(
                fullPath,
                indexerHit.Value.Indexer,
                indexerHit.Value.Owner,
                state));
        }

        if (projectAnchorChanged)
        {
            var projectMapResult =
                await BuildProjectMapAsync(host, ct).ConfigureAwait(false);
            if (!projectMapResult.Succeeded)
            {
                return new LanguageDispatchResult(
                    IndexedFiles: 0,
                    UsableOutputFiles: 0,
                    DeletedFiles: 0,
                    SkippedFiles: skipped + candidates.Count,
                    FailedFiles: failedFiles)
                {
                    FailedProjects = projectMapResult.FailedProjects,
                };
            }

            var fullResult =
                await DispatchAllAsync(host, ct).ConfigureAwait(false);
            return fullResult with
            {
                SkippedFiles = fullResult.SkippedFiles + skipped,
            };
        }

        var existingCandidates = candidates
            .Where(item => item.State == SourcePathState.File)
            .ToArray();
        var resourceProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceEdits =
            new Dictionary<string, (XamlLanguageProject Project, string TriggerPath)>(
                StringComparer.OrdinalIgnoreCase);
        var newXamlPaths = new List<string>();
        var xamlMembershipChanged = false;
        var knownXamlProjects = GetXamlProjects(host);
        foreach (var candidate in candidates.Where(candidate =>
                     IsXamlPath(candidate.Path)))
        {
            var owningProjects = knownXamlProjects
                .Where(project => project.FilePaths.Contains(
                    candidate.Path,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var declarationOwners = owningProjects
                .Where(project => IsDeclarationPath(project, candidate.Path))
                .ToArray();
            if (declarationOwners.Length > 0)
            {
                foreach (var xamlProject in declarationOwners)
                {
                    resourceProjectIds.Add(xamlProject.Id);
                    if (candidate.State == SourcePathState.File)
                    {
                        resourceEdits.TryAdd(
                            xamlProject.Id,
                            (xamlProject, candidate.Path));
                    }
                }

                if (candidate.State == SourcePathState.Missing)
                {
                    xamlMembershipChanged = true;
                }
                continue;
            }

            if (owningProjects.Length > 0)
            {
                // A vanished consumer still changes the immutable project membership. Refresh
                // the complete map before deleting its graph facts so later resource snapshot
                // rebuilds cannot retain and attempt to read the stale path.
                if (candidate.State == SourcePathState.Missing)
                {
                    xamlMembershipChanged = true;
                }
                continue;
            }

            if (candidate.State == SourcePathState.File)
            {
                // A newly-created/renamed XAML file is not present in the immutable project
                // membership captured by the prior discovery pass. Refresh the temporary map
                // before deciding whether the new file contributes to the resource cascade.
                xamlMembershipChanged = true;
                newXamlPaths.Add(candidate.Path);
            }
        }

        if ((existingCandidates.Length > 0 || xamlMembershipChanged)
            && !host.ProjectMapReady)
        {
            // Discover into a temporary map before mutating any file in this batch. A factory
            // failure retains both the previous project map and every prior file graph. Once a
            // complete map exists, ordinary source edits reuse its heavy project instances;
            // only control events or a pending failed discovery rebuild them.
            var projectMapResult =
                await BuildProjectMapAsync(host, ct).ConfigureAwait(false);
            if (!projectMapResult.Succeeded)
            {
                return new LanguageDispatchResult(
                    IndexedFiles: 0,
                    UsableOutputFiles: 0,
                    DeletedFiles: 0,
                    SkippedFiles: skipped + candidates.Count,
                    FailedFiles: failedFiles)
                {
                    FailedProjects = projectMapResult.FailedProjects,
                };
            }
            projectMapRebuilt = true;
        }

        if (xamlMembershipChanged && !projectMapRebuilt)
        {
            // XAML membership changes are structural for the project resource snapshot. Build
            // and publish a complete replacement map before deleting the vanished contributor
            // or indexing its replacement. Discovery failure leaves both the old map and every
            // prior consumer fact untouched.
            var projectMapResult =
                await BuildProjectMapAsync(host, ct).ConfigureAwait(false);
            if (!projectMapResult.Succeeded)
            {
                return new LanguageDispatchResult(
                    IndexedFiles: 0,
                    UsableOutputFiles: 0,
                    DeletedFiles: 0,
                    SkippedFiles: skipped + candidates.Count,
                    FailedFiles: failedFiles)
                {
                    FailedProjects = projectMapResult.FailedProjects,
                };
            }
            projectMapRebuilt = true;
        }

        var affectedResourceProjects =
            new Dictionary<string, XamlLanguageProject>(
                StringComparer.OrdinalIgnoreCase);
        if (projectMapRebuilt)
        {
            foreach (var project in GetXamlProjects(host)
                .Where(project => resourceProjectIds.Contains(project.Id)))
            {
                affectedResourceProjects[project.Id] = project;
            }
            foreach (var path in newXamlPaths)
            {
                foreach (var xamlProject in GetXamlProjects(host)
                             .Where(project =>
                                 IsDeclarationPath(project, path)))
                {
                    affectedResourceProjects[xamlProject.Id] = xamlProject;
                }
            }
        }
        else
        {
            var preparedSnapshots =
                new List<(string ProjectId, XamlLanguageProject Project,
                    XamlResourceSnapshot Snapshot)>();
            foreach (var entry in resourceEdits
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Prepare every affected project before publishing any replacement. This
                    // prevents a later project's read/parse failure from leaving an earlier
                    // project's in-memory snapshot ahead of its unchanged stored graph.
                    preparedSnapshots.Add((
                        entry.Key,
                        entry.Value.Project,
                        entry.Value.Project.PrepareResourceSnapshot()));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failedFiles.Add(new FileFailure(
                        entry.Value.TriggerPath,
                        FailureMessage.Truncate(ex.Message)));
                    _logger.LogWarning(
                        ex,
                        "Failed to rebuild XAML resource snapshot for project {Project}; "
                        + "keeping prior consumer graph facts",
                        entry.Key);
                    return new LanguageDispatchResult(
                        IndexedFiles: 0,
                        UsableOutputFiles: 0,
                        DeletedFiles: 0,
                        SkippedFiles: skipped + candidates.Count,
                        FailedFiles: failedFiles);
                }
            }

            // All builders succeeded. Publishing an already-built immutable snapshot performs no
            // I/O and cannot be cancelled halfway through the affected-project set.
            foreach (var prepared in preparedSnapshots)
            {
                prepared.Project.PublishResourceSnapshot(prepared.Snapshot);
                affectedResourceProjects[prepared.ProjectId] = prepared.Project;
            }
        }

        if (affectedResourceProjects.Count > 0)
        {
            var candidatePaths = candidates
                .Select(candidate => candidate.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var project in affectedResourceProjects.Values
                         .OrderBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(project => project.Id, StringComparer.Ordinal))
            {
                foreach (var projectPath in project.FilePaths
                             .OrderBy(
                                 NormalizePathForOrdering,
                                 StringComparer.OrdinalIgnoreCase)
                             .ThenBy(path => path, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsXamlPath(projectPath)
                        || candidatePaths.Contains(projectPath)
                        || pathPolicy.IsExcluded(projectPath)
                        || !projectSetMatcher.Includes(projectPath)
                        || GetSourcePathState(projectPath) != SourcePathState.File)
                    {
                        continue;
                    }

                    var indexerHit = _indexers.TryGet(
                        Path.GetExtension(projectPath));
                    if (indexerHit is null) continue;
                    candidates.Add(new DispatchCandidate(
                        projectPath,
                        indexerHit.Value.Indexer,
                        indexerHit.Value.Owner,
                        SourcePathState.File));
                    candidatePaths.Add(projectPath);
                }
            }
            existingCandidates = candidates
                .Where(item => item.State == SourcePathState.File)
                .ToArray();
        }

        var deleted = 0;
        foreach (var candidate in candidates.Where(item => item.State == SourcePathState.Missing))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await host.Store.DeleteFileAsync(candidate.Path, ct).ConfigureAwait(false))
                {
                    deleted++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                skipped++;
                failedFiles.Add(
                    new FileFailure(candidate.Path, FailureMessage.Truncate(ex.Message)));
                _logger.LogWarning(
                    ex,
                    "Failed to delete vanished indexed file {File}; keeping its prior graph state",
                    candidate.Path);
            }
        }

        if (existingCandidates.Length == 0)
        {
            return new LanguageDispatchResult(
                IndexedFiles: 0,
                UsableOutputFiles: 0,
                DeletedFiles: deleted,
                SkippedFiles: skipped,
                FailedFiles: failedFiles);
        }

        var existing = existingCandidates
            .OrderBy(
                candidate => GetDispatchPriority(
                    candidate.Path,
                    host.ProjectByFilePath,
                    host.LanguageProjects))
            .ThenBy(
                candidate => NormalizePathForOrdering(candidate.Path),
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ToArray();
        var dispatchCandidates = existing
            .Concat(existing.Where(candidate =>
                ShouldRetryDeclarationFile(
                    candidate.Path,
                    host.ProjectByFilePath,
                    host.LanguageProjects)))
            .ToArray();

        var symbolIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var symbolFileIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var fileIdByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await PopulateSymbolKeyMapAsync(
            host.Store,
            symbolIdByKey,
            symbolFileIdByKey,
            fileIdByPath,
            ct).ConfigureAwait(false);

        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usableOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in dispatchCandidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await DispatchOneAsync(
                    host,
                    candidate.Path,
                    candidate.Indexer,
                    candidate.Owner,
                    symbolIdByKey,
                    symbolFileIdByKey,
                    fileIdByPath,
                    pathPolicy,
                    ct).ConfigureAwait(false);
                if (outcome.Replaced) indexedPaths.Add(candidate.Path);
                if (outcome.HasUsableOutput) usableOutputPaths.Add(candidate.Path);
                if (outcome.WasSkipped) skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                skipped++;
                failedFiles.Add(
                    new FileFailure(candidate.Path, FailureMessage.Truncate(ex.Message)));
                _logger.LogWarning(
                    ex,
                    "Indexer `{Indexer}` threw on changed file {File}; keeping the batch alive",
                    candidate.Indexer.GetType().FullName,
                    candidate.Path);
                if (candidate.Owner is { } record)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage =
                        $"IndexAsync threw on `{candidate.Path}`: {ex.Message}";
                }
            }
        }

        return new LanguageDispatchResult(
            indexedPaths.Count,
            usableOutputPaths.Count,
            deleted,
            skipped,
            failedFiles);
    }

    /// <summary>
    /// Dispatch a single file. Reads the file, computes the SHA, builds the IndexContext (with
    /// the per-scope project lookup), runs the indexer, compiles the emitted events, and commits
    /// the file row plus all facts through one atomic store replacement. Running and compiling
    /// the plugin before the transaction means a failure leaves the last successful graph intact.
    /// </summary>
    private async Task<DispatchFileOutcome> DispatchOneAsync(
        ScopeHost host,
        string filePath,
        ILanguageIndexer indexer,
        PluginRecord? owner,
        Dictionary<string, long> symbolIdByKey,
        Dictionary<string, long> symbolFileIdByKey,
        Dictionary<string, long> fileIdByPath,
        ScopePathPolicy pathPolicy,
        CancellationToken ct) =>
        await DispatchOneCoreAsync(
            host.Store,
            host.Scope.Id,
            host.Scope.Root,
            host.ProjectByFilePath,
            host.LanguageProjects,
            filePath,
            indexer,
            symbolIdByKey,
            symbolFileIdByKey,
            fileIdByPath,
            pathPolicy,
            ct).ConfigureAwait(false);

    private async Task<DispatchFileOutcome> DispatchOneCoreAsync(
        IGraphStore store,
        string scopeId,
        string repoRoot,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        IReadOnlyList<ILanguageProject>? projects,
        string filePath,
        ILanguageIndexer indexer,
        Dictionary<string, long> symbolIdByKey,
        Dictionary<string, long> symbolFileIdByKey,
        Dictionary<string, long> fileIdByPath,
        ScopePathPolicy pathPolicy,
        CancellationToken ct)
    {
        if (pathPolicy.IsExcluded(filePath)) return DispatchFileOutcome.Skipped;

        byte[] contents;
        try
        {
            contents = await ReadSourceBytesAsync(
                filePath,
                indexer,
                ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return DispatchFileOutcome.Skipped;
        }
        catch (DirectoryNotFoundException)
        {
            return DispatchFileOutcome.Skipped;
        }
        catch (IOException) when (
            GetSourcePathState(filePath) == SourcePathState.Missing)
        {
            return DispatchFileOutcome.Skipped;
        }
        var sha = SHA256.HashData(contents);
        var project = SelectLanguageProject(filePath, projectMap, projects);
        var ctx = new IndexContext(
            filePath,
            contents,
            scopeId,
            repoRoot,
            project,
            pathPolicy.ConfiguredExcludePatterns);
        var languageEvents =
            await indexer.IndexAsync(ctx, ct).ConfigureAwait(false);
        IReadOnlyList<IndexEvent> events = languageEvents;
        if (_analyzerPipeline is { HasAnalyzers: true })
        {
            var analyzerEvents = await _analyzerPipeline.CollectEventsAsync(
                filePath,
                contents,
                scopeId,
                repoRoot,
                languageEvents,
                ct).ConfigureAwait(false);
            if (analyzerEvents.Count > 0)
            {
                events = languageEvents.Concat(analyzerEvents).ToArray();
            }
        }

        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in events)
        {
            if (ev is IndexEvent.SymbolDeclared sd) emittedKeys.Add(sd.CanonicalKey);
        }

        // Compile against the graph that will exist after this replacement: declarations
        // formerly owned by this file and absent from the new event set are no longer valid
        // targets, while every newly-declared key is immediately available to same-file facts.
        fileIdByPath.TryGetValue(filePath, out var priorFileId);
        var knownKeys = new HashSet<string>(symbolIdByKey.Keys, StringComparer.Ordinal);
        if (priorFileId > 0)
        {
            knownKeys.ExceptWith(symbolFileIdByKey
                .Where(pair => pair.Value == priorFileId && !emittedKeys.Contains(pair.Key))
                .Select(pair => pair.Key));
        }
        knownKeys.UnionWith(emittedKeys);
        var replacement = GraphStoreEmitter.CompileFileFacts(
            filePath,
            sha,
            events,
            knownKeys,
            _logger);
        var committed =
            await store.ReplaceFileFactsAsync(replacement, ct).ConfigureAwait(false);

        // Mutate the cross-file lookup caches only after the storage transaction committed.
        // A failed replacement therefore leaves both the database and this pass's resolver view
        // on the last successful state.
        foreach (var staleKey in symbolFileIdByKey
                     .Where(pair => pair.Value == priorFileId && !emittedKeys.Contains(pair.Key))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            symbolFileIdByKey.Remove(staleKey);
            symbolIdByKey.Remove(staleKey);
        }
        foreach (var pair in committed.SymbolIds)
        {
            symbolIdByKey[pair.Key] = pair.Value;
            symbolFileIdByKey[pair.Key] = committed.FileId;
        }
        fileIdByPath[filePath] = committed.FileId;

        var hasUsableOutput = replacement.Symbols.Count > 0
            || replacement.Edges.Count > 0
            || replacement.Annotations.Count > 0
            || replacement.References.Count > 0;
        return new DispatchFileOutcome(Replaced: true, HasUsableOutput: hasUsableOutput);
    }

    private static async Task<byte[]> ReadSourceBytesAsync(
        string filePath,
        ILanguageIndexer indexer,
        CancellationToken ct)
    {
        if (indexer is not IBoundedSourceLanguageIndexer bounded)
        {
            return await File.ReadAllBytesAsync(
                filePath,
                ct).ConfigureAwait(false);
        }

        var limit = bounded.MaximumSourceSizeBytes;
        if (limit <= 0)
        {
            throw new InvalidOperationException(
                $"Indexer `{indexer.GetType().FullName}` declared an invalid "
                + $"maximum source size of {limit} bytes.");
        }

        await using var stream = new FileStream(
            filePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81_920,
                Options = FileOptions.Asynchronous
                    | FileOptions.SequentialScan,
            });
        if (stream.CanSeek && stream.Length > limit)
        {
            throw new InvalidDataException(
                $"Source exceeds the indexer's {limit}-byte limit.");
        }

        var capacity = stream.CanSeek
            && stream.Length > 0
            && stream.Length <= limit
                ? checked((int)stream.Length)
                : 0;
        using var output = new MemoryStream(capacity);
        var buffer = new byte[Math.Min(81_920, limit)];
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(
                buffer,
                ct).ConfigureAwait(false);
            if (read == 0) break;
            if (read > limit - total)
            {
                throw new InvalidDataException(
                    $"Source exceeds the indexer's {limit}-byte limit.");
            }
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                ct).ConfigureAwait(false);
            total += read;
        }
        return output.ToArray();
    }

    private static ILanguageProject? SelectLanguageProject(
        string filePath,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        IReadOnlyList<ILanguageProject>? projects)
    {
        projectMap.TryGetValue(filePath, out var mappedProject);
        if (!IsXamlPath(filePath) || projects is null)
        {
            return mappedProject;
        }

        var normalizedPath = NormalizePathForOrdering(filePath);
        var xamlOwners = projects
            .OfType<XamlLanguageProject>()
            .Where(project => project.FilePaths.Any(path =>
                string.Equals(
                    NormalizePathForOrdering(path),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase)))
            .GroupBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(2)
            .ToArray();
        return xamlOwners.Length switch
        {
            0 => mappedProject,
            1 => xamlOwners[0],
            _ => null,
        };
    }

    private static async Task PopulateSymbolKeyMapAsync(
        IGraphStore store,
        Dictionary<string, long> map,
        Dictionary<string, long> fileIdByKey,
        Dictionary<string, long> fileIdByPath,
        CancellationToken ct)
    {
        var files = await store.GetAllFilesAsync(ct).ConfigureAwait(false);
        foreach (var file in files)
        {
            fileIdByPath[file.Path] = file.Id;
        }
        var rows = await store.GetAllSymbolKeysAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            map[row.CanonicalKey] = row.Id;
            fileIdByKey[row.CanonicalKey] = row.FileId;
        }
    }

    private static bool TryNormalizePath(string root, string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            fullPath = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, root);
            return true;
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

    private static SourcePathState GetSourcePathState(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0
                ? SourcePathState.File
                : SourcePathState.Rejected;
        }
        catch (FileNotFoundException)
        {
            return SourcePathState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return SourcePathState.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return SourcePathState.Rejected;
        }
        catch (IOException)
        {
            return SourcePathState.Rejected;
        }
        catch (NotSupportedException)
        {
            return SourcePathState.Rejected;
        }
        catch (System.Security.SecurityException)
        {
            return SourcePathState.Rejected;
        }
    }

    private static async Task<StaleDeletionResult> DeleteStaleRegisteredFilesAsync(
        IGraphStore store,
        HashSet<string> registeredExtensions,
        ScopePathPolicy pathPolicy,
        ScopeProjectSetPathMatcher? projectSetMatcher,
        CancellationToken ct)
    {
        var deleted = 0;
        var failedFiles = new List<FileFailure>();
        var indexedFiles = await store.GetAllFilesAsync(ct).ConfigureAwait(false);
        foreach (var indexedFile in indexedFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!registeredExtensions.Contains(Path.GetExtension(indexedFile.Path)))
            {
                continue;
            }

            var outsideBoundary = pathPolicy.IsExcluded(indexedFile.Path)
                || !(projectSetMatcher?.Includes(indexedFile.Path) ?? true);
            if (!outsideBoundary
                && GetSourcePathState(indexedFile.Path) != SourcePathState.Missing)
            {
                continue;
            }

            try
            {
                if (await store.DeleteFileAsync(indexedFile.Id, ct).ConfigureAwait(false))
                {
                    deleted++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failedFiles.Add(new FileFailure(
                    indexedFile.Path,
                    FailureMessage.Truncate(ex.Message)));
            }
        }
        return new StaleDeletionResult(deleted, failedFiles);
    }

    /// <summary>
    /// Give a language project the opportunity to schedule declaration-bearing documents before
    /// consumers in the same pass. Remaining files use a normalized path order so cold scans and
    /// watcher batches are deterministic regardless of filesystem or event enumeration order.
    /// </summary>
    private static IEnumerable<string> OrderDispatchFiles(
        IEnumerable<string> files,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        IReadOnlyList<ILanguageProject>? projects = null)
    {
        var ordered = files
            .OrderBy(path => GetDispatchPriority(path, projectMap, projects))
            .ThenBy(NormalizePathForOrdering, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // Declaration files can themselves consume declarations from another contributor.
        // On an empty graph their first pass publishes every declaration symbol; retrying just
        // those files after the complete declaration set exists preserves cross-declaration
        // edges without double-counting physical files in the dispatch result.
        return ordered.Concat(ordered.Where(path =>
            ShouldRetryDeclarationFile(path, projectMap, projects)));
    }

    private static bool ShouldRetryDeclarationFile(
        string path,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        IReadOnlyList<ILanguageProject>? projects)
    {
        // The second pass closes XAML resource-dictionary declaration-to-declaration edges.
        // IDeclarationFirstLanguageProject is a general scheduling contract; retrying every
        // implementation would invoke unrelated third-party indexers twice and can duplicate
        // their side effects.
        if (!IsXamlPath(path))
        {
            return false;
        }

        if (projects is not null
            && projects
                .OfType<XamlLanguageProject>()
                .Any(project => IsDeclarationPath(project, path)))
        {
            return true;
        }

        return projectMap.TryGetValue(path, out var mappedProject)
            && mappedProject is XamlLanguageProject xamlProject
            && IsDeclarationPath(xamlProject, path);
    }

    private static int GetDispatchPriority(
        string path,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        IReadOnlyList<ILanguageProject>? projects = null)
    {
        var normalizedPath = NormalizePathForOrdering(path);
        if (projects is not null
            && projects
                .OfType<IDeclarationFirstLanguageProject>()
                .Any(project => project.DeclarationFilePaths.Any(
                    declarationPath =>
                        string.Equals(
                            NormalizePathForOrdering(declarationPath),
                            normalizedPath,
                            StringComparison.OrdinalIgnoreCase))))
        {
            return 0;
        }

        if (!projectMap.TryGetValue(path, out var mappedProject)
            || mappedProject is not IDeclarationFirstLanguageProject declarationFirst)
        {
            return 1;
        }

        return declarationFirst.DeclarationFilePaths.Any(declarationPath =>
                string.Equals(
                    NormalizePathForOrdering(declarationPath),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
            ? 0
            : 1;
    }

    private static bool IsXamlPath(string path) =>
        string.Equals(
            Path.GetExtension(path),
            ".xaml",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<XamlLanguageProject> GetXamlProjects(
        ScopeHost host)
    {
        var source = host.LanguageProjects.Count > 0
            ? host.LanguageProjects
            : host.ProjectByFilePath.Values.ToArray();
        return source
            .OfType<XamlLanguageProject>()
            .GroupBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsDeclarationPath(
        IDeclarationFirstLanguageProject project,
        string path)
    {
        var normalizedPath = NormalizePathForOrdering(path);
        return project.DeclarationFilePaths.Any(declarationPath =>
            string.Equals(
                NormalizePathForOrdering(declarationPath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePathForOrdering(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return path.Replace('\\', '/');
        }
        catch (NotSupportedException)
        {
            return path.Replace('\\', '/');
        }
        catch (PathTooLongException)
        {
            return path.Replace('\\', '/');
        }
    }

    /// <summary>
    /// Walk <paramref name="root"/> for files matching any of <paramref name="extensions"/>.
    /// <see cref="ScopePathPolicy"/> prunes excluded subtrees before enumeration and rejects
    /// excluded files before they can be opened by <see cref="DispatchOneCoreAsync"/>.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(
        string root,
        HashSet<string> extensions,
        ScopePathPolicy pathPolicy,
        ScopeProjectSetPathMatcher? projectSetMatcher = null)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var stack = new Stack<string>();
        var visitedPhysicalDirectories = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        stack.Push(normalizedRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (pathPolicy.IsExcluded(dir)
                || !(projectSetMatcher?.ShouldTraverseDirectory(dir) ?? true)
                || !ScopePathPolicy.TryResolvePhysicalPath(dir, out var physicalDirectory)
                || !visitedPhysicalDirectories.Add(physicalDirectory))
            {
                continue;
            }

            IReadOnlyList<string> children;
            try
            {
                children = Directory
                    .EnumerateDirectories(dir)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            catch (System.Security.SecurityException) { continue; }

            foreach (var child in children)
            {
                if (!pathPolicy.IsExcluded(child)
                    && (projectSetMatcher?.ShouldTraverseDirectory(child) ?? true))
                {
                    stack.Push(child);
                }
            }

            IReadOnlyList<string> files;
            try
            {
                files = Directory
                    .EnumerateFiles(dir)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            catch (System.Security.SecurityException) { continue; }

            foreach (var f in files)
            {
                if (pathPolicy.IsExcluded(f)) continue;
                if (!(projectSetMatcher?.Includes(f) ?? true)) continue;
                var ext = Path.GetExtension(f);
                if (extensions.Contains(ext)) yield return f;
            }
        }
    }

    private enum SourcePathState
    {
        File,
        Missing,
        Rejected,
    }

    private sealed record DispatchCandidate(
        string Path,
        ILanguageIndexer Indexer,
        PluginRecord? Owner,
        SourcePathState State);

    private sealed record StaleDeletionResult(
        int DeletedFiles,
        IReadOnlyList<FileFailure> FailedFiles);

    private sealed record DispatchFileOutcome(
        bool Replaced,
        bool HasUsableOutput,
        bool WasSkipped = false)
    {
        public static DispatchFileOutcome Skipped { get; } = new(false, false, true);
    }
}

/// <summary>Outcome of one cold, one-shot, or live registered-language dispatch.</summary>
public sealed record LanguageDispatchResult(
    int IndexedFiles,
    int UsableOutputFiles,
    int DeletedFiles,
    int SkippedFiles,
    IReadOnlyList<FileFailure> FailedFiles)
{
    public IReadOnlyList<ProjectFailure> FailedProjects { get; init; } =
        Array.Empty<ProjectFailure>();

    public bool HasFailures => FailedFiles.Count > 0 || FailedProjects.Count > 0;

    public static LanguageDispatchResult Empty { get; } =
        new(0, 0, 0, 0, Array.Empty<FileFailure>());
}

/// <summary>
/// Result of an isolated project-discovery pass. A non-empty failure list means the temporary
/// map was not installed on the live scope host.
/// </summary>
public sealed record ProjectMapBuildResult(
    IReadOnlyDictionary<string, ILanguageProject> ProjectByFilePath,
    IReadOnlyList<ProjectFailure> FailedProjects)
{
    /// <summary>
    /// Complete discovered project set. Unlike <see cref="ProjectByFilePath"/>, this retains every
    /// owner when one physical document belongs to multiple projects.
    /// </summary>
    public IReadOnlyList<ILanguageProject> Projects { get; init; } =
        Array.Empty<ILanguageProject>();

    public bool Succeeded => FailedProjects.Count == 0;
}
