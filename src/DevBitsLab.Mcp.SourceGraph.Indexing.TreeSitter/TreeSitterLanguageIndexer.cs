using System.Security.Cryptography;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;
using TsNode = TreeSitter.Node;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter;

/// <summary>
/// Generic parse + walk + emit boilerplate that every tree-sitter-backed language indexer
/// reuses. Per-language plugins (TypeScript, future Python / Go / Rust) subclass this and
/// supply their grammar config plus the language-specific identity construction
/// (<see cref="OnDeclarationNode"/>, <see cref="OnReferenceNode"/>). The base owns the
/// iteration, the file-size cap, the SHA-256 of source bytes, the cancellation discipline,
/// and a single <see cref="IndexEvent.FileScanned"/> emission for every file the parser
/// actually processes (files skipped by the size cap return an empty event list — no
/// <see cref="IndexEvent.FileScanned"/>, no symbols).
///
/// <para>"Malformed source" surfaces in two distinguishable shapes from
/// <c>TreeSitter.DotNet</c>: a <c>null</c> tree (binary file masquerading as source) and a
/// non-null tree whose root reports <c>HasError == true</c> (syntactically invalid input).
/// The base treats both as malformed and skips the walk — partial recovery from an
/// ERROR-tainted parse tree tends to emit half-formed symbols whose canonical keys collide
/// with the next clean re-index, which is worse than emitting nothing. Either way the file's
/// <see cref="IndexEvent.FileScanned"/> still fires so the watcher records the SHA and
/// doesn't re-scan unchanged content next pass.</para>
///
/// <para>The variation that <em>must</em> live per-language is canonical-key construction
/// (each language picks its own scheme prefix + lexical-path shape) and reference target
/// resolution (which depends on language-specific symbol tables). The base intentionally
/// stops at "what kind is this node?" — beyond that, it hands off to the subclass.</para>
/// </summary>
public abstract class TreeSitterLanguageIndexer<TGrammarConfig> : ILanguageIndexer
    where TGrammarConfig : ITreeSitterGrammarConfig
{
    protected TreeSitterLanguageIndexer(TGrammarConfig config, ILogger? logger = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The per-language config object; subclasses access it for grammar identity, options, and any plugin-private state hung off their <typeparamref name="TGrammarConfig"/> implementation.</summary>
    protected TGrammarConfig Config { get; }

    /// <summary>Shared logger; subclasses log fall-back paths or skipped events at debug level so operators have a trail without paying for the verbosity by default.</summary>
    protected ILogger Logger { get; }

    /// <summary>Translation between tree-sitter node-type strings and the SDK's kebab-case vocabularies.</summary>
    protected abstract INodeKindMapper Mapper { get; }

    /// <summary>Optional cross-file resolver. <c>null</c> ⇒ intra-file refs only; cross-file resolution is left to a later pass (e.g. LSP enrichment).</summary>
    protected virtual IModuleResolver? Resolver => null;

    /// <inheritdoc />
    public virtual IReadOnlyCollection<string> FileExtensions => Config.FileExtensions;

    /// <summary>
    /// Hook for indexers that span multiple file extensions backed by different tree-sitter
    /// grammars (e.g. <c>.ts</c> uses <c>TypeScript</c>, <c>.tsx</c> uses <c>TSX</c>). Default
    /// implementation returns <see cref="ITreeSitterGrammarConfig.GrammarName"/> from
    /// <see cref="Config"/>; subclasses override when per-file dispatch is needed.
    /// </summary>
    protected virtual string GetGrammarName(IndexContext ctx) => Config.GrammarName;

    /// <inheritdoc />
    public Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        // File-size cap: keeps a vendored 50 MB minified bundle from wedging the indexer.
        if (ctx.Contents.Length > Config.Options.MaxFileSizeBytes)
        {
            Logger.LogDebug(
                "Skipping {Path} ({Length} bytes > MaxFileSizeBytes {Cap})",
                ctx.FilePath, ctx.Contents.Length, Config.Options.MaxFileSizeBytes);
            return Task.FromResult<IReadOnlyList<IndexEvent>>(Array.Empty<IndexEvent>());
        }

        var sha = SHA256.HashData(ctx.Contents);
        var source = Encoding.UTF8.GetString(ctx.Contents);
        var events = new List<IndexEvent>();

        // Per the design's malformed-source posture (mirroring XamlLanguageIndexer): a null
        // tree or a tree whose root reports HasError skips the walk and emits no symbol/ref/
        // edge events. FileScanned still fires below regardless — that sentinel is what the
        // watcher uses to record the SHA, so suppressing it would force re-scans of unchanged
        // malformed content on every pass. Subclass / SDK-validator exceptions propagate up so
        // the plugin host can mark the plugin failed for this file.
        var language = TreeSitterAdapter.GetLanguage(GetGrammarName(ctx));
        using var parser = new Parser(language);
        using var tree = parser.Parse(source);

        if (tree is null)
        {
            Logger.LogDebug("Parser returned null tree for {Path}; treating as empty.", ctx.FilePath);
        }
        else if (tree.RootNode.HasError)
        {
            // Match XAML's posture for malformed input: a syntactically-invalid tree could be
            // walked through the mapper, but the partial recovery would emit half-formed
            // symbols whose canonical keys collide with the next clean re-index. Skip the walk
            // and let the next file change try again. FileScanned still fires below so the
            // watcher's SHA cache stays accurate.
            Logger.LogDebug("Parse tree for {Path} contains ERROR nodes; treating as malformed and emitting empty events.", ctx.FilePath);
        }
        else
        {
            Walk(tree.RootNode, ctx, events, ct);
        }

        events.Add(new IndexEvent.FileScanned(ctx.FilePath, sha));
        return Task.FromResult<IReadOnlyList<IndexEvent>>(events);
    }

    /// <summary>
    /// Subclass hook: the base reached a node whose type maps to a declaration. Construct
    /// the language-specific <see cref="IndexEvent.SymbolDeclared"/> (canonical key, name,
    /// FQN, signature, container) and return it, or <c>null</c> to skip emission for this
    /// node. Position 1-basing is the subclass's responsibility — use
    /// <see cref="TreeSitterAdapter.ToOneBased"/>.
    /// </summary>
    protected abstract IndexEvent.SymbolDeclared? OnDeclarationNode(
        TsNode node, NodeMapping mapping, IndexContext ctx);

    /// <summary>
    /// Subclass hook: the base reached a node whose type maps to a reference. Construct the
    /// language-specific <see cref="IndexEvent.ReferenceFound"/> by resolving the target
    /// canonical key (using <see cref="Resolver"/> if provided), or return <c>null</c> if
    /// the target cannot be resolved.
    /// </summary>
    protected abstract IndexEvent.ReferenceFound? OnReferenceNode(
        TsNode node, string referenceKind, IndexContext ctx);

    /// <summary>
    /// Subclass hook: the base reached a node whose type maps to an edge. Construct one or more
    /// language-specific <see cref="IndexEvent.EdgeEmitted"/> entries (a JSX element may produce
    /// both an instantiates edge and a reference, so the return is a list rather than a single
    /// event) — or <c>null</c> to skip emission. Default implementation returns <c>null</c>;
    /// subclasses override when their <see cref="INodeKindMapper.TryMapEdge"/> returns true for
    /// any node type.
    /// </summary>
    protected virtual IReadOnlyList<IndexEvent>? OnEdgeNode(
        TsNode node, string edgeKindName, IndexContext ctx) => null;

    /// <summary>
    /// Walk a tree-sitter node, dispatching declarations / references / edges through the
    /// subclass hooks. Marked protected virtual so subclasses needing fancy traversal (e.g.
    /// skipping a declaration's name child to avoid double-emission) can override the recursion
    /// without re-implementing the dispatch.
    /// </summary>
    protected virtual void Walk(TsNode node, IndexContext ctx, List<IndexEvent> events, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var type = node.Type;
        if (Mapper.TryMapDeclaration(type, out var declMapping))
        {
            var ev = OnDeclarationNode(node, declMapping, ctx);
            if (ev is not null) events.Add(ev);
        }
        else if (Mapper.TryMapReference(type, out var refKind))
        {
            var ev = OnReferenceNode(node, refKind, ctx);
            if (ev is not null) events.Add(ev);
        }
        else if (Mapper.TryMapEdge(type, out var edgeKind))
        {
            var evs = OnEdgeNode(node, edgeKind, ctx);
            if (evs is not null) events.AddRange(evs);
        }

        foreach (var child in node.NamedChildren)
        {
            Walk(child, ctx, events, ct);
        }
    }
}
