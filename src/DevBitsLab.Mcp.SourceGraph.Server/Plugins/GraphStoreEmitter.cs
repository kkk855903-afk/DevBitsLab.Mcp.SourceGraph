using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreSymbol = DevBitsLab.Mcp.SourceGraph.Core.Symbol;
using SdkPluginSymbolKind = DevBitsLab.Mcp.SourceGraph.Sdk.PluginSymbolKind;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Concrete <see cref="IGraphEmitter"/> that batches plugin emissions and flushes them to an
/// <see cref="IGraphStore"/> with a single round-trip per kind on <see cref="FlushAsync"/>. The
/// emitter is per-file (one instance per analyzer invocation): the host instantiates it with the
/// file's database id, then hands it to the analyzer; on return, <see cref="FlushAsync"/> persists
/// every queued emission.
/// </summary>
public sealed class GraphStoreEmitter : IGraphEmitter
{
    private readonly IGraphStore _store;
    private readonly long _fileId;
    private readonly Dictionary<string, long> _symbolIdByCanonicalKey;
    private readonly ILogger _logger;

    private readonly List<IndexEvent.SymbolDeclared> _symbols = new();
    private readonly List<IndexEvent.EdgeEmitted> _edges = new();
    private readonly List<IndexEvent.AttributeAttached> _attributes = new();
    private readonly List<IndexEvent.ReferenceFound> _references = new();

    public GraphStoreEmitter(
        IGraphStore store,
        long fileId,
        Dictionary<string, long> symbolIdByCanonicalKey,
        ILogger? logger = null)
    {
        _store = store;
        _fileId = fileId;
        _symbolIdByCanonicalKey = symbolIdByCanonicalKey;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public void EmitSymbol(IndexEvent.SymbolDeclared symbol) => _symbols.Add(symbol);
    /// <inheritdoc />
    public void EmitEdge(IndexEvent.EdgeEmitted edge) => _edges.Add(edge);
    /// <inheritdoc />
    public void EmitAttribute(IndexEvent.AttributeAttached attribute) => _attributes.Add(attribute);
    /// <inheritdoc />
    public void EmitReference(IndexEvent.ReferenceFound reference) => _references.Add(reference);

    /// <summary>
    /// Resolve every queued emission into storage rows and write them. Symbols are upserted
    /// first so subsequent edges/attributes/refs can resolve their canonical-key references to
    /// stable ids. The same key map (<see cref="_symbolIdByCanonicalKey"/>) is shared with the
    /// language indexer's pass-1 so plugin-emitted symbols can target previously-walked symbols
    /// (and vice versa).
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Symbols first.
        foreach (var s in _symbols)
        {
            var coreKind = MapSymbolKind(s.Kind);
            var sym = new CoreSymbol(
                Id: 0,
                Name: s.Name,
                Fqn: s.Fqn,
                Kind: coreKind,
                FileId: _fileId,
                StartLine: s.StartLine,
                StartCol: s.StartColumn,
                EndLine: s.EndLine,
                EndCol: s.EndColumn,
                Signature: s.Signature,
                ContainerId: null,
                Modifiers: s.Modifiers,
                Accessibility: s.Accessibility,
                XmlSummary: s.XmlSummary);
            var id = await _store.UpsertSymbolAsync(s.CanonicalKey, sym, ct).ConfigureAwait(false);
            _symbolIdByCanonicalKey[s.CanonicalKey] = id;
        }

        // Edges. We resolve canonical keys defensively; an unmapped key produces a debug log and
        // the edge is skipped so analyzers that emit speculatively don't poison the graph.
        if (_edges.Count > 0)
        {
            var resolvedEdges = new List<Edge>(_edges.Count);
            foreach (var e in _edges)
            {
                if (!_symbolIdByCanonicalKey.TryGetValue(e.SourceCanonicalKey, out var src))
                {
                    _logger.LogDebug("Edge skipped: unknown source canonical key `{Key}`", e.SourceCanonicalKey);
                    continue;
                }
                if (!_symbolIdByCanonicalKey.TryGetValue(e.TargetCanonicalKey, out var dst))
                {
                    _logger.LogDebug("Edge skipped: unknown target canonical key `{Key}`", e.TargetCanonicalKey);
                    continue;
                }
                if (!Enum.TryParse<EdgeKind>(e.EdgeKindName, ignoreCase: true, out var kind))
                {
                    _logger.LogDebug("Edge skipped: unknown edge kind `{Kind}`", e.EdgeKindName);
                    continue;
                }
                resolvedEdges.Add(new Edge(src, dst, kind));
            }
            if (resolvedEdges.Count > 0)
            {
                await _store.BulkInsertEdgesAsync(resolvedEdges, ct).ConfigureAwait(false);
            }
        }

        // Attributes.
        if (_attributes.Count > 0)
        {
            var resolvedAttrs = new List<AttributeRecord>(_attributes.Count);
            foreach (var a in _attributes)
            {
                if (!_symbolIdByCanonicalKey.TryGetValue(a.SymbolCanonicalKey, out var symId))
                {
                    _logger.LogDebug("Attribute skipped: unknown symbol canonical key `{Key}`", a.SymbolCanonicalKey);
                    continue;
                }
                long? attrSymbolId = null;
                if (a.AttributeClassCanonicalKey is { } akey
                    && _symbolIdByCanonicalKey.TryGetValue(akey, out var aid))
                {
                    attrSymbolId = aid;
                }
                resolvedAttrs.Add(new AttributeRecord(symId, a.AttributeName, a.AttributeFullName, a.ArgsJson, attrSymbolId));
            }
            if (resolvedAttrs.Count > 0)
            {
                await _store.BulkInsertAttributesAsync(resolvedAttrs, ct).ConfigureAwait(false);
            }
        }

        // References.
        if (_references.Count > 0)
        {
            var resolvedRefs = new List<SymbolReference>(_references.Count);
            foreach (var r in _references)
            {
                if (!_symbolIdByCanonicalKey.TryGetValue(r.TargetCanonicalKey, out var sym))
                {
                    _logger.LogDebug("Reference skipped: unknown target canonical key `{Key}`", r.TargetCanonicalKey);
                    continue;
                }
                if (!Enum.TryParse<ReferenceKind>(r.Kind, ignoreCase: true, out var rk))
                {
                    rk = ReferenceKind.Reference;
                }
                resolvedRefs.Add(new SymbolReference(0, sym, _fileId, r.Line, r.Column, rk));
            }
            if (resolvedRefs.Count > 0)
            {
                await _store.BulkInsertReferencesAsync(resolvedRefs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Translate the SDK's wire-friendly symbol kind to the storage-level enum. Unknown values
    /// degrade to <see cref="SymbolKind.Unknown"/> so a future SDK adding a new kind doesn't
    /// break older hosts.
    /// </summary>
    private static SymbolKind MapSymbolKind(SdkPluginSymbolKind kind) => kind switch
    {
        SdkPluginSymbolKind.Namespace => SymbolKind.Namespace,
        SdkPluginSymbolKind.Class => SymbolKind.Class,
        SdkPluginSymbolKind.Interface => SymbolKind.Interface,
        SdkPluginSymbolKind.Struct => SymbolKind.Struct,
        SdkPluginSymbolKind.Enum => SymbolKind.Enum,
        SdkPluginSymbolKind.Delegate => SymbolKind.Delegate,
        SdkPluginSymbolKind.Method => SymbolKind.Method,
        SdkPluginSymbolKind.Constructor => SymbolKind.Constructor,
        SdkPluginSymbolKind.Property => SymbolKind.Property,
        SdkPluginSymbolKind.Field => SymbolKind.Field,
        SdkPluginSymbolKind.Event => SymbolKind.Event,
        SdkPluginSymbolKind.EnumMember => SymbolKind.EnumMember,
        SdkPluginSymbolKind.Operator => SymbolKind.Method,
        SdkPluginSymbolKind.Record => SymbolKind.Class, // Roslyn records resolve to Class in our enum
        _ => SymbolKind.Unknown,
    };
}
