using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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
/// <see cref="ILanguageIndexer.IndexAsync"/>, and persists the resulting events through a
/// <see cref="GraphStoreEmitter"/>.
/// </summary>
public sealed class LanguageIndexerDispatcher
{
    /// <summary>Extensions exempt from dispatch — handled by the workspace-aware C# bulk path.</summary>
    private static readonly HashSet<string> _csharpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs" };

    private readonly LanguageIndexerRegistry _indexers;
    private readonly LanguageProjectFactoryRegistry _factories;
    private readonly ILogger<LanguageIndexerDispatcher> _logger;

    public LanguageIndexerDispatcher(
        LanguageIndexerRegistry indexers,
        LanguageProjectFactoryRegistry factories,
        ILogger<LanguageIndexerDispatcher>? logger = null)
    {
        _indexers = indexers;
        _factories = factories;
        _logger = logger ?? NullLogger<LanguageIndexerDispatcher>.Instance;
    }

    /// <summary>
    /// The registry the dispatcher reads to look up indexers. Exposed so callers (e.g.
    /// per-scope dispatcher subclassing in <c>LiveIndexService</c>) can build a sibling
    /// dispatcher that shares the indexer set but has a different factory pool.
    /// </summary>
    internal LanguageIndexerRegistry Indexers => _indexers;

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
    /// Build the per-scope file → project map by running every registered factory's
    /// <c>DiscoverAsync</c>. Mutates <paramref name="host"/>'s <see cref="ScopeHost.ProjectByFilePath"/>
    /// in place; safe to call multiple times (the map is cleared first so a re-run reflects
    /// current factory state).
    /// </summary>
    public async Task BuildProjectMapAsync(ScopeHost host, CancellationToken ct = default)
    {
        host.ProjectByFilePath.Clear();
        foreach (var factory in _factories.All())
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ILanguageProject> projects;
            try
            {
                projects = await factory.DiscoverAsync(host.Scope.Root, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Factory `{Type}` failed DiscoverAsync for scope `{Scope}`",
                    factory.GetType().FullName, host.Scope.Id);
                continue;
            }
            foreach (var project in projects)
            {
                foreach (var path in project.FilePaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    // First-write-wins: the project that claims a file first owns it. Cross-factory
                    // overlap is rare today (MSBuild doesn't claim .xaml; XAML doesn't claim .cs)
                    // but the rule keeps behaviour predictable when a future factory pair overlaps.
                    if (!host.ProjectByFilePath.ContainsKey(path))
                    {
                        host.ProjectByFilePath[path] = project;
                    }
                }
            }
        }
    }

    /// <summary>
    /// ScopeHost-free overload: dispatch every non-C# file under <paramref name="repoRoot"/>
    /// directly against an <see cref="IGraphStore"/> + an externally-built project map. Used by
    /// the XAML indexer's smoke tests AND by the one-shot <c>index</c> CLI path — both want
    /// dispatcher behaviour but neither owns a <see cref="ScopeHost"/> (the test path doesn't
    /// need one; the CLI path keeps its own <c>await using</c> ownership of the store + indexer
    /// + embeddings to avoid double-dispose with ScopeHost.DisposeAsync).
    /// </summary>
    public async Task<int> DispatchAllForTestAsync(
        IGraphStore store,
        string scopeId,
        string repoRoot,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        CancellationToken ct = default)
    {
        if (!HasNonCSharpIndexers) return 0;
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot)) return 0;

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _indexers.All())
        {
            if (_csharpExtensions.Contains(pair.Key)) continue;
            extensions.Add(pair.Key);
        }
        if (extensions.Count == 0) return 0;

        var symbolIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        await PopulateSymbolKeyMapAsync(store, symbolIdByKey, ct).ConfigureAwait(false);

        var dispatched = 0;
        foreach (var file in EnumerateFiles(repoRoot, extensions))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            var indexerHit = _indexers.TryGet(ext);
            if (indexerHit is null) continue;
            try
            {
                await DispatchOneCoreAsync(store, scopeId, repoRoot, projectMap, file, indexerHit.Value.Indexer, symbolIdByKey, ct).ConfigureAwait(false);
                dispatched++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Indexer threw on {File}", file);
            }
        }
        return dispatched;
    }

    /// <summary>
    /// Walk the scope's repo root for every file whose extension has a registered non-C# indexer
    /// and dispatch each through that indexer. Events are persisted to <see cref="ScopeHost.Store"/>.
    /// Errors are isolated per file: a throwing indexer logs and skips the file but the rest of
    /// the pass continues.
    /// </summary>
    public async Task<int> DispatchAllAsync(ScopeHost host, CancellationToken ct = default)
    {
        if (!HasNonCSharpIndexers) return 0;
        if (string.IsNullOrEmpty(host.Scope.Root) || !Directory.Exists(host.Scope.Root)) return 0;

        // Snapshot the registered extensions so the file walk doesn't lock the registry on every
        // traversal step.
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _indexers.All())
        {
            if (_csharpExtensions.Contains(pair.Key)) continue;
            extensions.Add(pair.Key);
        }
        if (extensions.Count == 0) return 0;

        // Cache canonical-key → symbol-id across files in this pass so cross-file edges (e.g. a
        // XAML view binding to a viewmodel symbol the C# indexer wrote) resolve correctly.
        var symbolIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        await PopulateSymbolKeyMapAsync(host.Store, symbolIdByKey, ct).ConfigureAwait(false);

        var dispatched = 0;
        foreach (var file in EnumerateFiles(host.Scope.Root, extensions))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            var indexerHit = _indexers.TryGet(ext);
            if (indexerHit is null) continue;

            try
            {
                await DispatchOneAsync(host, file, indexerHit.Value.Indexer, indexerHit.Value.Owner, symbolIdByKey, ct).ConfigureAwait(false);
                dispatched++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Indexer `{Indexer}` threw on {File}; skipping",
                    indexerHit.Value.Indexer.GetType().FullName, file);
                if (indexerHit.Value.Owner is { } record)
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage = $"IndexAsync threw on `{file}`: {ex.Message}";
                }
            }
        }
        return dispatched;
    }

    /// <summary>
    /// Dispatch a single file. Reads the file, computes the SHA, upserts the file row, builds the
    /// IndexContext (with the per-scope project lookup), runs the indexer, then flushes the
    /// events through a <see cref="GraphStoreEmitter"/>. The emitter handles the symbol /
    /// edge / annotation / reference upsert pattern in one round-trip per kind.
    /// </summary>
    private async Task DispatchOneAsync(
        ScopeHost host,
        string filePath,
        ILanguageIndexer indexer,
        PluginRecord? owner,
        Dictionary<string, long> symbolIdByKey,
        CancellationToken ct) =>
        await DispatchOneCoreAsync(host.Store, host.Scope.Id, host.Scope.Root, host.ProjectByFilePath, filePath, indexer, symbolIdByKey, ct).ConfigureAwait(false);

    private async Task DispatchOneCoreAsync(
        IGraphStore store,
        string scopeId,
        string repoRoot,
        IReadOnlyDictionary<string, ILanguageProject> projectMap,
        string filePath,
        ILanguageIndexer indexer,
        Dictionary<string, long> symbolIdByKey,
        CancellationToken ct)
    {
        byte[] contents;
        try
        {
            contents = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Skipping {File}: read failed", filePath);
            return;
        }

        var sha = SHA256.HashData(contents);
        var fileId = await store.UpsertFileAsync(filePath, sha, DateTimeOffset.UtcNow, isGenerated: false, ct).ConfigureAwait(false);

        // Re-indexing replaces previously emitted edges from this file; symbols themselves are
        // upserted by canonical key so their integer ids stay stable across re-indexes.
        await store.ClearFileOutgoingAsync(fileId, ct).ConfigureAwait(false);

        projectMap.TryGetValue(filePath, out var project);
        var ctx = new IndexContext(filePath, contents, scopeId, repoRoot, project);

        var events = await indexer.IndexAsync(ctx, ct).ConfigureAwait(false);
        if (events.Count == 0) return;

        // Walk events twice: once to collect every canonical key the indexer declared (so the
        // stale-symbol sweep below knows which rows to keep), and once to feed the emitter.
        // The two-pass shape mirrors what the C# bulk path does and is mandatory because the
        // sweep MUST run BEFORE the flush — `DeleteSymbolsForFileNotInAsync` wipes every
        // annotation row whose symbol_id resolves to this file_id (so `[Foo]` removed from a
        // surviving method actually disappears). Calling it after flush would delete the
        // annotations we just inserted; calling it before delivers the documented invariant
        // (this-file's-annotations are always reconstructed from the current pass).
        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in events)
        {
            if (ev is IndexEvent.SymbolDeclared sd) emittedKeys.Add(sd.CanonicalKey);
        }
        await store.DeleteSymbolsForFileNotInAsync(fileId, emittedKeys, ct).ConfigureAwait(false);

        var emitter = new GraphStoreEmitter(store, fileId, symbolIdByKey, _logger);
        foreach (var ev in events)
        {
            switch (ev)
            {
                case IndexEvent.SymbolDeclared sd: emitter.EmitSymbol(sd); break;
                case IndexEvent.EdgeEmitted ee: emitter.EmitEdge(ee); break;
                case IndexEvent.AnnotationAttached aa: emitter.EmitAnnotation(aa); break;
                case IndexEvent.ReferenceFound rf: emitter.EmitReference(rf); break;
                case IndexEvent.FileScanned _: break; // sentinel; nothing to persist
            }
        }
        await emitter.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task PopulateSymbolKeyMapAsync(
        IGraphStore store,
        Dictionary<string, long> map,
        CancellationToken ct)
    {
        var rows = await store.GetAllSymbolKeysAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            map[row.CanonicalKey] = row.Id;
        }
    }

    /// <summary>
    /// Walk <paramref name="root"/> for files matching any of <paramref name="extensions"/>. Skips
    /// <c>bin/</c>, <c>obj/</c>, <c>node_modules/</c>, and <c>.git/</c> wholesale to keep the walk
    /// proportional to source-tree size, not build-output size.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root, HashSet<string> extensions)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ".sourcegraph", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(child);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (extensions.Contains(ext)) yield return f;
            }
        }
    }
}
