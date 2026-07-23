using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
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
    /// <inheritdoc />
    IReadOnlyCollection<string> ILanguageIndexer.FileExtensions => _fileExtensions;

    private readonly IGraphStore _store;
    private readonly IEmbeddingsRequestSink _embeddingsSink;
    private readonly ILogger<RoslynIndexer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string? _configuredPrivacyRoot;
    private readonly IReadOnlyList<string> _configuredExcludePatterns;

    private MSBuildWorkspace? _workspace;
    private Solution? _sanitizedSolution;
    private ScopePathPolicy? _pathPolicy;
    private string? _solutionPath;

    private readonly Dictionary<string, long> _symbolIdByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fileIdByPath = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Fires once per (re)indexed file with <c>(fileId, fullPath, contentSha256)</c>. The Server
    /// project hooks this to enqueue work into the history pipeline; the indexer itself never
    /// touches git. <c>null</c> by default — leaving the callback unset means no history feed.
    /// </summary>
    public Func<long, string, byte[], Task>? OnFileIndexed { get; set; }

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
        IReadOnlyList<string>? excludePatterns)
    {
        _store = store;
        _logger = logger ?? NullLogger<RoslynIndexer>.Instance;
        _embeddingsSink = embeddingsSink ?? new NoOpEmbeddingsRequestSink();
        _configuredPrivacyRoot = privacyRoot is null ? null : Path.GetFullPath(privacyRoot);
        _configuredExcludePatterns = excludePatterns?.ToArray() ?? Array.Empty<string>();
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

    public async Task OpenAsync(string solutionPath, CancellationToken ct = default)
    {
        MSBuildHost.EnsureRegistered();
        await _store.EnsureSchemaAsync(ct).ConfigureAwait(false);

        _solutionPath = Path.GetFullPath(solutionPath);
        var privacyRoot = _configuredPrivacyRoot ?? Path.GetDirectoryName(_solutionPath)
            ?? throw new InvalidOperationException("The solution path has no containing directory.");
        _pathPolicy = new ScopePathPolicy(privacyRoot, _configuredExcludePatterns);
        if (_pathPolicy.IsExcluded(_solutionPath))
        {
            throw new InvalidOperationException("The solution path is outside the indexing privacy boundary.");
        }

        _workspace = MSBuildWorkspace.Create();
        _workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                _logger.LogWarning("Workspace failure: {Message}", e.Diagnostic.Message);
            }
        });

        var sw = Stopwatch.StartNew();
        // MSBuildWorkspace evaluates project files before Roslyn gives us a Solution to filter.
        // Sanitizing immediately after OpenSolutionAsync prevents excluded documents from reaching
        // compilation, generators, diagnostics, or persistence, but it does not sandbox MSBuild
        // evaluation itself. Untrusted solutions still need process/OS isolation.
        var loadedSolution = await _workspace.OpenSolutionAsync(
            _solutionPath,
            cancellationToken: ct).ConfigureAwait(false);
        _sanitizedSolution = SolutionPrivacySanitizer.SanitizeForScope(loadedSolution, _pathPolicy);
        _logger.LogInformation("Opened {Path} ({ProjectCount} projects) in {Elapsed}",
            _solutionPath, _sanitizedSolution.Projects.Count(), sw.Elapsed);
    }

    public async Task<IndexResult> IndexAllAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var solution = _sanitizedSolution!;
            await ProbeProjectCompilationsAsync(solution.Projects, ct).ConfigureAwait(false);
            var docs = (await AllCSharpDocumentsAsync(solution, ct).ConfigureAwait(false)).ToList();
            return await IndexCoreAsync(docs, fullReset: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IndexResult> IndexChangedFilesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        EnsureOpen();
        if (paths.Count == 0) return new IndexResult(0, 0, 0, TimeSpan.Zero);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pathPolicy = _pathPolicy!;
            // Build an updated Solution snapshot in memory. We do NOT call
            // _workspace.TryApplyChanges — MSBuildWorkspace refuses ChangeDocument by default and
            // would throw. The local `solution` value is what IndexCoreAsync walks.
            var solution = _sanitizedSolution!;
            var pathSet = new HashSet<string>(
                paths.Select(Path.GetFullPath).Where(path => !pathPolicy.IsExcluded(path)),
                StringComparer.OrdinalIgnoreCase);
            if (pathSet.Count == 0)
            {
                return new IndexResult(0, 0, 0, TimeSpan.Zero);
            }
            var docs = new List<Document>();
            var deleted = new List<string>();

            foreach (var p in pathSet)
            {
                var docIds = solution.GetDocumentIdsWithFilePath(p);
                if (docIds.IsEmpty)
                {
                    // not part of solution; ignore
                    continue;
                }
                if (!File.Exists(p))
                {
                    deleted.Add(p);
                    continue;
                }
                string content;
                try
                {
                    // Editor saves can briefly expose a 0-byte view of the file; treat read errors
                    // as "skip this file for this batch". The watcher will fire again once the save
                    // settles.
                    content = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Skipping {Path} for this batch (read failed; will retry)", p);
                    continue;
                }
                var text = SourceText.From(content);
                foreach (var did in docIds)
                {
                    solution = solution.WithDocumentText(did, text);
                }
            }

            _sanitizedSolution = solution;

            // Resolve documents from the LOCAL updated solution (not _workspace.CurrentSolution).
            var touchedProjects = new HashSet<ProjectId>();
            foreach (var p in pathSet)
            {
                if (deleted.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                var docIds = solution.GetDocumentIdsWithFilePath(p);
                foreach (var did in docIds)
                {
                    var d = solution.GetDocument(did);
                    if (d is not null && d.SourceCodeKind == SourceCodeKind.Regular)
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
            foreach (var pid in touchedProjects)
            {
                if (_probedFailedProjectIds.Contains(pid)) continue;
                var project = solution.GetProject(pid);
                if (project is null || project.Language != LanguageNames.CSharp) continue;
                IEnumerable<SourceGeneratedDocument> sourceGenDocs;
                try
                {
                    sourceGenDocs = await project.GetSourceGeneratedDocumentsAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Source generator failure for project {Project} during incremental reindex", project.Name);
                    continue;
                }
                foreach (var d in sourceGenDocs)
                {
                    if (string.IsNullOrEmpty(d.FilePath)) continue;
                    docs.Add(d);
                }
            }

            // handle deletions: clear store and drop from maps
            foreach (var p in deleted)
            {
                if (_fileIdByPath.TryGetValue(p, out var fid))
                {
                    await _store.ClearFileOutgoingAsync(fid, ct).ConfigureAwait(false);
                    await _store.DeleteSymbolsForFileNotInAsync(fid, Array.Empty<string>(), ct).ConfigureAwait(false);
                    DropFileFromMaps(fid);
                    _fileIdByPath.Remove(p);
                }
            }

            return await IndexCoreAsync(docs, fullReset: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IndexResult> ReloadAndIndexAllAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        var slnPath = _solutionPath!;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _workspace!.CloseSolution();
            var loadedSolution = await _workspace.OpenSolutionAsync(
                slnPath,
                cancellationToken: ct).ConfigureAwait(false);
            _sanitizedSolution = SolutionPrivacySanitizer.SanitizeForScope(loadedSolution, _pathPolicy!);
        }
        finally
        {
            _lock.Release();
        }
        return await IndexAllAsync(ct).ConfigureAwait(false);
    }

    private void EnsureOpen()
    {
        if (_workspace is null || _sanitizedSolution is null || _pathPolicy is null)
        {
            throw new InvalidOperationException("Call OpenAsync before indexing.");
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
    private async Task ProbeProjectCompilationsAsync(IEnumerable<Project> projects, CancellationToken ct)
    {
        var ok = new Dictionary<ProjectId, Compilation>();
        var failed = new List<ProjectFailure>();
        var failedIds = new HashSet<ProjectId>();
        foreach (var project in projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            ct.ThrowIfCancellationRequested();
            try
            {
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
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Project {Project} compilation threw; skipping", project.Name);
                failed.Add(new ProjectFailure(project.Name, FailureMessage.Truncate(ex.Message)));
                failedIds.Add(project.Id);
            }
        }
        _probedCompilations = ok;
        _probedFailures = failed;
        _probedFailedProjectIds = failedIds;
    }

    private async Task<IEnumerable<Document>> AllCSharpDocumentsAsync(
        Solution solution,
        CancellationToken ct)
    {
        // Regular documents: same set we always walked, minus documents in projects whose
        // pre-flight compilation probe failed.
        var regular = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && !_probedFailedProjectIds.Contains(p.Id))
            .SelectMany(p => p.Documents)
            .Where(d => d.SourceCodeKind == SourceCodeKind.Regular && !string.IsNullOrEmpty(d.FilePath));

        // Source-generated documents: per-project Project.GetSourceGeneratedDocumentsAsync drives
        // the source generators that ship with the project (regex source-gen, MVVM Toolkit, ASP.NET
        // routing, etc.) and surfaces synthesised C# files. They have a stable Document.FilePath
        // string from Roslyn that we can use as the unique key. Marking those rows is_generated = 1
        // is what makes the find_references default-filter and the (generated) annotations work.
        // Failed-probe projects are skipped here too — their generators would almost certainly
        // throw given the underlying compilation is unavailable.
        var generatedList = new List<Document>();
        foreach (var project in solution.Projects
                     .Where(p => p.Language == LanguageNames.CSharp && !_probedFailedProjectIds.Contains(p.Id)))
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<SourceGeneratedDocument> sourceGenDocs;
            try
            {
                sourceGenDocs = await project.GetSourceGeneratedDocumentsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A misbehaving generator can throw during synthesis. Log and skip this project's
                // generated set rather than failing the whole index.
                _logger.LogWarning(ex, "Source generator failure for project {Project}; skipping generated docs", project.Name);
                continue;
            }
            foreach (var d in sourceGenDocs)
            {
                if (string.IsNullOrEmpty(d.FilePath)) continue;
                generatedList.Add(d);
            }
        }
        return regular.Concat(generatedList);
    }

    /// <summary>
    /// Best-effort post-failure clear of <paramref name="fileId"/>'s outgoing refs/edges.
    /// Called from <see cref="IndexCoreAsync"/>'s pass-2 catch handler when a file's walk
    /// threw partway through: a partial commit (refs done, edges throws) would leave the
    /// integrity check satisfied (refs > 0) on the next index and strand the missing edges,
    /// so we drop both halves here. A failure of the clear itself is logged but not rethrown
    /// — letting it propagate would defeat the surrounding catch's "keep walking the rest"
    /// purpose. The original walk failure is logged separately by the caller.
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Best-effort cleanup: a thrown exception here would propagate out of the catch handler in IndexCoreAsync and abort the entire pass-2 loop, defeating the surrounding catch's intent. The clear's failure is logged at warn so operators see secondary failures.")]
    private async Task TryClearFileOutgoingAsync(long fileId, string path, CancellationToken ct)
    {
        try
        {
            await _store.ClearFileOutgoingAsync(fileId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception clearEx)
        {
            // The clear itself failed. Best-effort: log and move on. The file may hold
            // partial state until the next clear+re-walk; an operator who notices repeated
            // recoveries can wipe the .sourcegraph DB to recover fully.
            _logger.LogWarning(clearEx,
                "Pass 2's post-failure clear for {Path} also failed; file may have stale partial refs/edges",
                path);
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "IndexCoreAsync's per-file walks must not let one document's failure (a misbehaving source generator, a transient compile gap, an analyzer throwing inside Roslyn) bring the whole indexing pass down. Each catch logs the file path + exception and continues with the next file; OperationCanceledException is rethrown explicitly so user-driven cancellation surfaces.")]
    private async Task<IndexResult> IndexCoreAsync(IReadOnlyList<Document> documents, bool fullReset, CancellationToken ct)
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
        // Pass 1D annotations, Pass 2 reference walk, and Pass 3 diagnostic reconcile so a
        // partial Pass-1B walk does not corrupt the file's prior store state. Files absent from
        // `walkedFileIds` keep their prior symbols/refs/edges/diagnostics until the next pass
        // re-attempts them. `failedFiles` accumulates FileFailure records for IndexResult.
        var walkedFileIds = new HashSet<long>();
        var failedFiles = new List<FileFailure>();

        // Hydrate in-memory maps from store on first run (or after a fullReset). This means
        // unchanged files don't need any DB hits — we already know their symbol ids.
        if (fullReset)
        {
            _symbolIdByKey.Clear();
            _keysByFileId.Clear();
            _fileIdByPath.Clear();
        }
        if (_symbolIdByKey.Count == 0)
        {
            await HydrateMapsFromStoreAsync(ct).ConfigureAwait(false);
        }

        // PASS 1 — phase A: SHA scan. Identify which files changed; clear their outgoing
        // refs/edges (will be rebuilt in pass 2). Group docs per fileId so we walk every TFM /
        // linked-project iteration of the same path before reconciling.
        var changedFileIds = new HashSet<long>();
        var docsByChangedFile = new Dictionary<long, List<Document>>();
        var changedFileMeta = new Dictionary<long, (string Path, byte[] Sha)>();
        // Memoize HasOutgoingReferencesAsync per fileId for the lifetime of this pass: in
        // multi-target / linked-project solutions the same fileId enumerates once per TFM,
        // and the integrity check answer is invariant within a single pass. Caching also
        // collapses the recovery log line to one entry per zombied file rather than N.
        var hasOutgoingRefsCache = new Dictionary<long, bool>();
        var symbolsIndexed = 0;

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            var path = document.FilePath;
            if (path is null) continue;

            // Source-generated documents don't exist on disk; their bytes come from the document's
            // SourceText. Same SHA gate either way — same hash on identical generator output means
            // we skip the file entirely (per design.md "SHA gate on generated content").
            var isGenerated = document is SourceGeneratedDocument;
            byte[] bytes;
            if (isGenerated)
            {
                try
                {
                    var text = await document.GetTextAsync(ct).ConfigureAwait(false);
                    bytes = System.Text.Encoding.UTF8.GetBytes(text.ToString());
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping generated doc {Path} (text fetch failed)", path);
                    continue;
                }
            }
            else
            {
                if (!File.Exists(path)) continue;
                try
                {
                    bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // Editor save in progress, antivirus, etc. Skip; the watcher will fire again.
                    _logger.LogDebug(ex, "Skipping {Path} (read failed; will retry on next event)", path);
                    continue;
                }
            }
            var sha = SHA256.HashData(bytes);
            var stored = await _store.GetFileContentHashAsync(path, ct).ConfigureAwait(false);
            var unchanged = stored is not null && stored.AsSpan().SequenceEqual(sha);

            var fileId = await _store.UpsertFileAsync(path, sha, DateTimeOffset.UtcNow, isGenerated, ct).ConfigureAwait(false);
            _fileIdByPath[path] = fileId;

            if (unchanged && !fullReset && _keysByFileId.TryGetValue(fileId, out var keysForFile))
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
                    continue;
                }
                bool hasOutgoingRefs;
                if (hasOutgoingRefsCache.TryGetValue(fileId, out var cached))
                {
                    hasOutgoingRefs = cached;
                }
                else
                {
                    hasOutgoingRefs = await _store.HasOutgoingReferencesAsync(fileId, ct).ConfigureAwait(false);
                    hasOutgoingRefsCache[fileId] = hasOutgoingRefs;
                    // Log once per zombied fileId on first detection — subsequent TFM
                    // iterations of the same path hit the cache branch and stay quiet.
                    if (!hasOutgoingRefs)
                    {
                        _logger.LogInformation(
                            "Re-walking references for {Path}: file SHA matches but no outgoing references in store " +
                            "(likely zombied by a prior incomplete indexing pass; recovering)",
                            path);
                    }
                }
                if (hasOutgoingRefs)
                {
                    continue;
                }
                // Fall through to the changed-file path so pass 2 walks this file.
            }

            if (changedFileIds.Add(fileId))
            {
                await _store.ClearFileOutgoingAsync(fileId, ct).ConfigureAwait(false);
                changedFileMeta[fileId] = (path, sha);
            }
            if (!docsByChangedFile.TryGetValue(fileId, out var list))
            {
                list = new List<Document>();
                docsByChangedFile[fileId] = list;
            }
            list.Add(document);
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
            // entry for this fileId, leaving the file's prior reconciled state intact.
            var fileKeys = new HashSet<string>(StringComparer.Ordinal);
            var pendingAttrs = new List<PendingAnnotation>();
            var attrSeen = new HashSet<string>(StringComparer.Ordinal);
            var path = changedFileMeta.TryGetValue(fileId, out var meta) ? meta.Path : "<unknown>";

            try
            {
                foreach (var document in docs)
                {
                    var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
                    var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    if (tree is null || model is null) continue;

                    var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                    foreach (var node in EnumerateDeclarations(root))
                    {
                        var symbol = model.GetDeclaredSymbol(node, ct);
                        if (symbol is null || !SymbolMapping.IsIndexable(symbol)) continue;

                        var key = SymbolMapping.CanonicalKey(symbol);
                        if (key is null) continue;
                        if (!fileKeys.Add(key)) continue;

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
                            AttributeExtractor.AppendAnnotations(symbol, id, pendingAttrs);

                            // Enqueue an embedding request once per (file, symbol). The sink is
                            // a no-op when --no-embeddings was passed or the model isn't available;
                            // the indexer never blocks on it.
                            if (_embeddingsSink.IsEnabled)
                            {
                                EnqueueEmbedRequest(id, document.FilePath, symbol, coreSymbol);
                            }
                        }
                    }
                }

                // Success: publish per-file state to the shared dictionaries that Pass 1C/1D
                // iterate. Pass 2 will also gate on walkedFileIds so failed files are skipped.
                newKeysForFile[fileId] = fileKeys;
                pendingAttrsByFile[fileId] = pendingAttrs;
                seenSymbolForAttr[fileId] = attrSeen;
                walkedFileIds.Add(fileId);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One file's Pass-1B walk threw — log it and let the next file proceed. The
                // file's prior store state (symbols, refs, edges, annotations, diagnostics) is
                // preserved because we never wrote to newKeysForFile/pendingAttrsByFile, so
                // Pass 1C's DeleteSymbolsForFileNotInAsync skips it. Pass 2 and Pass 3 also
                // gate on walkedFileIds. The next index will re-attempt this file.
                _logger.LogWarning(ex,
                    "Pass 1 walk failed for {Path}; preserving prior symbol state, file will be re-attempted on the next index",
                    path);
                failedFiles.Add(new FileFailure(path, FailureMessage.Truncate(ex.Message)));
            }
        }

        // Reconcile per changed fileId: delete any symbol attributed to this file in DB whose
        // canonical key is no longer declared anywhere in the file's tree (across all iterations).
        // The reconcile call also wipes the file's attribute rows; we re-insert the freshly
        // walked attribute set immediately after, in the same logical step.
        foreach (var (fileId, fileKeys) in newKeysForFile)
        {
            // Remove orphaned keys from in-memory map (best-effort; full correctness comes from
            // the next process restart's hydrate).
            if (_keysByFileId.TryGetValue(fileId, out var prevKeys))
            {
                foreach (var k in prevKeys)
                {
                    if (!fileKeys.Contains(k)) _symbolIdByKey.Remove(k);
                }
            }
            await _store.DeleteSymbolsForFileNotInAsync(fileId, fileKeys, ct).ConfigureAwait(false);
            _keysByFileId[fileId] = fileKeys.ToList();
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
                if (_symbolIdByKey.TryGetValue(childKey, out var childId)
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

        // PASS 1 — phase D: resolve and bulk-insert annotations per file, now that every changed
        // file has had its symbols upserted into _symbolIdByKey. Doing this in a separate sweep
        // lets a use-site reference an attribute class that lives in a file we hadn't walked yet
        // when we extracted the use site.
        foreach (var (fileId, pendingAttrs) in pendingAttrsByFile)
        {
            if (pendingAttrs.Count == 0) continue;
            var resolved = pendingAttrs.Select(p => AttributeExtractor.Resolve(p, _symbolIdByKey)).ToList();
            await _store.BulkInsertAnnotationsAsync(resolved, ct).ConfigureAwait(false);
        }

        // PASS 1 — phase E: flush detected test_framework values for the methods we walked.
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
            .Select(kv => kv.Value[0])
            .ToList();
        var filesIndexed = walkedFileIds.Count;
        foreach (var document in docsToIndexRefs)
        {
            ct.ThrowIfCancellationRequested();
            var path = document.FilePath;
            if (path is null || !_fileIdByPath.TryGetValue(path, out var fileId)) continue;

            try
            {
                var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (tree is null || model is null) continue;

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
                            referenced = model.GetSymbolInfo(id, ct).Symbol;
                            refNode = id;
                            kind = id.Parent is InvocationExpressionSyntax inv && inv.Expression == id
                                ? ReferenceKind.Call
                                : ClassifyReadWrite(id, referenced) ?? ReferenceKind.Reference;
                            break;

                        case GenericNameSyntax gn:
                            referenced = model.GetSymbolInfo(gn, ct).Symbol;
                            refNode = gn;
                            kind = gn.Parent is InvocationExpressionSyntax invGn
                                && invGn.Expression == gn
                                    ? ReferenceKind.Call
                                    : ReferenceKind.Reference;
                            break;

                        case MemberAccessExpressionSyntax mae:
                            referenced = model.GetSymbolInfo(mae.Name, ct).Symbol;
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
                            referenced = model.GetSymbolInfo(mbe.Name, ct).Symbol;
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
                    refsIndexed += refBatch.Count;
                }
                if (edgeBatch.Count > 0)
                {
                    await _store.BulkInsertEdgesAsync(edgeBatch, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // User-driven cancellation must propagate so the caller learns the run aborted.
                throw;
            }
            catch (Exception ex)
            {
                // One file's walk threw — log it and let the next file's walk proceed. Pass 2
                // calls BulkInsertReferencesAsync first then BulkInsertEdgesAsync; each is
                // atomic but they aren't sequenced under one outer transaction, so a throw on
                // edges would leave refs committed. That state is invisible to the next
                // index's integrity check (refs > 0 → SHA-skip → edges stranded forever), so
                // re-clear here to drop any partial commit and force a full re-walk next time.
                await TryClearFileOutgoingAsync(fileId, path, ct).ConfigureAwait(false);

                _logger.LogWarning(ex,
                    "Pass 2 walk failed for {Path}; cleared partial refs/edges, will re-attempt on the next index",
                    path);
            }
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
            catch (Exception ex)
            {
                var project = _sanitizedSolution!.GetProject(pid);
                _logger.LogWarning(ex, "Failed to read diagnostics for {Project}", project?.Name ?? pid.ToString());
                continue;
            }

            foreach (var diag in diags)
            {
                ct.ThrowIfCancellationRequested();
                var loc = diag.Location;
                if (loc.SourceSpan.IsEmpty) continue;
                var tree = loc.SourceTree;
                if (tree is null) continue;
                var path = tree.FilePath;
                if (string.IsNullOrEmpty(path)) continue;
                if (!_fileIdByPath.TryGetValue(path, out var fileId)) continue;
                if (!changedFileIds.Contains(fileId)) continue; // only touch files we re-indexed
                if (!walkedFileIds.Contains(fileId)) continue;  // skip files whose Pass 1B threw

                long? symbolId = null;
                try
                {
                    var model = compilation.GetSemanticModel(tree);
                    var enclosing = model.GetEnclosingSymbol(loc.SourceSpan.Start, ct);
                    while (enclosing is not null && !SymbolMapping.IsIndexable(enclosing))
                    {
                        enclosing = enclosing.ContainingSymbol;
                    }
                    if (enclosing is not null)
                    {
                        var key = SymbolMapping.CanonicalKey(enclosing);
                        if (key is not null && _symbolIdByKey.TryGetValue(key, out var sid))
                        {
                            symbolId = sid;
                        }
                    }
                }
                catch
                {
                    // Best-effort symbol attribution. If lookup fails, leave the diagnostic
                    // file-scoped (symbolId = NULL) and persist anyway.
                }

                var lineSpan = loc.GetLineSpan();
                var rec = new DiagnosticRecord(
                    SymbolId: symbolId,
                    FileId: fileId,
                    Severity: (int)diag.Severity,
                    Code: diag.Id,
                    Message: diag.GetMessage(),
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Col: lineSpan.StartLinePosition.Character + 1);
                if (!diagnosticsByFile.TryGetValue(fileId, out var bucket))
                {
                    bucket = new List<DiagnosticRecord>();
                    diagnosticsByFile[fileId] = bucket;
                }
                bucket.Add(rec);
            }
        }

        // Reconcile diagnostics: even files with zero diagnostics get an Upsert call so that
        // freshly-fixed warnings disappear from the table on the next index.
        foreach (var (fid, bucket) in diagnosticsByFile)
        {
            await _store.UpsertDiagnosticsForFileAsync(fid, bucket, ct).ConfigureAwait(false);
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
                try
                {
                    await OnFileIndexed(fileId, meta.Path, meta.Sha).ConfigureAwait(false);
                }
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
            }
        }
    }

    private void DropFileFromMaps(long fileId)
    {
        if (_keysByFileId.TryGetValue(fileId, out var keys))
        {
            foreach (var k in keys) _symbolIdByKey.Remove(k);
            _keysByFileId.Remove(fileId);
        }
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
                case EventFieldDeclarationSyntax:
                case EnumMemberDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                    yield return node;
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

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
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
    /// Projects whose Roslyn <c>Compilation</c> could not be obtained during the index pass.
    /// Empty for healthy installs. The pre-flight probe in <see cref="RoslynIndexer"/> populates
    /// this list and skips the project's documents in every subsequent pass.
    /// </summary>
    public IReadOnlyList<ProjectFailure> FailedProjects { get; init; } = Array.Empty<ProjectFailure>();

    /// <summary>
    /// Files whose Pass 1 symbol walk threw during the index pass. Empty for healthy installs.
    /// The pass 1B per-document try/catch in <see cref="RoslynIndexer"/> populates this list;
    /// failed files retain their prior store state until the next successful walk.
    /// </summary>
    public IReadOnlyList<FileFailure> FailedFiles { get; init; } = Array.Empty<FileFailure>();
}
