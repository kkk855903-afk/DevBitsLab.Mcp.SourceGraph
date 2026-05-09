using System.Threading;
using System.Threading.Tasks;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// A second-pass analyzer that observes documents already indexed by an
/// <see cref="ILanguageIndexer"/> and emits additional rows (synthetic symbols, custom edges,
/// extra attributes, additional refs) via the supplied <see cref="IGraphEmitter"/>.
///
/// <para>
/// Analyzers run AFTER the language indexer for a document has finished, so they can rely on the
/// canonical-key mapping being populated. They run inside a 30-second per-document timeout (host
/// default); a throwing analyzer is marked <c>failed</c> for that pass but other analyzers keep
/// running.
/// </para>
/// </summary>
public interface ICodeAnalyzer
{
    /// <summary>
    /// Stable analyzer name. Used in <c>plugins info</c> output and in failure messages.
    /// SHOULD be globally unique within the plugin (the plugin id + this name combine to identify
    /// the analyzer in error messages).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Inspect the document described by <paramref name="ctx"/> and emit additional facts via
    /// <paramref name="emitter"/>. Throwing inside this method marks the analyzer failed for this
    /// pass; the host logs and continues with the remaining analyzers.
    /// </summary>
    Task AnalyzeAsync(AnalyzerContext ctx, IGraphEmitter emitter, CancellationToken ct);
}

/// <summary>
/// Per-document context handed to an <see cref="ICodeAnalyzer"/>. Includes the events the
/// language indexer emitted so analyzers can chain off them without re-parsing.
/// </summary>
public sealed class AnalyzerContext
{
    public AnalyzerContext(
        string filePath,
        byte[] contents,
        string scopeId,
        string repoRoot,
        System.Collections.Generic.IReadOnlyList<IndexEvent> indexerEvents)
    {
        FilePath = filePath;
        Contents = contents;
        ScopeId = scopeId;
        RepoRoot = repoRoot;
        IndexerEvents = indexerEvents;
    }

    public string FilePath { get; }
    public byte[] Contents { get; }
    public string ScopeId { get; }
    public string RepoRoot { get; }

    /// <summary>
    /// Events the language indexer emitted for this document. Useful for analyzers that want to
    /// react to specific declarations or attributes without re-walking the syntax tree.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<IndexEvent> IndexerEvents { get; }

    /// <summary>UTF-8 text view of <see cref="Contents"/>.</summary>
    public string GetText() => System.Text.Encoding.UTF8.GetString(Contents);
}

/// <summary>
/// Sink given to analyzers and tool plugins that need to add facts to the graph. Mirrors the
/// <see cref="IndexEvent"/> shape, but with a method-call API so the call site reads as imperative
/// "I emit X" rather than "I return X". The host validates and persists each emission.
/// </summary>
public interface IGraphEmitter
{
    /// <summary>Emit a synthetic symbol the language indexer didn't produce.</summary>
    void EmitSymbol(IndexEvent.SymbolDeclared symbol);

    /// <summary>Emit an edge between two symbols.</summary>
    void EmitEdge(IndexEvent.EdgeEmitted edge);

    /// <summary>Attach an annotation record to an existing symbol.</summary>
    void EmitAnnotation(IndexEvent.AnnotationAttached annotation);

    /// <summary>Record a position-level reference.</summary>
    void EmitReference(IndexEvent.ReferenceFound reference);
}
