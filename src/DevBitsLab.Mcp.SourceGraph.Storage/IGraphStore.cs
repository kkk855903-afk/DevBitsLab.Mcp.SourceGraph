using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public interface IGraphStore : IAsyncDisposable
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task<long> UpsertFileAsync(string path, byte[] contentSha256, DateTimeOffset indexedAt, CancellationToken ct = default);
    Task<byte[]?> GetFileContentHashAsync(string path, CancellationToken ct = default);

    Task ClearFileAsync(long fileId, CancellationToken ct = default);

    Task<long> InsertSymbolAsync(Symbol symbol, CancellationToken ct = default);
    Task BulkInsertReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default);
    Task BulkInsertEdgesAsync(IEnumerable<Edge> edges, CancellationToken ct = default);

    Task<GraphStats> GetStatsAsync(CancellationToken ct = default);

    // Queries
    Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(string query, string? filePathHint = null, int limit = 25, CancellationToken ct = default);
    Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(long symbolId, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolHit>> ListSymbolsInFileAsync(string filePath, CancellationToken ct = default);
    Task<SymbolHit?> GetSymbolByIdAsync(long symbolId, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolHit>> ListCallersAsync(long symbolId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolHit>> ListCalleesAsync(long symbolId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolHit>> SearchSymbolsAsync(string ftsQuery, Core.SymbolKind? kindFilter = null, int limit = 25, CancellationToken ct = default);
    Task<IReadOnlyList<ModuleSymbol>> ModuleSummaryAsync(string namespaceOrPathPrefix, int limit = 25, CancellationToken ct = default);
    Task<IReadOnlyList<ImpactedSymbol>> ImpactOfChangeAsync(long symbolId, int maxDepth = 4, int limit = 100, CancellationToken ct = default);
}

public sealed record ModuleSymbol(SymbolHit Symbol, int InDegree);
public sealed record ImpactedSymbol(SymbolHit Symbol, int Depth);

public sealed record GraphStats(int FileCount, int SymbolCount, int ReferenceCount, int EdgeCount);
