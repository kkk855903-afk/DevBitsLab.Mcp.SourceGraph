using System.Diagnostics;
using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Long-lived indexer that owns an MSBuildWorkspace and a symbol-key map.
/// One-shot use: <see cref="IndexSolutionOnceAsync"/>. Live use: open then call
/// <see cref="IndexAllAsync"/> once, then <see cref="IndexChangedFilesAsync"/> as files change.
/// </summary>
public sealed class RoslynIndexer : IAsyncDisposable
{
    private readonly IGraphStore _store;
    private readonly ILogger<RoslynIndexer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private MSBuildWorkspace? _workspace;
    private string? _solutionPath;

    private readonly Dictionary<string, long> _symbolIdByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fileIdByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, List<string>> _keysByFileId = new();

    /// <summary>
    /// Fires once per (re)indexed file with <c>(fileId, fullPath, contentSha256)</c>. The Server
    /// project hooks this to enqueue work into the history pipeline; the indexer itself never
    /// touches git. <c>null</c> by default — leaving the callback unset means no history feed.
    /// </summary>
    public Func<long, string, byte[], Task>? OnFileIndexed { get; set; }

    public RoslynIndexer(IGraphStore store, ILogger<RoslynIndexer>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<RoslynIndexer>.Instance;
    }

    public string? SolutionPath => _solutionPath;

    public async Task OpenAsync(string solutionPath, CancellationToken ct = default)
    {
        MSBuildHost.EnsureRegistered();
        await _store.EnsureSchemaAsync(ct).ConfigureAwait(false);

        _solutionPath = Path.GetFullPath(solutionPath);
        _workspace = MSBuildWorkspace.Create();
        _workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                _logger.LogWarning("Workspace failure: {Message}", e.Diagnostic.Message);
            }
        });

        var sw = Stopwatch.StartNew();
        await _workspace.OpenSolutionAsync(_solutionPath, cancellationToken: ct).ConfigureAwait(false);
        _logger.LogInformation("Opened {Path} ({ProjectCount} projects) in {Elapsed}",
            _solutionPath, _workspace.CurrentSolution.Projects.Count(), sw.Elapsed);
    }

    public async Task<IndexResult> IndexAllAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var docs = AllCSharpDocuments().ToList();
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
            // Build an updated Solution snapshot in memory. We do NOT call
            // _workspace.TryApplyChanges — MSBuildWorkspace refuses ChangeDocument by default and
            // would throw. The local `solution` value is what IndexCoreAsync walks.
            var solution = _workspace!.CurrentSolution;
            var pathSet = new HashSet<string>(paths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
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

            // Resolve documents from the LOCAL updated solution (not _workspace.CurrentSolution).
            foreach (var p in pathSet)
            {
                if (deleted.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                var docIds = solution.GetDocumentIdsWithFilePath(p);
                foreach (var did in docIds)
                {
                    var d = solution.GetDocument(did);
                    if (d is not null && d.SourceCodeKind == SourceCodeKind.Regular) docs.Add(d);
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
            await _workspace.OpenSolutionAsync(slnPath, cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
        return await IndexAllAsync(ct).ConfigureAwait(false);
    }

    private void EnsureOpen()
    {
        if (_workspace is null) throw new InvalidOperationException("Call OpenAsync before indexing.");
    }

    private IEnumerable<Document> AllCSharpDocuments()
    {
        return _workspace!.CurrentSolution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .SelectMany(p => p.Documents)
            .Where(d => d.SourceCodeKind == SourceCodeKind.Regular && !string.IsNullOrEmpty(d.FilePath));
    }

    private async Task<IndexResult> IndexCoreAsync(IReadOnlyList<Document> documents, bool fullReset, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

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
        var symbolsIndexed = 0;

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            var path = document.FilePath;
            if (path is null || !File.Exists(path)) continue;

            byte[] bytes;
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
            var sha = SHA256.HashData(bytes);
            var stored = await _store.GetFileContentHashAsync(path, ct).ConfigureAwait(false);
            var unchanged = stored is not null && stored.AsSpan().SequenceEqual(sha);

            var fileId = await _store.UpsertFileAsync(path, sha, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            _fileIdByPath[path] = fileId;

            if (unchanged && !fullReset && _keysByFileId.ContainsKey(fileId))
            {
                // DB and in-memory map are already consistent for this file — skip entirely.
                continue;
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
        // Attributes are gathered as PendingAttributes during this phase; their
        // attribute_symbol_id is resolved after the whole pass completes, so a use site can
        // link to a user-defined attribute class declared in another file we haven't walked
        // yet (e.g. [Legacy] on Greeter.cs resolving to LegacyAttribute.cs even though
        // Greeter.cs is processed first alphabetically).
        var newKeysForFile = new Dictionary<long, HashSet<string>>();
        var parentKeyByChildKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingAttrsByFile = new Dictionary<long, List<PendingAttribute>>();
        var seenSymbolForAttr = new Dictionary<long, HashSet<string>>();
        // Test framework detection: keyed by symbol id, value = "xunit"/"nunit"/"mstest".
        // Populated as we walk method symbols in pass 1; flushed in a single batch update once
        // pass-1 completes. Doing it as a separate update lets us avoid clobbering the value
        // on the symbol's ON-CONFLICT path during re-upserts.
        var testFrameworkBySymbolId = new Dictionary<long, string>();
        foreach (var (fileId, docs) in docsByChangedFile)
        {
            var fileKeys = new HashSet<string>(StringComparer.Ordinal);
            newKeysForFile[fileId] = fileKeys;
            var pendingAttrs = new List<PendingAttribute>();
            pendingAttrsByFile[fileId] = pendingAttrs;
            var attrSeen = new HashSet<string>(StringComparer.Ordinal);
            seenSymbolForAttr[fileId] = attrSeen;

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

                    // Attributes: only collect once per (file, symbol) tuple even if the symbol
                    // was discovered in multiple TFM iterations. If the same attribute is
                    // visible across TFM iterations of the same source file we don't want to
                    // double-store it.
                    if (attrSeen.Add(key))
                    {
                        AttributeExtractor.AppendAttributes(symbol, id, pendingAttrs);
                    }
                }
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

        // PASS 1 — phase D: resolve and bulk-insert attributes per file, now that every changed
        // file has had its symbols upserted into _symbolIdByKey. Doing this in a separate sweep
        // lets a use-site reference an attribute class that lives in a file we hadn't walked yet
        // when we extracted the use site.
        foreach (var (fileId, pendingAttrs) in pendingAttrsByFile)
        {
            if (pendingAttrs.Count == 0) continue;
            var resolved = pendingAttrs.Select(p => AttributeExtractor.Resolve(p, _symbolIdByKey)).ToList();
            await _store.BulkInsertAttributesAsync(resolved, ct).ConfigureAwait(false);
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
        var refsIndexed = 0;
        var docsToIndexRefs = docsByChangedFile.Values.Select(list => list[0]).ToList();
        var filesIndexed = changedFileIds.Count;
        foreach (var document in docsToIndexRefs)
        {
            ct.ThrowIfCancellationRequested();
            var path = document.FilePath;
            if (path is null || !_fileIdByPath.TryGetValue(path, out var fileId)) continue;

            var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (tree is null || model is null) continue;

            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
            var refBatch = new List<SymbolReference>(capacity: 256);
            var edgeBatch = new List<Edge>(capacity: 64);
            // Dedupe edges within this file's pass-2 to avoid bombarding SQLite with duplicates that
            // INSERT OR IGNORE would just throw away. Tuple key keeps it allocation-light.
            var emittedEdges = new HashSet<(long src, long dst, EdgeKind kind)>();
            void AddEdge(long src, long dst, EdgeKind kind)
            {
                if (src == dst) return;
                if (emittedEdges.Add((src, dst, kind)))
                {
                    edgeBatch.Add(new Edge(src, dst, kind));
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
                        if (id.Parent is InvocationExpressionSyntax inv && inv.Expression == id)
                        {
                            kind = ReferenceKind.Call;
                        }
                        else
                        {
                            kind = ClassifyReadWrite(id, referenced) ?? ReferenceKind.Reference;
                        }
                        break;

                    case GenericNameSyntax gn:
                        referenced = model.GetSymbolInfo(gn, ct).Symbol;
                        refNode = gn;
                        break;

                    case MemberAccessExpressionSyntax mae:
                        referenced = model.GetSymbolInfo(mae.Name, ct).Symbol;
                        refNode = mae.Name;
                        if (mae.Parent is InvocationExpressionSyntax invMa && invMa.Expression == mae)
                        {
                            kind = ReferenceKind.Call;
                        }
                        else
                        {
                            kind = ClassifyReadWrite(mae, referenced) ?? ReferenceKind.Reference;
                        }
                        break;

                    case ObjectCreationExpressionSyntax oce:
                        referenced = model.GetSymbolInfo(oce, ct).Symbol;
                        refNode = oce;
                        kind = ReferenceKind.Call;
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
                            AddEdge(srcId, symId, EdgeKind.Calls);
                        }
                    }
                }

                // Instantiates edge: every `new T()` becomes an Instantiates(enclosing -> T) edge,
                // alongside the Calls edge to the constructor that the case above already emitted.
                // We also emit a UsesType edge so kind=uses_type can answer "every consumer of T",
                // including body-local instantiations (per design.md point 1).
                if (node is ObjectCreationExpressionSyntax oceNode && referenced is IMethodSymbol ctor)
                {
                    var typeSym = ctor.ContainingType;
                    if (typeSym is not null)
                    {
                        var typeKey = SymbolMapping.CanonicalKey(typeSym);
                        if (typeKey is not null && _symbolIdByKey.TryGetValue(typeKey, out var dstId))
                        {
                            var enclosing = FindEnclosingMember(model, oceNode.SpanStart, ct);
                            if (enclosing is not null)
                            {
                                var encKey = SymbolMapping.CanonicalKey(enclosing);
                                if (encKey is not null && _symbolIdByKey.TryGetValue(encKey, out var srcId))
                                {
                                    AddEdge(srcId, dstId, EdgeKind.Instantiates);
                                    AddEdge(srcId, dstId, EdgeKind.UsesType);
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
                AddEdge(srcId, dstId, EdgeKind.Throws);
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
                            ? EdgeKind.Implements
                            : EdgeKind.Inherits;
                        AddEdge(srcId, dstId, ek);
                        // Also a UsesType edge so kind=uses_type can answer "every consumer of B".
                        AddEdge(srcId, dstId, EdgeKind.UsesType);
                    }
                }

                // Member-level ImplementsMember: walk every interface this type implements and ask
                // Roslyn which member satisfies each interface member. Done once per type declaration.
                EmitMemberImplements(typeSym, AddEdge);
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

                EmitUsesTypeForSignature(declSym, memberId, AddEdge);
                EmitOverrides(declSym, memberId, AddEdge);

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

        return new IndexResult(filesIndexed, symbolsIndexed, refsIndexed, sw.Elapsed);
    }

    private async Task HydrateMapsFromStoreAsync(CancellationToken ct)
    {
        var symbolRows = await _store.GetAllSymbolKeysAsync(ct).ConfigureAwait(false);
        foreach (var row in symbolRows)
        {
            _symbolIdByKey[row.CanonicalKey] = row.Id;
            if (!_keysByFileId.TryGetValue(row.FileId, out var keys))
            {
                keys = new List<string>();
                _keysByFileId[row.FileId] = keys;
            }
            keys.Add(row.CanonicalKey);
        }
        var fileRows = await _store.GetAllFilesAsync(ct).ConfigureAwait(false);
        foreach (var fr in fileRows)
        {
            _fileIdByPath[fr.Path] = fr.Id;
        }
        if (symbolRows.Count > 0)
        {
            _logger.LogInformation("Hydrated {Symbols} symbol(s) and {Files} file(s) from graph store", symbolRows.Count, fileRows.Count);
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
    private void EmitUsesTypeForSignature(ISymbol member, long memberId, Action<long, long, EdgeKind> addEdge)
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
            addEdge(memberId, typeId, EdgeKind.UsesType);
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
    /// Emits a single <see cref="EdgeKind.Tests"/> edge from a test method (id =
    /// <paramref name="testMemberId"/>) to the first non-trivial production-code call inside
    /// the method's body. "Non-trivial" excludes calls into other test methods, calls into a
    /// type whose <c>[TestFixture]</c>/<c>[TestClass]</c> marker labels it as a test fixture,
    /// and calls into files under a <c>/tests/</c> path segment. We pick only the first such
    /// call to keep the edge focused and avoid noisy 1:N fanout per parametrised test.
    /// </summary>
    private void EmitTestsEdge(SyntaxNode methodNode, SemanticModel model, long testMemberId, Action<long, long, EdgeKind> addEdge, CancellationToken ct)
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
            addEdge(testMemberId, dstId, EdgeKind.Tests);
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
    private void EmitOverrides(ISymbol member, long memberId, Action<long, long, EdgeKind> addEdge)
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
        addEdge(memberId, dstId, EdgeKind.OverridesMember);
    }

    /// <summary>
    /// Walks <paramref name="typeSymbol"/>'s implemented interfaces and emits ImplementsMember
    /// edges from each implementing member to the satisfied interface member.
    /// </summary>
    private void EmitMemberImplements(INamedTypeSymbol typeSymbol, Action<long, long, EdgeKind> addEdge)
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
                addEdge(srcId, dstId, EdgeKind.ImplementsMember);
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
        CancellationToken ct = default)
    {
        await using var indexer = new RoslynIndexer(store, logger);
        await indexer.OpenAsync(solutionPath, ct).ConfigureAwait(false);
        return await indexer.IndexAllAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record IndexResult(int FilesIndexed, int SymbolsIndexed, int ReferencesIndexed, TimeSpan Elapsed);
