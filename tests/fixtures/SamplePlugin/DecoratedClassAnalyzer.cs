using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace SamplePlugin;

/// <summary>
/// Reference analyzer that demonstrates the <see cref="ICodeAnalyzer"/> seam. Reacts to symbols
/// the language indexer flagged with the <c>[Decorated]</c> attribute (any class carrying it is
/// a candidate) and emits a synthetic edge to a placeholder target. We re-use the existing
/// <c>UsesType</c> edge kind because the SDK doesn't yet expose user-defined edge kinds in v1;
/// the test asserts that an edge of that kind appears whose source matches a decorated class.
/// </summary>
public sealed class DecoratedClassAnalyzer : ICodeAnalyzer
{
    /// <inheritdoc />
    public string Name => "decorated-class";

    /// <summary>
    /// Walks the events the language indexer just produced. For every <c>SymbolDeclared</c>
    /// whose container has a sibling <c>AttributeAttached</c> named <c>Decorated</c>, emit a
    /// <see cref="IndexEvent.EdgeEmitted"/> against the decorated class itself (self-loop) so the
    /// test fixture can detect it.
    /// </summary>
    public Task AnalyzeAsync(AnalyzerContext ctx, IGraphEmitter emitter, CancellationToken ct)
    {
        // Build a quick lookup of decorated keys from the events. The language indexer fires
        // SymbolDeclared first then AttributeAttached, so we walk the full list once.
        var decoratedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in ctx.IndexerEvents)
        {
            if (ev is IndexEvent.AttributeAttached a
                && string.Equals(a.AttributeName, "Decorated", StringComparison.Ordinal))
            {
                decoratedKeys.Add(a.SymbolCanonicalKey);
            }
        }
        foreach (var key in decoratedKeys)
        {
            // Emit a self-edge on the UsesType kind. Edge dst = src so the test can find it
            // via storage queries without depending on a synthetic target symbol.
            emitter.EmitEdge(new IndexEvent.EdgeEmitted(key, key, "UsesType"));
        }
        return Task.CompletedTask;
    }
}
