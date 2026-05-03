using System.Diagnostics;
using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.CodeAnalysis;
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
        // Attributes are gathered as PendingAttributes during this phase; their
        // attribute_symbol_id is resolved after the whole pass completes, so a use site can
        // link to a user-defined attribute class declared in another file we haven't walked
        // yet (e.g. [Legacy] on Greeter.cs resolving to LegacyAttribute.cs even though
        // Greeter.cs is processed first alphabetically).
        var newKeysForFile = new Dictionary<long, HashSet<string>>();
        var pendingAttrsByFile = new Dictionary<long, List<PendingAttribute>>();
        var seenSymbolForAttr = new Dictionary<long, HashSet<string>>();
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
                        ContainerId: null);

                    var id = await _store.UpsertSymbolAsync(key, coreSymbol, ct).ConfigureAwait(false);
                    var isNew = !_symbolIdByKey.ContainsKey(key);
                    _symbolIdByKey[key] = id;
                    if (isNew) symbolsIndexed++;

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

        // Resolve and bulk-insert attributes per file, now that every changed file has had
        // its symbols upserted into _symbolIdByKey. Doing this in a separate sweep lets a
        // use-site reference an attribute class that lives in a file we hadn't walked yet
        // when we extracted the use site.
        foreach (var (fileId, pendingAttrs) in pendingAttrsByFile)
        {
            if (pendingAttrs.Count == 0) continue;
            var resolved = pendingAttrs.Select(p => AttributeExtractor.Resolve(p, _symbolIdByKey)).ToList();
            await _store.BulkInsertAttributesAsync(resolved, ct).ConfigureAwait(false);
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

            foreach (var node in root.DescendantNodes())
            {
                ISymbol? referenced = null;
                ReferenceKind kind = ReferenceKind.Reference;

                switch (node)
                {
                    case IdentifierNameSyntax id when id.Parent is not (NamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax or VariableDeclaratorSyntax or ParameterSyntax or TypeParameterSyntax):
                        referenced = model.GetSymbolInfo(id, ct).Symbol;
                        if (id.Parent is InvocationExpressionSyntax inv && inv.Expression == id)
                        {
                            kind = ReferenceKind.Call;
                        }
                        break;

                    case GenericNameSyntax gn:
                        referenced = model.GetSymbolInfo(gn, ct).Symbol;
                        break;

                    case MemberAccessExpressionSyntax mae:
                        referenced = model.GetSymbolInfo(mae.Name, ct).Symbol;
                        if (mae.Parent is InvocationExpressionSyntax invMa && invMa.Expression == mae)
                        {
                            kind = ReferenceKind.Call;
                        }
                        break;

                    case ObjectCreationExpressionSyntax oce:
                        referenced = model.GetSymbolInfo(oce, ct).Symbol;
                        kind = ReferenceKind.Call;
                        break;
                }

                if (referenced is null) continue;
                var key = SymbolMapping.CanonicalKey(referenced);
                if (key is null) continue;
                if (!_symbolIdByKey.TryGetValue(key, out var symId)) continue;

                var pos = node.GetLocation().GetLineSpan().StartLinePosition;
                refBatch.Add(new SymbolReference(
                    Id: 0,
                    SymbolId: symId,
                    FileId: fileId,
                    Line: pos.Line + 1,
                    Col: pos.Character + 1,
                    Kind: kind));

                // Calls edge: source = enclosing named member, target = referenced
                if (kind == ReferenceKind.Call)
                {
                    var enclosing = FindEnclosingMember(model, node.SpanStart, ct);
                    if (enclosing is not null)
                    {
                        var encKey = SymbolMapping.CanonicalKey(enclosing);
                        if (encKey is not null && _symbolIdByKey.TryGetValue(encKey, out var srcId) && srcId != symId)
                        {
                            edgeBatch.Add(new Edge(srcId, symId, EdgeKind.Calls));
                        }
                    }
                }
            }

            // Inherits / Implements edges from BaseListSyntax on type declarations
            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (typeDecl.BaseList is null) continue;
                var typeSym = model.GetDeclaredSymbol(typeDecl, ct);
                if (typeSym is null) continue;
                var typeKey = SymbolMapping.CanonicalKey(typeSym);
                if (typeKey is null || !_symbolIdByKey.TryGetValue(typeKey, out var srcId)) continue;

                foreach (var baseTypeSyntax in typeDecl.BaseList.Types)
                {
                    var baseSym = model.GetSymbolInfo(baseTypeSyntax.Type, ct).Symbol;
                    if (baseSym is null) continue;
                    var baseKey = SymbolMapping.CanonicalKey(baseSym);
                    if (baseKey is null || !_symbolIdByKey.TryGetValue(baseKey, out var dstId)) continue;

                    var ek = baseSym is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Interface
                        ? EdgeKind.Implements
                        : EdgeKind.Inherits;
                    edgeBatch.Add(new Edge(srcId, dstId, ek));
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
