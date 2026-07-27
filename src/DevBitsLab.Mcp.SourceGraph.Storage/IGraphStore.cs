using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public interface IGraphStore : IAsyncDisposable
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads the bounded connection-local version pair used to detect graph changes caused by
    /// this connection or by another connection. The operation does not mutate graph state.
    /// </summary>
    Task<GraphReadVersion> GetReadVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the last completely published version for a derived-fact producer, or null when
    /// that producer has never completed. Producer versions let an indexer refresh only its own
    /// files after an implementation upgrade instead of forcing a whole-schema rebuild.
    /// </summary>
    Task<int?> GetProjectionVersionAsync(
        string producer,
        CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    /// <summary>Records a producer version only after its complete projection succeeds.</summary>
    Task SetProjectionVersionAsync(
        string producer,
        int version,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    Task<long> UpsertFileAsync(string path, byte[] contentSha256, DateTimeOffset indexedAt, bool isGenerated = false, CancellationToken ct = default);
    Task<byte[]?> GetFileContentHashAsync(string path, CancellationToken ct = default);

    /// <summary>Wipes refs and edge evidence emitted by this file, then removes logical edges
    /// left with no evidence. Does NOT delete symbols — they retain stable ids across edits.</summary>
    Task ClearFileOutgoingAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// Wipes references emitted by this file and all of its edge evidence except rows whose exact
    /// producer is selected for preservation. Logical edges and compatibility payloads are
    /// reconciled transactionally against the surviving evidence. This supports source refreshes
    /// that must retain a separately published last-good derived projection.
    /// </summary>
    Task ClearFileOutgoingAsync(
        long fileId,
        IReadOnlyCollection<string> edgeEvidenceProducersToPreserve,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates producer-preserving outgoing cleanup.");

    /// <summary>
    /// Transactionally removes only edge evidence produced by the exact
    /// <paramref name="producingFileId"/> + <paramref name="producer"/> pair. Compatibility
    /// payloads on surviving logical edges are resynchronised to their earliest remaining
    /// evidence; logical edges whose final evidence is removed are deleted. Returns the number
    /// of evidence occurrences removed.
    /// </summary>
    Task<int> ClearEdgeEvidenceAsync(
        long producingFileId,
        string producer,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates producer-specific edge-evidence cleanup.");

    /// <summary>
    /// Atomically replaces every edge-evidence occurrence owned by the exact indexed
    /// <paramref name="producingFilePath"/> + <paramref name="producer"/> pair. All canonical
    /// endpoints and evidence are resolved and validated before prior evidence is changed.
    /// Empty <paramref name="edges"/> therefore performs a precise producer cleanup. Logical
    /// edges retain evidence from other producers and keep their compatibility payload aligned
    /// with the earliest surviving occurrence.
    /// </summary>
    Task ReplaceProducerEdgeEvidenceAsync(
        string producingFilePath,
        string producer,
        IReadOnlyList<ProducerEdgeEvidenceFact> edges,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic producer edge-evidence replacement.");

    /// <summary>
    /// Atomically replaces every edge-evidence occurrence owned by one producer across the
    /// complete scope database. Each candidate occurrence names its indexed producing file
    /// through <see cref="FileEvidenceFact.Location"/>. All files, canonical endpoints, and
    /// evidence are resolved and validated before any prior producer row is changed. This is
    /// intended for cross-file projections that must never expose a mix of old and new
    /// generations; an empty candidate list performs a producer-wide cleanup.
    /// </summary>
    Task ReplaceProducerEdgeEvidenceProjectionAsync(
        string producer,
        IReadOnlyList<ProducerEdgeEvidenceFact> edges,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic producer projection replacement.");

    /// <summary>
    /// Transactionally establishes the first complete protobuf contract baseline for each exact
    /// canonical key. Existing rows are deliberately never overwritten. The caller must pass a
    /// fully validated, complete contract universe; any invalid or duplicate candidate rolls the
    /// entire insert back, preserving every prior baseline.
    /// </summary>
    Task<int> EnsureGrpcContractBaselinesAsync(
        IReadOnlyList<GrpcContractBaselineFact> facts,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates gRPC contract baselines.");

    /// <summary>
    /// Reads persisted first-successful protobuf baselines in exact canonical-key order. The
    /// concrete store rejects invalid or unbounded limits.
    /// </summary>
    Task<IReadOnlyList<GrpcContractBaselineRow>>
        ListGrpcContractBaselinesAsync(
            int limit,
            CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates gRPC contract baselines.");

    /// <summary>
    /// Atomically replaces a file-owned derived projection consisting of selected annotation
    /// flavors and edge evidence from one exact producer. Candidate annotations and edge
    /// endpoints are fully resolved before prior projection rows are changed. Empty fact
    /// collections are a successful precise cleanup; annotations of other flavors and edge
    /// evidence from other producers are preserved.
    /// </summary>
    Task ReplaceFileDerivedProjectionAsync(
        string producingFilePath,
        string producer,
        IReadOnlyCollection<string> annotationFlavors,
        IReadOnlyList<FileAnnotationFact> annotations,
        IReadOnlyList<ProducerEdgeEvidenceFact> edges,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic file-derived projection replacement.");

    /// <summary>
    /// Atomically replaces derived projections for multiple distinct producing files. Every
    /// caller-owned collection is snapshotted and every file, annotation, edge, and canonical
    /// endpoint is validated and resolved before any prior row is changed. Empty per-file fact
    /// collections perform precise cleanup while preserving unselected annotation flavors and
    /// evidence from other producers.
    /// </summary>
    Task ReplaceFileDerivedProjectionsAsync(
        IReadOnlyList<FileDerivedProjectionReplacement> projections,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic multi-file derived projection replacement.");

    /// <summary>Delete every symbol declared in <paramref name="fileId"/> whose canonical key is not in
    /// <paramref name="keysToKeep"/>, plus all refs/edges that touch those removed symbols.</summary>
    Task DeleteSymbolsForFileNotInAsync(long fileId, IReadOnlyCollection<string> keysToKeep, CancellationToken ct = default);

    /// <summary>
    /// Transactionally removes an indexed file and every artifact owned by it or pointing at one
    /// of its symbols. This includes refs, logical edges and evidence, annotations, diagnostics,
    /// history, and embeddings. Returns <see langword="true"/> when the file row existed.
    /// </summary>
    Task<bool> DeleteFileAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// Path-based counterpart to <see cref="DeleteFileAsync(long, CancellationToken)"/>.
    /// Path lookup and deletion happen in the same transaction. Returns <see langword="false"/>
    /// when no indexed file has the exact stored path.
    /// </summary>
    Task<bool> DeleteFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces one file row and every graph fact produced by that file. The store
    /// resolves canonical-key references, reconciles stale declarations, and writes symbols,
    /// edges/evidence, annotations, and references in one transaction. Any validation, lookup,
    /// or database failure leaves the prior file hash and graph untouched.
    /// </summary>
    Task<FileFactsReplacementResult> ReplaceFileFactsAsync(
        FileFactsReplacement replacement,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic file-facts replacement.");

    /// <summary>
    /// Atomically replaces all annotations and direct-call evidence owned by lower-case
    /// <c>c:</c>/<c>cpp:</c> native declarations and upserts their physical-file declarations.
    /// Managed <c>csharp:</c>, protobuf, and independently-produced facts are preserved. Prior
    /// native symbols are deliberately retained until the caller has successfully refreshed
    /// every managed boundary, so last-good interop edges remain resolvable if downstream
    /// analysis fails.
    /// </summary>
    Task<NativeInteropSnapshotReplacementResult>
        ReplaceNativeInteropSnapshotAsync(
            NativeInteropSnapshotReplacement replacement,
            CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic native interop snapshots.");

    /// <summary>
    /// Transactionally deletes only the requested stale C/C++ native-export, struct, function,
    /// or method declarations that are proven orphaned. A declaration is retained when it still
    /// owns or participates in any stored fact, including a <c>pinvoke-maps-to</c> edge targeting it.
    /// Callers must invoke this only after a complete native snapshot publication and a
    /// successful refresh of every managed boundary.
    /// </summary>
    Task<NativeInteropStaleSymbolCleanupResult>
        DeleteOrphanedNativeInteropSymbolsAsync(
            IReadOnlyCollection<string> staleCanonicalKeys,
            CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates stale native-symbol cleanup.");

    /// <summary>
    /// Atomically replaces annotations of one exact <paramref name="flavor"/> on declarations
    /// owned by the indexed <paramref name="filePath"/>. Every annotation host is resolved by
    /// canonical key and verified to belong to that file before prior rows are changed. An empty
    /// <paramref name="annotations"/> collection therefore clears only that file/flavor
    /// projection while preserving all other annotation flavors and files.
    /// </summary>
    Task ReplaceAnnotationsForFileByFlavorAsync(
        string filePath,
        string flavor,
        IReadOnlyList<FileAnnotationFact> annotations,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic annotation-flavor replacement.");

    /// <summary>
    /// Atomically reconciles the declarations owned by one indexed file and replaces the complete
    /// annotation set on its surviving declarations. Every keep key, annotation host, optional
    /// attribute definition, and JSON payload is resolved and validated before prior declarations
    /// or annotations are changed. Empty <paramref name="annotations"/> is a successful complete
    /// annotation cleanup. File-scoped diagnostics are also cleared because the indexing pass
    /// rebuilds them only after declaration reconciliation succeeds.
    /// </summary>
    Task ReconcileFileDeclarationsAndAnnotationsAsync(
        string filePath,
        IReadOnlyCollection<string> keysToKeep,
        IReadOnlyList<FileAnnotationFact> annotations,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates atomic declaration/annotation reconciliation.");

    /// <summary>
    /// Reconciles one file like the backward-compatible overload while retaining annotations
    /// whose flavor is in <paramref name="annotationFlavorsToPreserve"/> on surviving symbols.
    /// Annotations hosted by stale symbols are always removed, including preserved flavors.
    /// </summary>
    Task ReconcileFileDeclarationsAndAnnotationsAsync(
        string filePath,
        IReadOnlyCollection<string> keysToKeep,
        IReadOnlyList<FileAnnotationFact> annotations,
        IReadOnlyCollection<string> annotationFlavorsToPreserve,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates flavor-preserving declaration/annotation reconciliation.");

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
    /// Bulk-insert logical edges and their occurrence-level evidence. Repeated
    /// <c>(src, dst, kind)</c> values retain one logical edge while every distinct
    /// <see cref="Edge.Evidence"/> row is preserved. When a legacy caller omits evidence, the
    /// store creates inferred declaration evidence from the source symbol.
    /// </summary>
    Task BulkInsertEdgesAsync(IEnumerable<Edge> edges, CancellationToken ct = default);

    /// <summary>
    /// Return every stored proof for one logical edge in stable source order.
    /// </summary>
    Task<IReadOnlyList<Evidence>> ListEdgeEvidenceAsync(
        long sourceSymbolId,
        long targetSymbolId,
        string edgeKind,
        int limit = 100,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates occurrence-level edge evidence.");

    /// <summary>
    /// Returns whether at least one persisted edge-evidence occurrence is owned by the exact
    /// <paramref name="producer"/>. The concrete store validates the bounded producer identity
    /// and uses an existence probe; no evidence payloads are materialized.
    /// </summary>
    Task<bool> HasEdgeEvidenceByProducerAsync(
        string producer,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation cannot probe edge evidence by producer.");

    /// <summary>
    /// Bulk-insert the annotations attached to a set of symbols. Run after the symbols
    /// themselves are upserted, so <c>symbol_id</c> already resolves to a stable row.
    /// <see cref="AnnotationRecord.Flavor"/> discriminates annotation patterns across languages.
    /// </summary>
    Task BulkInsertAnnotationsAsync(IEnumerable<AnnotationRecord> annotations, CancellationToken ct = default);

    /// <summary>
    /// Lists one exact annotation flavor in stable annotation-id order. <paramref name="afterId"/>
    /// is an exclusive store-local cursor; callers page until fewer than <paramref name="limit"/>
    /// rows are returned. The concrete store rejects unbounded or invalid page sizes.
    /// </summary>
    Task<IReadOnlyList<StoredAnnotationRow>> ListAnnotationsByFlavorAsync(
        string flavor,
        long afterId,
        int limit,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates paged annotation-flavor reads.");

    /// <summary>
    /// Lists at most <paramref name="limit"/> annotations whose declaration owner path and flavor
    /// exactly match the supplied sets. Both sets are submitted as bounded values in one store
    /// round-trip; paths are not suffix- or fuzzy-matched. Callers can request one row beyond an
    /// expected projection size to detect unexpected extras without scanning the global flavor
    /// universe.
    /// </summary>
    Task<IReadOnlyList<StoredAnnotationRow>> ListAnnotationsForFilesByFlavorsAsync(
        IReadOnlyCollection<string> filePaths,
        IReadOnlyCollection<string> flavors,
        int limit,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This graph-store implementation predates bounded file/flavor annotation reads.");

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

    /// <summary>
    /// True when the store holds at least one pass-2 artifact attributable to the given file —
    /// either an outgoing-reference row whose <c>file_id</c> matches <paramref name="fileId"/>,
    /// or an outgoing edge whose source symbol is declared in this file. Used by the indexer's
    /// pass-1 integrity check to detect "zombied" files whose outgoing refs/edges were cleared
    /// by a prior pass-1 <see cref="ClearFileOutgoingAsync"/> but never repopulated by pass 2
    /// (transient compilation gap, exception in the per-file walk, etc.). Checking edges in
    /// addition to refs avoids re-walking files that legitimately produce zero refs but
    /// non-zero edges (e.g. types whose only outgoing connections are <c>uses-type</c> /
    /// <c>inherits</c> / <c>implements-member</c> from member signatures, with no body
    /// invocations). The default implementation returns <c>true</c> so legacy stores preserve
    /// today's "always trust SHA-skip" behaviour; concrete stores should override with an
    /// indexed <c>EXISTS</c> probe over both tables.
    /// </summary>
    Task<bool> HasOutgoingReferencesAsync(long fileId, CancellationToken ct = default)
        => Task.FromResult(true);

    Task<GraphStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Detailed row counts for the graph's main tables. Returned to <c>verify_scope</c> as part
    /// of its structured health snapshot. Counts are taken in a single round-trip via subselect.
    /// </summary>
    Task<RowCountsRow> RowCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs <c>PRAGMA integrity_check</c> on the main DB and the FTS5 integrity-check on
    /// <c>symbols_fts</c>. Returns the literal string <c>"ok"</c> when both pass; otherwise a
    /// short diagnostic string identifying the first failure (used as the <c>integrity_check</c>
    /// field of <c>verify_scope</c> and as the trigger for corruption-recovery flows in later
    /// phases). The check is read-only and may take seconds on large DBs.
    /// </summary>
    Task<string> IntegrityCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> random rows from <c>files</c> as
    /// <c>(path, content_sha256)</c> tuples. Used by <c>verify_scope</c> to compute a drift sample
    /// against the on-disk file contents. Order is randomised via <c>ORDER BY RANDOM()</c>; ties
    /// (none — all rows are distinct) are broken arbitrarily.
    /// </summary>
    Task<IReadOnlyList<FileShaRow>> SampleFileShasAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Returns every row from <c>files</c> as <c>(path, content_sha256)</c> tuples in arbitrary
    /// order. Used by <c>reconcile_drift</c> to compute the full symmetric difference against the
    /// on-disk file set. Unbounded — caller is responsible for sizing.
    /// </summary>
    Task<IReadOnlyList<FileShaRow>> GetAllFileShasAsync(CancellationToken ct = default);

    // Queries
    Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(string query, string? filePathHint = null, int limit = 25, CancellationToken ct = default);
    Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(long symbolId, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolHit>> ListSymbolsInFileAsync(string filePath, CancellationToken ct = default);
    Task<SymbolHit?> GetSymbolByIdAsync(long symbolId, CancellationToken ct = default);

    /// <summary>
    /// Gets the symbol whose canonical key exactly equals <paramref name="canonicalKey"/>.
    /// Canonical keys are unique within a store; no fuzzy name or FQN matching is performed.
    /// </summary>
    Task<SymbolHit?> GetSymbolByCanonicalKeyAsync(
        string canonicalKey,
        CancellationToken ct = default);

    /// <summary>
    /// Lists named callers of <paramref name="symbolId"/>. With the default
    /// <paramref name="edgeKind"/> = <c>"calls"</c> this preserves the legacy behaviour. Pass a
    /// different kebab-case kind (<c>"uses-type"</c>, <c>"renders-component"</c>, …) to walk
    /// other edge types; pass <c>null</c> to walk every edge kind.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> ListCallersAsync(long symbolId, int limit = 50, string? edgeKind = "calls", CancellationToken ct = default);

    /// <summary>
    /// Lists inbound logical edges that have at least one stored occurrence in
    /// <c>edge_evidence</c>. Each row preserves the edge's actual relation, including when
    /// <paramref name="edgeKind"/> is <c>null</c> (all relations).
    /// </summary>
    Task<IReadOnlyList<EdgeTraversalHit>> ListAuditableInboundEdgesAsync(
        long symbolId,
        int limit = 50,
        string? edgeKind = "calls",
        CancellationToken ct = default);

    /// <summary>
    /// Lists outgoing targets from <paramref name="symbolId"/>. With the default
    /// <paramref name="edgeKind"/> = <c>"calls"</c> this preserves the legacy behaviour. Pass a
    /// different kebab-case kind to walk other edge types; pass <c>null</c> to walk every kind.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> ListCalleesAsync(long symbolId, int limit = 50, string? edgeKind = "calls", CancellationToken ct = default);

    /// <summary>
    /// Lists outbound logical edges that have at least one stored occurrence in
    /// <c>edge_evidence</c>. Each row preserves the edge's actual relation, including when
    /// <paramref name="edgeKind"/> is <c>null</c> (all relations).
    /// </summary>
    Task<IReadOnlyList<EdgeTraversalHit>> ListAuditableOutboundEdgesAsync(
        long symbolId,
        int limit = 50,
        string? edgeKind = "calls",
        CancellationToken ct = default);

    /// <summary>
    /// Lists evidence-backed outbound logical edges whose relation is one of
    /// <paramref name="edgeKinds"/>. Filtering occurs in storage before the deterministic
    /// ordering and <paramref name="limit"/> are applied, so unrelated relations cannot consume
    /// the requested result budget.
    /// </summary>
    Task<IReadOnlyList<EdgeTraversalHit>> ListAuditableOutboundEdgesByKindsAsync(
        long symbolId,
        IReadOnlyCollection<string> edgeKinds,
        int limit = 50,
        CancellationToken ct = default);

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
    /// Return the distinct file ids that currently own at least one diagnostic whose code is in
    /// <paramref name="codes"/>. This unbounded-by-row-count maintenance query lets derived
    /// diagnostic producers reconcile stale rows without scanning or rewriting every file.
    /// </summary>
    Task<IReadOnlyList<long>> ListDiagnosticFileIdsByCodesAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken ct = default);

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
        CancellationToken ct = default,
        string? elementNameExact = null);

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
    /// Walk an exact XAML resource relation (<c>uses-resource</c> or <c>applies-style</c>)
    /// with an optional exact resource-key filter. This avoids enumerating every XAML source
    /// symbol when a caller already supplied a precise key.
    /// </summary>
    Task<IReadOnlyList<EdgeWithPayload>> FindResourceReferencesAsync(
        string edgeKind,
        string? keyExact,
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

/// <summary>
/// Per-table row counts surfaced by <see cref="IGraphStore.RowCountsAsync"/>. Distinct from
/// <see cref="GraphStats"/> which uses <c>int</c> and predates the broader table set; this record
/// is the agent-facing shape returned by the <c>verify_scope</c> tool.
/// </summary>
public sealed record RowCountsRow(
    long Symbols,
    long Refs,
    long Edges,
    long Files,
    long Annotations,
    long Diagnostics);

/// <summary>
/// One <c>files</c> row projected to the columns the drift-sample needs.
/// </summary>
public sealed record FileShaRow(string Path, byte[] ContentSha256);

/// <summary>One row from <c>vw_recent_changes</c>: the symbol joined to its history entry.</summary>
public sealed record RecentChangeHit(SymbolHit Symbol, SymbolHistory History);

/// <summary>Result of <see cref="IGraphStore.ListTestsForAsync"/> — the test method + its framework.</summary>
public sealed record TestForHit(SymbolHit Test, string? Framework);

/// <summary>Lightweight projection of (symbol_id, file_path, start/end line) for the history pipeline.</summary>
public sealed record SymbolSpan(long SymbolId, string FilePath, int StartLine, int EndLine);
