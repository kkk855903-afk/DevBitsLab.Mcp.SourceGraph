using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public interface IGraphStore : IAsyncDisposable
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task<long> UpsertFileAsync(string path, byte[] contentSha256, DateTimeOffset indexedAt, bool isGenerated = false, CancellationToken ct = default);
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

    /// <summary>
    /// Pass-1c container-id reconciliation: for each <c>(childId, parentId)</c> pair, set
    /// <c>symbols.container_id = parentId WHERE id = childId</c>. Runs inside a single
    /// <c>BEGIN/COMMIT</c>. Pairs whose <c>parentId</c> doesn't exist are skipped (the schema
    /// has no FK on <c>container_id</c>, so the row simply remains uncorrelated).
    /// </summary>
    Task BatchUpdateContainerIdsAsync(IReadOnlyList<(long ChildId, long ParentId)> pairs, CancellationToken ct = default);

    Task BulkInsertReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default);

    /// <summary>
    /// Bulk-insert edges. <see cref="Edge.Metadata"/> is JSON-serialised into the
    /// <c>edges.payload</c> column when non-null. <see cref="Edge.Kind"/> is the kebab-case
    /// kind identifier (e.g. <c>"calls"</c>, <c>"binds-path"</c>).
    /// </summary>
    Task BulkInsertEdgesAsync(IEnumerable<Edge> edges, CancellationToken ct = default);

    /// <summary>
    /// Bulk-insert the annotations attached to a set of symbols. Run after the symbols
    /// themselves are upserted, so <c>symbol_id</c> already resolves to a stable row.
    /// <see cref="AnnotationRecord.Flavor"/> discriminates annotation patterns across languages.
    /// </summary>
    Task BulkInsertAnnotationsAsync(IEnumerable<AnnotationRecord> annotations, CancellationToken ct = default);

    /// <summary>
    /// Set <c>symbols.test_framework</c> for the given symbol ids. Pairs whose <c>symbol_id</c>
    /// is missing are silently skipped. Runs in a single transaction.
    /// </summary>
    Task UpdateTestFrameworksAsync(IReadOnlyList<(long SymbolId, string Framework)> rows, CancellationToken ct = default);

    /// <summary>
    /// Upsert one <see cref="SymbolHistory"/> row keyed by <c>symbol_id</c>.
    /// </summary>
    Task UpsertSymbolHistoryAsync(SymbolHistory history, CancellationToken ct = default);

    /// <summary>
    /// Get the cached blamed content sha for every indexed symbol whose <c>file_id</c> matches
    /// <paramref name="fileId"/>. Used by the history pipeline to skip blame on unchanged files.
    /// Returns the dictionary <c>symbol_id -&gt; blamed_content_sha</c> for rows that exist;
    /// missing rows are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<long, byte[]?>> GetBlamedShasForFileAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// Look up the cached <see cref="SymbolHistory"/> row for <paramref name="symbolId"/>.
    /// Returns <c>null</c> when no row exists (e.g., history disabled or never blamed).
    /// </summary>
    Task<SymbolHistory?> GetSymbolHistoryAsync(long symbolId, CancellationToken ct = default);

    /// <summary>
    /// Lookup history for a batch of symbols at once. Useful when annotating <c>find_definition</c>
    /// or <c>list_symbols_in_file</c> output without firing one round-trip per row.
    /// </summary>
    Task<IReadOnlyDictionary<long, SymbolHistory>> GetSymbolHistoryBatchAsync(
        IReadOnlyCollection<long> symbolIds, CancellationToken ct = default);

    /// <summary>
    /// List symbols whose <c>last_authored_at</c> falls in <c>[sinceUnixMs, +inf)</c>. Optionally
    /// filtered by author (case-insensitive substring on <c>last_author</c>). Ordered by
    /// recency descending. Joined to symbol metadata so callers can format full hits.
    /// </summary>
    Task<IReadOnlyList<RecentChangeHit>> ListRecentChangesAsync(
        long sinceUnixMs, string? authorSubstring, int limit, CancellationToken ct = default);

    /// <summary>
    /// Walks the file_id maps to return every (symbol_id, file_path, start_line, end_line) tuple
    /// whose file_id matches <paramref name="fileId"/>. Used by the history pipeline to discover
    /// which symbols need a blame slice.
    /// </summary>
    Task<IReadOnlyList<SymbolSpan>> GetSymbolSpansForFileAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// Inbound <c>tests</c> edges for <paramref name="symbolId"/> — every test method that
    /// targets this production symbol. Returns the SymbolHit + the framework recorded on
    /// the test method's row.
    /// </summary>
    Task<IReadOnlyList<TestForHit>> ListTestsForAsync(long symbolId, int limit, CancellationToken ct = default);

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
    /// Lists named callers of <paramref name="symbolId"/>. With the default
    /// <paramref name="edgeKind"/> = <c>"calls"</c> this preserves the legacy behaviour. Pass a
    /// different kebab-case kind (<c>"uses-type"</c>, <c>"renders-component"</c>, …) to walk
    /// other edge types; pass <c>null</c> to walk every edge kind.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> ListCallersAsync(long symbolId, int limit = 50, string? edgeKind = "calls", CancellationToken ct = default);

    /// <summary>
    /// Lists outgoing targets from <paramref name="symbolId"/>. With the default
    /// <paramref name="edgeKind"/> = <c>"calls"</c> this preserves the legacy behaviour. Pass a
    /// different kebab-case kind to walk other edge types; pass <c>null</c> to walk every kind.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> ListCalleesAsync(long symbolId, int limit = 50, string? edgeKind = "calls", CancellationToken ct = default);

    /// <summary>Lists every member that satisfies the named interface member via <c>"implements-member"</c> edges.</summary>
    Task<IReadOnlyList<SymbolHit>> ListImplementationsAsync(long symbolId, int limit = 50, CancellationToken ct = default);

    /// <summary>Lists every member that consumes the given type via <c>"uses-type"</c> edges.</summary>
    Task<IReadOnlyList<SymbolHit>> ListUsersOfTypeAsync(long symbolId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// FTS-backed symbol search. <paramref name="kindFilter"/> is the kebab-case symbol kind
    /// (<c>"class"</c>, <c>"method"</c>, …) — pass <c>null</c> for all kinds.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> SearchSymbolsAsync(string ftsQuery, string? kindFilter = null, int limit = 25, CancellationToken ct = default);

    Task<IReadOnlyList<ModuleSymbol>> ModuleSummaryAsync(string namespaceOrPathPrefix, int limit = 25, CancellationToken ct = default);

    /// <summary>
    /// Walks the upstream graph of <paramref name="symbolId"/> via the given
    /// <paramref name="edgeKind"/> (default = <c>"calls"</c>) up to <paramref name="maxDepth"/>
    /// hops. Pass <c>null</c> to walk every edge kind.
    /// </summary>
    Task<IReadOnlyList<ImpactedSymbol>> ImpactOfChangeAsync(long symbolId, int maxDepth = 4, int limit = 100, string? edgeKind = "calls", CancellationToken ct = default);

    /// <summary>
    /// Direct children of a container (rows whose <c>container_id = containerId</c>),
    /// optionally filtered by <see cref="Microsoft.CodeAnalysis.Accessibility"/> integer value,
    /// ordered by file path then <c>start_line</c>.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> ListMembersAsync(long containerId, int? accessibilityFilter = null, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Find symbols that carry an annotation with the given short <paramref name="name"/>
    /// (e.g. <c>"HttpGet"</c>). When <paramref name="flavor"/> is non-null, restrict to
    /// annotations of that flavor (<c>"csharp-attribute"</c>, <c>"ts-decorator"</c>, …);
    /// <c>null</c> matches across all flavors. When <paramref name="argSubstring"/> is non-null,
    /// restrict results to annotations whose serialised arguments match the substring via the
    /// FTS5 trigram index over <c>annotations_fts.args_text</c>. <paramref name="kindFilter"/>
    /// narrows by kebab-case symbol kind (e.g. <c>"method"</c>).
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> FindByAnnotationAsync(string name, string? flavor, string? argSubstring, string? kindFilter, int limit, CancellationToken ct = default);

    /// <summary>Return every annotation attached to <paramref name="symbolId"/>, in row-id (insert) order.</summary>
    Task<IReadOnlyList<AnnotationRecord>> GetAnnotationsForSymbolAsync(long symbolId, CancellationToken ct = default);

    /// <summary>
    /// Replace the diagnostic set for <paramref name="fileId"/>: deletes every existing row with
    /// <c>file_id = fileId</c> and inserts <paramref name="diagnostics"/> in a single transaction.
    /// Run as the last step of per-file reindex, after symbols are upserted (so symbol_id values
    /// are stable).
    /// </summary>
    Task UpsertDiagnosticsForFileAsync(long fileId, IEnumerable<DiagnosticRecord> diagnostics, CancellationToken ct = default);

    /// <summary>
    /// Filtered diagnostics query. <paramref name="severity"/> filters via <c>severity &gt;= ?</c>
    /// (Roslyn's enum integer values: Hidden=0, Info=1, Warning=2, Error=3); <c>null</c> means
    /// "every severity". <paramref name="code"/> matches the diagnostic id (e.g. <c>CS0618</c>);
    /// <paramref name="symbolId"/> restricts to diagnostics attributed to that symbol.
    /// Results are ordered by file then line.
    /// </summary>
    Task<IReadOnlyList<DiagnosticHit>> FindDiagnosticsAsync(int? severity, string? code, long? symbolId, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Files marked <c>is_generated = 1</c>, with the count of symbols emitted from each file,
    /// ordered by symbol count descending. Used by the <c>list_generated_files</c> MCP tool.
    /// </summary>
    Task<IReadOnlyList<GeneratedFileRow>> ListGeneratedFilesAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// True if <paramref name="fileId"/>'s row has <c>is_generated = 1</c>. Used by tools that
    /// annotate <c>(generated)</c> in their output.
    /// </summary>
    Task<bool> IsGeneratedFileAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="FindReferencesAsync"/> but with an option to filter out references
    /// whose source file is generated. Default behaviour for <c>find_references</c> excludes
    /// generated refs; pass <c>includeGenerated = true</c> to include them.
    /// </summary>
    Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(long symbolId, bool includeGenerated, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Walk <c>binds-path</c> edges with optional payload-aware filters (<c>path</c>, <c>mode</c>,
    /// <c>converter</c>) plus optional source/target canonical-key narrowing. Returns one
    /// <see cref="EdgeWithPayload"/> per matching edge so the tool renderer has both endpoints
    /// in hand. Filters are AND-combined; pass <c>null</c> to skip a filter.
    /// <see cref="EdgeWithPayload.PayloadJson"/> is the verbatim <c>edges.payload</c> column
    /// (nullable when the originating edge had no metadata).
    /// </summary>
    Task<IReadOnlyList<EdgeWithPayload>> FindDataBindingsAsync(
        string? targetCanonicalKey,
        string? sourceCanonicalKey,
        string? pathContains,
        string? modeExact,
        string? converterExact,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Walk <c>handles-event</c> edges with optional payload-aware filters (<c>event</c>,
    /// <c>command</c>) plus optional handler/element canonical-key narrowing. Returns the same
    /// <see cref="EdgeWithPayload"/> shape as <see cref="FindDataBindingsAsync"/>. Filters are
    /// AND-combined; pass <c>null</c> to skip a filter.
    /// </summary>
    Task<IReadOnlyList<EdgeWithPayload>> FindEventHandlersAsync(
        string? handlerCanonicalKey,
        string? eventExact,
        string? elementCanonicalKey,
        string? commandExact,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Probe whether <i>any</i> edge of <paramref name="edgeKind"/> has a non-null
    /// <c>json_extract(payload, '$.&lt;payloadKey&gt;')</c>. Used by tools that need to
    /// distinguish "the filter didn't match anything" from "the indexer for this scope never
    /// emits this payload key" (e.g. <c>find_event_handlers --command=…</c> on a XAML scope
    /// whose indexer didn't record command names). Cheap: stops at the first hit.
    /// </summary>
    Task<bool> AnyEdgeHasPayloadKeyAsync(string edgeKind, string payloadKey, CancellationToken ct = default);

    /// <summary>
    /// Distinct edge kind names already present in the store, sorted lowercase. Used by the
    /// vocabulary publisher to enrich the MCP <c>initialize</c> response with what's actually
    /// queryable in this scope.
    /// </summary>
    Task<IReadOnlyList<string>> GetDistinctEdgeKindsAsync(CancellationToken ct = default);

    /// <summary>Distinct symbol kind names already present in the store, sorted lowercase.</summary>
    Task<IReadOnlyList<string>> GetDistinctSymbolKindsAsync(CancellationToken ct = default);

    /// <summary>Distinct annotation flavors already present in the store, sorted lowercase.</summary>
    Task<IReadOnlyList<string>> GetDistinctAnnotationFlavorsAsync(CancellationToken ct = default);
}

public sealed record ModuleSymbol(SymbolHit Symbol, int InDegree);
public sealed record ImpactedSymbol(SymbolHit Symbol, int Depth);

public sealed record SymbolKeyRow(string CanonicalKey, long Id, long FileId);
public sealed record FileRow(string Path, long Id);

public sealed record GraphStats(int FileCount, int SymbolCount, int ReferenceCount, int EdgeCount);

/// <summary>One row from <c>vw_recent_changes</c>: the symbol joined to its history entry.</summary>
public sealed record RecentChangeHit(SymbolHit Symbol, SymbolHistory History);

/// <summary>Result of <see cref="IGraphStore.ListTestsForAsync"/> — the test method + its framework.</summary>
public sealed record TestForHit(SymbolHit Test, string? Framework);

/// <summary>Lightweight projection of (symbol_id, file_path, start/end line) for the history pipeline.</summary>
public sealed record SymbolSpan(long SymbolId, string FilePath, int StartLine, int EndLine);
