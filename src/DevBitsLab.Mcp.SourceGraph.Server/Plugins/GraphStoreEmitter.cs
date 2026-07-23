using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreEvidence = DevBitsLab.Mcp.SourceGraph.Core.Evidence;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation = DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;
using CoreSymbol = DevBitsLab.Mcp.SourceGraph.Core.Symbol;
using SdkEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Sdk.EvidenceConfidence;

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
    private readonly List<IndexEvent.AnnotationAttached> _annotations = new();
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
    public void EmitAnnotation(IndexEvent.AnnotationAttached annotation) => _annotations.Add(annotation);
    /// <inheritdoc />
    public void EmitReference(IndexEvent.ReferenceFound reference) => _references.Add(reference);

    /// <summary>
    /// Compile and validate one indexer's complete event list into storage-native, canonical-key
    /// facts. Unknown speculative targets retain the emitter's existing skip semantics. The
    /// returned batch has no database ids and is safe to hand to
    /// <see cref="IGraphStore.ReplaceFileFactsAsync"/> for one-transaction resolution.
    /// </summary>
    internal static FileFactsReplacement CompileFileFacts(
        string filePath,
        byte[] contentSha256,
        IReadOnlyList<IndexEvent> events,
        IReadOnlySet<string> knownCanonicalKeys,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(contentSha256);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(knownCanonicalKeys);
        var log = logger ?? NullLogger.Instance;

        var symbols = new List<FileSymbolFact>();
        var edges = new List<FileEdgeFact>();
        var annotations = new List<FileAnnotationFact>();
        var references = new List<FileReferenceFact>();
        foreach (var ev in events)
        {
            switch (ev)
            {
                case IndexEvent.SymbolDeclared symbol:
                    var containerKey = symbol.ContainerCanonicalKey;
                    if (containerKey is not null && !knownCanonicalKeys.Contains(containerKey))
                    {
                        log.LogDebug(
                            "Container skipped: unknown parent canonical key `{Key}`",
                            containerKey);
                        containerKey = null;
                    }
                    symbols.Add(new FileSymbolFact(
                        symbol.CanonicalKey,
                        symbol.Name,
                        symbol.Fqn,
                        symbol.Kind,
                        symbol.StartLine,
                        symbol.StartColumn,
                        symbol.EndLine,
                        symbol.EndColumn,
                        symbol.Signature,
                        containerKey,
                        symbol.Modifiers,
                        symbol.Accessibility,
                        symbol.XmlSummary));
                    break;

                case IndexEvent.EdgeEmitted edge:
                    if (!knownCanonicalKeys.Contains(edge.SourceCanonicalKey))
                    {
                        log.LogDebug(
                            "Edge skipped: unknown source canonical key `{Key}`",
                            edge.SourceCanonicalKey);
                        break;
                    }
                    if (!knownCanonicalKeys.Contains(edge.TargetCanonicalKey))
                    {
                        log.LogDebug(
                            "Edge skipped: unknown target canonical key `{Key}`",
                            edge.TargetCanonicalKey);
                        break;
                    }
                    edges.Add(new FileEdgeFact(
                        edge.SourceCanonicalKey,
                        edge.TargetCanonicalKey,
                        edge.EdgeKindName,
                        edge.Metadata,
                        CompileEvidence(edge.Evidence)));
                    break;

                case IndexEvent.AnnotationAttached annotation:
                    if (!knownCanonicalKeys.Contains(annotation.SymbolCanonicalKey))
                    {
                        log.LogDebug(
                            "Annotation skipped: unknown symbol canonical key `{Key}`",
                            annotation.SymbolCanonicalKey);
                        break;
                    }
                    var attributeKey = annotation.TargetCanonicalKey;
                    if (attributeKey is not null && !knownCanonicalKeys.Contains(attributeKey))
                    {
                        attributeKey = null;
                    }
                    annotations.Add(new FileAnnotationFact(
                        annotation.SymbolCanonicalKey,
                        annotation.AnnotationName,
                        annotation.FullName ?? annotation.AnnotationName,
                        annotation.Flavor,
                        annotation.ArgsJson,
                        attributeKey));
                    break;

                case IndexEvent.ReferenceFound reference:
                    if (!knownCanonicalKeys.Contains(reference.TargetCanonicalKey))
                    {
                        log.LogDebug(
                            "Reference skipped: unknown target canonical key `{Key}`",
                            reference.TargetCanonicalKey);
                        break;
                    }
                    if (!Enum.TryParse<ReferenceKind>(
                            reference.Kind,
                            ignoreCase: true,
                            out var referenceKind))
                    {
                        referenceKind = ReferenceKind.Reference;
                    }
                    references.Add(new FileReferenceFact(
                        reference.TargetCanonicalKey,
                        reference.Line,
                        reference.Column,
                        referenceKind));
                    break;

                case IndexEvent.FileScanned:
                    break;
            }
        }

        return new FileFactsReplacement(
            filePath,
            contentSha256,
            DateTimeOffset.UtcNow,
            IsGenerated: false,
            symbols,
            edges,
            annotations,
            references);
    }

    /// <summary>
    /// Resolve every queued emission into storage rows and write them. Symbols are upserted
    /// first so subsequent edges/annotations/refs can resolve their canonical-key references to
    /// stable ids. The same key map (<see cref="_symbolIdByCanonicalKey"/>) is shared with the
    /// language indexer's pass-1 so plugin-emitted symbols can target previously-walked symbols
    /// (and vice versa).
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Symbols first.
        foreach (var s in _symbols)
        {
            var sym = new CoreSymbol(
                Id: 0,
                Name: s.Name,
                Fqn: s.Fqn,
                Kind: s.Kind,
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

        // Container keys can point forward or backward within the current emission batch. Resolve
        // them only after every declaration has an id, then use the store's transactional
        // reconciliation API. An unknown key is deliberately left unresolved — lexical-name
        // guessing would turn an uncertain relationship into a false graph fact.
        if (_symbols.Count > 0)
        {
            var containers = new List<(long ChildId, long ParentId)>();
            foreach (var s in _symbols)
            {
                if (s.ContainerCanonicalKey is null)
                {
                    continue;
                }
                if (!_symbolIdByCanonicalKey.TryGetValue(s.CanonicalKey, out var childId))
                {
                    _logger.LogDebug(
                        "Container skipped: unknown child canonical key `{Key}`",
                        s.CanonicalKey);
                    continue;
                }
                if (!_symbolIdByCanonicalKey.TryGetValue(
                        s.ContainerCanonicalKey,
                        out var parentId))
                {
                    _logger.LogDebug(
                        "Container skipped: unknown parent canonical key `{Key}`",
                        s.ContainerCanonicalKey);
                    continue;
                }
                containers.Add((childId, parentId));
            }
            if (containers.Count > 0)
            {
                await _store.BatchUpdateContainerIdsAsync(containers, ct).ConfigureAwait(false);
            }
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
                // Edge kind is now an open kebab-case string: pass through unchanged. The SDK has
                // already validated kebab-case-ness at construction time. Storage accepts the
                // string as-is (TEXT column, indexed).
                resolvedEdges.Add(new Edge(
                    src,
                    dst,
                    e.EdgeKindName,
                    e.Metadata,
                    MapEvidence(e.Evidence)));
            }
            if (resolvedEdges.Count > 0)
            {
                await _store.BulkInsertEdgesAsync(resolvedEdges, ct).ConfigureAwait(false);
            }
        }

        // Annotations.
        if (_annotations.Count > 0)
        {
            var resolvedAnnotations = new List<AnnotationRecord>(_annotations.Count);
            foreach (var a in _annotations)
            {
                if (!_symbolIdByCanonicalKey.TryGetValue(a.SymbolCanonicalKey, out var symId))
                {
                    _logger.LogDebug("Annotation skipped: unknown symbol canonical key `{Key}`", a.SymbolCanonicalKey);
                    continue;
                }
                long? attrSymbolId = null;
                if (a.TargetCanonicalKey is { } akey
                    && _symbolIdByCanonicalKey.TryGetValue(akey, out var aid))
                {
                    attrSymbolId = aid;
                }
                // FullName is non-null in storage's record but optional in the SDK event; if the
                // emitter didn't supply one, fall back to the short name so legacy queries that
                // join against a non-empty FullName still work.
                var fullName = a.FullName ?? a.AnnotationName;
                resolvedAnnotations.Add(new AnnotationRecord(symId, a.AnnotationName, fullName, a.Flavor, a.ArgsJson, attrSymbolId));
            }
            if (resolvedAnnotations.Count > 0)
            {
                await _store.BulkInsertAnnotationsAsync(resolvedAnnotations, ct).ConfigureAwait(false);
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

    private CoreEvidence? MapEvidence(EdgeEvidence? evidence)
    {
        var compiled = CompileEvidence(evidence);
        return compiled is null
            ? null
            : new CoreEvidence(
                _fileId,
                compiled.Location,
                compiled.Confidence,
                compiled.Producer,
                compiled.Metadata);
    }

    private static FileEvidenceFact? CompileEvidence(EdgeEvidence? evidence)
    {
        if (evidence is null) return null;
        var location = evidence.Location
            ?? throw new ArgumentException("Edge evidence location is required.", nameof(evidence));
        if (string.IsNullOrWhiteSpace(location.FilePath)
            || !Path.IsPathFullyQualified(location.FilePath))
        {
            throw new ArgumentException(
                "Edge evidence file path must be an absolute path.",
                nameof(evidence));
        }
        if (location.StartLine <= 0
            || location.StartColumn <= 0
            || location.EndLine < location.StartLine
            || location.EndColumn <= 0
            || (location.EndLine == location.StartLine
                && location.EndColumn < location.StartColumn))
        {
            throw new ArgumentException(
                "Edge evidence must use a valid 1-based source range.",
                nameof(evidence));
        }
        if (string.IsNullOrWhiteSpace(evidence.Producer))
        {
            throw new ArgumentException("Edge evidence producer is required.", nameof(evidence));
        }

        var confidence = evidence.Confidence switch
        {
            SdkEvidenceConfidence.Inferred => CoreEvidenceConfidence.Inferred,
            SdkEvidenceConfidence.Semantic => CoreEvidenceConfidence.Semantic,
            SdkEvidenceConfidence.Exact => CoreEvidenceConfidence.Exact,
            _ => throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.Confidence,
                "Edge evidence confidence is not defined."),
        };

        return new FileEvidenceFact(
            new CoreSourceLocation(
                location.FilePath,
                location.StartLine,
                location.StartColumn,
                location.EndLine,
                location.EndColumn),
            confidence,
            evidence.Producer,
            evidence.Metadata);
    }
}
