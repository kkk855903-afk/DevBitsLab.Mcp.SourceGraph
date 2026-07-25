using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Registers every in-tree language indexer that ships with the server. Centralised so the
/// <c>serve</c> and <c>index</c> CLI paths can share one source of truth — adding a new
/// in-tree indexer means touching this list once, not the two registration sites in
/// <c>Program.cs</c>.
///
/// <para>The current list (in registration order):</para>
/// <list type="bullet">
/// <item><see cref="BuiltInRoslynLanguageIndexerStub"/> for <c>.cs</c>.</item>
/// <item><see cref="DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf.ProtobufLanguageIndexer"/> for <c>.proto</c>.</item>
/// <item><see cref="DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageIndexer"/> for <c>.xaml</c>.</item>
    /// <item><see cref="DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript.TypeScriptLanguageIndexer"/> for <c>.ts</c> / <c>.tsx</c> / <c>.js</c> / <c>.jsx</c>.</item>
    /// <item><see cref="DevBitsLab.Mcp.SourceGraph.Indexing.Cpp.CppSyntaxLanguageIndexer"/> for C/C++ source and headers.</item>
/// </list>
/// </summary>
internal static class BuiltInIndexers
{
    /// <summary>
    /// Register every in-tree indexer into <paramref name="registry"/>. Returns the per-indexer
    /// rejected-extension lists so callers can log which extensions a later registration
    /// shadowed (today's invariant: each built-in claims a disjoint extension set, so all
    /// returned lists should be empty).
    /// </summary>
    public static IReadOnlyList<(string IndexerName, IReadOnlyList<string> RejectedExtensions)> RegisterAll(LanguageIndexerRegistry registry)
    {
        var indexers = new ILanguageIndexer[]
        {
            new BuiltInRoslynLanguageIndexerStub(),
            new DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf.ProtobufLanguageIndexer(),
            new DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageIndexer(),
            new DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript.TypeScriptLanguageIndexer(),
            new DevBitsLab.Mcp.SourceGraph.Indexing.Cpp.CppSyntaxLanguageIndexer(),
        };

        var results = new List<(string, IReadOnlyList<string>)>(indexers.Length);
        foreach (var indexer in indexers)
        {
            var rejected = registry.Register(indexer);
            results.Add((indexer.GetType().Name, rejected));
        }
        return results;
    }
}
