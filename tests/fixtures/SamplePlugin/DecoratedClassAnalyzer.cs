using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace SamplePlugin;

/// <summary>
/// Reference analyzer that demonstrates the <see cref="ICodeAnalyzer"/> seam. Reacts to symbols
/// the language indexer flagged with the <c>[Decorated]</c> attribute (any class carrying it is
/// a candidate) and emits a synthetic edge to a placeholder target. The SDK accepts any
/// kebab-case edge-kind name (plugins are free to invent their own — see
/// <see cref="EdgeKinds"/> for the well-known set), but this fixture re-uses
/// <see cref="EdgeKinds.UsesType"/> so the assertion stays anchored to a stable built-in
/// constant rather than a fixture-private name that could drift.
/// </summary>
public sealed class DecoratedClassAnalyzer : ICodeAnalyzer
{
    /// <inheritdoc />
    public string Name => "decorated-class";

    /// <summary>
    /// Walks the events the language indexer just produced. For every <c>SymbolDeclared</c>
    /// whose container has a sibling <c>AnnotationAttached</c> named <c>Decorated</c>, emit an
    /// <see cref="IndexEvent.EdgeEmitted"/> against the decorated class itself (self-loop) so the
    /// test fixture can detect it.
    /// </summary>
    public Task AnalyzeAsync(AnalyzerContext ctx, IGraphEmitter emitter, CancellationToken ct)
    {
        // Build a quick lookup of decorated keys from the events. The language indexer fires
        // SymbolDeclared first then AnnotationAttached, so we walk the full list once.
        var decoratedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in ctx.IndexerEvents)
        {
            if (ev is IndexEvent.AnnotationAttached a
                && string.Equals(a.AnnotationName, "Decorated", StringComparison.Ordinal))
            {
                decoratedKeys.Add(a.SymbolCanonicalKey);
            }
        }
        foreach (var key in decoratedKeys)
        {
            // Emit a self-edge on the uses-type kind. Edge dst = src so the test can find it
            // via storage queries without depending on a synthetic target symbol.
            emitter.EmitEdge(new IndexEvent.EdgeEmitted(key, key, EdgeKinds.UsesType));
        }
        return Task.CompletedTask;
    }
}
