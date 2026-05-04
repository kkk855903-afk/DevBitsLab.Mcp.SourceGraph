using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Stub implementation of <see cref="ILanguageIndexer"/> registered for the built-in C# pathway.
/// The real indexing for <c>.cs</c> files runs through <c>LiveIndexService</c>'s workspace-aware
/// solution walk; this stub exists so <see cref="LanguageIndexerRegistry"/> reports <c>.cs</c> as
/// covered without actually routing requests through here. A direct invocation of
/// <see cref="IndexAsync"/> would produce no events — the dispatcher is expected to special-case
/// <c>.cs</c> and use the workspace-aware bulk path.
/// </summary>
internal sealed class BuiltInRoslynLanguageIndexerStub : ILanguageIndexer
{
    private static readonly IReadOnlyCollection<string> _exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };

    public IReadOnlyCollection<string> FileExtensions => _exts;

    public Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
    {
        IReadOnlyList<IndexEvent> empty = Array.Empty<IndexEvent>();
        return Task.FromResult(empty);
    }
}
