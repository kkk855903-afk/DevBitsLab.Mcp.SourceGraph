using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public interface IGraphStore : IAsyncDisposable
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task<long> UpsertFileAsync(string path, byte[] contentSha256, DateTimeOffset indexedAt, CancellationToken ct = default);
    Task<byte[]?> GetFileContentHashAsync(string path, CancellationToken ct = default);

    /// <summary>Wipes refs and edges that originate IN this file (ref.file_id = id, edge.src in file's symbols).
    /// Does NOT delete the symbols themselves — they're upserted by canonical key so their integer ids stay
    /// stable across edits, keeping incoming refs/edges from other files valid.</summary>
    Task ClearFileOutgoingAsync(long fileId, CancellationToken ct = default);

    /// <summary>Delete every symbol declared in <paramref name="fileId"/> whose canonical key is not in
    /// <paramref name="keysToKeep"/>, plus all refs/edges that touch those removed symbols.</summary>
    Task DeleteSymbolsForFileNotInAsync(long fileId, IReadOnlyCollection<string> keysToKeep, CancellationToken ct = default);

    /// <summary>Upsert a symbol by canonical key. Returns the symbol's stable id (existing or newly created).</summary>
    Task<long> UpsertSymbolAsync(string canonicalKey, Symbol symbol, CancellationToken ct = default);

    Task BulkInsertReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default);
    Task BulkInsertEdgesAsync(IEnumerable<Edge> edges, CancellationToken ct = default);

    /// <summary>Used by the indexer to re-hydrate its in-memory maps after a process restart.</summary>
    Task<IReadOnlyList<SymbolKeyRow>> GetAllSymbolKeysAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FileRow>> GetAllFilesAsync(CancellationToken ct = default);

    Task<GraphStats> GetStatsAsync(CancellationToken ct = default);

    // Queries
    Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(string query, string? filePathHint = null, int limit = 25, CancellationToken ct = default);
    Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(long symbolId, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolHit>> ListSymbolsInFileAsync(string filePath, CancellationToken ct = default);
    Task<SymbolHit?> GetSymbolByIdAsync(long symbolId, CancellationToken ct = default);
    /// <summary>
    /// Lists named callers of <paramref name="symbolId"/>. With the default <paramref name="edgeKind"/> = <see cref="EdgeKind.Calls"/>
    /// this preserves the legacy behaviour. Pass a different kind to walk other edge types
    /// (e.g. <see cref="EdgeKind.UsesType"/> to get type consumers); pass <c>null</c> to walk every edge kind.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> ListCallersAsync(long symbolId, int limit = 50, EdgeKind? edgeKind = EdgeKind.Calls, CancellationToken ct = default);
    /// <summary>
    /// Lists outgoing targets from <paramref name="symbolId"/>. With the default <paramref name="edgeKind"/> = <see cref="EdgeKind.Calls"/>
    /// this preserves the legacy behaviour. Pass a different kind to walk other edge types; pass <c>null</c> to walk every edge kind.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> ListCalleesAsync(long symbolId, int limit = 50, EdgeKind? edgeKind = EdgeKind.Calls, CancellationToken ct = default);
    /// <summary>Lists every member that satisfies the named interface member via <see cref="EdgeKind.ImplementsMember"/> edges.</summary>
    Task<IReadOnlyList<SymbolHit>> ListImplementationsAsync(long symbolId, int limit = 50, CancellationToken ct = default);
    /// <summary>Lists every member that consumes the given type via <see cref="EdgeKind.UsesType"/> edges.</summary>
    Task<IReadOnlyList<SymbolHit>> ListUsersOfTypeAsync(long symbolId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolHit>> SearchSymbolsAsync(string ftsQuery, Core.SymbolKind? kindFilter = null, int limit = 25, CancellationToken ct = default);
    Task<IReadOnlyList<ModuleSymbol>> ModuleSummaryAsync(string namespaceOrPathPrefix, int limit = 25, CancellationToken ct = default);
    /// <summary>
    /// Walks the upstream graph of <paramref name="symbolId"/> via the given <paramref name="edgeKind"/> (default = <see cref="EdgeKind.Calls"/>)
    /// up to <paramref name="maxDepth"/> hops. Pass <c>null</c> to walk every edge kind.
    /// </summary>
    Task<IReadOnlyList<ImpactedSymbol>> ImpactOfChangeAsync(long symbolId, int maxDepth = 4, int limit = 100, EdgeKind? edgeKind = EdgeKind.Calls, CancellationToken ct = default);
}

public sealed record ModuleSymbol(SymbolHit Symbol, int InDegree);
public sealed record ImpactedSymbol(SymbolHit Symbol, int Depth);

public sealed record SymbolKeyRow(string CanonicalKey, long Id, long FileId);
public sealed record FileRow(string Path, long Id);

public sealed record GraphStats(int FileCount, int SymbolCount, int ReferenceCount, int EdgeCount);
