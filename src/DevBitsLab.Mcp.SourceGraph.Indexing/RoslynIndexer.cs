using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing.Interop;
using DevBitsLab.Mcp.SourceGraph.Indexing.Wpf;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation = DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Long-lived indexer that owns an MSBuildWorkspace and a symbol-key map.
/// One-shot use: <see cref="IndexSolutionOnceAsync"/>. Live use: open then call
/// <see cref="IndexAllAsync"/> once, then <see cref="IndexChangedFilesAsync"/> as files change.
///
/// <para>
/// Also implements <see cref="ILanguageIndexer"/> so the v0.6 plugin host can register the
/// built-in C# pathway alongside third-party language indexers. For <c>.cs</c> files the host
/// continues to drive the workspace-aware bulk path (<see cref="IndexAllAsync"/>); the contract's
/// per-document <see cref="ILanguageIndexer.IndexAsync"/> is implemented for completeness so a
/// chained dispatcher (e.g. tests) can still extract events from a single file without opening a
/// workspace. Routing the workspace-aware bulk path through the dispatcher would have meant
/// re-architecting <see cref="LiveIndexService"/>; the brief explicitly preserves single-solution
/// back-compat, so the dispatcher special-cases <c>.cs</c> and keeps the existing solution walk.
/// </para>
/// </summary>
public sealed class RoslynIndexer : IAsyncDisposable, ILanguageIndexer
{
    /// <summary>The single .cs extension this indexer claims.</summary>
    private static readonly IReadOnlyCollection<string> _fileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
    private static readonly StringComparer _pathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    private static readonly string[] _analysisEdgeProducer =
    [
        InteropFactProducers.Analysis,
    ];
    private const string ManagedInteropAnnotationName = "ManagedImportV1";
    private const string ManagedInteropAnnotationFullName =
        "MedInterop.ManagedImport.v1";
    private const string ManagedCallbackUsageAnnotationName =
        "ManagedCallbackUsageV1";
    private const string ManagedCallbackUsageAnnotationFullName =
        "MedInterop.ManagedCallbackUsage.v1";
    private const string ManagedReturnReleaseAnnotationName =
        "ManagedReturnReleaseV1";
    private const string ManagedReturnReleaseAnnotationFullName =
        "MedInterop.ManagedReturnRelease.v1";
    private const string ManagedAbiRecordAnnotationName = "InteropFact";
    private const string ManagedAbiRecordAnnotationFullName =
        "MedInterop.AbiRecord";
    private const int MaximumManagedAbiRecordsPerFile = 4096;
    private const int MaximumManagedInteropUsagesPerFile = 4096;
    private const int MaximumManagedImportFanoutProbeRows = 50_000;
    /// <inheritdoc />
    IReadOnlyCollection<string> ILanguageIndexer.FileExtensions => _fileExtensions;

    private readonly IGraphStore _store;
    private readonly IEmbeddingsRequestSink _embeddingsSink;
    private readonly ILogger<RoslynIndexer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string? _configuredPrivacyRoot;
    private readonly IReadOnlyList<string> _configuredExcludePatterns;
    private readonly InteropTarget? _interopTarget;
    private readonly TestHooks? _testHooks;
    private readonly ConcurrentDictionary<
        MSBuildWorkspace,
        ConcurrentQueue<WorkspaceDiagnostic>> _workspaceDiagnostics = new();

    private MSBuildWorkspace? _workspace;
    private Solution? _sanitizedSolution;
    private ScopePathPolicy? _pathPolicy;
    private string? _solutionPath;
    private bool _requiresStructuralReload;
    private bool _disposed;
    private bool _mapsHydrated;
    private readonly HashSet<string> _confirmedOutsideSolutionPaths =
        new(_pathComparer);

    private readonly Dictionary<string, long> _symbolIdByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fileIdByPath = new(_pathComparer);
    private readonly Dictionary<long, string> _storedPathByFileId = new();
    private readonly Dictionary<long, List<string>> _keysByFileId = new();

    // Set fresh by ProbeProjectCompilationsAsync at the start of every indexing pass. Read by
    // AllCSharpDocumentsAsync (to filter out failed projects' regular + source-generated docs),
    // by IndexCoreAsync (to filter inbound documents and populate IndexResult.FailedProjects),
    // and by Pass 3 (to look up compilations by ProjectId without double-calling
    // GetCompilationAsync). Stored as instance fields rather than threaded through call args
    // because the same data feeds three call sites and the indexer holds a single-thread lock
    // for the duration of a pass.
    private IReadOnlyDictionary<ProjectId, Compilation> _probedCompilations = new Dictionary<ProjectId, Compilation>();
    private IReadOnlyList<ProjectFailure> _probedFailures = Array.Empty<ProjectFailure>();
    private HashSet<ProjectId> _probedFailedProjectIds = new();
    private IReadOnlyDictionary<ProjectId, bool> _analyzerReferenceLoadCompleteByProject =
        new Dictionary<ProjectId, bool>();

    /// <summary>
    /// Fires once per (re)indexed file with <c>(fileId, fullPath, contentSha256)</c>. The Server
    /// project hooks this to enqueue work into the history pipeline; the indexer itself never
    /// touches git. <c>null</c> by default — leaving the callback unset means no history feed.
    /// </summary>
    public Func<long, string, byte[], Task>? OnFileIndexed { get; set; }

    internal sealed record WorkspaceOpenResult(
        Solution Solution,
        IReadOnlyList<WorkspaceDiagnostic> Diagnostics);

    internal sealed record TestHooks(
        Func<
            MSBuildWorkspace,
            string,
            CancellationToken,
            Task<WorkspaceOpenResult>>? OpenWorkspaceAsync = null,
        Func<string, CancellationToken, Task<byte[]>>? ReadIncrementalBytesAsync = null,
        Func<string, CancellationToken, Task<byte[]>>? ReadIndexCoreBytesAsync = null,
        Action<MSBuildWorkspace>? WorkspaceDisposed = null,
        Func<Task>? DisposeAsyncEntered = null,
        Func<SourceGeneratedDocument, string>? GeneratedOwnerIdentity = null,
        Func<SourceGeneratedDocument, string>? GeneratedDisplayPath = null,
        Action<Document>? BeforePassOneWalk = null);

    public RoslynIndexer(
        IGraphStore store,
        ILogger<RoslynIndexer>? logger = null,
        IEmbeddingsRequestSink? embeddingsSink = null,
        string? privacyRoot = null)
        : this(store, logger, embeddingsSink, privacyRoot, Array.Empty<string>())
    {
    }

    public RoslynIndexer(
        IGraphStore store,
        ILogger<RoslynIndexer>? logger,
        IEmbeddingsRequestSink? embeddingsSink,
        string? privacyRoot,
        IReadOnlyList<string>? excludePatterns,
        InteropTarget? interopTarget = null)
        : this(
            store,
            logger,
            embeddingsSink,
            privacyRoot,
            excludePatterns,
            testHooks: null,
            interopTarget: interopTarget)
    {
    }

    internal RoslynIndexer(
        IGraphStore store,
        ILogger<RoslynIndexer>? logger,
        IEmbeddingsRequestSink? embeddingsSink,
        string? privacyRoot,
        IReadOnlyList<string>? excludePatterns,
        TestHooks? testHooks,
        InteropTarget? interopTarget = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<RoslynIndexer>.Instance;
        _embeddingsSink = embeddingsSink ?? new NoOpEmbeddingsRequestSink();
        _configuredPrivacyRoot = privacyRoot is null ? null : Path.GetFullPath(privacyRoot);
        _configuredExcludePatterns = excludePatterns?.ToArray() ?? Array.Empty<string>();
        _interopTarget = interopTarget;
        _testHooks = testHooks;
    }

    public string? SolutionPath => _solutionPath;

    /// <summary>
    /// The underlying <see cref="MSBuildWorkspace"/> after <see cref="OpenAsync"/> completes.
    /// Exposed so the server can construct a <see cref="MSBuildLanguageProjectFactory"/> that
    /// surfaces every loaded project's file paths to the per-scope project lookup map without
    /// re-opening the solution.
    /// </summary>
    public MSBuildWorkspace? Workspace => _workspace;

    /// <summary>
    /// The immutable, privacy-filtered snapshot used by every indexing and project-discovery
    /// operation. Callers must not substitute <see cref="Workspace"/>'s unfiltered
    /// <see cref="MSBuildWorkspace.CurrentSolution"/>.
    /// </summary>
    public Solution? SanitizedSolution => _sanitizedSolution;

    /// <summary>
    /// Returns whether every Roslyn input for all target-framework iterations of
    /// <paramref name="projectFilePath"/> survived the scope privacy sanitizer. XAML semantic
    /// analysis uses this as a fail-closed completeness signal: a clean compilation assembled
    /// after an excluded source/additional/config document or referenced project was removed
    /// cannot prove that a binding member is missing.
    /// </summary>
    public bool IsProjectSemanticInputComplete(string projectFilePath)
    {
        var workspace = _workspace;
        var sanitized = _sanitizedSolution;
        var analyzerReferenceState = _analyzerReferenceLoadCompleteByProject;
        var requiresStructuralReload = _requiresStructuralReload;
        if (workspace is null
            || sanitized is null
            || _disposed
            || requiresStructuralReload)
        {
            return false;
        }

        var complete = IsProjectSemanticInputComplete(
            workspace.CurrentSolution,
            sanitized,
            projectFilePath,
            analyzerReferenceState);

        // A structural reload can swap either snapshot while this read-only comparison runs.
        // Treat a mixed generation as incomplete and let the next dispatch retry.
        return complete
               && ReferenceEquals(workspace, _workspace)
               && ReferenceEquals(sanitized, _sanitizedSolution)
               && ReferenceEquals(
                   analyzerReferenceState,
                   _analyzerReferenceLoadCompleteByProject)
               && requiresStructuralReload == _requiresStructuralReload;
    }

    /// <summary>
    /// Returns whether XAML may emit positive binding facts from the privacy-sanitized
    /// compilation. Build-generated documents omitted only because they live below
    /// <c>obj/</c> or <c>bin/</c> make the input incomplete, but do not invalidate a property
    /// already declared directly by an allowed source type. Every other omission fails closed.
    /// </summary>
    public bool IsProjectXamlPositiveResolutionSafe(string projectFilePath)
    {
        var workspace = _workspace;
        var sanitized = _sanitizedSolution;
        var pathPolicy = _pathPolicy;
        var analyzerReferenceState = _analyzerReferenceLoadCompleteByProject;
        var requiresStructuralReload = _requiresStructuralReload;
        if (workspace is null
            || sanitized is null
            || pathPolicy is null
            || _disposed
            || requiresStructuralReload)
        {
            return false;
        }

        var safe = IsProjectXamlPositiveResolutionSafe(
            workspace.CurrentSolution,
            sanitized,
            projectFilePath,
            pathPolicy,
            analyzerReferenceState);

        // A structural reload can swap either snapshot while this read-only comparison runs.
        // Treat a mixed generation as unsafe and let the next dispatch retry.
        return safe
               && ReferenceEquals(workspace, _workspace)
               && ReferenceEquals(sanitized, _sanitizedSolution)
               && ReferenceEquals(pathPolicy, _pathPolicy)
               && ReferenceEquals(
                   analyzerReferenceState,
                   _analyzerReferenceLoadCompleteByProject)
               && requiresStructuralReload == _requiresStructuralReload;
    }

    internal static bool IsProjectSemanticInputComplete(
        Solution rawSolution,
        Solution sanitized,
        string projectFilePath,
        IReadOnlyDictionary<ProjectId, bool>? analyzerReferenceLoadCompleteByProject = null)
        => CompareProjectSemanticInputs(
               rawSolution,
               sanitized,
               projectFilePath,
               analyzerReferenceLoadCompleteByProject,
               omittedGeneratedDocumentIsSafe: null)
           == ProjectSemanticInputState.Complete;

    internal static bool IsProjectXamlPositiveResolutionSafe(
        Solution rawSolution,
        Solution sanitized,
        string projectFilePath,
        ScopePathPolicy pathPolicy,
        IReadOnlyDictionary<ProjectId, bool>? analyzerReferenceLoadCompleteByProject = null)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);
        return CompareProjectSemanticInputs(
                   rawSolution,
                   sanitized,
                   projectFilePath,
                   analyzerReferenceLoadCompleteByProject,
                   document =>
                       IsBuildGeneratedDocument(document)
                       && !pathPolicy.IsGeneratedDocumentExcluded(
                           document.FilePath))
               != ProjectSemanticInputState.Unsafe;
    }

    private static ProjectSemanticInputState CompareProjectSemanticInputs(
        Solution rawSolution,
        Solution sanitized,
        string projectFilePath,
        IReadOnlyDictionary<ProjectId, bool>? analyzerReferenceLoadCompleteByProject,
        Func<Document, bool>? omittedGeneratedDocumentIsSafe)
    {
        ArgumentNullException.ThrowIfNull(rawSolution);
        ArgumentNullException.ThrowIfNull(sanitized);
        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            return ProjectSemanticInputState.Unsafe;
        }

        string normalizedProjectPath;
        try
        {
            normalizedProjectPath = Path.GetFullPath(projectFilePath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            return ProjectSemanticInputState.Unsafe;
        }

        var rawMatches = rawSolution.Projects
            .Where(project =>
                project.Language == LanguageNames.CSharp
                && ProjectPathMatches(project.FilePath, normalizedProjectPath))
            .ToArray();
        if (rawMatches.Length == 0)
        {
            return ProjectSemanticInputState.Unsafe;
        }

        var result = ProjectSemanticInputState.Complete;
        var pending = new Stack<Project>(rawMatches);
        var visited = new HashSet<ProjectId>();
        while (pending.Count > 0)
        {
            var rawProject = pending.Pop();
            if (!visited.Add(rawProject.Id)) continue;

            var safeProject = sanitized.GetProject(rawProject.Id);
            if (safeProject is null
                || (analyzerReferenceLoadCompleteByProject is not null
                    && (!analyzerReferenceLoadCompleteByProject.TryGetValue(
                            rawProject.Id,
                            out var analyzerReferencesComplete)
                        || !analyzerReferencesComplete)))
            {
                return ProjectSemanticInputState.Unsafe;
            }

            foreach (var document in rawProject.Documents)
            {
                if (safeProject.GetDocument(document.Id) is not null)
                {
                    continue;
                }
                if (omittedGeneratedDocumentIsSafe?.Invoke(document) != true)
                {
                    return ProjectSemanticInputState.Unsafe;
                }
                result = ProjectSemanticInputState.PositiveResolutionSafe;
            }

            if (rawProject.AdditionalDocuments.Any(document =>
                    safeProject.GetAdditionalDocument(document.Id) is null)
                || rawProject.AnalyzerConfigDocuments.Any(document =>
                    safeProject.GetAnalyzerConfigDocument(document.Id) is null)
                || rawProject.ProjectReferences.Any(reference =>
                    sanitized.GetProject(reference.ProjectId) is null
                    || !safeProject.ProjectReferences.Any(candidate =>
                        candidate.ProjectId == reference.ProjectId)))
            {
                return ProjectSemanticInputState.Unsafe;
            }

            foreach (var reference in rawProject.ProjectReferences)
            {
                var referencedProject = rawSolution.GetProject(reference.ProjectId);
                if (referencedProject is null)
                {
                    return ProjectSemanticInputState.Unsafe;
                }
                pending.Push(referencedProject);
            }
        }

        return result;

        static bool ProjectPathMatches(string? candidatePath, string expectedPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(candidatePath),
                    expectedPath,
                    _pathComparison);
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    private static bool IsBuildGeneratedDocument(Document document) =>
        SolutionPrivacySanitizer.IsBuildGeneratedDocument(document);

    private enum ProjectSemanticInputState
    {
        Unsafe,
        PositiveResolutionSafe,
        Complete,
    }

    public async Task OpenAsync(string solutionPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MSBuildHost.EnsureRegistered();
            await _store.EnsureSchemaAsync(ct).ConfigureAwait(false);

            var candidateSolutionPath = Path.GetFullPath(solutionPath);
            var privacyRoot = _configuredPrivacyRoot ?? Path.GetDirectoryName(candidateSolutionPath)
                ?? throw new InvalidOperationException("The solution path has no containing directory.");
            var candidatePathPolicy = new ScopePathPolicy(
                privacyRoot,
                _configuredExcludePatterns);
            if (candidatePathPolicy.IsExcluded(candidateSolutionPath))
            {
                throw new InvalidOperationException("The solution path is outside the indexing privacy boundary.");
            }

            var sw = Stopwatch.StartNew();
            // MSBuildWorkspace evaluates project files before Roslyn gives us a Solution to filter.
            // Sanitizing immediately after OpenSolutionAsync prevents excluded documents from
            // reaching compilation, generators, diagnostics, or persistence, but it does not
            // sandbox MSBuild evaluation itself. Untrusted solutions still need process/OS
            // isolation.
            var candidateWorkspace = CreateWorkspace();
            Solution candidateSolution;
            try
            {
                var openResult = await OpenWorkspaceSolutionAsync(
                    candidateWorkspace,
                    candidateSolutionPath,
                    ct).ConfigureAwait(false);
                ThrowIfWorkspaceLoadFailed(
                    openResult.Diagnostics,
                    _workspace is null ? "Initial" : "Re-open");
                candidateSolution = SolutionPrivacySanitizer.SanitizeForScope(
                    openResult.Solution,
                    candidatePathPolicy);
            }
            catch
            {
                DisposeWorkspace(candidateWorkspace);
                throw;
            }

            var previousWorkspace = _workspace;
            var preserveStructuralReload =
                previousWorkspace is not null && _requiresStructuralReload;
            _workspace = candidateWorkspace;
            _sanitizedSolution = candidateSolution;
            _analyzerReferenceLoadCompleteByProject =
                new Dictionary<ProjectId, bool>();
            _pathPolicy = candidatePathPolicy;
            _solutionPath = candidateSolutionPath;
            _requiresStructuralReload = preserveStructuralReload;
            _confirmedOutsideSolutionPaths.Clear();
            if (previousWorkspace is not null)
            {
                DisposeWorkspace(previousWorkspace);
            }

            _logger.LogInformation(
                "Opened {Path} ({ProjectCount} projects) in {Elapsed}",
                candidateSolutionPath,
                candidateSolution.Projects.Count(),
                sw.Elapsed);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IndexResult> IndexAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (_requiresStructuralReload)
            {
                return await ReloadAndIndexAllCoreAsync(
                    structuralCandidates: null,
                    ct).ConfigureAwait(false);
            }
            return await IndexAllCoreAsync(fullReset: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IndexResult> IndexChangedFilesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            var pathPolicy = _pathPolicy!;
            // Build an updated Solution snapshot in memory. We do NOT call
            // _workspace.TryApplyChanges — MSBuildWorkspace refuses ChangeDocument by default and
            // would throw. The local `solution` value is what IndexCoreAsync walks.
            var solution = _sanitizedSolution!;
            var pathSet = new HashSet<string>(_pathComparer);
            var pathFailures = new List<FileFailure>();
            foreach (var rawPath in paths)
            {
                ct.ThrowIfCancellationRequested();
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(rawPath);
                }
                catch (Exception ex) when (
                    ex is ArgumentException
                        or NotSupportedException
                        or PathTooLongException
                        or System.Security.SecurityException)
                {
                    AddFileFailure(
                        pathFailures,
                        rawPath ?? "<null>",
                        FailureMessage.Truncate(ex.Message));
                    continue;
                }

                if (string.Equals(
                        Path.GetExtension(fullPath),
                        ".cs",
                        StringComparison.OrdinalIgnoreCase)
                    && !pathPolicy.IsExcluded(fullPath))
                {
                    pathSet.Add(fullPath);
                }
            }
            if (pathSet.Count == 0)
            {
                return new IndexResult(0, 0, 0, TimeSpan.Zero)
                {
                    FailedFiles = pathFailures,
                };
            }

            if (_requiresStructuralReload)
            {
                var retryResult = await ReloadAndIndexAllCoreAsync(
                    pathSet,
                    ct).ConfigureAwait(false);
                return WithAdditionalFileFailures(retryResult, pathFailures);
            }

            // Roslyn's immutable Solution can accept new text for documents it already knows,
            // but it cannot discover a newly included SDK-style Compile item, and retaining a
            // deleted Document would keep stale declarations alive. Treat those changes (and a
            // rename, which arrives as delete + create) as solution-structure changes. Reload
            // while this method still owns _lock so no incremental pass can observe the gap
            // between the new workspace snapshot and its full graph reconciliation.
            var structuralPaths = pathSet
                .Where(path => IsCSharpStructureChange(solution, path))
                .ToArray();
            if (structuralPaths.Length > 0)
            {
                _requiresStructuralReload = true;
                var structuralResult = await ReloadAndIndexAllCoreAsync(
                    structuralPaths,
                    ct).ConfigureAwait(false);
                return WithAdditionalFileFailures(structuralResult, pathFailures);
            }

            var docs = new List<Document>();
            var preIndexFailures = new List<FileFailure>(pathFailures);
            var snapshotPreparation = await PrepareRegularDocumentSnapshotsAsync(
                solution,
                pathSet,
                incremental: true,
                ct).ConfigureAwait(false);
            solution = snapshotPreparation.Solution;
            var projectsWithReadFailures = snapshotPreparation.FailedProjectIds;
            foreach (var failure in snapshotPreparation.Failures)
            {
                AddFileFailure(preIndexFailures, failure.Path, failure.Reason);
            }
            if (projectsWithReadFailures.Count > 0)
            {
                _requiresStructuralReload = true;
            }

            _sanitizedSolution = solution;

            // Resolve documents from the LOCAL updated solution (not _workspace.CurrentSolution).
            var touchedProjects = new HashSet<ProjectId>();
            foreach (var p in pathSet)
            {
                var docIds = solution.GetDocumentIdsWithFilePath(p);
                foreach (var did in docIds)
                {
                    var d = solution.GetDocument(did);
                    if (d is not null
                        && d.SourceCodeKind == SourceCodeKind.Regular
                        && !projectsWithReadFailures.Contains(d.Project.Id))
                    {
                        docs.Add(d);
                        touchedProjects.Add(d.Project.Id);
                    }
                }
            }

            // Pre-flight probe over just the touched projects. The probe sets _probedFailedProjectIds
            // which IndexCoreAsync's filter consumes; touched-project failures land in the resulting
            // IndexResult.FailedProjects so LiveIndexService can flip the scope to `partial`.
            await ProbeProjectCompilationsAsync(
                touchedProjects.Select(pid => solution.GetProject(pid)).Where(p => p is not null)!,
                ct).ConfigureAwait(false);

            // For every project whose input files changed, also walk its source-generated docs.
            // The SHA gate inside IndexCoreAsync will skip generated docs whose synthesised text
            // hasn't changed (per design.md "SHA gate on generated content").
            var touchedProjectInstances = touchedProjects
                .Select(pid => solution.GetProject(pid))
                .Where(project => project is not null)
                .Cast<Project>()
                .ToArray();
            var generatedDiscovery = await DiscoverSourceGeneratedDocumentsAsync(
                touchedProjectInstances.Where(project =>
                    !_probedFailedProjectIds.Contains(project.Id)),
                ct).ConfigureAwait(false);
            docs.AddRange(generatedDiscovery.Documents);
            foreach (var failure in generatedDiscovery.Failures)
            {
                AddFileFailure(preIndexFailures, failure.Path, failure.Reason);
            }
            if (!generatedDiscovery.IsComplete)
            {
                _requiresStructuralReload = true;
            }

            IReadOnlySet<string>? generatedPathsForReconcile = null;
            var generatedReconcileUniverseComplete = false;
            var hasStaleGeneratedManagedImport = false;
            if (_interopTarget is not null
                && await HasStoredGeneratedManagedImportAsync(ct)
                    .ConfigureAwait(false))
            {
                // A generator can stop emitting an import even though the touched physical path
                // never owned that generated annotation. Discover the complete generated owner
                // universe before deciding whether import-to-caller fanout is needed.
                await ProbeProjectCompilationsAsync(
                        solution.Projects.Where(project =>
                            !projectsWithReadFailures.Contains(project.Id)),
                        ct)
                    .ConfigureAwait(false);
                _probedFailedProjectIds.UnionWith(
                    projectsWithReadFailures);
                var generatedUniverse = await AllCSharpDocumentsAsync(
                        solution,
                        ct)
                    .ConfigureAwait(false);
                generatedPathsForReconcile =
                    generatedUniverse.GeneratedPaths;
                generatedReconcileUniverseComplete =
                    generatedUniverse.IsComplete;
                foreach (var failure in generatedUniverse.Failures)
                {
                    AddFileFailure(
                        preIndexFailures,
                        failure.Path,
                        failure.Reason);
                }
                if (generatedUniverse.IsComplete)
                {
                    var currentGeneratedManagedImportPaths =
                        await GetGeneratedManagedImportPathsAsync(
                                generatedUniverse.Documents,
                                ct)
                            .ConfigureAwait(false);
                    hasStaleGeneratedManagedImport =
                        await HasStaleGeneratedManagedImportAsync(
                                currentGeneratedManagedImportPaths,
                                ct)
                            .ConfigureAwait(false);
                }
                else
                {
                    _requiresStructuralReload = true;
                }
            }

            var refreshAllManagedInteropUsages =
                _interopTarget is not null
                && (hasStaleGeneratedManagedImport
                    || await HasStoredManagedImportInPathsAsync(
                        pathSet,
                        ct)
                    .ConfigureAwait(false)
                    || await DocumentsContainManagedImportAsync(
                            docs,
                            ct)
                        .ConfigureAwait(false));
            if (refreshAllManagedInteropUsages)
            {
                // A declaration change can add, remove, or retarget an import while its callers
                // remain byte-for-byte unchanged. Re-evaluate every C# usage in the same
                // successful pass so caller-owned lifetime/ownership facts cannot become
                // permanent orphans. This fanout is deliberately conservative; it runs only
                // when the touched inputs currently or previously own a managed import.
                snapshotPreparation =
                    await PrepareRegularDocumentSnapshotsAsync(
                            solution,
                            selectedPaths: null,
                            incremental: true,
                            ct)
                        .ConfigureAwait(false);
                solution = snapshotPreparation.Solution;
                _sanitizedSolution = solution;
                foreach (var failure in snapshotPreparation.Failures)
                {
                    AddFileFailure(
                        preIndexFailures,
                        failure.Path,
                        failure.Reason);
                }
                if (snapshotPreparation.FailedProjectIds.Count > 0)
                {
                    _requiresStructuralReload = true;
                }

                await ProbeProjectCompilationsAsync(
                        solution.Projects.Where(project =>
                            !snapshotPreparation.FailedProjectIds.Contains(
                                project.Id)),
                        ct)
                    .ConfigureAwait(false);
                _probedFailedProjectIds.UnionWith(
                    snapshotPreparation.FailedProjectIds);
                var allDocuments = await AllCSharpDocumentsAsync(
                        solution,
                        ct)
                    .ConfigureAwait(false);
                docs = allDocuments.Documents.ToList();
                generatedPathsForReconcile =
                    allDocuments.GeneratedPaths;
                generatedReconcileUniverseComplete =
                    allDocuments.IsComplete;
                foreach (var failure in allDocuments.Failures)
                {
                    AddFileFailure(
                        preIndexFailures,
                        failure.Path,
                        failure.Reason);
                }
                if (!allDocuments.IsComplete)
                {
                    _requiresStructuralReload = true;
                }
            }

            var result = await IndexCoreAsync(
                docs,
                fullReset: false,
                preIndexFailures,
                snapshotPreparation.Snapshots,
                forceInteropProjectionRefresh:
                    refreshAllManagedInteropUsages,
                ct).ConfigureAwait(false);
            if (generatedPathsForReconcile is not null)
            {
                if (generatedReconcileUniverseComplete
                    && result.FailedProjects.Count == 0
                    && result.FailedFiles.Count == 0)
                {
                    await ReconcileGeneratedFilesAsync(
                            generatedPathsForReconcile,
                            ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    _requiresStructuralReload = true;
                }
            }
            if (result.FailedProjects.Count > 0
                || result.FailedFiles.Count > 0)
            {
                // Any incomplete incremental result must schedule a complete-universe retry.
                // Otherwise LiveIndexService's managed interop completeness bit would remain
                // false forever after a one-shot Pass 1/reconciliation/diagnostic failure.
                _requiresStructuralReload = true;
            }
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IndexResult> ReloadAndIndexAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            _confirmedOutsideSolutionPaths.Clear();
            _requiresStructuralReload = true;
            return await ReloadAndIndexAllCoreAsync(
                structuralCandidates: null,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IndexResult> IndexAllCoreAsync(bool fullReset, CancellationToken ct)
    {
        if (_interopTarget is null)
        {
            await ClearDisabledManagedInteropProjectionAsync(ct)
                .ConfigureAwait(false);
        }

        var solution = SolutionPrivacySanitizer.SanitizeForScope(
            _sanitizedSolution!,
            _pathPolicy!);
        var snapshotPreparation = await PrepareRegularDocumentSnapshotsAsync(
            solution,
            selectedPaths: null,
            incremental: false,
            ct).ConfigureAwait(false);
        solution = snapshotPreparation.Solution;
        _sanitizedSolution = solution;
        await ProbeProjectCompilationsAsync(
            solution.Projects.Where(project =>
                !snapshotPreparation.FailedProjectIds.Contains(project.Id)),
            ct).ConfigureAwait(false);
        _probedFailedProjectIds.UnionWith(snapshotPreparation.FailedProjectIds);
        var discovery = await AllCSharpDocumentsAsync(solution, ct).ConfigureAwait(false);
        if (!discovery.IsComplete)
        {
            _requiresStructuralReload = true;
        }
        var initialFailures = snapshotPreparation.Failures.ToList();
        foreach (var failure in discovery.Failures)
        {
            AddFileFailure(initialFailures, failure.Path, failure.Reason);
        }
        var result = await IndexCoreAsync(
            discovery.Documents,
            fullReset,
            initialFailures,
            snapshotPreparation.Snapshots,
            forceInteropProjectionRefresh: _interopTarget is not null,
            ct).ConfigureAwait(false);
        var passIsComplete =
            discovery.IsComplete
            && result.FailedProjects.Count == 0
            && result.FailedFiles.Count == 0;
        if (passIsComplete)
        {
            await ReconcileGeneratedFilesAsync(
                discovery.GeneratedPaths,
                ct).ConfigureAwait(false);
            result = result with
            {
                ReconciledCompleteUniverse = true,
            };
        }
        else
        {
            // Complete discovery only proves that we know the current owner set. A later
            // phase may still have failed before it rebuilt an owner's symbols/references.
            // Deleting prior-workspace owners in that state would turn a visible partial
            // result into irreversible graph loss. Retain them until one complete pass has
            // both discovered and indexed every current owner successfully.
            _requiresStructuralReload = true;
        }
        return result;
    }

    private async Task ClearDisabledManagedInteropProjectionAsync(
        CancellationToken ct)
    {
        const int pageSize = 1000;
        var flavorsByPath = new Dictionary<string, HashSet<string>>(
            _pathComparer);
        foreach (var flavor in new[]
                 {
                     InteropAnnotationFlavors.ManagedImport,
                     InteropAnnotationFlavors.ManagedCallbackUsage,
                     InteropAnnotationFlavors.ManagedReturnRelease,
                     InteropAnnotationFlavors.AbiRecord,
                 })
        {
            long afterId = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _store.ListAnnotationsByFlavorAsync(
                        flavor,
                        afterId,
                        pageSize,
                        ct)
                    .ConfigureAwait(false);
                foreach (var row in page)
                {
                    if (!row.SymbolCanonicalKey.StartsWith(
                            SymbolMapping.CanonicalKeyScheme,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!flavorsByPath.TryGetValue(
                            row.FilePath,
                            out var fileFlavors))
                    {
                        fileFlavors = new HashSet<string>(
                            StringComparer.Ordinal);
                        flavorsByPath.Add(row.FilePath, fileFlavors);
                    }
                    fileFlavors.Add(flavor);
                }
                if (page.Count < pageSize) break;
                afterId = page[^1].AnnotationId;
            }
        }

        foreach (var pair in flavorsByPath
                     .OrderBy(item => item.Key, _pathComparer)
                     .ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            foreach (var flavor in pair.Value.OrderBy(
                         value => value,
                         StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                await _store.ReplaceAnnotationsForFileByFlavorAsync(
                        pair.Key,
                        flavor,
                        [],
                        ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private long? ResolveManagedImportFileId(
        IMethodSymbol method,
        IReadOnlyDictionary<SyntaxTree, long>? currentSyntaxTreeOwners = null)
    {
        foreach (var location in method.Locations.Where(item => item.IsInSource))
        {
            if (location.SourceTree is { } sourceTree
                && currentSyntaxTreeOwners is not null
                && currentSyntaxTreeOwners.TryGetValue(
                    sourceTree,
                    out var currentOwnerFileId))
            {
                return currentOwnerFileId;
            }

            var path = location.SourceTree?.FilePath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (_fileIdByPath.TryGetValue(path, out var fileId))
            {
                return fileId;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (_fileIdByPath.TryGetValue(fullPath, out fileId))
                {
                    return fileId;
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                // A malformed source location cannot own a trustworthy persisted fact.
            }
        }
        return null;
    }

    private async Task<bool> HasStoredManagedImportInPathsAsync(
        IReadOnlySet<string> paths,
        CancellationToken ct)
    {
        long afterId = 0;
        var rowsRead = 0;
        const int pageSize = 1000;
        while (rowsRead < MaximumManagedImportFanoutProbeRows)
        {
            ct.ThrowIfCancellationRequested();
            var limit = Math.Min(
                pageSize,
                MaximumManagedImportFanoutProbeRows - rowsRead);
            var page = await _store.ListAnnotationsByFlavorAsync(
                    InteropAnnotationFlavors.ManagedImport,
                    afterId,
                    limit,
                    ct)
                .ConfigureAwait(false);
            if (page.Count == 0)
            {
                return false;
            }
            foreach (var row in page)
            {
                rowsRead++;
                afterId = row.AnnotationId;
                if (paths.Contains(row.FilePath))
                {
                    return true;
                }
            }
            if (page.Count < limit)
            {
                return false;
            }
        }

        // An over-bound fact universe cannot prove that none of the remaining rows belongs to a
        // touched file. Fan out conservatively instead of publishing stale caller facts.
        var probe = await _store.ListAnnotationsByFlavorAsync(
                InteropAnnotationFlavors.ManagedImport,
                afterId,
                limit: 1,
                ct)
            .ConfigureAwait(false);
        return probe.Count > 0;
    }

    private async Task<bool> HasStoredGeneratedManagedImportAsync(
        CancellationToken cancellationToken)
    {
        var generatedPaths = (await _store.ListGeneratedFilesAsync(
                    int.MaxValue,
                    cancellationToken)
                .ConfigureAwait(false))
            .Select(file => file.FilePath)
            .ToHashSet(_pathComparer);
        if (generatedPaths.Count == 0)
        {
            return false;
        }
        return await HasStoredManagedImportInPathsAsync(
                generatedPaths,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> HasStaleGeneratedManagedImportAsync(
        IReadOnlySet<string> currentGeneratedManagedImportPaths,
        CancellationToken cancellationToken)
    {
        // A generated owner can disappear entirely or keep the same stable path while changing
        // from an import declaration to ordinary generated code. Both transitions invalidate
        // unchanged caller-owned usage facts.
        var generatedPathsWithoutCurrentImport =
            (await _store.ListGeneratedFilesAsync(
                    int.MaxValue,
                    cancellationToken)
                .ConfigureAwait(false))
            .Select(file => file.FilePath)
            .Where(path => !currentGeneratedManagedImportPaths.Contains(path))
            .ToHashSet(_pathComparer);
        return generatedPathsWithoutCurrentImport.Count > 0
            && await HasStoredManagedImportInPathsAsync(
                    generatedPathsWithoutCurrentImport,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<IReadOnlySet<string>> GetGeneratedManagedImportPathsAsync(
        IReadOnlyList<Document> documents,
        CancellationToken ct)
    {
        var paths = new HashSet<string>(_pathComparer);
        foreach (var document in documents.OfType<SourceGeneratedDocument>())
        {
            ct.ThrowIfCancellationRequested();
            if (await DocumentContainsManagedImportAsync(document, ct)
                    .ConfigureAwait(false))
            {
                paths.Add(GetGeneratedStoragePath(document));
            }
        }
        return paths;
    }

    private async Task<bool> DocumentsContainManagedImportAsync(
        IReadOnlyList<Document> documents,
        CancellationToken ct)
    {
        if (_interopTarget is null)
        {
            return false;
        }

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (await DocumentContainsManagedImportAsync(document, ct)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<IReadOnlyList<FileAnnotationFact>?>
        TryBuildExpectedManagedInteropProjectionAsync(
            string producingFilePath,
            long producingFileId,
            IReadOnlyList<Document> documents,
            IReadOnlySet<string> ownedCanonicalKeys,
            CancellationToken ct)
    {
        var importPayloadByKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var abiRecordPayloadByKey =
            new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            var root = await document.GetSyntaxRootAsync(ct)
                .ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(ct)
                .ConfigureAwait(false);
            if (root is null || model is null)
            {
                return null;
            }

            foreach (var declaration in EnumerateDeclarations(root))
            {
                ct.ThrowIfCancellationRequested();
                var symbol = model.GetDeclaredSymbol(declaration, ct);
                if (symbol is null || !SymbolMapping.IsIndexable(symbol))
                {
                    continue;
                }
                var key = SymbolMapping.CanonicalKey(symbol);
                if (key is null)
                {
                    continue;
                }

                if (symbol is IMethodSymbol interopMethod)
                {
                    var import = ManagedInteropExtractor.TryExtract(
                        interopMethod,
                        _interopTarget!,
                        producingFileId,
                        producingFilePath);
                    if (import is not null)
                    {
                        var payload =
                            InteropFactPayloadCodec.EncodeManagedImport(import);
                        if (importPayloadByKey.TryGetValue(
                                import.SymbolCanonicalKey,
                                out var previousPayload)
                            && !string.Equals(
                                previousPayload,
                                payload,
                                StringComparison.Ordinal))
                        {
                            return null;
                        }
                        importPayloadByKey[import.SymbolCanonicalKey] = payload;
                    }
                }

                if (symbol is INamedTypeSymbol
                    {
                        TypeKind: TypeKind.Struct,
                    } interopRecord)
                {
                    var layout = ManagedRecordLayoutExtractor.TryExtract(
                        interopRecord,
                        _interopTarget!,
                        producingFileId);
                    var payload = layout is null
                        ? null
                        : InteropFactPayloadCodec.EncodeAbiRecord(layout);
                    if (abiRecordPayloadByKey.TryGetValue(
                            key,
                            out var previousPayload)
                        && !string.Equals(
                            previousPayload,
                            payload,
                            StringComparison.Ordinal))
                    {
                        return null;
                    }
                    if (!abiRecordPayloadByKey.ContainsKey(key)
                        && abiRecordPayloadByKey.Count
                            >= MaximumManagedAbiRecordsPerFile)
                    {
                        return null;
                    }
                    abiRecordPayloadByKey[key] = payload;
                }
            }
        }

        var expected = importPayloadByKey
            .Where(pair => ownedCanonicalKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new FileAnnotationFact(
                pair.Key,
                ManagedInteropAnnotationName,
                ManagedInteropAnnotationFullName,
                InteropAnnotationFlavors.ManagedImport,
                pair.Value,
                AttributeCanonicalKey: null))
            .ToList();
        expected.AddRange(abiRecordPayloadByKey
            .Where(pair =>
                pair.Value is not null
                && ownedCanonicalKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new FileAnnotationFact(
                pair.Key,
                ManagedAbiRecordAnnotationName,
                ManagedAbiRecordAnnotationFullName,
                InteropAnnotationFlavors.AbiRecord,
                pair.Value,
                AttributeCanonicalKey: null)));
        return expected;
    }

    private async Task<IReadOnlySet<string>>
        FindManagedInteropProjectionRefreshPathsAsync(
            IReadOnlyList<(
                string StoragePath,
                string DisplayPath,
                bool IsGenerated,
                List<Document> Documents)> documentGroups,
            CancellationToken ct)
    {
        var refreshPaths = new HashSet<string>(_pathComparer);
        var expectedByPath = new Dictionary<
            string,
            (long FileId, IReadOnlyList<FileAnnotationFact> Facts)>(
            _pathComparer);

        foreach (var group in documentGroups)
        {
            ct.ThrowIfCancellationRequested();
            if (!_fileIdByPath.TryGetValue(group.StoragePath, out var fileId)
                || !_keysByFileId.TryGetValue(fileId, out var ownedKeys))
            {
                // New or otherwise unhydrated owners already bypass the SHA gate.
                continue;
            }

            try
            {
                var expected =
                    await TryBuildExpectedManagedInteropProjectionAsync(
                            group.StoragePath,
                            fileId,
                            group.Documents,
                            ownedKeys.ToHashSet(StringComparer.Ordinal),
                            ct)
                        .ConfigureAwait(false);
                if (expected is null)
                {
                    refreshPaths.Add(group.StoragePath);
                    continue;
                }
                expectedByPath[group.StoragePath] = (fileId, expected);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A source-side integrity probe is advisory only. Re-walk the owner through the
                // normal per-file failure boundary instead of trusting an unverifiable SHA skip.
                _logger.LogDebug(
                    ex,
                    "Re-walking {Path}: managed interop projection could not be verified",
                    group.StoragePath);
                refreshPaths.Add(group.StoragePath);
            }
        }

        if (expectedByPath.Count == 0)
        {
            return refreshPaths;
        }

        var expectedCount =
            expectedByPath.Values.Sum(item => (long)item.Facts.Count);
        if (expectedCount >= int.MaxValue)
        {
            refreshPaths.UnionWith(expectedByPath.Keys);
            return refreshPaths;
        }
        var queryLimit = checked((int)expectedCount + 1);
        var stored = await _store.ListAnnotationsForFilesByFlavorsAsync(
                expectedByPath.Keys.ToArray(),
                [
                    InteropAnnotationFlavors.ManagedImport,
                    InteropAnnotationFlavors.AbiRecord,
                ],
                queryLimit,
                ct)
            .ConfigureAwait(false);
        if (stored.Count == queryLimit)
        {
            // More rows exist than the complete Roslyn projection can contain. Rebuild all
            // queried owners because the bounded result cannot identify every extra owner.
            refreshPaths.UnionWith(expectedByPath.Keys);
            return refreshPaths;
        }

        var storedByPath = stored.ToLookup(
            row => row.FilePath,
            _pathComparer);
        foreach (var pair in expectedByPath)
        {
            if (!ManagedInteropProjectionMatches(
                    pair.Key,
                    pair.Value.FileId,
                    pair.Value.Facts,
                    storedByPath[pair.Key]))
            {
                refreshPaths.Add(pair.Key);
            }
        }
        return refreshPaths;
    }

    private static bool ManagedInteropProjectionMatches(
        string expectedFilePath,
        long expectedFileId,
        IReadOnlyList<FileAnnotationFact> expected,
        IEnumerable<StoredAnnotationRow> stored)
    {
        var actualByIdentity =
            new Dictionary<
                (string CanonicalKey, string Flavor),
                StoredAnnotationRow>();
        foreach (var row in stored)
        {
            if (!actualByIdentity.TryAdd(
                    (row.SymbolCanonicalKey, row.Flavor),
                    row))
            {
                return false;
            }
        }
        if (actualByIdentity.Count != expected.Count)
        {
            return false;
        }

        foreach (var fact in expected)
        {
            if (!actualByIdentity.TryGetValue(
                    (fact.SymbolCanonicalKey, fact.Flavor),
                    out var actual)
                || actual.FileId != expectedFileId
                || !string.Equals(
                    actual.FilePath,
                    expectedFilePath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    actual.Name,
                    fact.Name,
                    StringComparison.Ordinal)
                || !string.Equals(
                    actual.FullName,
                    fact.FullName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    actual.ArgsJson,
                    fact.ArgsJson,
                    StringComparison.Ordinal)
                || actual.AttributeSymbolId is not null)
            {
                return false;
            }
        }
        return true;
    }

    private async Task<bool> DocumentContainsManagedImportAsync(
        Document document,
        CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct)
            .ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct)
            .ConfigureAwait(false);
        if (root is null || model is null)
        {
            return false;
        }
        foreach (var declaration in root.DescendantNodes()
                     .OfType<MethodDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetDeclaredSymbol(
                    declaration,
                    ct)
                is not IMethodSymbol method)
            {
                continue;
            }
            if (ManagedInteropExtractor.TryExtract(
                    method,
                    _interopTarget!,
                    producingFileId: 1)
                is not null)
            {
                return true;
            }
        }
        return false;
    }

    private static void MergeManagedUsageProjection(
        ManagedInteropUsageExtraction extraction,
        IDictionary<string, FileAnnotationFact> annotationsByIdentity,
        ref HashSet<string>? firstTargetFrameworkProjection)
    {
        var currentProjection =
            new Dictionary<string, FileAnnotationFact>(
                StringComparer.Ordinal);
        foreach (var callback in extraction.CallbackUsages)
        {
            var payload =
                InteropFactPayloadCodec.EncodeManagedCallbackUsage(callback);
            var fact = new FileAnnotationFact(
                callback.Usage.CallerSymbolCanonicalKey,
                ManagedCallbackUsageAnnotationName,
                ManagedCallbackUsageAnnotationFullName,
                InteropAnnotationFlavors.ManagedCallbackUsage,
                payload,
                AttributeCanonicalKey: null);
            currentProjection.TryAdd(fact.Flavor + "\0" + payload, fact);
        }
        foreach (var release in extraction.ReturnReleases)
        {
            var payload =
                InteropFactPayloadCodec.EncodeManagedReturnRelease(release);
            var fact = new FileAnnotationFact(
                release.Release.CallerSymbolCanonicalKey,
                ManagedReturnReleaseAnnotationName,
                ManagedReturnReleaseAnnotationFullName,
                InteropAnnotationFlavors.ManagedReturnRelease,
                payload,
                AttributeCanonicalKey: null);
            currentProjection.TryAdd(fact.Flavor + "\0" + payload, fact);
        }
        if (currentProjection.Count > MaximumManagedInteropUsagesPerFile)
        {
            throw new InvalidOperationException(
                "Managed interop usage count exceeds the "
                + $"{MaximumManagedInteropUsagesPerFile}-item per-file limit.");
        }

        var currentIdentities = currentProjection.Keys.ToHashSet(
            StringComparer.Ordinal);
        if (firstTargetFrameworkProjection is null)
        {
            firstTargetFrameworkProjection = currentIdentities;
            foreach (var pair in currentProjection)
            {
                annotationsByIdentity.Add(pair.Key, pair.Value);
            }
            return;
        }
        if (!firstTargetFrameworkProjection.SetEquals(currentIdentities))
        {
            throw new InvalidOperationException(
                "Managed interop usages have conflicting "
                + "target-framework projections.");
        }
    }

    private async Task<IReadOnlyList<FileAnnotationFact>>
        ExtractManagedUsageAnnotationsAsync(
            long fileId,
            string producingFilePath,
            IReadOnlyList<Document> documents,
            Func<IMethodSymbol, long?> importFileIdResolver,
            CancellationToken cancellationToken)
    {
        var annotationsByIdentity =
            new Dictionary<string, FileAnnotationFact>(
                StringComparer.Ordinal);
        HashSet<string>? firstTargetFrameworkProjection = null;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = await document.GetSyntaxTreeAsync(cancellationToken)
                .ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (tree is null || model is null)
            {
                throw new InvalidOperationException(
                    "Roslyn returned no syntax tree or semantic model "
                    + "during managed interop usage refresh.");
            }
            var root = await tree.GetRootAsync(cancellationToken)
                .ConfigureAwait(false);
            var extraction = ManagedInteropUsageExtractor.Extract(
                root,
                model,
                _interopTarget
                    ?? throw new InvalidOperationException(
                        "Managed interop usage refresh has no target."),
                fileId,
                producingFilePath,
                importFileIdResolver,
                ownerFileId => _storedPathByFileId.TryGetValue(
                    ownerFileId,
                    out var ownerPath)
                    ? ownerPath
                    : null,
                cancellationToken);
            MergeManagedUsageProjection(
                extraction,
                annotationsByIdentity,
                ref firstTargetFrameworkProjection);
        }

        return annotationsByIdentity
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToArray();
    }

    private async Task<IReadOnlyList<FileFailure>>
        RefreshUnchangedManagedInteropUsagesAsync(
            IReadOnlyDictionary<
                long,
                (string Path, List<Document> Documents)> refreshes,
            Func<IMethodSymbol, long?> importFileIdResolver,
            CancellationToken cancellationToken)
    {
        if (refreshes.Count == 0)
        {
            return [];
        }

        var failures = new List<FileFailure>();
        var projections =
            new List<FileDerivedProjectionReplacement>(refreshes.Count);
        foreach (var (fileId, refresh) in refreshes
                     .OrderBy(pair => pair.Value.Path, _pathComparer)
                     .ThenBy(pair => pair.Value.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var annotations =
                    await ExtractManagedUsageAnnotationsAsync(
                            fileId,
                            refresh.Path,
                            refresh.Documents,
                            importFileIdResolver,
                            cancellationToken)
                        .ConfigureAwait(false);
                var ownedKeys = (await _store.ListSymbolsInFileAsync(
                            refresh.Path,
                            cancellationToken)
                        .ConfigureAwait(false))
                    .Select(symbol => symbol.CanonicalKey)
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
                projections.Add(new FileDerivedProjectionReplacement(
                    refresh.Path,
                    ManagedInteropUsageExtractor.Producer,
                    [
                        InteropAnnotationFlavors.ManagedCallbackUsage,
                        InteropAnnotationFlavors.ManagedReturnRelease,
                    ],
                    annotations
                        .Where(annotation =>
                            ownedKeys.Contains(
                                annotation.SymbolCanonicalKey))
                        .ToArray(),
                    Edges: []));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AddFileFailure(
                    failures,
                    refresh.Path,
                    "managed interop usage refresh failed: "
                    + FailureMessage.Truncate(ex.Message));
            }
        }

        if (failures.Count > 0)
        {
            return failures;
        }

        try
        {
            await _store.ReplaceFileDerivedProjectionsAsync(
                    projections,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddFileFailure(
                failures,
                projections[0].ProducingFilePath,
                "managed interop usage publication failed: "
                + FailureMessage.Truncate(ex.Message));
        }
        return failures;
    }

    /// <summary>
    /// Reloads the solution and performs a forced full graph pass. The caller must own
    /// <see cref="_lock"/>; keeping reload and indexing in one critical section prevents another
    /// live batch from indexing against the replacement snapshot before stale files are removed.
    /// </summary>
    private async Task<IndexResult> ReloadAndIndexAllCoreAsync(
        IReadOnlyCollection<string>? structuralCandidates,
        CancellationToken ct)
    {
        _requiresStructuralReload = true;
        var slnPath = _solutionPath!;
        var pathPolicy = _pathPolicy!;
        if (pathPolicy.IsExcluded(slnPath))
        {
            throw new InvalidOperationException(
                "The solution path is outside the indexing privacy boundary.");
        }

        ct.ThrowIfCancellationRequested();
        var previousPaths = RegularCSharpDocumentPaths(_sanitizedSolution!, pathPolicy);
        var replacementWorkspace = CreateWorkspace();
        Solution replacementSolution;
        try
        {
            var openResult = await OpenWorkspaceSolutionAsync(
                replacementWorkspace,
                slnPath,
                ct).ConfigureAwait(false);
            ThrowIfWorkspaceLoadFailed(openResult.Diagnostics, "Replacement");
            replacementSolution = SolutionPrivacySanitizer.SanitizeForScope(
                openResult.Solution,
                pathPolicy);
        }
        catch
        {
            DisposeWorkspace(replacementWorkspace);
            throw;
        }

        var replacementPaths = RegularCSharpDocumentPaths(replacementSolution, pathPolicy);
        var previousWorkspace = _workspace!;
        var previousSolution = _sanitizedSolution!;
        var previousAnalyzerReferenceState =
            _analyzerReferenceLoadCompleteByProject;
        _workspace = replacementWorkspace;
        _sanitizedSolution = replacementSolution;
        _analyzerReferenceLoadCompleteByProject =
            new Dictionary<ProjectId, bool>();
        IndexResult result;
        try
        {
            if (!_mapsHydrated)
            {
                await HydrateMapsFromStoreAsync(ct).ConfigureAwait(false);
            }
            foreach (var removedPath in previousPaths.Except(
                         replacementPaths,
                         _pathComparer))
            {
                ct.ThrowIfCancellationRequested();
                if (_fileIdByPath.TryGetValue(removedPath, out var removedFileId)
                    && _storedPathByFileId.TryGetValue(removedFileId, out var storedPath))
                {
                    await _store.DeleteFileAsync(storedPath, ct).ConfigureAwait(false);
                    DropFileFromMaps(removedFileId);
                }
                else
                {
                    await _store.DeleteFileAsync(removedPath, ct).ConfigureAwait(false);
                }
            }

            // fullReset deliberately bypasses the SHA gate for every surviving document. That is
            // required after a structural change because deleting one declaration can invalidate
            // references and edges produced by otherwise byte-identical files.
            result = await IndexAllCoreAsync(fullReset: true, ct).ConfigureAwait(false);
        }
        catch
        {
            // Keep the previous structural snapshot retryable. The graph pass can have made
            // partial progress under its existing cancellation semantics, but restoring these
            // fields means the same add/delete/rename paths still compare as structural on the
            // next watcher batch and therefore force another reload + full reconciliation.
            _workspace = previousWorkspace;
            _sanitizedSolution = previousSolution;
            _analyzerReferenceLoadCompleteByProject =
                previousAnalyzerReferenceState;
            DisposeWorkspace(replacementWorkspace);
            throw;
        }

        DisposeWorkspace(previousWorkspace);
        foreach (var includedPath in replacementPaths)
        {
            _confirmedOutsideSolutionPaths.Remove(includedPath);
        }
        if (structuralCandidates is not null)
        {
            foreach (var candidate in structuralCandidates)
            {
                if (File.Exists(candidate)
                    && !replacementPaths.Contains(candidate))
                {
                    _confirmedOutsideSolutionPaths.Add(candidate);
                }
            }
        }
        _requiresStructuralReload =
            result.FailedProjects.Count > 0 ||
            result.FailedFiles.Count > 0;
        return result;
    }

    private MSBuildWorkspace CreateWorkspace()
    {
        var workspace = MSBuildWorkspace.Create();
        var diagnostics = new ConcurrentQueue<WorkspaceDiagnostic>();
        _workspaceDiagnostics[workspace] = diagnostics;
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            diagnostics.Enqueue(e.Diagnostic);
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                _logger.LogWarning("Workspace failure: {Message}", e.Diagnostic.Message);
            }
            else
            {
                _logger.LogInformation("Workspace warning: {Message}", e.Diagnostic.Message);
            }
        });
        return workspace;
    }

    private async Task<WorkspaceOpenResult> OpenWorkspaceSolutionAsync(
        MSBuildWorkspace workspace,
        string solutionPath,
        CancellationToken ct)
    {
        WorkspaceOpenResult result;
        if (_testHooks?.OpenWorkspaceAsync is { } testOpen)
        {
            result = await testOpen(workspace, solutionPath, ct).ConfigureAwait(false);
        }
        else
        {
            var solution = await workspace.OpenSolutionAsync(
                solutionPath,
                cancellationToken: ct).ConfigureAwait(false);
            result = new WorkspaceOpenResult(
                solution,
                Array.Empty<WorkspaceDiagnostic>());
        }

        var combinedDiagnostics = result.Diagnostics
            .Concat(SnapshotWorkspaceDiagnostics(workspace))
            .DistinctBy(diagnostic => (diagnostic.Kind, diagnostic.Message))
            .ToList();
        return result with
        {
            Diagnostics = combinedDiagnostics,
        };
    }

    private IReadOnlyList<WorkspaceDiagnostic> SnapshotWorkspaceDiagnostics(
        MSBuildWorkspace workspace)
    {
        if (!_workspaceDiagnostics.TryGetValue(workspace, out var diagnostics))
        {
            return Array.Empty<WorkspaceDiagnostic>();
        }
        return diagnostics.ToArray();
    }

    private void DisposeWorkspace(MSBuildWorkspace workspace)
    {
        _workspaceDiagnostics.TryRemove(workspace, out _);
        try
        {
            workspace.Dispose();
        }
        finally
        {
            _testHooks?.WorkspaceDisposed?.Invoke(workspace);
        }
    }

    private static void ThrowIfWorkspaceLoadFailed(
        IReadOnlyList<WorkspaceDiagnostic> diagnostics,
        string phase)
    {
        var failureDiagnostics = diagnostics
            .Where(diagnostic =>
                diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            .ToList();
        if (failureDiagnostics.Count == 0)
        {
            return;
        }
        throw new InvalidOperationException(
            $"{phase} workspace load was incomplete: " +
            string.Join(
                " | ",
                failureDiagnostics.Select(diagnostic =>
                    FailureMessage.Truncate(diagnostic.Message))));
    }

    private bool IsCSharpStructureChange(Solution solution, string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isKnownDocument = !solution.GetDocumentIdsWithFilePath(path).IsEmpty;
        if (isKnownDocument)
        {
            _confirmedOutsideSolutionPaths.Remove(path);
            return !File.Exists(path);
        }

        if (!File.Exists(path))
        {
            _confirmedOutsideSolutionPaths.Remove(path);
            return false;
        }
        return !_confirmedOutsideSolutionPaths.Contains(path);
    }

    private static HashSet<string> RegularCSharpDocumentPaths(
        Solution solution,
        ScopePathPolicy pathPolicy)
    {
        return solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .SelectMany(project => project.Documents)
            .Where(document =>
                document.SourceCodeKind == SourceCodeKind.Regular &&
                !string.IsNullOrEmpty(document.FilePath) &&
                !pathPolicy.IsExcluded(document.FilePath))
            .Select(document => Path.GetFullPath(document.FilePath!))
            .ToHashSet(_pathComparer);
    }

    private async Task<RegularSnapshotPreparation> PrepareRegularDocumentSnapshotsAsync(
        Solution solution,
        IReadOnlySet<string>? selectedPaths,
        bool incremental,
        CancellationToken ct)
    {
        var snapshots = new Dictionary<string, RegularDocumentSnapshot>(
            _pathComparer);
        var failures = new List<FileFailure>();
        var failedProjectIds = new HashSet<ProjectId>();
        var documentGroups = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .SelectMany(project => project.Documents)
            .Where(document =>
                document.SourceCodeKind == SourceCodeKind.Regular
                && !string.IsNullOrEmpty(document.FilePath)
                && !_pathPolicy!.IsExcluded(document.FilePath)
                && (selectedPaths is null
                    || selectedPaths.Contains(document.FilePath)))
            .GroupBy(
                document => document.FilePath!,
                _pathComparer)
            .ToArray();

        var updatedSolution = solution;
        foreach (var documentsForPath in documentGroups)
        {
            ct.ThrowIfCancellationRequested();
            var path = documentsForPath.Key;
            var documents = documentsForPath.ToArray();
            if (!File.Exists(path))
            {
                foreach (var document in documents)
                {
                    failedProjectIds.Add(document.Project.Id);
                }
                AddFileFailure(
                    failures,
                    path,
                    "file disappeared before its content snapshot was captured");
                continue;
            }

            byte[] bytes;
            SourceText text;
            try
            {
                bytes = await ReadRegularDocumentBytesAsync(
                    path,
                    incremental,
                    ct).ConfigureAwait(false);
                // Passing null lets Roslyn honor a BOM when present and otherwise use UTF-8.
                // The exact original bytes remain alongside the decoded SourceText for hashing.
                text = SourceText.From(
                    bytes,
                    bytes.Length,
                    encoding: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableRegularSnapshotFailure(ex))
            {
                _logger.LogDebug(
                    ex,
                    "Skipping {Path} for this batch (snapshot read failed; will retry)",
                    path);
                foreach (var document in documents)
                {
                    failedProjectIds.Add(document.Project.Id);
                }
                AddFileFailure(
                    failures,
                    path,
                    FailureMessage.Truncate(ex.Message));
                continue;
            }

            foreach (var document in documents)
            {
                updatedSolution = updatedSolution.WithDocumentText(
                    document.Id,
                    text);
            }
            snapshots[path] = new RegularDocumentSnapshot(bytes, text);
        }

        return new RegularSnapshotPreparation(
            updatedSolution,
            snapshots,
            failures,
            failedProjectIds);
    }

    private async Task<byte[]> ReadRegularDocumentBytesAsync(
        string path,
        bool incremental,
        CancellationToken ct)
    {
        if (incremental
            && _testHooks?.ReadIncrementalBytesAsync is { } incrementalRead)
        {
            return await incrementalRead(path, ct).ConfigureAwait(false);
        }
        if (!incremental
            && _testHooks?.ReadIndexCoreBytesAsync is { } indexCoreRead)
        {
            return await indexCoreRead(path, ct).ConfigureAwait(false);
        }
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    private static bool IsRecoverableRegularSnapshotFailure(Exception exception)
    {
        return exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or System.Text.DecoderFallbackException;
    }

    private void EnsureOpen()
    {
        ThrowIfDisposed();
        if (_workspace is null || _sanitizedSolution is null || _pathPolicy is null)
        {
            throw new InvalidOperationException("Call OpenAsync before indexing.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RoslynIndexer));
        }
    }

    /// <summary>
    /// Pre-flight probe: ask each project for its <see cref="Compilation"/> once before Pass 1
    /// begins. Projects that throw or return <c>null</c> are recorded as <see cref="ProjectFailure"/>
    /// entries and added to <see cref="_probedFailedProjectIds"/>; their documents are excluded from
    /// every subsequent pass. Successful compilations are cached in <see cref="_probedCompilations"/>
    /// so Pass 3's diagnostics walk reuses them rather than re-calling <c>GetCompilationAsync</c>.
    ///
    /// <para>This produces one log entry per failed project rather than N per-document errors when
    /// the same root cause (e.g. an unresolvable PackageReference, a missing SDK) hits every doc
    /// in the project. Cancellation propagates; other exceptions are converted into ProjectFailure
    /// entries with the truncated exception message as the reason.</para>
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "A misbehaving project (unresolvable references, malformed csproj, source-generator throwing during compilation construction) must not abort the entire indexing pass. Each failure is logged and converted into a ProjectFailure record so the scope can settle to `partial` rather than `degraded`.")]
    private async Task ProbeProjectCompilationsAsync(
        IEnumerable<Project> projects,
        CancellationToken ct)
    {
        var ok = new Dictionary<ProjectId, Compilation>();
        var failed = new List<ProjectFailure>();
        var failedIds = new HashSet<ProjectId>();
        var analyzerReferenceState =
            _analyzerReferenceLoadCompleteByProject.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
        foreach (var project in projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            ct.ThrowIfCancellationRequested();
            var analyzerLoadFailed = false;
            var generatorDiscoveryComplete = true;
            EventHandler<AnalyzerLoadFailureEventArgs> loadFailureHandler =
                (_, _) => analyzerLoadFailed = true;
            var analyzerFileReferences = project.AnalyzerReferences
                .OfType<AnalyzerFileReference>()
                .ToArray();
            foreach (var reference in analyzerFileReferences)
            {
                reference.AnalyzerLoadFailed += loadFailureHandler;
            }

            try
            {
                // This is the first generator-reference access in the production indexing flow.
                // Capture AnalyzerFileReference failures before GetCompilationAsync can cache an
                // empty per-language result and make a later WPF subscriber miss the one-shot
                // AnalyzerLoadFailed event. Enumerating generator instances does not execute them.
                foreach (var reference in project.AnalyzerReferences)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        _ = reference.GetGenerators(project.Language);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        generatorDiscoveryComplete = false;
                        _logger.LogWarning(
                            ex,
                            "Project {Project} generator discovery threw",
                            project.Name);
                    }
                }

                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is null)
                {
                    _logger.LogWarning("Project {Project} returned null compilation; skipping", project.Name);
                    failed.Add(new ProjectFailure(project.Name, "compilation null"));
                    failedIds.Add(project.Id);
                }
                else
                {
                    ok[project.Id] = compilation;
                }

                RecordAnalyzerReferenceState(
                    analyzerReferenceState,
                    project.Id,
                    generatorDiscoveryComplete
                    && !analyzerLoadFailed
                    && compilation is not null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Project {Project} compilation threw; skipping", project.Name);
                failed.Add(new ProjectFailure(project.Name, FailureMessage.Truncate(ex.Message)));
                failedIds.Add(project.Id);
                RecordAnalyzerReferenceState(
                    analyzerReferenceState,
                    project.Id,
                    isComplete: false);
            }
            finally
            {
                foreach (var reference in analyzerFileReferences)
                {
                    reference.AnalyzerLoadFailed -= loadFailureHandler;
                }
            }
        }
        _probedCompilations = ok;
        _probedFailures = failed;
        _probedFailedProjectIds = failedIds;
        _analyzerReferenceLoadCompleteByProject = analyzerReferenceState;

        static void RecordAnalyzerReferenceState(
            Dictionary<ProjectId, bool> states,
            ProjectId projectId,
            bool isComplete)
        {
            // AnalyzerFileReference caches a failed first load as an empty extension array and
            // does not raise AnalyzerLoadFailed on later reads. Negative evidence must therefore
            // be sticky for this workspace/reference generation. OpenAsync and structural reload
            // replace the AnalyzerFileReference instances and explicitly clear this map.
            states[projectId] =
                (!states.TryGetValue(projectId, out var prior) || prior)
                && isComplete;
        }
    }

    private async Task<CSharpDocumentDiscovery> AllCSharpDocumentsAsync(
        Solution solution,
        CancellationToken ct)
    {
        // Regular documents: same set we always walked, minus documents in projects whose
        // pre-flight compilation probe failed.
        var regular = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && !_probedFailedProjectIds.Contains(p.Id))
            .SelectMany(p => p.Documents)
            .Where(d => d.SourceCodeKind == SourceCodeKind.Regular && !string.IsNullOrEmpty(d.FilePath))
            .ToList();

        // Source-generated documents: per-project Project.GetSourceGeneratedDocumentsAsync drives
        // the source generators that ship with the project (regex source-gen, MVVM Toolkit, ASP.NET
        // routing, etc.) and surfaces synthesised C# files. Their display FilePath is not globally
        // unique: separate projects/generators can report the same value, and a regular document
        // can carry it too. GetGeneratedStoragePath creates a kind-namespaced owner path instead.
        // Marking those rows is_generated = 1 is what makes the find_references default-filter and
        // the (generated) annotations work.
        // Failed-probe projects are skipped here too — their generators would almost certainly
        // throw given the underlying compilation is unavailable.
        var projects = solution.Projects
            .Where(p =>
                p.Language == LanguageNames.CSharp &&
                !_probedFailedProjectIds.Contains(p.Id))
            .ToArray();
        var generatedDiscovery = await DiscoverSourceGeneratedDocumentsAsync(
            projects,
            ct).ConfigureAwait(false);
        var generated = generatedDiscovery.Documents
            .Where(document =>
                !_pathPolicy!.IsGeneratedDocumentExcluded(document.FilePath))
            .ToList();
        var generatedPaths = generated
            .Select(GetGeneratedStoragePath)
            .ToHashSet(_pathComparer);
        return new CSharpDocumentDiscovery(
            regular.Concat<Document>(generated).ToList(),
            generatedDiscovery.Failures,
            generatedPaths,
            generatedDiscovery.IsComplete && _probedFailedProjectIds.Count == 0);
    }

    private async Task<GeneratedDocumentDiscovery> DiscoverSourceGeneratedDocumentsAsync(
        IEnumerable<Project> projects,
        CancellationToken ct)
    {
        var generated = new List<SourceGeneratedDocument>();
        var failures = new List<FileFailure>();
        var complete = true;
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sourceGenDocs = await project
                    .GetSourceGeneratedDocumentsAsync(ct)
                    .ConfigureAwait(false);
                foreach (var document in sourceGenDocs)
                {
                    if (string.IsNullOrEmpty(document.FilePath))
                    {
                        complete = false;
                        AddFileFailure(
                            failures,
                            project.FilePath ?? project.Name,
                            "source-generated document has no file path");
                        continue;
                    }
                    generated.Add(document);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source generator failure for project {Project}; skipping generated docs", project.Name);
                complete = false;
                AddFileFailure(
                    failures,
                    project.FilePath ?? project.Name,
                    "source generator discovery failed: " +
                    FailureMessage.Truncate(ex.Message));
                continue;
            }
        }
        return new GeneratedDocumentDiscovery(generated, failures, complete);
    }

    private string GetGeneratedStoragePath(SourceGeneratedDocument document)
    {
        var root = _configuredPrivacyRoot
            ?? Path.GetDirectoryName(_solutionPath!)
            ?? throw new InvalidOperationException(
                "The solution directory is unavailable for generated-document identity.");
        var projectPath = document.Project.FilePath is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : document.Project.Name;
        var ownerIdentity = _testHooks?.GeneratedOwnerIdentity?.Invoke(document)
            ?? document.Id.Id.ToString("N");
        // The digest is over identity metadata, never generated content: an edit keeps the same
        // owner inside one workspace. DocumentId is authoritative only for that workspace; after
        // a re-open it may churn, so every complete all-document discovery reconciles stale
        // generated owners against the current set.
        var identity = string.Join(
            "\n",
            "source-generated-v1",
            projectPath,
            document.Project.Name,
            GetGeneratedDisplayPath(document),
            document.HintName,
            ownerIdentity);
        var digest = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        var displayName = Path.GetFileName(document.HintName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Path.GetFileName(document.FilePath);
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "generated.cs";
        }

        // Ordinary C# documents below obj/ are rejected by the privacy policy, while generated
        // documents are explicitly allowed there. This reserved valid absolute path therefore
        // cannot collide with a regular document and remains compatible with path-based tools.
        return Path.Combine(
            root,
            "obj",
            ".sourcegraph-generated",
            "v1",
            digest[..2],
            digest,
            displayName);
    }

    private string GetGeneratedDisplayPath(SourceGeneratedDocument document) =>
        _testHooks?.GeneratedDisplayPath?.Invoke(document)
        ?? document.FilePath
        ?? document.HintName;

    private string ResolveRegularStoragePath(string displayPath)
    {
        var fullPath = Path.GetFullPath(displayPath);
        if (_fileIdByPath.TryGetValue(fullPath, out var fileId)
            && _storedPathByFileId.TryGetValue(fileId, out var storedPath))
        {
            // On Windows, _pathComparer treats casing-only variants as the same physical path.
            // Keep using the exact persisted spelling so SQLite's ordinal UNIQUE(path) upsert
            // updates the existing row rather than creating a second file id.
            return storedPath;
        }
        return fullPath;
    }

    private async Task ReconcileGeneratedFilesAsync(
        IReadOnlySet<string> currentGeneratedPaths,
        CancellationToken ct)
    {
        var persisted = await _store
            .ListGeneratedFilesAsync(int.MaxValue, ct)
            .ConfigureAwait(false);
        foreach (var stale in persisted.Where(row =>
                     !currentGeneratedPaths.Contains(row.FilePath)))
        {
            ct.ThrowIfCancellationRequested();
            await _store.DeleteFileAsync(stale.FileId, ct).ConfigureAwait(false);
            DropFileFromMaps(stale.FileId);
        }
    }

    private sealed record CSharpDocumentDiscovery(
        IReadOnlyList<Document> Documents,
        IReadOnlyList<FileFailure> Failures,
        IReadOnlySet<string> GeneratedPaths,
        bool IsComplete);

    private sealed record GeneratedDocumentDiscovery(
        IReadOnlyList<SourceGeneratedDocument> Documents,
        IReadOnlyList<FileFailure> Failures,
        bool IsComplete);

    private sealed record RegularDocumentSnapshot(
        byte[] Bytes,
        SourceText Text);

    private sealed record RegularSnapshotPreparation(
        Solution Solution,
        IReadOnlyDictionary<string, RegularDocumentSnapshot> Snapshots,
        IReadOnlyList<FileFailure> Failures,
        IReadOnlySet<ProjectId> FailedProjectIds);

    private Task ClearSourceFileOutgoingAsync(
        long fileId,
        CancellationToken cancellationToken) =>
        _interopTarget is null
            ? _store.ClearFileOutgoingAsync(fileId, cancellationToken)
            : _store.ClearFileOutgoingAsync(
                fileId,
                _analysisEdgeProducer,
                cancellationToken);

    /// <summary>
    /// Marks <paramref name="fileId"/> as requiring a durable Pass-2 retry, then best-effort
    /// clears its outgoing refs/edges. The marker is an empty content-hash blob, which can
    /// never equal a real SHA-256 digest. Writing it before the clear means that even a process
    /// crash between the two operations makes the next index bypass the SHA fast path.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Best-effort persistent recovery runs with CancellationToken.None after the caller's token may already be cancelled; marker/clear failures are logged without hiding the original indexing failure or cancellation.")]
    private async Task MarkPassTwoIncompleteAsync(
        long fileId,
        string path,
        bool isGenerated)
    {
        try
        {
            await _store.UpsertFileAsync(
                    path,
                    Array.Empty<byte>(),
                    DateTimeOffset.UtcNow,
                    isGenerated,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception markerEx)
        {
            _logger.LogWarning(
                markerEx,
                "Could not persist Pass 2 retry marker for {Path}; file may retain partial refs/edges",
                path);
        }

        try
        {
            await ClearSourceFileOutgoingAsync(
                    fileId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception clearEx)
        {
            _logger.LogWarning(
                clearEx,
                "Pass 2's recovery clear for {Path} failed; retry marker will force another walk",
                path);
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "IndexCoreAsync's per-file walks must not let one document's failure (a misbehaving source generator, a transient compile gap, an analyzer throwing inside Roslyn) bring the whole indexing pass down. Each catch logs the file path + exception and continues with the next file; OperationCanceledException is rethrown explicitly so user-driven cancellation surfaces.")]
    private async Task<IndexResult> IndexCoreAsync(
        IReadOnlyList<Document> documents,
        bool fullReset,
        IReadOnlyCollection<FileFailure>? initialFailures,
        IReadOnlyDictionary<string, RegularDocumentSnapshot> regularSnapshots,
        bool forceInteropProjectionRefresh,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Source-generated documents are in-memory Roslyn outputs and may legitimately carry an
        // obj/ pseudo-path. Ordinary documents must remain inside the sanitized privacy boundary.
        documents = documents
            .Where(d => d is SourceGeneratedDocument generated
                ? !_pathPolicy!.IsGeneratedDocumentExcluded(generated.FilePath)
                : !_pathPolicy!.IsExcluded(d.FilePath))
            .ToList();

        // Defensive filter: skip documents in projects whose pre-flight probe failed. The probe
        // populates _probedFailedProjectIds before AllCSharpDocumentsAsync is called, so this is
        // belt-and-suspenders for callers that built `documents` themselves
        // (IndexChangedFilesAsync's path takes a separate filter for source-generated docs).
        if (_probedFailedProjectIds.Count > 0)
        {
            documents = documents.Where(d => !_probedFailedProjectIds.Contains(d.Project.Id)).ToList();
        }

        // Track files whose Pass 1B walk completed end-to-end. The set gates Pass 1C reconcile,
        // Pass 1D annotations, Pass 2 reference walk, and Pass 3 diagnostic reconcile. Phase A
        // may already have stored the new SHA and cleared outgoing facts, and a failed Phase 1B
        // walk may have upserted some symbols, so failure does not mean the old graph is untouched.
        // Skipping reconcile avoids deleting old declarations; FailedFiles makes the incomplete
        // state visible so a later integrity/structural retry can repair it.
        var walkedFileIds = new HashSet<long>();
        var failedFiles = new List<FileFailure>();
        var compilationErrorCount = 0;
        if (initialFailures is not null)
        {
            foreach (var failure in initialFailures)
            {
                AddFileFailure(failedFiles, failure.Path, failure.Reason);
            }
        }

        // Validate every multi-target/linked iteration before Phase A mutates storage. Each
        // regular path must still expose the SourceText decoded from the exact byte snapshot
        // captured for this pass; otherwise one iteration could hash one version and walk
        // semantics from another.
        var rejectedSnapshotPaths = new HashSet<string>(
            _pathComparer);
        var regularDocumentGroups = documents
            .Where(document =>
                document is not SourceGeneratedDocument
                && !string.IsNullOrEmpty(document.FilePath))
            .GroupBy(
                document => document.FilePath!,
                _pathComparer);
        foreach (var documentsForPath in regularDocumentGroups)
        {
            ct.ThrowIfCancellationRequested();
            var path = documentsForPath.Key;
            if (!regularSnapshots.TryGetValue(path, out var snapshot))
            {
                rejectedSnapshotPaths.Add(path);
                AddFileFailure(
                    failedFiles,
                    path,
                    "regular document byte snapshot was unavailable");
                _requiresStructuralReload = true;
                continue;
            }

            try
            {
                foreach (var document in documentsForPath)
                {
                    var boundText = await document
                        .GetTextAsync(ct)
                        .ConfigureAwait(false);
                    if (!boundText.ContentEquals(snapshot.Text))
                    {
                        throw new InvalidOperationException(
                            "Roslyn document text did not match its captured byte snapshot.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping {Path} (document snapshot validation failed; will retry)",
                    path);
                rejectedSnapshotPaths.Add(path);
                AddFileFailure(
                    failedFiles,
                    path,
                    FailureMessage.Truncate(ex.Message));
                _requiresStructuralReload = true;
            }
        }
        if (rejectedSnapshotPaths.Count > 0)
        {
            documents = documents
                .Where(document =>
                    document is SourceGeneratedDocument
                    || string.IsNullOrEmpty(document.FilePath)
                    || !rejectedSnapshotPaths.Contains(document.FilePath))
                .ToList();
        }

        // Hydrate in-memory maps from store on first run (or after a fullReset). This means
        // unchanged files don't need any DB hits — we already know their symbol ids.
        if (fullReset)
        {
            _symbolIdByKey.Clear();
            _keysByFileId.Clear();
            _fileIdByPath.Clear();
            _storedPathByFileId.Clear();
            _mapsHydrated = false;
        }
        if (!_mapsHydrated)
        {
            await HydrateMapsFromStoreAsync(ct).ConfigureAwait(false);
        }

        // PASS 1 — phase A: SHA scan. Identify which files changed; clear their outgoing
        // refs/edges (will be rebuilt in pass 2). Group docs per fileId so we walk every TFM /
        // linked-project iteration of the same path before reconciling.
        var changedFileIds = new HashSet<long>();
        var docsByChangedFile = new Dictionary<long, List<Document>>();
        var unchangedManagedInteropRefreshes =
            new Dictionary<long, (string Path, List<Document> Documents)>();
        var changedFileMeta = new Dictionary<long, (string Path, byte[] Sha, bool IsGenerated)>();
        var fileIdBySyntaxTree = new Dictionary<SyntaxTree, long>(
            ReferenceEqualityComparer.Instance);
        var symbolsIndexed = 0;

        var documentGroups = new List<(
            string StoragePath,
            string DisplayPath,
            bool IsGenerated,
            List<Document> Documents)>();
        foreach (var regularDocuments in documents
                     .Where(document =>
                         document is not SourceGeneratedDocument
                         && document.FilePath is not null)
                     .GroupBy(document => document.FilePath!, _pathComparer))
        {
            var displayPath = Path.GetFullPath(regularDocuments.Key);
            documentGroups.Add((
                ResolveRegularStoragePath(displayPath),
                displayPath,
                IsGenerated: false,
                regularDocuments.ToList()));
        }

        var generatedOwners = documents
            .OfType<SourceGeneratedDocument>()
            .Select(document => (
                Document: document,
                StoragePath: GetGeneratedStoragePath(document)))
            .GroupBy(owner => owner.StoragePath, _pathComparer);
        foreach (var generatedOwner in generatedOwners)
        {
            var owners = generatedOwner.ToList();
            if (owners.Count > 1)
            {
                var displayPath = GetGeneratedDisplayPath(owners[0].Document);
                AddFileFailure(
                    failedFiles,
                    displayPath,
                    "multiple source-generated documents resolved to the same stable owner");
                _requiresStructuralReload = true;
                continue;
            }

            var owner = owners[0];
            documentGroups.Add((
                owner.StoragePath,
                GetGeneratedDisplayPath(owner.Document),
                IsGenerated: true,
                [owner.Document]));
        }

        var managedInteropProjectionRefreshPaths =
            !fullReset && forceInteropProjectionRefresh
                ? await FindManagedInteropProjectionRefreshPathsAsync(
                        documentGroups,
                        ct)
                    .ConfigureAwait(false)
                : new HashSet<string>(_pathComparer);

        foreach (var documentsForOwner in documentGroups)
        {
            ct.ThrowIfCancellationRequested();
            var path = documentsForOwner.StoragePath;
            var displayPath = documentsForOwner.DisplayPath;
            var groupedDocuments = documentsForOwner.Documents;
            var representative = groupedDocuments[0];

            // Source-generated documents don't exist on disk; their bytes come from the document's
            // SourceText. Same SHA gate either way — same hash on identical generator output means
            // we skip the file entirely (per design.md "SHA gate on generated content").
            var isGenerated = documentsForOwner.IsGenerated;
            byte[] bytes;
            if (isGenerated)
            {
                try
                {
                    var text = await representative.GetTextAsync(ct).ConfigureAwait(false);
                    bytes = System.Text.Encoding.UTF8.GetBytes(text.ToString());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping generated doc {Path} (text fetch failed)", displayPath);
                    AddFileFailure(failedFiles, displayPath, FailureMessage.Truncate(ex.Message));
                    _requiresStructuralReload = true;
                    continue;
                }
            }
            else
            {
                if (_pathPolicy!.IsExcluded(displayPath)) continue;
                if (!regularSnapshots.TryGetValue(displayPath, out var snapshot))
                {
                    AddFileFailure(
                        failedFiles,
                        displayPath,
                        "regular document byte snapshot was unavailable during Phase A");
                    _requiresStructuralReload = true;
                    continue;
                }
                bytes = snapshot.Bytes;
            }
            var sha = SHA256.HashData(bytes);
            var stored = await _store.GetFileContentHashAsync(path, ct).ConfigureAwait(false);
            var unchanged = stored is not null && stored.AsSpan().SequenceEqual(sha);

            var fileId = await _store.UpsertFileAsync(path, sha, DateTimeOffset.UtcNow, isGenerated, ct).ConfigureAwait(false);
            _fileIdByPath[path] = fileId;
            if (!isGenerated)
            {
                _fileIdByPath[displayPath] = fileId;
            }
            _storedPathByFileId[fileId] = path;
            foreach (var document in groupedDocuments)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync(ct)
                    .ConfigureAwait(false);
                if (syntaxTree is not null)
                {
                    fileIdBySyntaxTree[syntaxTree] = fileId;
                }
            }

            // A matching source hash does not prove that this file's independently stored
            // managed-import/ABI projection survived. The preflight compares the complete
            // Roslyn projection against one bounded batch read and marks only mismatched owners.
            var missingManagedInteropProjection =
                unchanged
                && managedInteropProjectionRefreshPaths.Contains(path);
            if (unchanged
                && !fullReset
                && !missingManagedInteropProjection
                && _keysByFileId.TryGetValue(fileId, out var keysForFile))
            {
                // SHA matches and the in-memory symbol map is hydrated. Verify the store's
                // refs/edges are in agreement before we skip pass 2: a symbol-bearing file
                // with zero outgoing refs AND zero outgoing edges is "zombied" (pass 1
                // cleared, pass 2 never repopulated). Without this check the SHA-skip would
                // keep that file stranded forever.
                //
                // Files with zero declared symbols (a usings-only file, an [assembly:]
                // attribute file, etc.) take the early-out: pass 2 has nothing useful to
                // walk for them. Hydration seeds `_keysByFileId[fileId] = []` for every
                // file row regardless of whether the file has any symbol rows, so this
                // branch fires on a process restart even for symbol-less files.
                if (keysForFile.Count == 0)
                {
                    if (forceInteropProjectionRefresh)
                    {
                        unchangedManagedInteropRefreshes[fileId] =
                            (path, groupedDocuments);
                    }
                    continue;
                }
                var hasOutgoingRefs =
                    await _store.HasOutgoingReferencesAsync(fileId, ct).ConfigureAwait(false);
                if (!hasOutgoingRefs)
                {
                    _logger.LogInformation(
                        "Re-walking references for {Path}: file SHA matches but no outgoing references in store " +
                        "(likely zombied by a prior incomplete indexing pass; recovering)",
                        path);
                }
                if (hasOutgoingRefs)
                {
                    if (forceInteropProjectionRefresh)
                    {
                        unchangedManagedInteropRefreshes[fileId] =
                            (path, groupedDocuments);
                    }
                    continue;
                }
                // Fall through to the changed-file path so pass 2 walks this file.
            }

            if (changedFileIds.Add(fileId))
            {
                await ClearSourceFileOutgoingAsync(fileId, ct)
                    .ConfigureAwait(false);
                changedFileMeta[fileId] = (path, sha, isGenerated);
            }
            docsByChangedFile[fileId] = groupedDocuments;
        }

        // PASS 1 — phase B: walk every iteration of each changed file (one path may have N
        // iterations across multi-target / linked / shared projects), upserting symbols and
        // accumulating the union of canonical keys per fileId before we reconcile.
        // Per fileId we also record (childKey -> parentKey) so pass-1c can resolve the parent
        // canonical key into its row id (which only exists after the corresponding upsert).
        // Annotations are gathered as PendingAnnotations during this phase; their
        // attribute_symbol_id is resolved after the whole pass completes, so a use site can
        // link to a user-defined attribute class declared in another file we haven't walked
        // yet (e.g. [Legacy] on Greeter.cs resolving to LegacyAttribute.cs even though
        // Greeter.cs is processed first alphabetically).
        var newKeysForFile = new Dictionary<long, HashSet<string>>();
        var parentKeyByChildKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingAttrsByFile = new Dictionary<long, List<PendingAnnotation>>();
        var pendingInteropByFile =
            new Dictionary<long, List<FileAnnotationFact>>();
        var seenSymbolForAttr = new Dictionary<long, HashSet<string>>();
        // Test framework detection: keyed by symbol id, value = "xunit"/"nunit"/"mstest".
        // Populated as we walk method symbols in pass 1; flushed in a single batch update once
        // pass-1 completes. Doing it as a separate update lets us avoid clobbering the value
        // on the symbol's ON-CONFLICT path during re-upserts.
        var testFrameworkBySymbolId = new Dictionary<long, string>();
        foreach (var (fileId, docs) in docsByChangedFile)
        {
            // Per-file locals — only published into the shared dictionaries (newKeysForFile /
            // pendingAttrsByFile / seenSymbolForAttr) on the success path. If the inner walk
            // throws, these locals are dropped on the floor and Pass 1C/1D iterate without an
            // entry for this fileId. That leaves prior declarations unreconciled, although Phase A
            // has already updated the file row and cleared its outgoing facts.
            var fileKeys = new HashSet<string>(StringComparer.Ordinal);
            var pendingAttrs = new List<PendingAnnotation>();
            var interopPayloadByKey =
                new Dictionary<string, string>(StringComparer.Ordinal);
            // A null value is a real per-TFM observation: the declaration exists but has no
            // publishable layout (for example LayoutKind.Auto). Retaining it lets a later TFM
            // with a payload conflict fail closed instead of silently winning.
            var abiRecordPayloadByKey =
                new Dictionary<string, string?>(StringComparer.Ordinal);
            var managedUsageAnnotationsByIdentity =
                new Dictionary<string, FileAnnotationFact>(
                    StringComparer.Ordinal);
            HashSet<string>? firstManagedUsageProjection = null;
            var attrSeen = new HashSet<string>(StringComparer.Ordinal);
            var path = changedFileMeta.TryGetValue(fileId, out var meta) ? meta.Path : "<unknown>";

            try
            {
                foreach (var document in docs)
                {
                    _testHooks?.BeforePassOneWalk?.Invoke(document);
                    var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
                    var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    if (tree is null || model is null)
                    {
                        throw new InvalidOperationException(
                            "Roslyn returned no syntax tree or semantic model.");
                    }

                    fileIdBySyntaxTree[tree] = fileId;
                    var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                    foreach (var node in EnumerateDeclarations(root))
                    {
                        var symbol = model.GetDeclaredSymbol(node, ct);
                        if (symbol is null || !SymbolMapping.IsIndexable(symbol)) continue;

                        var key = SymbolMapping.CanonicalKey(symbol);
                        if (key is null) continue;
                        var isFirstSymbolIteration = fileKeys.Add(key);

                        // A physical source path can appear in multiple target-framework or
                        // linked-project compilations. Extract every iteration before applying
                        // the normal symbol de-duplication gate so a TFM-dependent ABI conflict
                        // is surfaced instead of whichever compilation happened to enumerate
                        // first winning silently.
                        if (_interopTarget is not null
                            && symbol is IMethodSymbol interopMethod)
                        {
                            var import = ManagedInteropExtractor.TryExtract(
                                interopMethod,
                                _interopTarget,
                                fileId,
                                path);
                            if (import is not null)
                            {
                                var payload =
                                    InteropFactPayloadCodec.EncodeManagedImport(
                                        import);
                                if (interopPayloadByKey.TryGetValue(
                                        import.SymbolCanonicalKey,
                                        out var previousPayload)
                                    && !string.Equals(
                                        previousPayload,
                                        payload,
                                        StringComparison.Ordinal))
                                {
                                    throw new InvalidOperationException(
                                        "Managed interop declaration "
                                        + $"`{import.SymbolCanonicalKey}` has conflicting "
                                        + "target-framework projections.");
                                }
                                interopPayloadByKey[import.SymbolCanonicalKey] =
                                    payload;
                            }
                        }
                        if (_interopTarget is not null
                            && symbol is INamedTypeSymbol
                            {
                                TypeKind: TypeKind.Struct,
                            } interopRecord)
                        {
                            var layout =
                                ManagedRecordLayoutExtractor.TryExtract(
                                    interopRecord,
                                    _interopTarget,
                                    fileId);
                            var payload = layout is null
                                ? null
                                : InteropFactPayloadCodec.EncodeAbiRecord(
                                    layout);
                            if (abiRecordPayloadByKey.TryGetValue(
                                    key,
                                    out var previousPayload)
                                && !string.Equals(
                                    previousPayload,
                                    payload,
                                    StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(
                                    "Managed ABI record "
                                    + $"`{key}` has conflicting "
                                    + "target-framework projections.");
                            }
                            if (!abiRecordPayloadByKey.ContainsKey(key)
                                && abiRecordPayloadByKey.Count
                                    >= MaximumManagedAbiRecordsPerFile)
                            {
                                throw new InvalidOperationException(
                                    "Managed ABI record count exceeds the "
                                    + $"{MaximumManagedAbiRecordsPerFile}-item "
                                    + "per-file limit.");
                            }
                            abiRecordPayloadByKey[key] = payload;
                        }

                        if (!isFirstSymbolIteration) continue;

                        // Remember parent canonical key for the pass-1c container_id batch update.
                        var parentSym = symbol.ContainingSymbol;
                        if (parentSym is not null && SymbolMapping.IsIndexable(parentSym))
                        {
                            var parentKey = SymbolMapping.CanonicalKey(parentSym);
                            if (parentKey is not null) parentKeyByChildKey[key] = parentKey;
                        }

                        // Test framework discrimination — only meaningful for methods.
                        string? testFramework = symbol is IMethodSymbol ms ? TestDiscriminator.Detect(ms) : null;

                        var loc = node.GetLocation().GetLineSpan();
                        var coreSymbol = new Symbol(
                            Id: 0,
                            Name: symbol.Name,
                            Fqn: SymbolMapping.Fqn(symbol),
                            Kind: SymbolMapping.ToCoreKind(symbol),
                            FileId: fileId,
                            StartLine: loc.StartLinePosition.Line + 1,
                            StartCol: loc.StartLinePosition.Character + 1,
                            EndLine: loc.EndLinePosition.Line + 1,
                            EndCol: loc.EndLinePosition.Character + 1,
                            Signature: SymbolMapping.Signature(symbol),
                            ContainerId: null,
                            Modifiers: SymbolMapping.Modifiers(symbol),
                            Accessibility: SymbolMapping.Accessibility(symbol),
                            XmlSummary: SymbolMapping.XmlSummary(symbol),
                            TestFramework: testFramework);

                        var id = await _store.UpsertSymbolAsync(key, coreSymbol, ct).ConfigureAwait(false);
                        var isNew = !_symbolIdByKey.ContainsKey(key);
                        _symbolIdByKey[key] = id;
                        if (isNew) symbolsIndexed++;

                        if (testFramework is not null)
                        {
                            testFrameworkBySymbolId[id] = testFramework;
                        }

                        // Annotations: only collect once per (file, symbol) tuple even if the symbol
                        // was discovered in multiple TFM iterations. If the same attribute is
                        // visible across TFM iterations of the same source file we don't want to
                        // double-store it.
                        if (attrSeen.Add(key))
                        {
                            AttributeExtractor.AppendAnnotations(
                                symbol,
                                key,
                                id,
                                pendingAttrs);

                            // Enqueue an embedding request once per (file, symbol). The sink is
                            // a no-op when --no-embeddings was passed or the model isn't available;
                            // the indexer never blocks on it.
                            if (_embeddingsSink.IsEnabled)
                            {
                                EnqueueEmbedRequest(id, document.FilePath, symbol, coreSymbol);
                            }
                        }
                    }

                    if (_interopTarget is not null)
                    {
                        var usage = ManagedInteropUsageExtractor.Extract(
                            root,
                            model,
                            _interopTarget,
                            fileId,
                            path,
                            method => ResolveManagedImportFileId(
                                method,
                                fileIdBySyntaxTree),
                            ownerFileId =>
                                _storedPathByFileId.TryGetValue(
                                    ownerFileId,
                                    out var ownerPath)
                                    ? ownerPath
                                    : null,
                            ct);
                        MergeManagedUsageProjection(
                            usage,
                            managedUsageAnnotationsByIdentity,
                            ref firstManagedUsageProjection);
                    }
                }

                // Success: publish per-file state to the shared dictionaries that Pass 1C/1D
                // iterate. Pass 2 will also gate on walkedFileIds so failed files are skipped.
                newKeysForFile[fileId] = fileKeys;
                pendingAttrsByFile[fileId] = pendingAttrs;
                if (_interopTarget is not null)
                {
                    var interopAnnotations = interopPayloadByKey
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new FileAnnotationFact(
                            pair.Key,
                            ManagedInteropAnnotationName,
                            ManagedInteropAnnotationFullName,
                            InteropAnnotationFlavors.ManagedImport,
                            pair.Value,
                            AttributeCanonicalKey: null))
                        .ToList();
                    interopAnnotations.AddRange(abiRecordPayloadByKey
                        .Where(pair => pair.Value is not null)
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new FileAnnotationFact(
                            pair.Key,
                            ManagedAbiRecordAnnotationName,
                            ManagedAbiRecordAnnotationFullName,
                            InteropAnnotationFlavors.AbiRecord,
                            pair.Value,
                            AttributeCanonicalKey: null)));
                    interopAnnotations.AddRange(
                        managedUsageAnnotationsByIdentity
                            .OrderBy(
                                pair => pair.Key,
                                StringComparer.Ordinal)
                            .Select(pair => pair.Value));
                    pendingInteropByFile[fileId] = interopAnnotations;
                }
                seenSymbolForAttr[fileId] = attrSeen;
                walkedFileIds.Add(fileId);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One file's Pass-1B walk threw — log it and let the next file proceed.
                // Pass 1C's declaration reconcile and the later annotation/reference/diagnostic
                // phases skip it, but Phase A's SHA/outgoing mutations and any symbol upserts
                // completed before the throw remain. The result therefore reports an explicitly
                // incomplete file for a later integrity or structural retry to repair.
                _logger.LogWarning(ex,
                    "Pass 1 walk failed for {Path}; graph state is incomplete and will be re-attempted",
                    path);
                AddFileFailure(failedFiles, path, FailureMessage.Truncate(ex.Message));
            }
        }

        // Reconcile declarations and publish the complete annotation projection per changed
        // physical file in one store transaction. This keeps ordinary C# attributes and the
        // managed-interop projection on the same successful declaration snapshot, including a
        // successful zero-annotation replacement.
        var reconciledKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fileId, fileKeys) in newKeysForFile)
        {
            var path = changedFileMeta.TryGetValue(fileId, out var meta)
                ? meta.Path
                : throw new InvalidOperationException(
                    $"Missing changed-file metadata for declaration owner {fileId}.");
            try
            {
                // Namespace and partial declarations can share one canonical key across physical
                // files. The graph intentionally has one stable symbol row per key, so the final
                // Pass-B upsert owns that row. Reconcile only the keys currently attributed to
                // this file; requiring every syntactically observed key to be owned here would
                // reject valid multi-file declarations.
                var storedOwners = await _store.ListSymbolsInFileAsync(path, ct)
                    .ConfigureAwait(false);
                var currentlyOwnedKeys = storedOwners
                    .Select(symbol => symbol.CanonicalKey)
                    .OfType<string>()
                    .Where(fileKeys.Contains)
                    .ToHashSet(StringComparer.Ordinal);
                var annotations = new List<FileAnnotationFact>();
                if (pendingAttrsByFile.TryGetValue(fileId, out var pendingAttrs))
                {
                    annotations.AddRange(pendingAttrs
                        .Where(pending =>
                            currentlyOwnedKeys.Contains(
                                pending.SymbolCanonicalKey))
                        .Select(pending =>
                            AttributeExtractor.ToFact(
                                pending,
                                _symbolIdByKey)));
                }
                if (pendingInteropByFile.TryGetValue(
                        fileId,
                        out var interopAnnotations))
                {
                    annotations.AddRange(interopAnnotations.Where(annotation =>
                        currentlyOwnedKeys.Contains(
                            annotation.SymbolCanonicalKey)));
                }

                await _store.ReconcileFileDeclarationsAndAnnotationsAsync(
                        path,
                        currentlyOwnedKeys,
                        annotations,
                        _interopTarget is null
                            ? []
                            :
                            [
                                InteropAnnotationFlavors.Match,
                                InteropAnnotationFlavors.Finding,
                            ],
                        ct)
                    .ConfigureAwait(false);

                // Update the in-memory declaration map only after the store transaction commits.
                foreach (var storedOwner in storedOwners)
                {
                    if (storedOwner.CanonicalKey is { } storedKey
                        && !currentlyOwnedKeys.Contains(storedKey))
                    {
                        _symbolIdByKey.Remove(storedKey);
                    }
                }
                _keysByFileId[fileId] = currentlyOwnedKeys.ToList();
                reconciledKeys.UnionWith(currentlyOwnedKeys);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _requiresStructuralReload = true;
                walkedFileIds.Remove(fileId);
                foreach (var key in fileKeys)
                {
                    if (_symbolIdByKey.TryGetValue(key, out var symbolId))
                    {
                        testFrameworkBySymbolId.Remove(symbolId);
                    }
                }
                _logger.LogWarning(
                    ex,
                    "Declaration/annotation reconciliation failed for {Path}; "
                    + "the prior annotation projection was retained",
                    path);
                AddFileFailure(
                    failedFiles,
                    path,
                    FailureMessage.Truncate(ex.Message));
            }
        }

        // PASS 1 — phase C: resolve every recorded (childKey -> parentKey) into row ids and
        // batch-update symbols.container_id. Lookups can fail if the parent isn't indexable
        // (e.g., the global namespace) or if the parent lives in a file we didn't reprocess
        // this round but isn't in _symbolIdByKey for some reason — those rows are skipped.
        if (parentKeyByChildKey.Count > 0)
        {
            var pairs = new List<(long ChildId, long ParentId)>(parentKeyByChildKey.Count);
            foreach (var (childKey, parentKey) in parentKeyByChildKey)
            {
                if (reconciledKeys.Contains(childKey)
                    && _symbolIdByKey.TryGetValue(childKey, out var childId)
                    && _symbolIdByKey.TryGetValue(parentKey, out var parentId)
                    && childId != parentId)
                {
                    pairs.Add((childId, parentId));
                }
            }
            if (pairs.Count > 0)
            {
                await _store.BatchUpdateContainerIdsAsync(pairs, ct).ConfigureAwait(false);
            }
        }

        // PASS 1 — phase D: flush detected test_framework values for the methods we walked.
        // Done as a UPDATE since the value was set during initial UpsertSymbolAsync but the
        // ON-CONFLICT path doesn't update test_framework, so an edit that changes a method's
        // attached attributes still propagates here.
        if (testFrameworkBySymbolId.Count > 0)
        {
            var rows = testFrameworkBySymbolId.Select(kv => (SymbolId: kv.Key, Framework: kv.Value)).ToList();
            await _store.UpdateTestFrameworksAsync(rows, ct).ConfigureAwait(false);
        }

        // PASS 2: references — only for files we (re)indexed in pass 1.
        // IMPORTANT: walk one document per fileId. The same source file appears once per project /
        // TFM in a multi-targeted solution; walking each iteration would emit duplicate refs and
        // inflate counts. The first doc's tree+model is sufficient since the source file's
        // declarations and references are the same across TFMs (modulo #if-conditional code, which
        // we accept losing visibility into for now).
        // Files whose Pass 1B walk threw (absent from walkedFileIds) are skipped here too —
        // walking refs against incomplete pass-1 symbol state would emit refs targeting symbols
        // that don't yet exist in the store.
        var refsIndexed = 0;
        var docsToIndexRefs = docsByChangedFile
            .Where(kv => walkedFileIds.Contains(kv.Key))
            .Select(kv => (
                FileId: kv.Key,
                Document: kv.Value[0],
                Path: changedFileMeta[kv.Key].Path))
            .ToList();
        var filesIndexed = walkedFileIds.Count;
        var completedPassTwoFileIds = new HashSet<long>();
        try
        {
            foreach (var item in docsToIndexRefs)
            {
                ct.ThrowIfCancellationRequested();
                var fileId = item.FileId;
                var document = item.Document;
                var path = item.Path;

                try
                {
                    var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
                    var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    if (tree is null || model is null)
                    {
                        throw new InvalidOperationException(
                            "Roslyn returned no syntax tree or semantic model during reference walk.");
                    }

                    var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                    var refBatch = new List<SymbolReference>(capacity: 256);
                    var edgeBatch = new List<Edge>(capacity: 64);
                // Dedupe duplicate syntax visits while retaining distinct occurrences between the
                // same logical endpoints. Roslyn can surface one generic/member-access name through
                // more than one syntax-node case; the range-inclusive key collapses only those
                // duplicate visits, never two separate call sites.
                var emittedEvidence = new HashSet<(
                    long Src,
                    long Dst,
                    string Kind,
                    int StartLine,
                    int StartColumn,
                    int EndLine,
                    int EndColumn,
                    CoreEvidenceConfidence Confidence)>();
                void AddEdge(
                    long src,
                    long dst,
                    string kind,
                    SyntaxNode evidenceNode,
                    CoreEvidenceConfidence confidence)
                {
                    if (src == dst) return;
                    var lineSpan = evidenceNode.GetLocation().GetLineSpan();
                    var startLine = lineSpan.StartLinePosition.Line + 1;
                    var startColumn = lineSpan.StartLinePosition.Character + 1;
                    var endLine = lineSpan.EndLinePosition.Line + 1;
                    var endColumn = lineSpan.EndLinePosition.Character + 1;
                    if (emittedEvidence.Add((
                            src,
                            dst,
                            kind,
                            startLine,
                            startColumn,
                            endLine,
                            endColumn,
                            confidence)))
                    {
                        edgeBatch.Add(new Edge(src, dst, kind)
                        {
                            Evidence = new Evidence(
                                fileId,
                                new CoreSourceLocation(
                                    path,
                                    startLine,
                                    startColumn,
                                    endLine,
                                    endColumn),
                                confidence,
                                "roslyn"),
                        });
                    }
                }

                foreach (var node in root.DescendantNodes())
                {
                    ISymbol? referenced = null;
                    ReferenceKind kind = ReferenceKind.Reference;
                    SyntaxNode? refNode = null; // node whose position we record

                    switch (node)
                    {
                        case IdentifierNameSyntax id when id.Parent is not (NamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax or VariableDeclaratorSyntax or ParameterSyntax or TypeParameterSyntax):
                            referenced = id.Parent is InvocationExpressionSyntax invocationId
                                && invocationId.Expression == id
                                    ? model.GetSymbolInfo(invocationId, ct).Symbol
                                    : model.GetSymbolInfo(id, ct).Symbol;
                            refNode = id;
                            kind = id.Parent is InvocationExpressionSyntax inv
                                && inv.Expression == id
                                ? ReferenceKind.Call
                                : ClassifyReadWrite(id, referenced) ?? ReferenceKind.Reference;
                            break;

                        case GenericNameSyntax gn:
                            referenced = gn.Parent is InvocationExpressionSyntax invocationGeneric
                                && invocationGeneric.Expression == gn
                                    ? model.GetSymbolInfo(invocationGeneric, ct).Symbol
                                    : model.GetSymbolInfo(gn, ct).Symbol;
                            refNode = gn;
                            kind = gn.Parent is InvocationExpressionSyntax invGn
                                && invGn.Expression == gn
                                    ? ReferenceKind.Call
                                    : ReferenceKind.Reference;
                            break;

                        case MemberAccessExpressionSyntax mae:
                            referenced = mae.Parent is InvocationExpressionSyntax invocationMember
                                && invocationMember.Expression == mae
                                    ? model.GetSymbolInfo(invocationMember, ct).Symbol
                                    : model.GetSymbolInfo(mae.Name, ct).Symbol;
                            refNode = mae.Name;
                            kind = mae.Parent is InvocationExpressionSyntax invMa && invMa.Expression == mae
                                ? ReferenceKind.Call
                                : ClassifyReadWrite(mae, referenced) ?? ReferenceKind.Reference;
                            break;

                        case ObjectCreationExpressionSyntax oce:
                            referenced = model.GetSymbolInfo(oce, ct).Symbol;
                            refNode = oce;
                            kind = ReferenceKind.Call;
                            break;

                        case ImplicitObjectCreationExpressionSyntax ioce:
                            referenced = model.GetSymbolInfo(ioce, ct).Symbol;
                            refNode = ioce;
                            kind = ReferenceKind.Call;
                            break;

                        case MemberBindingExpressionSyntax mbe:
                            referenced = mbe.Parent is InvocationExpressionSyntax invocationBinding
                                && invocationBinding.Expression == mbe
                                    ? model.GetSymbolInfo(invocationBinding, ct).Symbol
                                    : model.GetSymbolInfo(mbe.Name, ct).Symbol;
                            refNode = mbe.Name;
                            kind = mbe.Parent is InvocationExpressionSyntax invMb
                                && invMb.Expression == mbe
                                    ? ReferenceKind.Call
                                    : ClassifyReadWrite(mbe, referenced) ?? ReferenceKind.Reference;
                            break;
                    }

                    if (referenced is null || refNode is null) continue;
                    var key = SymbolMapping.CanonicalKey(referenced);
                    if (key is null) continue;
                    if (!_symbolIdByKey.TryGetValue(key, out var symId)) continue;

                    var pos = refNode.GetLocation().GetLineSpan().StartLinePosition;

                    // For ReferenceKind.Read|Write on increments/decrements/compound-assignment/ref params,
                    // we may need to emit two ref rows at the same position (one Read, one Write).
                    var emit = SplitReadWrite(kind, refNode, referenced);
                    foreach (var rk in emit)
                    {
                        refBatch.Add(new SymbolReference(
                            Id: 0,
                            SymbolId: symId,
                            FileId: fileId,
                            Line: pos.Line + 1,
                            Col: pos.Character + 1,
                            Kind: rk));
                    }

                    // Calls edge: source = enclosing named member, target = referenced
                    if (kind == ReferenceKind.Call)
                    {
                        var enclosing = FindEnclosingMember(model, refNode.SpanStart, ct);
                        if (enclosing is not null)
                        {
                            var encKey = SymbolMapping.CanonicalKey(enclosing);
                            if (encKey is not null && _symbolIdByKey.TryGetValue(encKey, out var srcId))
                            {
                                AddEdge(
                                    srcId,
                                    symId,
                                    EdgeKinds.Calls,
                                    refNode,
                                    CoreEvidenceConfidence.Exact);
                            }
                        }
                    }

                    if (referenced is IEventSymbol)
                    {
                        var eventRelation = ClassifyEventRelation(refNode);
                        if (eventRelation is not null)
                        {
                            var enclosing = FindEnclosingMember(
                                model,
                                refNode.SpanStart,
                                ct);
                            var enclosingKey = enclosing is null
                                ? null
                                : SymbolMapping.CanonicalKey(enclosing);
                            if (enclosingKey is not null
                                && _symbolIdByKey.TryGetValue(
                                    enclosingKey,
                                    out var sourceId))
                            {
                                AddEdge(
                                    sourceId,
                                    symId,
                                    eventRelation,
                                    refNode,
                                    CoreEvidenceConfidence.Exact);
                            }
                        }
                    }

                    // Instantiates edge: every `new T()` becomes an Instantiates(enclosing -> T) edge,
                    // alongside the Calls edge to the constructor that the case above already emitted.
                    // We also emit a UsesType edge so kind=uses_type can answer "every consumer of T",
                    // including body-local instantiations (per design.md point 1).
                    if (node is BaseObjectCreationExpressionSyntax creationNode
                        && referenced is IMethodSymbol ctor)
                    {
                        var typeSym = ctor.ContainingType;
                        if (typeSym is not null)
                        {
                            var typeKey = SymbolMapping.CanonicalKey(typeSym);
                            if (typeKey is not null && _symbolIdByKey.TryGetValue(typeKey, out var dstId))
                            {
                                var enclosing = FindEnclosingMember(model, creationNode.SpanStart, ct);
                                if (enclosing is not null)
                                {
                                    var encKey = SymbolMapping.CanonicalKey(enclosing);
                                    if (encKey is not null && _symbolIdByKey.TryGetValue(encKey, out var srcId))
                                    {
                                        AddEdge(
                                            srcId,
                                            dstId,
                                            EdgeKinds.Instantiates,
                                            creationNode,
                                            CoreEvidenceConfidence.Exact);
                                        AddEdge(
                                            srcId,
                                            dstId,
                                            EdgeKinds.UsesType,
                                            creationNode,
                                            CoreEvidenceConfidence.Exact);
                                    }
                                }
                            }
                        }
                    }
                }

                EmitScheduledExecutions(root, model, AddEdge, ct);

                // Connect an ICommand property to the source method captured by the command
                // object's single delegate argument. This is deliberately operation-based:
                // syntax or name matching would turn overload gaps, dynamic values, and
                // non-command properties into false cross-layer call paths.
                EmitCommandExecutes(root, model, AddEdge, ct);

                // Throws edges from `throw` syntax (statement and expression).
                foreach (var node in root.DescendantNodes())
                {
                    ExpressionSyntax? thrown = node switch
                    {
                        ThrowStatementSyntax ts => ts.Expression,
                        ThrowExpressionSyntax te => te.Expression,
                        _ => null,
                    };
                    if (thrown is null) continue;
                    var thrownType = model.GetTypeInfo(thrown, ct).Type;
                    if (thrownType is null) continue;
                    var typeKey = SymbolMapping.CanonicalKey(thrownType);
                    if (typeKey is null || !_symbolIdByKey.TryGetValue(typeKey, out var dstId)) continue;
                    var enclosing = FindEnclosingMember(model, node.SpanStart, ct);
                    if (enclosing is null) continue;
                    var encKey = SymbolMapping.CanonicalKey(enclosing);
                    if (encKey is null || !_symbolIdByKey.TryGetValue(encKey, out var srcId)) continue;
                    AddEdge(
                        srcId,
                        dstId,
                        EdgeKinds.Throws,
                        node,
                        CoreEvidenceConfidence.Exact);
                }

                // Inherits / Implements edges from BaseListSyntax + UsesType for the same targets.
                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var typeSym = model.GetDeclaredSymbol(typeDecl, ct);
                    if (typeSym is null) continue;
                    var typeKey = SymbolMapping.CanonicalKey(typeSym);
                    if (typeKey is null || !_symbolIdByKey.TryGetValue(typeKey, out var srcId)) continue;

                    if (typeDecl.BaseList is not null)
                    {
                        foreach (var baseTypeSyntax in typeDecl.BaseList.Types)
                        {
                            var baseSym = model.GetSymbolInfo(baseTypeSyntax.Type, ct).Symbol;
                            if (baseSym is null) continue;
                            var baseKey = SymbolMapping.CanonicalKey(baseSym);
                            if (baseKey is null || !_symbolIdByKey.TryGetValue(baseKey, out var dstId)) continue;

                            var ek = baseSym is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Interface
                                ? EdgeKinds.Implements
                                : EdgeKinds.Inherits;
                            AddEdge(
                                srcId,
                                dstId,
                                ek,
                                baseTypeSyntax.Type,
                                CoreEvidenceConfidence.Exact);
                            // Also a UsesType edge so kind=uses_type can answer "every consumer of B".
                            AddEdge(
                                srcId,
                                dstId,
                                EdgeKinds.UsesType,
                                baseTypeSyntax.Type,
                                CoreEvidenceConfidence.Exact);
                        }
                    }

                    // Member-level ImplementsMember: walk every interface this type implements and ask
                    // Roslyn which member satisfies each interface member. Done once per type declaration.
                    EmitMemberImplements(typeSym, typeDecl, AddEdge, ct);
                }

                // Per-member emitters: UsesType from signatures, OverridesMember from Overridden*,
                // Tests from test methods to first non-test production call.
                // We walk the same declaration set pass 1 does (via EnumerateDeclarations) so this
                // touches type/method/property/event/field nodes — the only ones that have signatures
                // worth scanning for type usage.
                foreach (var node in EnumerateDeclarations(root))
                {
                    if (model.GetDeclaredSymbol(node, ct) is not ISymbol declSym) continue;
                    var key = SymbolMapping.CanonicalKey(declSym);
                    if (key is null || !_symbolIdByKey.TryGetValue(key, out var memberId)) continue;

                    EmitUsesTypeForSignature(declSym, memberId, node, AddEdge);
                    EmitOverrides(declSym, memberId, node, AddEdge);

                    // Tests edge: the source is a test method (carries a recognised framework),
                    // the destination is the first non-trivial production call inside its body.
                    if (declSym is IMethodSymbol testMethod && TestDiscriminator.Detect(testMethod) is not null)
                    {
                        EmitTestsEdge(node, model, memberId, AddEdge, ct);
                    }
                }

                    if (refBatch.Count > 0)
                    {
                        await _store.BulkInsertReferencesAsync(refBatch, ct).ConfigureAwait(false);
                    }
                    if (edgeBatch.Count > 0)
                    {
                        await _store.BulkInsertEdgesAsync(edgeBatch, ct).ConfigureAwait(false);
                    }
                    refsIndexed += refBatch.Count;
                    completedPassTwoFileIds.Add(fileId);
                }
                catch (OperationCanceledException)
                {
                    // The outer catch persistently marks this and every not-yet-completed
                    // changed file before cancellation escapes to the caller.
                    throw;
                }
                catch (Exception ex)
                {
                    var isGenerated = changedFileMeta.TryGetValue(fileId, out var meta)
                        && meta.IsGenerated;
                    await MarkPassTwoIncompleteAsync(fileId, path, isGenerated).ConfigureAwait(false);
                    completedPassTwoFileIds.Add(fileId);
                    _requiresStructuralReload = true;

                    _logger.LogWarning(
                        ex,
                        "Pass 2 walk failed for {Path}; persisted a retry marker and cleared partial refs/edges",
                        path);
                    AddFileFailure(failedFiles, path, FailureMessage.Truncate(ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            _requiresStructuralReload = true;
            foreach (var fileId in walkedFileIds)
            {
                if (completedPassTwoFileIds.Contains(fileId)
                    || !changedFileMeta.TryGetValue(fileId, out var meta))
                {
                    continue;
                }

                await MarkPassTwoIncompleteAsync(
                        fileId,
                        meta.Path,
                        meta.IsGenerated)
                    .ConfigureAwait(false);
            }
            throw;
        }

        // PASS 3 — diagnostics. compilation.GetDiagnostics(ct) returns every diagnostic the workspace
        // would surface in IDE squiggles: analyzer warnings, compiler warnings/errors, etc. We persist
        // every one whose Location.SourceSpan is non-empty, attributing it to the smallest enclosing
        // indexed declaration when its position falls inside one. The reconcile step inside
        // UpsertDiagnosticsForFileAsync deletes existing rows for the file before inserting, so a
        // re-index naturally drops stale diagnostics for files whose warnings were silenced.
        //
        // We gather diagnostics per project (one compilation per project), then bucket them by
        // fileId so each file gets a single Upsert call with its full set in one transaction.
        var projectsTouched = new HashSet<ProjectId>();
        foreach (var docs in docsByChangedFile.Values)
        {
            foreach (var d in docs) projectsTouched.Add(d.Project.Id);
        }
        var diagnosticsByFile = new Dictionary<long, List<DiagnosticRecord>>();
        // Pre-create empty buckets for every successfully-walked file so files with zero
        // diagnostics still get an Upsert call to clear out stale rows from a prior index.
        // Files whose Pass 1B threw (absent from walkedFileIds) are deliberately not pre-bucketed —
        // their prior diagnostic rows stay in place until a successful re-walk.
        foreach (var fid in changedFileIds)
        {
            if (walkedFileIds.Contains(fid)) diagnosticsByFile[fid] = new List<DiagnosticRecord>();
        }
        var priorWpfDiagnosticFileIds =
            (await _store.ListDiagnosticFileIdsByCodesAsync(
                    [
                        WpfEventUnsubscriptionFinding.RuleId,
                        WpfUiThreadRiskAnalyzer.DiagnosticId,
                    ],
                    ct)
                .ConfigureAwait(false))
            .ToHashSet();

        bool TryResolveDiagnosticFileId(
            SyntaxTree tree,
            out long fileId)
        {
            if (fileIdBySyntaxTree.TryGetValue(tree, out fileId))
            {
                return true;
            }

            var path = tree.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                fileId = default;
                return false;
            }
            if (_fileIdByPath.TryGetValue(path, out fileId))
            {
                return true;
            }

            try
            {
                return _fileIdByPath.TryGetValue(
                    Path.GetFullPath(path),
                    out fileId);
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException)
            {
                fileId = default;
                return false;
            }
        }

        long? ResolveEnclosingSymbolId(
            Compilation compilation,
            SyntaxTree tree,
            int position)
        {
            try
            {
                var model = compilation.GetSemanticModel(tree);
                var enclosing = model.GetEnclosingSymbol(position, ct);
                while (enclosing is not null
                       && !SymbolMapping.IsIndexable(enclosing))
                {
                    enclosing = enclosing.ContainingSymbol;
                }
                if (enclosing is not null)
                {
                    var key = SymbolMapping.CanonicalKey(enclosing);
                    if (key is not null
                        && _symbolIdByKey.TryGetValue(key, out var symbolId))
                    {
                        return symbolId;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best-effort symbol attribution. File-scoped diagnostics remain useful when
                // Roslyn cannot identify the smallest indexed declaration.
            }

            return null;
        }

        bool TryCreateDiagnosticRecord(
            Compilation compilation,
            Diagnostic diagnostic,
            out DiagnosticRecord record)
        {
            record = default!;
            var location = diagnostic.Location;
            if (location.SourceSpan.IsEmpty
                || location.SourceTree is not { } tree
                || string.IsNullOrEmpty(tree.FilePath)
                || !TryResolveDiagnosticFileId(tree, out var fileId)
                || !diagnosticsByFile.ContainsKey(fileId)
                || (changedFileIds.Contains(fileId)
                    && !walkedFileIds.Contains(fileId)))
            {
                return false;
            }

            var lineSpan = location.GetLineSpan();
            record = new DiagnosticRecord(
                SymbolId: ResolveEnclosingSymbolId(
                    compilation,
                    tree,
                    location.SourceSpan.Start),
                FileId: fileId,
                Severity: (int)diagnostic.Severity,
                Code: diagnostic.Id,
                Message: diagnostic.GetMessage(),
                Line: lineSpan.StartLinePosition.Line + 1,
                Col: lineSpan.StartLinePosition.Character + 1);
            return true;
        }

        foreach (var pid in projectsTouched)
        {
            ct.ThrowIfCancellationRequested();
            // Reuse the pre-flight probe's compilation rather than calling GetCompilationAsync
            // again (Roslyn caches per project, but skipping the round-trip is cheaper). Failed
            // probe projects are already excluded from `projectsTouched` because their docs were
            // filtered out before Pass 1A; the TryGetValue branch is a safety net for any TFM
            // iteration that escaped the filter.
            if (!_probedCompilations.TryGetValue(pid, out var compilation))
            {
                continue;
            }

            ImmutableArray<Diagnostic> diags;
            try
            {
                diags = compilation.GetDiagnostics(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var diagnosticProject = _sanitizedSolution!.GetProject(pid);
                _logger.LogWarning(
                    ex,
                    "Failed to read diagnostics for {Project}",
                    diagnosticProject?.Name ?? pid.ToString());
                AddFileFailure(
                    failedFiles,
                    diagnosticProject?.FilePath
                    ?? diagnosticProject?.Name
                    ?? pid.ToString(),
                    "diagnostic discovery failed: " +
                    FailureMessage.Truncate(ex.Message));
                continue;
            }

            // A project-wide WPF risk can change when a different file is edited (for example,
            // adding an exact -= in another partial declaration or changing a receiver's base
            // type). Map the complete touched project, but rewrite only changed files plus files
            // that owned a prior or current WPF diagnostic. That clears stale rows on unchanged
            // files without turning each edit into thousands of empty diagnostic transactions.
            var projectFileIds = new HashSet<long>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!TryResolveDiagnosticFileId(tree, out var projectFileId)
                    || (changedFileIds.Contains(projectFileId)
                        && !walkedFileIds.Contains(projectFileId)))
                {
                    continue;
                }

                projectFileIds.Add(projectFileId);
                if (priorWpfDiagnosticFileIds.Contains(projectFileId))
                {
                    diagnosticsByFile.TryAdd(
                        projectFileId,
                        new List<DiagnosticRecord>());
                }
            }

            var project = _sanitizedSolution!.GetProject(pid);
            var projectCompilationErrorCount = diags.Count(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error);
            compilationErrorCount += projectCompilationErrorCount;
            var compilationContainsErrors = projectCompilationErrorCount > 0;
            var semanticInputComplete =
                project?.FilePath is { Length: > 0 } projectFilePath
                && IsProjectSemanticInputComplete(
                    _workspace!.CurrentSolution,
                    _sanitizedSolution,
                    projectFilePath,
                    _analyzerReferenceLoadCompleteByProject);

            ImmutableArray<Diagnostic> wpfUiDiagnostics = [];
            if (semanticInputComplete && !compilationContainsErrors)
            {
                try
                {
                    wpfUiDiagnostics =
                        WpfUiThreadRiskAnalyzer.Analyze(compilation, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed WPF UI-thread analysis for {Project}",
                        project?.Name ?? pid.ToString());
                    AddFileFailure(
                        failedFiles,
                        project?.FilePath ?? project?.Name ?? pid.ToString(),
                        "WPF UI-thread analysis failed: "
                        + FailureMessage.Truncate(ex.Message));
                    _requiresStructuralReload = true;
                }
            }

            IReadOnlyList<WpfEventUnsubscriptionFinding> eventFindings = [];
            if (semanticInputComplete && !compilationContainsErrors)
            {
                var eventAnalysis = WpfEventSubscriptionAnalyzer.Analyze(
                    compilation,
                    semanticInputComplete: true,
                    compilationContainsErrors: false,
                    cancellationToken: ct);
                if (eventAnalysis.IsComplete)
                {
                    eventFindings = eventAnalysis.Findings;
                }
                else
                {
                    var reasons = string.Join(
                        ", ",
                        eventAnalysis.Unknowns
                            .Select(unknown => unknown.Reason)
                            .Distinct(StringComparer.Ordinal));
                    AddFileFailure(
                        failedFiles,
                        project?.FilePath ?? project?.Name ?? pid.ToString(),
                        "WPF event-lifetime analysis incomplete"
                        + (reasons.Length > 0 ? $": {reasons}" : string.Empty));
                    _requiresStructuralReload = true;
                }
            }

            foreach (var diagnostic in wpfUiDiagnostics)
            {
                if (diagnostic.Location.SourceTree is { } tree
                    && TryResolveDiagnosticFileId(tree, out var fileId)
                    && projectFileIds.Contains(fileId))
                {
                    diagnosticsByFile.TryAdd(
                        fileId,
                        new List<DiagnosticRecord>());
                }
            }
            foreach (var finding in eventFindings)
            {
                if (TryResolveDiagnosticFileId(
                        finding.SyntaxTree,
                        out var fileId)
                    && projectFileIds.Contains(fileId))
                {
                    diagnosticsByFile.TryAdd(
                        fileId,
                        new List<DiagnosticRecord>());
                }
            }

            foreach (var diag in diags.Concat(wpfUiDiagnostics))
            {
                ct.ThrowIfCancellationRequested();
                if (!TryCreateDiagnosticRecord(
                        compilation,
                        diag,
                        out var record))
                {
                    continue;
                }

                diagnosticsByFile[record.FileId].Add(record);
            }

            foreach (var finding in eventFindings)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryResolveDiagnosticFileId(
                        finding.SyntaxTree,
                        out var fileId)
                    || !diagnosticsByFile.TryGetValue(fileId, out var bucket)
                    || (changedFileIds.Contains(fileId)
                        && !walkedFileIds.Contains(fileId)))
                {
                    continue;
                }

                bucket.Add(finding.ToDiagnosticRecord(
                    fileId,
                    ResolveEnclosingSymbolId(
                        compilation,
                        finding.SyntaxTree,
                        finding.SourceSpan.Start)));
            }
        }

        // Reconcile diagnostics: even files with zero diagnostics get an Upsert call so that
        // freshly-fixed warnings disappear from the table on the next index.
        foreach (var (fid, bucket) in diagnosticsByFile)
        {
            await _store.UpsertDiagnosticsForFileAsync(
                    fid,
                    bucket.Distinct(),
                    ct)
                .ConfigureAwait(false);
        }

        if (unchangedManagedInteropRefreshes.Count > 0)
        {
            if (failedFiles.Count > 0 || _probedFailures.Count > 0)
            {
                // Keep every prior caller-owned fact if any other part of the pass is
                // incomplete. A structural retry will rebuild the import and caller universes
                // together before analysis publication is allowed again.
                _requiresStructuralReload = true;
            }
            else
            {
                try
                {
                    var usageRefreshFailures =
                        await RefreshUnchangedManagedInteropUsagesAsync(
                                unchangedManagedInteropRefreshes,
                                method => ResolveManagedImportFileId(
                                    method,
                                    fileIdBySyntaxTree),
                                ct)
                            .ConfigureAwait(false);
                    foreach (var failure in usageRefreshFailures)
                    {
                        AddFileFailure(
                            failedFiles,
                            failure.Path,
                            failure.Reason);
                    }
                    if (usageRefreshFailures.Count > 0)
                    {
                        _requiresStructuralReload = true;
                    }
                    else
                    {
                        filesIndexed +=
                            unchangedManagedInteropRefreshes.Count;
                    }
                }
                catch (OperationCanceledException)
                {
                    _requiresStructuralReload = true;
                    throw;
                }
            }
        }

        sw.Stop();
        var stats = await _store.GetStatsAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Indexed {Files} (re)processed files, {Symbols} new symbols, {Refs} new references in {Elapsed} (store totals: {SF}/{SS}/{SR})",
            filesIndexed, symbolsIndexed, refsIndexed, sw.Elapsed, stats.FileCount, stats.SymbolCount, stats.ReferenceCount);

        // Notify history pipeline (or any other downstream consumer) that these files just had
        // their symbols re-upserted. Fired at the end so all symbol ids are stable in the store
        // by the time the consumer queries by file_id. Best-effort: callback failures are
        // swallowed (logged at debug) so a flaky consumer never breaks the index pass.
        if (OnFileIndexed is not null && changedFileMeta.Count > 0)
        {
            foreach (var (fileId, meta) in changedFileMeta)
            {
                // A declaration/annotation reconciliation failure removes the owner from the
                // successful walked set. Do not let downstream consumers observe Phase-B
                // upserts paired with a hash whose projection never committed.
                if (!walkedFileIds.Contains(fileId))
                {
                    continue;
                }
                // Generated owner paths are deliberately virtual and have no disk/git object to
                // blame. Do not enqueue them into the history pipeline, whose contract is a
                // physical repository path.
                if (meta.IsGenerated)
                {
                    continue;
                }
                try
                {
                    await OnFileIndexed(fileId, meta.Path, meta.Sha).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OnFileIndexed callback threw for {Path}; continuing", meta.Path);
                }
            }
        }

        return new IndexResult(filesIndexed, symbolsIndexed, refsIndexed, sw.Elapsed)
        {
            FailedProjects = _probedFailures,
            FailedFiles = failedFiles,
            CompilationErrorCount = compilationErrorCount,
        };
    }

    /// <summary>
    /// Build the synthesised text for a symbol and enqueue an embed request, applying the
    /// generated-file/trivial-accessor skip rules first. Producer side never blocks.
    /// </summary>
    private void EnqueueEmbedRequest(long id, string? filePath, ISymbol roslynSymbol, Symbol coreSymbol)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        // Skip generated files and trivial-accessor-only symbols cheaply, before paying the
        // cost of a syntax-tree round trip for the body excerpt.
        if (SymbolTextBuilder.IsGeneratedFile(filePath)) return;

        // Body excerpt is only meaningful for kinds that have one. Types/namespaces/fields
        // get their text from the kind+fqn+xml_summary triple, which is enough for the
        // skip rule to keep them when there's a doc summary, otherwise discard.
        string? body = roslynSymbol switch
        {
            IMethodSymbol or IPropertySymbol or INamedTypeSymbol => SymbolMapping.BodyExcerpt(roslynSymbol, maxLines: 40),
            _ => null,
        };

        // coreSymbol.Kind is already a lowercase kebab-case identifier (e.g. "method", "enum-member").
        var text = SymbolTextBuilder.Build(coreSymbol.Kind, coreSymbol.Fqn, coreSymbol.XmlSummary, coreSymbol.Signature, body);

        if (SymbolTextBuilder.ShouldSkip(filePath, coreSymbol.XmlSummary, coreSymbol.Signature, body)) return;

        var hash = SymbolTextBuilder.HashOf(text);
        _embeddingsSink.Enqueue(new EmbedRequest(id, text, hash));
    }

    private async Task HydrateMapsFromStoreAsync(CancellationToken ct)
    {
        _symbolIdByKey.Clear();
        _keysByFileId.Clear();
        _fileIdByPath.Clear();
        _storedPathByFileId.Clear();

        var symbolRows = await _store.GetAllSymbolKeysAsync(ct).ConfigureAwait(false);
        var hydrated = 0;
        foreach (var row in symbolRows)
        {
            // Multiple language indexers share the same per-scope store now that
            // `xaml-language-indexer` shipped: rows here may carry `csharp:`, `xaml:`, or any
            // future scheme. The C# pathway only owns its own scheme — silently skip rows from
            // other languages so the in-memory `_symbolIdByKey` map (which is consulted only by
            // C# emission sites) stays scheme-pure. The XAML indexer maintains its own per-pass
            // map via the dispatcher's `GraphStoreEmitter`, so non-csharp rows aren't lost,
            // they're just not the C# pathway's concern.
            if (!row.CanonicalKey.StartsWith(SymbolMapping.CanonicalKeyScheme, StringComparison.Ordinal))
            {
                continue;
            }
            _symbolIdByKey[row.CanonicalKey] = row.Id;
            if (!_keysByFileId.TryGetValue(row.FileId, out var keys))
            {
                keys = new List<string>();
                _keysByFileId[row.FileId] = keys;
            }
            keys.Add(row.CanonicalKey);
            hydrated++;
        }
        var fileRows = await _store.GetAllFilesAsync(ct).ConfigureAwait(false);
        foreach (var fr in fileRows)
        {
            _fileIdByPath[fr.Path] = fr.Id;
            _storedPathByFileId[fr.Id] = fr.Path;
            // Seed `_keysByFileId` with an empty list for files that didn't contribute any
            // symbol rows above (usings-only files, files containing only `[assembly:]` /
            // `[module:]` attributes, etc.). Without this, the pass-1 SHA-skip path's
            // TryGetValue check would miss those files after a process restart and fall
            // through to a redundant pass-2 walk that emits nothing.
            if (!_keysByFileId.ContainsKey(fr.Id))
            {
                _keysByFileId[fr.Id] = new List<string>();
            }
        }
        if (hydrated > 0)
        {
            _logger.LogInformation("Hydrated {Symbols} csharp symbol(s) and {Files} file(s) from graph store", hydrated, fileRows.Count);
        }
        _mapsHydrated = true;
    }

    private static ISymbol? FindEnclosingMember(SemanticModel model, int position, CancellationToken ct)
    {
        var symbol = model.GetEnclosingSymbol(position, ct);
        while (symbol is not null and not IMethodSymbol and not IPropertySymbol and not IFieldSymbol and not IEventSymbol)
        {
            symbol = symbol.ContainingSymbol;
        }
        return symbol;
    }

    /// <summary>
    /// Returns Read/Write for field/property/local/parameter accesses based on syntactic position;
    /// returns null when the access doesn't qualify (e.g. method or type reference) so the caller
    /// can default to <see cref="ReferenceKind.Reference"/>.
    /// Compound increments and ref args produce <see cref="ReferenceKind.Write"/> here; the caller
    /// uses <see cref="SplitReadWrite"/> to also emit a Read row at the same position.
    /// </summary>
    private static ReferenceKind? ClassifyReadWrite(SyntaxNode node, ISymbol? symbol)
    {
        if (symbol is null) return null;
        // Only data-bearing symbols have meaningful read/write.
        if (symbol is not IFieldSymbol and not IPropertySymbol and not IEventSymbol and not ILocalSymbol and not IParameterSymbol)
        {
            return null;
        }

        // Walk outward through any conditional access / paren / cast wrappers to find the position
        // (LHS of an assignment? operand of ++ / --? argument with a ref/out modifier?).
        SyntaxNode current = node;
        while (current.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax or ConditionalAccessExpressionSyntax)
        {
            current = current.Parent;
        }

        var parent = current.Parent;
        switch (parent)
        {
            case AssignmentExpressionSyntax assign when assign.Left == current:
                // = is a pure write; +=, -=, *=, etc. are read-then-write — SplitReadWrite fans
                // out a Read row in addition to the Write returned here.
                return ReferenceKind.Write;
            case PostfixUnaryExpressionSyntax post when (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression)) && post.Operand == current:
                return ReferenceKind.Write;
            case PrefixUnaryExpressionSyntax pre when (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression)) && pre.Operand == current:
                return ReferenceKind.Write;
            case ArgumentSyntax arg when arg.Expression == current && arg.RefKindKeyword.Kind() is SyntaxKind.OutKeyword or SyntaxKind.RefKeyword:
                return ReferenceKind.Write;
        }

        return ReferenceKind.Read;
    }

    /// <summary>
    /// For positions that semantically read AND write (++, --, +=, ref args), returns both
    /// <see cref="ReferenceKind.Read"/> and <see cref="ReferenceKind.Write"/>; otherwise returns
    /// the single classified kind.
    /// </summary>
    private static IEnumerable<ReferenceKind> SplitReadWrite(ReferenceKind kind, SyntaxNode node, ISymbol symbol)
    {
        if (kind != ReferenceKind.Write)
        {
            yield return kind;
            yield break;
        }
        if (symbol is not IFieldSymbol and not IPropertySymbol and not IEventSymbol and not ILocalSymbol and not IParameterSymbol)
        {
            yield return kind;
            yield break;
        }

        SyntaxNode current = node;
        while (current.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax or ConditionalAccessExpressionSyntax)
        {
            current = current.Parent;
        }
        var parent = current.Parent;
        var dual = parent switch
        {
            AssignmentExpressionSyntax a => a.Left == current && !a.IsKind(SyntaxKind.SimpleAssignmentExpression),
            PostfixUnaryExpressionSyntax po => po.Operand == current,
            PrefixUnaryExpressionSyntax pr => pr.Operand == current,
            ArgumentSyntax arg => arg.Expression == current && arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword),
            _ => false,
        };
        if (dual)
        {
            yield return ReferenceKind.Read;
            yield return ReferenceKind.Write;
        }
        else
        {
            yield return ReferenceKind.Write;
        }
    }

    /// <summary>
    /// Emits <see cref="EdgeKinds.CommandExecutes"/> from an indexed ICommand-like property to
    /// the one indexed source method that Roslyn proves is captured by a newly constructed
    /// ICommand implementation. Both property initializers and simple property assignments are
    /// supported. Multiple delegate arguments, lambdas, dynamic/invalid operations, metadata
    /// methods, and unresolved endpoints fail closed.
    /// </summary>
    private void EmitCommandExecutes(
        SyntaxNode root,
        SemanticModel model,
        Action<long, long, string, SyntaxNode, CoreEvidenceConfidence> addEdge,
        CancellationToken ct)
    {
        var commandType = model.Compilation.GetTypeByMetadataName(
            "System.Windows.Input.ICommand");
        if (commandType is null)
        {
            return;
        }

        foreach (var propertyDeclaration in root
                     .DescendantNodes()
                     .OfType<PropertyDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (propertyDeclaration.Initializer?.Value is not { } initializer)
            {
                continue;
            }

            if (model.GetDeclaredSymbol(propertyDeclaration, ct) is not
                    IPropertySymbol property
                || model.GetOperation(initializer, ct) is not { } value)
            {
                continue;
            }

            TryEmit(property, value, addEdge);
        }

        foreach (var assignmentSyntax in root
                     .DescendantNodes()
                     .OfType<AssignmentExpressionSyntax>()
                     .Where(assignment =>
                         assignment.IsKind(
                             SyntaxKind.SimpleAssignmentExpression)))
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetOperation(assignmentSyntax, ct) is not
                    ISimpleAssignmentOperation assignment
                || UnwrapOperation(assignment.Target) is not
                    IPropertyReferenceOperation propertyReference)
            {
                continue;
            }

            TryEmit(propertyReference.Property, assignment.Value, addEdge);
        }

        void TryEmit(
            IPropertySymbol property,
            IOperation value,
            Action<long, long, string, SyntaxNode, CoreEvidenceConfidence>
                emit)
        {
            if (!IsICommandLike(property.Type, commandType)
                || UnwrapOperation(value) is not
                    IObjectCreationOperation creation
                || creation.Constructor is null
                || creation.Type is null
                || !IsICommandLike(creation.Type, commandType))
            {
                return;
            }

            var delegateArguments = creation.Arguments
                .Where(argument =>
                    argument.ArgumentKind != ArgumentKind.DefaultValue
                    && argument.Parameter?.Type.TypeKind == TypeKind.Delegate)
                .ToArray();
            if (delegateArguments.Length != 1
                || UnwrapOperation(delegateArguments[0].Value) is not
                    IDelegateCreationOperation delegateCreation
                || UnwrapOperation(delegateCreation.Target) is not
                    IMethodReferenceOperation uniqueMethodReference)
            {
                return;
            }

            if (uniqueMethodReference.Method is not { } handler
                || handler.DeclaringSyntaxReferences.Length == 0
                || !SymbolMapping.IsIndexable(handler))
            {
                return;
            }

            var propertyKey = SymbolMapping.CanonicalKey(property);
            var handlerKey = SymbolMapping.CanonicalKey(handler);
            if (propertyKey is null
                || handlerKey is null
                || !_symbolIdByKey.TryGetValue(propertyKey, out var propertyId)
                || !_symbolIdByKey.TryGetValue(handlerKey, out var handlerId))
            {
                return;
            }

            emit(
                propertyId,
                handlerId,
                EdgeKinds.CommandExecutes,
                uniqueMethodReference.Syntax,
                CoreEvidenceConfidence.Semantic);
        }
    }

    private void EmitScheduledExecutions(
        SyntaxNode root,
        SemanticModel model,
        Action<long, long, string, SyntaxNode, CoreEvidenceConfidence> addEdge,
        CancellationToken ct)
    {
        foreach (var schedulerSyntax in root
                     .DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetOperation(schedulerSyntax, ct) is not
                IInvocationOperation scheduler)
            {
                continue;
            }

            var relation = SchedulerRelation(scheduler.TargetMethod);
            if (relation is null)
            {
                continue;
            }

            var source = FindEnclosingNamedMember(
                model,
                schedulerSyntax.SpanStart,
                ct);
            var sourceKey = source is null
                ? null
                : SymbolMapping.CanonicalKey(source);
            if (sourceKey is null
                || !_symbolIdByKey.TryGetValue(sourceKey, out var sourceId))
            {
                continue;
            }

            foreach (var argument in schedulerSyntax.ArgumentList.Arguments)
            {
                if (UnwrapArgument(argument.Expression) is
                    AnonymousFunctionExpressionSyntax lambda)
                {
                    foreach (var nestedCall in lambda
                                 .DescendantNodes()
                                 .OfType<InvocationExpressionSyntax>()
                                 .Where(call =>
                                     ReferenceEquals(
                                         call.Ancestors()
                                             .OfType<
                                                 AnonymousFunctionExpressionSyntax>()
                                             .FirstOrDefault(),
                                         lambda)))
                    {
                        TryEmitTarget(
                            model.GetSymbolInfo(nestedCall, ct).Symbol
                            as IMethodSymbol,
                            nestedCall);
                    }
                    continue;
                }

                TryEmitTarget(
                    model.GetSymbolInfo(argument.Expression, ct).Symbol
                    as IMethodSymbol,
                    argument.Expression);
            }

            void TryEmitTarget(
                IMethodSymbol? target,
                SyntaxNode evidenceNode)
            {
                if (target is null)
                {
                    return;
                }
                var targetKey = SymbolMapping.CanonicalKey(target);
                if (targetKey is null
                    || !_symbolIdByKey.TryGetValue(
                        targetKey,
                        out var targetId))
                {
                    return;
                }
                addEdge(
                    sourceId,
                    targetId,
                    relation,
                    evidenceNode,
                    CoreEvidenceConfidence.Semantic);
            }
        }
    }

    private static ExpressionSyntax UnwrapArgument(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                default:
                    return expression;
            }
        }
    }

    private static string? SchedulerRelation(IMethodSymbol method)
    {
        var containingType = method.ContainingType?.ToDisplayString();
        if ((containingType == "System.Threading.Tasks.Task"
             && method.Name == "Run")
            || (containingType == "System.Threading.Tasks.TaskFactory"
                && method.Name == "StartNew"))
        {
            return EdgeKinds.Schedules;
        }

        if ((containingType == "System.Windows.Threading.Dispatcher"
             && method.Name is "Invoke" or "BeginInvoke" or "InvokeAsync")
            || (containingType == "System.Threading.SynchronizationContext"
                && method.Name == "Post"))
        {
            return EdgeKinds.Dispatches;
        }

        return null;
    }

    private static ISymbol? FindEnclosingNamedMember(
        SemanticModel model,
        int position,
        CancellationToken ct)
    {
        var symbol = model.GetEnclosingSymbol(position, ct);
        while (symbol is IMethodSymbol
               {
                   MethodKind: MethodKind.AnonymousFunction
                       or MethodKind.LocalFunction,
               })
        {
            symbol = symbol.ContainingSymbol;
        }
        while (symbol is not null
               and not IMethodSymbol
               and not IPropertySymbol
               and not IFieldSymbol
               and not IEventSymbol)
        {
            symbol = symbol.ContainingSymbol;
        }
        return symbol;
    }

    private static IOperation UnwrapOperation(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                default:
                    return operation;
            }
        }
    }

    private static bool IsICommandLike(
        ITypeSymbol type,
        INamedTypeSymbol commandType)
    {
        return type is INamedTypeSymbol named
            && (SymbolEqualityComparer.Default.Equals(named, commandType)
                || named.AllInterfaces.Any(interfaceType =>
                    SymbolEqualityComparer.Default.Equals(
                        interfaceType,
                        commandType)));
    }

    /// <summary>
    /// Emits UsesType edges from <paramref name="memberId"/> to every type appearing in
    /// <paramref name="member"/>'s signature (return type, parameter types, generic args)
    /// when those types are themselves indexed. BCL types are skipped by the
    /// <c>_symbolIdByKey</c> lookup.
    /// </summary>
    private void EmitUsesTypeForSignature(
        ISymbol member,
        long memberId,
        SyntaxNode declarationNode,
        Action<long, long, string, SyntaxNode, CoreEvidenceConfidence> addEdge)
    {
        IEnumerable<ITypeSymbol> types = member switch
        {
            IMethodSymbol m => SignatureTypes(m),
            IPropertySymbol p => new[] { p.Type }.Concat(p.Parameters.SelectMany(pp => Walk(pp.Type))),
            IEventSymbol e => Walk(e.Type),
            IFieldSymbol f => Walk(f.Type),
            _ => Array.Empty<ITypeSymbol>(),
        };
        foreach (var t in types.Distinct(SymbolEqualityComparer.Default).OfType<ITypeSymbol>())
        {
            var key = SymbolMapping.CanonicalKey(t);
            if (key is null) continue;
            if (!_symbolIdByKey.TryGetValue(key, out var typeId)) continue;
            addEdge(
                memberId,
                typeId,
                EdgeKinds.UsesType,
                declarationNode,
                CoreEvidenceConfidence.Semantic);
        }

        static IEnumerable<ITypeSymbol> SignatureTypes(IMethodSymbol m)
        {
            foreach (var t in Walk(m.ReturnType)) yield return t;
            foreach (var p in m.Parameters)
                foreach (var t in Walk(p.Type)) yield return t;
            foreach (var tp in m.TypeArguments)
                foreach (var t in Walk(tp)) yield return t;
        }

        // Walk type and any closed generic arguments (e.g. List<Bar> -> List<>, Bar).
        static IEnumerable<ITypeSymbol> Walk(ITypeSymbol type)
        {
            if (type is null) yield break;
            yield return type.OriginalDefinition;
            if (type is INamedTypeSymbol named)
            {
                foreach (var arg in named.TypeArguments)
                    foreach (var t in Walk(arg)) yield return t;
            }
            else if (type is IArrayTypeSymbol arr)
            {
                foreach (var t in Walk(arr.ElementType)) yield return t;
            }
        }
    }

    /// <summary>
    /// Emits a single <see cref="EdgeKinds.Tests"/> edge from a test method (id =
    /// <paramref name="testMemberId"/>) to the first non-trivial production-code call inside
    /// the method's body. "Non-trivial" excludes calls into other test methods, calls into a
    /// type whose <c>[TestFixture]</c>/<c>[TestClass]</c> marker labels it as a test fixture,
    /// and calls into files under a <c>/tests/</c> path segment. We pick only the first such
    /// call to keep the edge focused and avoid noisy 1:N fanout per parametrised test.
    /// </summary>
    private void EmitTestsEdge(
        SyntaxNode methodNode,
        SemanticModel model,
        long testMemberId,
        Action<long, long, string, SyntaxNode, CoreEvidenceConfidence> addEdge,
        CancellationToken ct)
    {
        // Methods we walk include MethodDeclarationSyntax but EnumerateDeclarations also yields
        // type/property/etc. nodes. Restrict ourselves to method bodies and expression-bodied
        // members; nothing else has invocations worth interpreting as a test target.
        foreach (var inv in methodNode.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            var calledSym = model.GetSymbolInfo(inv, ct).Symbol;
            if (calledSym is not IMethodSymbol calledMethod) continue;
            if (IsTestSymbol(calledMethod)) continue;
            var key = SymbolMapping.CanonicalKey(calledMethod);
            if (key is null) continue;
            if (!_symbolIdByKey.TryGetValue(key, out var dstId)) continue;
            if (dstId == testMemberId) continue;
            addEdge(
                testMemberId,
                dstId,
                EdgeKinds.Tests,
                inv,
                CoreEvidenceConfidence.Exact);
            return; // first non-trivial production call wins
        }
    }

    /// <summary>
    /// Heuristic for "this symbol lives in test code": its containing assembly name ends in
    /// <c>.Tests</c>, the symbol itself is a test method, or its containing type is a test
    /// fixture (carries <c>[TestFixture]</c>/<c>[TestClass]</c>, or contains an xUnit
    /// <c>[Fact]</c>/<c>[Theory]</c> method).
    /// We intentionally DO NOT check whether the file path contains <c>/tests/</c>: in the
    /// fixture solution under <c>tests/fixtures/Sample.Domain/</c>, the production code lives
    /// under a path with that segment, and we still want it to be a Tests-edge target. The
    /// assembly-name + container-fixture signals are sufficient for real-world code.
    /// </summary>
    private static bool IsTestSymbol(ISymbol symbol)
    {
        if (symbol is IMethodSymbol m && TestDiscriminator.Detect(m) is not null) return true;
        if (TestDiscriminator.IsTestFixture(symbol.ContainingType)) return true;
        var assembly = symbol.ContainingAssembly?.Name;
        if (!string.IsNullOrEmpty(assembly) && assembly.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Emits OverridesMember edges when <paramref name="member"/>'s Roslyn Overridden* property
    /// points at an indexed symbol.
    /// </summary>
    private void EmitOverrides(
        ISymbol member,
        long memberId,
        SyntaxNode declarationNode,
        Action<long, long, string, SyntaxNode, CoreEvidenceConfidence> addEdge)
    {
        ISymbol? overridden = member switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null,
        };
        if (overridden is null) return;
        var key = SymbolMapping.CanonicalKey(overridden);
        if (key is null || !_symbolIdByKey.TryGetValue(key, out var dstId)) return;
        addEdge(
            memberId,
            dstId,
            EdgeKinds.OverridesMember,
            declarationNode,
            CoreEvidenceConfidence.Semantic);
    }

    /// <summary>
    /// Walks <paramref name="typeSymbol"/>'s implemented interfaces and emits ImplementsMember
    /// edges from each implementing member to the satisfied interface member.
    /// </summary>
    private void EmitMemberImplements(
        INamedTypeSymbol typeSymbol,
        SyntaxNode typeDeclarationNode,
        Action<long, long, string, SyntaxNode, CoreEvidenceConfidence> addEdge,
        CancellationToken ct)
    {
        if (typeSymbol.TypeKind is TypeKind.Interface) return; // interface declarations don't implement
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers())
            {
                if (ifaceMember is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) continue;
                var impl = typeSymbol.FindImplementationForInterfaceMember(ifaceMember);
                if (impl is null) continue;
                // Only emit when the implementation lives on this type (not inherited from a base).
                if (!SymbolEqualityComparer.Default.Equals(impl.ContainingType, typeSymbol)) continue;
                var srcKey = SymbolMapping.CanonicalKey(impl);
                if (srcKey is null || !_symbolIdByKey.TryGetValue(srcKey, out var srcId)) continue;
                var dstKey = SymbolMapping.CanonicalKey(ifaceMember);
                if (dstKey is null || !_symbolIdByKey.TryGetValue(dstKey, out var dstId)) continue;
                var evidenceNode = impl.DeclaringSyntaxReferences
                    .FirstOrDefault(reference =>
                        reference.SyntaxTree == typeDeclarationNode.SyntaxTree)
                    ?.GetSyntax(ct)
                    ?? typeDeclarationNode;
                addEdge(
                    srcId,
                    dstId,
                    EdgeKinds.ImplementsMember,
                    evidenceNode,
                    CoreEvidenceConfidence.Semantic);
                addEdge(
                    dstId,
                    srcId,
                    EdgeKinds.InterfaceDispatchesTo,
                    evidenceNode,
                    CoreEvidenceConfidence.Semantic);
            }
        }
    }

    private void DropFileFromMaps(long fileId)
    {
        if (_keysByFileId.TryGetValue(fileId, out var keys))
        {
            _keysByFileId.Remove(fileId);
            foreach (var key in keys)
            {
                // A generated owner can churn across workspace re-open. Pass 1 upserts its
                // canonical symbols onto the new file id before stale-owner reconciliation
                // deletes the old row; retain map entries now owned by another file.
                if (!_keysByFileId.Values.Any(otherKeys =>
                        otherKeys.Contains(key, StringComparer.Ordinal)))
                {
                    _symbolIdByKey.Remove(key);
                }
            }
        }
        foreach (var path in _fileIdByPath
                     .Where(entry => entry.Value == fileId)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _fileIdByPath.Remove(path);
        }
        _storedPathByFileId.Remove(fileId);
    }

    private static void AddFileFailure(
        ICollection<FileFailure> failures,
        string path,
        string reason)
    {
        if (failures.Any(existing =>
                string.Equals(existing.Path, path, _pathComparison)))
        {
            return;
        }
        failures.Add(new FileFailure(path, reason));
    }

    private static IndexResult WithAdditionalFileFailures(
        IndexResult result,
        IReadOnlyCollection<FileFailure> additionalFailures)
    {
        if (additionalFailures.Count == 0)
        {
            return result;
        }

        var mergedFailures = result.FailedFiles.ToList();
        foreach (var failure in additionalFailures)
        {
            AddFileFailure(mergedFailures, failure.Path, failure.Reason);
        }
        return result with
        {
            FailedFiles = mergedFailures,
        };
    }

    private static IEnumerable<SyntaxNode> EnumerateDeclarations(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseNamespaceDeclarationSyntax:
                case BaseTypeDeclarationSyntax:
                case DelegateDeclarationSyntax:
                case MethodDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case EventDeclarationSyntax:
                case EnumMemberDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                    yield return node;
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        yield return variable;
                    }
                    break;
                case FieldDeclarationSyntax fd:
                    foreach (var v in fd.Declaration.Variables) yield return v;
                    break;
            }
        }
    }

    /// <summary>
    /// Convenience for one-shot CLI: opens the solution, runs a full index, disposes.
    /// </summary>
    public static async Task<IndexResult> IndexSolutionOnceAsync(
        string solutionPath,
        IGraphStore store,
        ILogger<RoslynIndexer>? logger = null,
        IEmbeddingsRequestSink? embeddingsSink = null,
        CancellationToken ct = default)
    {
        await using var indexer = new RoslynIndexer(store, logger, embeddingsSink);
        await indexer.OpenAsync(solutionPath, ct).ConfigureAwait(false);
        return await indexer.IndexAllAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_testHooks?.DisposeAsyncEntered is { } disposeAsyncEntered)
            {
                await disposeAsyncEntered().ConfigureAwait(false);
            }
            var workspace = _workspace;
            _workspace = null;
            _sanitizedSolution = null;
            _analyzerReferenceLoadCompleteByProject =
                new Dictionary<ProjectId, bool>();
            _pathPolicy = null;
            _solutionPath = null;
            _requiresStructuralReload = false;
            _confirmedOutsideSolutionPaths.Clear();
            try
            {
                if (workspace is not null)
                {
                    DisposeWorkspace(workspace);
                }
            }
            finally
            {
                _workspaceDiagnostics.Clear();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string? ClassifyEventRelation(SyntaxNode reference)
    {
        var assignment = reference
            .AncestorsAndSelf()
            .OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(candidate =>
                candidate.Left.Span.Contains(reference.Span));
        if (assignment is not null)
        {
            return assignment.Kind() switch
            {
                SyntaxKind.AddAssignmentExpression =>
                    EdgeKinds.SubscribesEvent,
                SyntaxKind.SubtractAssignmentExpression =>
                    EdgeKinds.UnsubscribesEvent,
                _ => null,
            };
        }

        var conditionalRaise = reference
            .AncestorsAndSelf()
            .OfType<ConditionalAccessExpressionSyntax>()
            .Any(candidate =>
                candidate.Expression.Span.Contains(reference.Span)
                && candidate.WhenNotNull
                    .DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(invocation =>
                        invocation.Expression switch
                        {
                            MemberBindingExpressionSyntax binding =>
                                binding.Name.Identifier.ValueText == "Invoke",
                            MemberAccessExpressionSyntax access =>
                                access.Name.Identifier.ValueText == "Invoke",
                            _ => false,
                        }));
        if (conditionalRaise)
        {
            return EdgeKinds.RaisesEvent;
        }

        var directRaise = reference
            .AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax access
                && access.Expression.Span.Contains(reference.Span)
                && access.Name.Identifier.ValueText == "Invoke");
        return directRaise ? EdgeKinds.RaisesEvent : null;
    }

    /// <summary>
    /// <see cref="ILanguageIndexer"/> contract entry point. The plugin-aware dispatcher in the
    /// server special-cases <c>.cs</c> and keeps the existing workspace-aware bulk path, so this
    /// method is rarely invoked at runtime. It exists so the contract is satisfied (the type
    /// really IS the seam) and so a downstream tool that wants per-file events without opening
    /// an MSBuildWorkspace can still get them via syntax-tree-only parsing.
    /// </summary>
    Task<IReadOnlyList<Sdk.IndexEvent>> ILanguageIndexer.IndexAsync(IndexContext ctx, CancellationToken ct)
    {
        IReadOnlyList<Sdk.IndexEvent> events = ExtractEventsFromSyntaxTree(ctx);
        return Task.FromResult(events);
    }

    /// <summary>
    /// Parse <paramref name="ctx"/>'s contents as a C# syntax tree (no workspace, no semantics)
    /// and emit <see cref="Sdk.IndexEvent.SymbolDeclared"/> + <see cref="Sdk.IndexEvent.FileScanned"/>
    /// records for each top-level declaration. Used by the per-document <see cref="ILanguageIndexer.IndexAsync"/>
    /// path; intentionally does NOT call into the workspace-aware bulk path so this method is
    /// safe to invoke from a thread that doesn't own the indexer's <see cref="_lock"/>.
    /// </summary>
    private static IReadOnlyList<Sdk.IndexEvent> ExtractEventsFromSyntaxTree(IndexContext ctx)
    {
        var events = new List<Sdk.IndexEvent>();
        var sha = SHA256.HashData(ctx.Contents);
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(ctx.Contents, ctx.Contents.Length, System.Text.Encoding.UTF8));
        var root = tree.GetRoot();
        foreach (var node in EnumerateDeclarations(root))
        {
            string? name = null;
            string? fqn = null;
            string kind = Sdk.SymbolKinds.Other;
            switch (node)
            {
                case ClassDeclarationSyntax c:
                    name = c.Identifier.ValueText;
                    fqn = QualifyTypeName(c, name);
                    kind = Sdk.SymbolKinds.Class;
                    break;
                case StructDeclarationSyntax s:
                    name = s.Identifier.ValueText;
                    fqn = QualifyTypeName(s, name);
                    kind = Sdk.SymbolKinds.Struct;
                    break;
                case InterfaceDeclarationSyntax iface:
                    name = iface.Identifier.ValueText;
                    fqn = QualifyTypeName(iface, name);
                    kind = Sdk.SymbolKinds.Interface;
                    break;
                case RecordDeclarationSyntax r:
                    name = r.Identifier.ValueText;
                    fqn = QualifyTypeName(r, name);
                    kind = Sdk.SymbolKinds.Record;
                    break;
                case EnumDeclarationSyntax en:
                    name = en.Identifier.ValueText;
                    fqn = QualifyTypeName(en, name);
                    kind = Sdk.SymbolKinds.Enum;
                    break;
                case MethodDeclarationSyntax m:
                    name = m.Identifier.ValueText;
                    fqn = QualifyMemberName(m, name);
                    kind = Sdk.SymbolKinds.Method;
                    break;
                case PropertyDeclarationSyntax p:
                    name = p.Identifier.ValueText;
                    fqn = QualifyMemberName(p, name);
                    kind = Sdk.SymbolKinds.Property;
                    break;
            }
            if (name is null || fqn is null) continue;
            var span = node.GetLocation().GetLineSpan();
            // Syntax-tree-only path: no semantics, so we cannot derive a Roslyn DocumentationCommentId.
            // Fall back to a stable position+name shape under the reserved <c>csharp:</c> scheme so
            // the SDK's CanonicalKeyValidator accepts the emission. Include a forward-slash-
            // normalised file component (repo-relative when possible, absolute otherwise) so two
            // files declaring the same symbol name on the same line don't collide on the upsert
            // primary key. The "approx" sub-form signals these are best-effort keys; bulk-mode
            // workspaces always emit the doc-id form.
            var keyPath = NormalizePathForKey(ctx.FilePath, ctx.RepoRoot);
            events.Add(new Sdk.IndexEvent.SymbolDeclared(
                canonicalKey: $"{SymbolMapping.CanonicalKeyScheme}approx:{keyPath}#{span.StartLinePosition.Line}:{name}",
                name: name,
                fqn: fqn,
                kind: kind,
                startLine: span.StartLinePosition.Line + 1,
                startColumn: span.StartLinePosition.Character + 1,
                endLine: span.EndLinePosition.Line + 1,
                endColumn: span.EndLinePosition.Character + 1));
        }
        events.Add(new Sdk.IndexEvent.FileScanned(ctx.FilePath, sha, IsGenerated: false));
        return events;
    }

    private static string QualifyTypeName(SyntaxNode node, string name)
    {
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var nsName = ns?.Name.ToString();
        return string.IsNullOrEmpty(nsName) ? name : $"{nsName}.{name}";
    }

    private static string QualifyMemberName(SyntaxNode node, string name)
    {
        var typeDecl = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl is null) return name;
        var typeName = typeDecl.Identifier.ValueText;
        var nsName = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        return string.IsNullOrEmpty(nsName) ? $"{typeName}.{name}" : $"{nsName}.{typeName}.{name}";
    }

    /// <summary>
    /// Build the file component used in the <c>csharp:approx:&lt;path&gt;#&lt;line&gt;:&lt;name&gt;</c>
    /// fallback canonical key produced by the syntax-tree-only path. Repo-relative when
    /// <paramref name="filePath"/> sits under <paramref name="repoRoot"/> (so two clones of the
    /// same repo at different absolute locations produce identical keys); absolute otherwise.
    /// Always forward-slashed so the SDK's <c>CanonicalKeyValidator</c> (which forbids
    /// backslashes) accepts the result on Windows.
    /// </summary>
    private static string NormalizePathForKey(string filePath, string repoRoot)
    {
        var relative = !string.IsNullOrEmpty(repoRoot)
            ? Path.GetRelativePath(repoRoot, filePath)
            : filePath;
        // GetRelativePath returns the input verbatim when it can't be made relative; fall back
        // to the original absolute path in that case so we still emit something stable.
        var chosen = relative.StartsWith("..", StringComparison.Ordinal) ? filePath : relative;
        return chosen.Replace('\\', '/');
    }
}

public sealed record IndexResult(int FilesIndexed, int SymbolsIndexed, int ReferencesIndexed, TimeSpan Elapsed)
{
    /// <summary>
    /// Number of Roslyn error diagnostics observed in the projects touched by this pass.
    /// A non-zero value means symbol/reference output can still be useful, but semantic
    /// projections are not authoritative and the owning scope must not report itself as healthy.
    /// </summary>
    public int CompilationErrorCount { get; init; }

    /// <summary>
    /// True only when this result came from a successful all-document discovery, index, and
    /// stale generated-owner reconciliation. An incremental entry point may return true when it
    /// internally performed the required structural full reload.
    /// </summary>
    public bool ReconciledCompleteUniverse { get; init; }

    /// <summary>
    /// Projects whose Roslyn <c>Compilation</c> could not be obtained during the index pass.
    /// Empty for healthy installs. The pre-flight probe in <see cref="RoslynIndexer"/> populates
    /// this list and skips the project's documents in every subsequent pass.
    /// </summary>
    public IReadOnlyList<ProjectFailure> FailedProjects { get; init; } = Array.Empty<ProjectFailure>();

    /// <summary>
    /// Files whose discovery, content read, symbol walk, reference walk, or diagnostic discovery
    /// did not complete during the index pass. Empty for healthy installs. Entries are
    /// deduplicated by path; cancellation is never converted into a failure entry.
    /// </summary>
    public IReadOnlyList<FileFailure> FailedFiles { get; init; } = Array.Empty<FileFailure>();
}
