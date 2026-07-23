# Storage

## Purpose

Persist the code graph (files, symbols, refs, edges) in a single SQLite file
with FTS5 full-text search, and expose query and write operations to the rest
of the system through `IGraphStore`.
## Requirements
### Requirement: Self-applying schema migrations
`SqliteGraphStore` SHALL apply the bundled schema on connect; if the on-disk
version is below `Schema.Version`, all data tables and triggers SHALL be
dropped and recreated from the embedded SQL. The current `Schema.Version`
is `11`.

#### Scenario: Open a DB on an older schema
- **WHEN** the `schema_version` table reports a value less than
  `Schema.Version` (currently `11`)
- **THEN** `EnsureSchemaAsync` runs `Schema.DropAll`, applies `Schema.V1` and
  `Schema.V2` from scratch, inserts the new version row, and logs
  `"On-disk graph schema is vOLD; rebuilding to vNEW"`

#### Scenario: Open a v10 DB after the contract reform
- **WHEN** a server built against `Schema.Version = 11` opens a `.sourcegraph/scopes/<id>.db` whose `schema_version` row reports `10` (written by the previous server)
- **THEN** `EnsureSchemaAsync` drops every data table — including the legacy `attributes` / `attributes_fts` virtual table — and recreates them from `Schema.V1` + `Schema.V2`; the watcher's next index pass populates them from source

### Requirement: Stable symbol id by canonical key
`UpsertSymbolAsync` SHALL preserve a symbol's row id across successive calls
that share the same canonical key, updating the other columns in place.

#### Scenario: Same key, new line/col
- **WHEN** a symbol with canonical key `K` is upserted twice with different
  `start_line` / `signature` values
- **THEN** the row id returned by the second call equals the first; the
  row's other columns reflect the latest call (last-write-wins on
  `name`, `fqn`, `kind`, `file_id`, `start_line`, etc.)

### Requirement: Producer-specific edge-evidence cleanup
The graph store SHALL expose a transactional cleanup operation keyed by the exact
`(producing_file_id, producer)` pair. It SHALL delete only matching `edge_evidence` rows,
resynchronise each touched surviving logical edge's compatibility payload from its earliest
remaining evidence, and delete a touched logical edge when its final evidence disappears.

#### Scenario: Two analyzers produced the same edge from one file
- **WHEN** producer `native-a` and producer `native-b` both support one logical edge from file `F`, and cleanup runs for `(F, native-a)`
- **THEN** only `native-a` evidence is removed, the logical edge survives on `native-b` evidence, and its payload reflects that surviving occurrence

#### Scenario: Exact pair supplied the final evidence
- **WHEN** every evidence occurrence for a touched logical edge matches the cleanup pair
- **THEN** both those occurrences and the unsupported logical edge are deleted in one transaction

### Requirement: FTS5 trigram index over symbol text
The schema SHALL maintain a `symbols_fts` virtual table over
`symbols.{name, fqn, signature}` using the trigram tokenizer, kept in sync
via triggers on `symbols`.

#### Scenario: Trigram match on a fragment
- **WHEN** `SearchSymbolsAsync("Greet", null, 25)` is called against a graph
  that contains symbols `Greeter`, `IGreeter.Greet`, and `Greeter.Greet`
- **THEN** all three are returned, ordered by FTS5 `rank`

#### Scenario: Triggers keep FTS in sync on insert/delete
- **WHEN** a symbol row is inserted, updated, or deleted in `symbols`
- **THEN** the `symbols_ai` / `symbols_au` / `symbols_ad` triggers fire so
  that subsequent FTS queries reflect the change without an explicit rebuild

### Requirement: Per-file outgoing cleanup
`ClearFileOutgoingAsync(fileId)` SHALL delete only the refs whose
`file_id = fileId` and edge-evidence occurrences whose
`producing_file_id = fileId`. It SHALL then delete each logical edge that
has no remaining evidence, leaving the file's symbols and independently
supported incoming/outgoing edges intact.

#### Scenario: Reset a file's outgoing data before reindex
- **WHEN** `ClearFileOutgoingAsync(F)` is called as the first step of a live
  reindex of file `F`
- **THEN** all rows in `refs` with `file_id = F` are removed, all rows in
  `edge_evidence` with `producing_file_id = F` are removed, logical
  `edges` without any remaining evidence are removed, logical edges still
  supported by another producer are retained, and no rows in `symbols` are touched

### Requirement: Reconcile a file's symbol set
`DeleteSymbolsForFileNotInAsync(fileId, keepKeys)` SHALL remove every symbol
attributed to `fileId` whose `canonical_key` is not in `keepKeys`, plus the
refs and edges that touched those removed symbols.

#### Scenario: Drop a deleted declaration
- **WHEN** file `F` previously declared `{A, B, C}` and is reindexed with the
  new key set `{A, B}`
- **THEN** the row for `C` is deleted, refs and edges referencing `C.id`
  are deleted in the same transaction, and `A` and `B` are preserved

### Requirement: Read-only query API
`IGraphStore` SHALL expose query methods (`FindSymbolsAsync`,
`FindReferencesAsync`, `ListSymbolsInFileAsync`, `ListCallersAsync`,
`ListCalleesAsync`, `SearchSymbolsAsync`, `ModuleSummaryAsync`,
`ImpactOfChangeAsync`, `GetSymbolByIdAsync`) that return strongly typed hit
records joined to file paths.

#### Scenario: FQN suffix match on find_symbols
- **WHEN** `FindSymbolsAsync("Calculator.Add", null, 25)` is called
- **THEN** results are ranked: exact-name (1), exact-fqn (2), fqn-suffix (3),
  prefix (4), substring (5); ties are broken by `length(fqn)` ascending

### Requirement: Symbol metadata columns
The `symbols` table SHALL include `modifiers TEXT`, `accessibility INTEGER NOT NULL DEFAULT 0`, and `xml_summary TEXT` columns alongside the existing fields.

#### Scenario: Column presence after migration
- **WHEN** `EnsureSchemaAsync` runs against a v4 (or older) DB
- **THEN** the resulting `symbols` table has `modifiers`, `accessibility`, and `xml_summary` columns and `Schema.Version = 5`

### Requirement: FTS5 indexes XML summary
The `symbols_fts` virtual table SHALL include `xml_summary` as a tokenised column so `search_symbols` matches against it.

#### Scenario: Search by description
- **WHEN** `SearchSymbolsAsync("retry", null, 25)` is called against a graph that contains a method with `xml_summary = "Retries the operation on transient errors"`
- **THEN** that method appears in the result set

### Requirement: Container-id batch update
`SqliteGraphStore` SHALL expose `BatchUpdateContainerIdsAsync(IReadOnlyList<(long childId, long parentId)>)` that updates many `container_id` values in a single transaction.

#### Scenario: Bulk container update
- **WHEN** `BatchUpdateContainerIdsAsync` is called with N pairs
- **THEN** all updates run inside a single `BEGIN/COMMIT`, the affected row count equals N, and rows whose `parentId` doesn't exist are skipped (no FK error since FK was previously dropped)

### Requirement: Annotations table replaces attributes
The schema SHALL include an `annotations(id, symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)` table indexed on `(symbol_id)`, `(name)`, `(flavor)`, and `(attribute_symbol_id)`, plus an `annotations_fts` virtual table tokenising the synthesised `args_text` column. The legacy `attributes` table from prior schema versions SHALL NOT exist in `Schema.Version = 11`.

`flavor` SHALL be `TEXT NOT NULL`.

#### Scenario: Storing a C# attribute as an annotation
- **WHEN** the C# indexer emits `AnnotationAttached(name: "HttpGet", flavor: "csharp-attribute", ...)` and the host persists it
- **THEN** the resulting row in `annotations` has `flavor = 'csharp-attribute'`, `name = 'HttpGet'`, and the FTS table contains the args text for trigram matching

#### Scenario: Filter annotations by flavor
- **WHEN** the host runs `SELECT ... FROM annotations WHERE flavor = 'csharp-attribute' AND name = 'Authorize'`
- **THEN** the SQL plan uses the `idx_annotations_flavor` and `idx_annotations_name` indexes and returns matching rows

### Requirement: find_by_annotation query API
`IGraphStore` SHALL expose `FindByAnnotationAsync(name, flavor?, argSubstring?, kindFilter?, limit)` returning `SymbolHit` rows whose attached annotations match the criteria. When `flavor` is `null`, the query matches across all flavors. The legacy `FindByAttributeAsync` method SHALL NOT exist on `IGraphStore` after this change.

#### Scenario: Strict name match plus wildcard arg match
- **WHEN** `FindByAnnotationAsync("HttpGet", flavor: "csharp-attribute", argSubstring: "users", kindFilter: null, limit: 50)` is called against a graph with `[HttpGet("/api/users")]` on method `M`
- **THEN** `M` is in the result set

#### Scenario: Cross-flavor name match
- **WHEN** `FindByAnnotationAsync("Component", flavor: null, argSubstring: null, kindFilter: null, limit: 50)` is called against a future polyglot graph that has both a C# `[Component]` attribute and a TS `@Component` decorator with the same `name = "Component"`
- **THEN** both rows are returned, each carrying its `flavor` so the caller can distinguish them

### Requirement: is_generated column on files
The `files` table SHALL include `is_generated INTEGER NOT NULL DEFAULT 0`; the column is `1` for any document obtained from `Project.GetSourceGeneratedDocumentsAsync()` and `0` for regular documents.

#### Scenario: Generated file flag
- **WHEN** an indexed solution contains a source generator emitting `Foo.g.cs`
- **THEN** the `files` row for `Foo.g.cs` has `is_generated = 1`

### Requirement: Diagnostics table
The schema SHALL include `diagnostics(id, symbol_id, file_id, severity, code, message, line, col)` with indexes on `(file_id)`, `(severity)`, `(code)`, and `(symbol_id)` for fast filtering.

#### Scenario: Severity filter
- **WHEN** `FindDiagnosticsAsync(severity: 2 (Warning), null, null, 100)` is called
- **THEN** the SQL plan uses `idx_diagnostics_severity` and returns rows with severity `>= 2`

### Requirement: test_framework column on symbols
The `symbols` table SHALL include `test_framework TEXT NULL` to record the detected test framework (`xunit | nunit | mstest`).

#### Scenario: xUnit method recorded
- **WHEN** a method tagged `[Fact]` is indexed
- **THEN** its `symbols.test_framework = 'xunit'`

### Requirement: symbol_history table
The schema SHALL include `symbol_history(symbol_id PRIMARY KEY, last_commit_sha TEXT, last_author TEXT, last_authored_at INTEGER, line_count INTEGER, blamed_content_sha BLOB)` with the cache key `blamed_content_sha` matching the source file's current `content_sha256` to skip redundant blame.

#### Scenario: Blame cache key
- **WHEN** a file's `content_sha256` matches the symbol's `symbol_history.blamed_content_sha`
- **THEN** the indexer skips `git blame` for that file

### Requirement: Edge and symbol kinds stored as TEXT
The `edges.kind_name` column and the `symbols.kind_name` column SHALL be `TEXT NOT NULL` containing the kebab-case kind identifier (e.g. `"calls"`, `"renders-component"`, `"class"`, `"xaml-element"`). The legacy integer `kind` columns from prior schema versions SHALL NOT exist in `Schema.Version = 11`.

Indexes SHALL exist on `edges(kind_name)` and `symbols(kind_name)` to keep filter queries plan-efficient.

#### Scenario: Storing an edge with a string kind
- **WHEN** `BulkInsertEdgesAsync` is called with an edge whose kind is `"binds-path"`
- **THEN** the resulting row's `kind_name` column equals the literal text `"binds-path"`

#### Scenario: Querying edges by kind
- **WHEN** the host runs `SELECT ... FROM edges WHERE kind_name = 'calls'`
- **THEN** the SQL plan uses the `idx_edges_kind_name` index and returns matching rows

### Requirement: Edge payload column carries metadata
The `edges` table SHALL include a `payload TEXT NULL` column that stores the JSON serialization of an `EdgeEmitted.Metadata` dictionary when present, and `NULL` otherwise.

#### Scenario: Edge written without metadata
- **WHEN** an edge is emitted with `Metadata = null`
- **THEN** the resulting `edges` row has `payload IS NULL`

#### Scenario: Edge written with metadata
- **WHEN** an edge is emitted with `Metadata = { ["path"] = "User.Name" }`
- **THEN** the resulting `edges` row has `payload = '{"path":"User.Name"}'` (JSON object form), and `json_extract(payload, '$.path')` returns `'User.Name'`

### Requirement: Occurrence-level edge evidence
The schema SHALL store one logical `edges` row per `(src, dst, kind_name)` and one
`edge_evidence` row per independently attributable occurrence. Each evidence row SHALL
carry the producing file id, the matching indexed file path, a valid 1-based half-open source range,
an ordered confidence (`0=inferred`, `1=semantic`, `2=exact`), a non-empty producer, and
optional occurrence metadata. Duplicate evidence emissions SHALL be idempotent.

Legacy callers that omit evidence SHALL receive an `inferred` proof at the source
declaration for compatibility. New production indexers SHALL provide the actual producing
file and occurrence range.

#### Scenario: Repeated edge keeps distinct call sites
- **WHEN** the same caller emits two `calls` relationships to the same callee from two source ranges
- **THEN** `edges` contains one logical row and `edge_evidence` contains two rows

#### Scenario: Producer cleanup retains independently supported edge
- **GIVEN** files `F1` and `F2` each produced evidence for the same logical edge
- **WHEN** `ClearFileOutgoingAsync(F1)` runs
- **THEN** only `F1`'s evidence is removed, the logical edge remains supported by `F2`, and the compatibility `edges.payload` reflects surviving evidence rather than deleted metadata

#### Scenario: Evidence cannot spoof another path
- **WHEN** evidence names a missing producing file id or a path that differs from that file's indexed path
- **THEN** the write fails transactionally and neither the logical edge nor the evidence row is persisted

### Requirement: Stable view layer over the underlying tables
The storage layer SHALL ship a versioned set of read-only SQL views (`v_symbols`, `v_files`, `v_edges`, `v_edge_evidence`, `v_references`, `v_scopes`, `v_annotations`, `v_diagnostics`, `v_history`) that present a denormalised, agent-facing contract over the underlying `symbols` / `edges` / `edge_evidence` / `symbol_references` / `files` / `annotations` / `diagnostics` / `symbol_history` tables and the `_meta.db` scope-registry tables. The views SHALL be the public contract for any consumer that runs ad-hoc SQL via the MCP `query_graph` tool; the underlying tables remain implementation details and may evolve without bumping `Views.SchemaVersion`.

`Views.SchemaVersion` SHALL be a compile-time integer constant exposed on the storage assembly (current value `3`); it SHALL bump on **any view-set change** — addition, removal, column rename, or column-type change — so clients that cache `describe_schema` by version always re-introspect after a server upgrade. The original "backwards-incompatible only" wording is superseded: cache-aware clients need a signal even for additive changes (a new view added without a version bump would otherwise be invisible to a client still serving cached schema).

The view definitions SHALL live in an embedded SQL resource (`Views.sql`) with `{{SCOPE_UNION_BLOCK_<view>}}` placeholder tokens for the per-scope `UNION ALL` branches that the connection helper inlines at attach time. Each view (except `v_scopes`) SHALL include a `scope TEXT NOT NULL` column carrying the scope id, populated by the per-scope branch's `SELECT '<scope_id>' AS scope, …` clause; cross-scope joins SHALL use the composite `(scope, id)` tuple.

`v_edge_evidence` SHALL expose one row per proof occurrence with the logical edge tuple, producing file id, file path, 1-based source range, producer, occurrence payload, the text confidence (`inferred` / `semantic` / `exact`), and its ordered integer level (0 / 1 / 2).

#### Scenario: View definitions execute against a single-scope DB
- **GIVEN** a temp SQLite DB containing one row each in `symbols`, `edges`, `edge_evidence`, `files`, `symbol_references`, `annotations`, `diagnostics`, `symbol_history` (mimicking a single attached scope)
- **WHEN** the test inlines `Views.Sql` with one branch per per-view template and applies it as TEMP views
- **THEN** every view from the set executes without syntax error and returns the expected column shapes; `v_symbols.is_public` is `1` for the symbol whose `accessibility = 6`; `v_symbols.is_type` is `1` for the symbol whose `kind_name = 'class'`; `v_edge_evidence.confidence` maps level `2` to `exact`; `v_diagnostics.severity_name` reflects the documented CASE mapping; `v_history` returns the seeded blame row with all six columns

#### Scenario: View columns match describe_schema's response
- **WHEN** `describe_schema` returns the `views` array
- **THEN** every column listed in the `Views.All` descriptor for each of the nine views is present (and only those columns) when the view is queried via `SELECT * FROM <view> LIMIT 0`; the SQLite `name` and `type` for each column matches the descriptor

#### Scenario: View schema version bumps with the view-set change
- **WHEN** a consumer reads `Views.SchemaVersion` (or the `view_schema_version` field returned by `describe_schema`)
- **THEN** the value is `3` for this revision; was `2` for the prior revision (which shipped eight views); a future change adding or removing a view SHALL increment the version again

#### Scenario: Underlying table schema evolution does not bump view version
- **GIVEN** a hypothetical future storage change that adds an internal column `symbols.cyclomatic_complexity INTEGER` without exposing it via `v_symbols`
- **WHEN** that change ships
- **THEN** `Views.SchemaVersion` remains unchanged; agent queries against `v_symbols` return the same columns as before; `Schema.Version` (the storage on-disk version) bumps independently

#### Scenario: Cache-aware client re-introspects after additive view change
- **GIVEN** a client that called `describe_schema` against a server reporting `view_schema_version = 2` and cached the resulting schema
- **WHEN** the client reconnects to a server reporting `view_schema_version = 3` after this change ships
- **THEN** the client SHOULD discard its cached schema and re-call `describe_schema`; `v_edge_evidence` appears in the refreshed view list with its columns

### Requirement: Read-only multi-scope attached connection helper
The storage layer SHALL expose `MultiScopeReadOnlyConnection.OpenAsync(IScopeRegistry registry, string repoRoot, string scopeFilter, int maxAttached, CancellationToken ct)` returning an open `SqliteConnection` configured for read-only access to a resolved set of scope DBs plus the `_meta.db` registry, with the view layer (per the `Stable view layer over the underlying tables` requirement) created as TEMP views ready for query. The `repoRoot` parameter resolves the per-scope DB locations via `ScopeLayout.ScopeDbPath(repoRoot, id)` and the registry DB via `ScopeLayout.MetaDbPath(repoRoot)`.

The helper SHALL:
- Open an in-memory SQLite connection (`Data Source=:memory:`).
- Raise the runtime ATTACH limit to `maxAttached` (default `64`, hard-bounded by SQLite's compile-time ceiling of `125`) via `sqlite3_limit(SQLITE_LIMIT_ATTACHED, …)`. Note: the bundled `e_sqlite3` ships with `SQLITE_MAX_ATTACHED = 10`, silently clamping any higher limit; the practical ceiling is therefore 9 scope DBs (one ATTACH slot is reserved for `meta`).
- Resolve `scopeFilter` against `IScopeRegistry`:
  - `"*"` → all scopes whose `isolated` flag is `false`.
  - Comma-separated list → those scopes by id (isolated permitted when explicitly named).
  - Single id → just that scope.
- Throw `ArgumentException(paramName: "scopeFilter")` with a diagnostic message **before** opening any ATTACH when the resolved scope set is empty (e.g. `*` against a registry with no non-isolated scopes, or a comma-list that filters everything out). The message SHALL distinguish "registry has no scopes" from "every scope is isolated" so callers can render an actionable hint.
- For each ATTACH (meta + per-scope), validate `File.Exists(absolutePath)` first and throw `FileNotFoundException` with the alias and absolute path if the file is missing. SQLite's `ATTACH DATABASE` materialises an empty file at the given path when the file doesn't exist (the connection isn't read-only at this point — `query_only` fires later); the explicit existence check prevents accidentally creating phantom empty scope DBs that would later fail with cryptic "no such table" errors.
- ATTACH `_meta.db` AS `meta` with a literal absolute path (no URI), once per connection regardless of scope filter.
- ATTACH each per-scope DB AS `<scope_id>` (double-quoted in the DDL so kebab-case ids parse cleanly) with a literal absolute path.
- Expand `Views.Sql`'s `{{SCOPE_UNION_BLOCK_<view>}}` tokens into one `SELECT '<scope_id>' AS scope, … FROM "<scope_id>".<table>` branch per attached scope, joined by `UNION ALL`, then execute the resulting DDL.
- After the TEMP VIEW DDL is applied, set `PRAGMA query_only = 1` on the connection so any subsequent `INSERT` / `UPDATE` / `DELETE` / `DROP` / `CREATE` / `REPLACE` against any attached DB returns `SQLITE_READONLY` (8). `query_only` is per-connection state — it does not require a global `SQLITE_CONFIG_URI` flip and never races with other `SqliteConnection`s in the process. (An earlier revision used `ATTACH 'file:…?mode=ro'` URIs; the URI form needed a process-global `sqlite3_shutdown / config / initialize` dance that raced with parallel `SqliteConnection`s under xUnit's collection runner.)
- If the resolved scope set's count exceeds `maxAttached` (or the SQLite-imposed ceiling, whichever is lower), throw `ScopeAttachLimitExceededException` carrying the resolved scope-id list and the configured ceiling, **before** opening any per-scope ATTACH.

#### Scenario: Default filter resolves to non-isolated scopes
- **GIVEN** a scope registry with `frontend` (not isolated), `backend` (not isolated), and `vendor` (isolated)
- **WHEN** `OpenAsync(registry, repoRoot, "*", maxAttached: 64, ct)` runs
- **THEN** the returned connection has `meta`, `frontend`, and `backend` attached; `vendor` is NOT attached; `v_symbols` enumerates rows from `frontend` and `backend` only

#### Scenario: Explicit naming includes isolated scopes
- **WHEN** `OpenAsync(registry, repoRoot, "vendor", maxAttached: 64, ct)` runs
- **THEN** the returned connection has `meta` and `vendor` attached; `v_symbols` enumerates rows from `vendor` only

#### Scenario: Comma-list filter is honoured exactly
- **WHEN** `OpenAsync(registry, repoRoot, "frontend,vendor", maxAttached: 64, ct)` runs
- **THEN** the returned connection has `meta`, `frontend`, and `vendor` attached; `backend` is NOT attached even though it's not isolated

#### Scenario: Read-only enforcement via PRAGMA query_only
- **GIVEN** an open multi-scope connection
- **WHEN** the caller executes `INSERT INTO frontend.symbols(name) VALUES ('evil')`
- **THEN** SQLite returns error code `SQLITE_READONLY` (8); no row is inserted; the on-disk `frontend.db` is untouched. The same applies to writes against the `meta` ATTACH and to schema mutations (`CREATE`, `DROP`, `ALTER`).

#### Scenario: Empty scope set throws ArgumentException
- **GIVEN** a registry containing only isolated scopes (or no scopes at all)
- **WHEN** `OpenAsync(registry, repoRoot, "*", maxAttached: 64, ct)` runs
- **THEN** the helper throws `ArgumentException` with `ParamName == "scopeFilter"` and a diagnostic message naming the cause; no SQLite connection is leaked; tool bodies (`describe_schema`, `query_graph`) catch this and surface a structured `no_scopes` error to the agent

#### Scenario: Missing scope DB file throws FileNotFoundException
- **GIVEN** a scope registered in `_meta.db` whose per-scope DB file (`scopes/<id>.db`) has been deleted from disk
- **WHEN** the resolved scope set includes that id and `OpenAsync` reaches the corresponding ATTACH
- **THEN** the helper throws `FileNotFoundException` with the alias and absolute path; the SQLite ATTACH is never issued, so no phantom empty file is created on disk; tool bodies catch this and surface a structured `scope_db_missing` error

#### Scenario: ATTACH ceiling enforced
- **GIVEN** a registry containing 70 non-isolated scopes
- **WHEN** `OpenAsync(registry, repoRoot, "*", maxAttached: 64, ct)` runs
- **THEN** the helper throws `ScopeAttachLimitExceededException`; the exception's `ResolvedScopes` property lists all 70 scope ids; `Limit` is the lower of `maxAttached` and the SQLite-imposed ceiling; no SQLite connection is leaked

#### Scenario: Connection is per-call and disposable
- **WHEN** `query_graph` opens a multi-scope connection, executes one query, and disposes it
- **THEN** the in-memory main DB and every ATTACH are released; subsequent calls do not see leftover TEMP views from a prior call; opening two connections concurrently does not interfere with each other (no shared state and no global SQLite engine reconfigure)

### Requirement: Extended view coverage for annotations, diagnostics, and per-symbol git history
The storage layer SHALL extend the view layer (per the existing `Stable view layer over the underlying tables` requirement) with three additional views — `v_annotations`, `v_diagnostics`, `v_history` — covering the corresponding underlying tables (`annotations`, `diagnostics`, `symbol_history`). The new views SHALL follow the same per-scope `UNION ALL` pattern, the same `(scope, id)` composite-uniqueness convention, and the same `Views.PerScopeBlockTemplates` registration that the existing five views use.

The view shapes:

- `v_annotations(scope, id, symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)` — one row per indexed annotation (C# attribute, XAML attached property, future plugin-defined flavor). `flavor` discriminates the source language / framework. `args_json` is raw TEXT; agents who need substring search over arguments SHOULD prefer the `find_by_annotation` curated tool (FTS5-indexed) and use this view for compositional queries (joins / aggregations) instead.
- `v_diagnostics(scope, id, symbol_id, file_id, severity, severity_name, code, message, line, column_number)` — one row per Roslyn diagnostic. `severity` is the raw integer (matching `Microsoft.CodeAnalysis.DiagnosticSeverity`: 0=Hidden, 1=Info, 2=Warning, 3=Error). `severity_name` is the convenience text mapping (`hidden` / `info` / `warning` / `error`) computed via CASE. `symbol_id` is nullable (some diagnostics — e.g. unused-using directives — don't fall inside any indexed declaration). `column_number` renames the underlying `col` to avoid the SQL-reserved bare `column`.
- `v_history(scope, symbol_id, last_commit_sha, last_author, last_authored_at, line_count, blamed_content_sha)` — one row per symbol with cached git-blame metadata. `last_authored_at` is Unix-millis (matches `v_files.last_indexed_at` and `v_scopes.last_indexed_at`); agents needing ISO-8601 use `datetime(last_authored_at / 1000, 'unixepoch')`. Empty when the server runs with `--no-history` or against an environment without git on PATH.

The new views SHALL appear in `Views.All` (the curated descriptor list returned by `describe_schema`) with the same column-by-column documentation depth as the existing five.

#### Scenario: v_annotations exposes the indexed annotation set
- **GIVEN** an indexed scope containing one C# class decorated with `[Obsolete("use Foo")]` and one XAML element with `Grid.Row="2"`
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT name, flavor, args_json FROM v_annotations ORDER BY name"`
- **THEN** the result contains two rows: one with `name = 'Obsolete'`, `flavor = 'csharp-attribute'`, `args_json` containing the literal `"use Foo"`; one with `name = 'Grid.Row'`, `flavor = 'xaml-attached-property'`, `args_json` containing `2`

#### Scenario: v_annotations joins to v_symbols
- **WHEN** the agent invokes `query_graph` with
  ```sql
  SELECT s.fqn FROM v_annotations a
  JOIN v_symbols s ON s.id = a.symbol_id AND s.scope = a.scope
  WHERE a.name = 'Obsolete' AND s.is_type = 1
  ```
- **THEN** the result lists the FQN of every type-kind symbol decorated with `[Obsolete]`, joined via the `(scope, id)` composite key

#### Scenario: v_diagnostics maps severity integer to text
- **GIVEN** a scope where a public class symbol has one diagnostic stored at `severity = 2`
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT severity, severity_name FROM v_diagnostics WHERE code = 'CS0612'"`
- **THEN** the row reads `severity = 2`, `severity_name = 'warning'`

#### Scenario: v_diagnostics with nullable symbol_id
- **GIVEN** a diagnostic whose source span doesn't fall inside any indexed declaration (e.g. an unused-using on a using directive at file scope)
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT code, symbol_id FROM v_diagnostics WHERE symbol_id IS NULL"`
- **THEN** the result includes that row with `symbol_id = NULL`; an INNER JOIN against `v_symbols` would silently drop it (LEFT JOIN preserves it)

#### Scenario: v_history shape against an empty history table
- **GIVEN** a scope whose `symbol_history` table is empty (e.g. test fixture, `--no-history` mode)
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT scope, symbol_id, last_commit_sha, last_authored_at FROM v_history LIMIT 5"`
- **THEN** the call succeeds with `row_count = 0`; the response's `columns` array carries the four named columns with the documented types

#### Scenario: v_history joins to v_symbols when populated
- **GIVEN** a scope with at least one populated `symbol_history` row referencing a symbol whose `fqn` is `Sample.Foo.Bar`
- **WHEN** the agent invokes `query_graph` with
  ```sql
  SELECT s.fqn, h.last_author, h.last_authored_at FROM v_history h
  JOIN v_symbols s ON s.id = h.symbol_id AND s.scope = h.scope
  WHERE s.fqn = @fqn
  ```
- **THEN** the result row joins the history record to the symbol via `(scope, symbol_id)`, returning the author + Unix-millis timestamp

### Requirement: Typed exception type for SQLite corruption

The storage assembly SHALL expose a public `GraphStoreCorruptedException` type so any layer that wants to translate SQLite corruption into a typed exception (now or in the future) has a stable contract to throw. The exception SHALL carry:
- `ScopeId` (string) — the scope id of the store that surfaced the error
- `InnerSqliteException` (`SqliteException`) — the original SQLite exception, preserved unmodified

The current implementation does NOT wrap `SqliteException` at the storage boundary — `SqliteGraphStore` lets raw `SqliteException` propagate. The dispatch layer (`ScopedExecution`, per `Reactive integrity check on corruption suspicion` below) recognises both forms (`SqliteException` with `SqliteErrorCode is 11 or 26` AND `GraphStoreCorruptedException`) via `CorruptionGuard.IsCorruptionError`, so the user-facing behaviour — "corrupt DB → first call fails → subsequent calls return the degraded short-circuit" — is identical regardless of which exception form surfaces. The type is retained as the typed-throw contract for callers (production or test) that want to surface corruption explicitly without depending on `SqliteException`-specific error codes.

#### Scenario: GraphStoreCorruptedException type is exposed
- **WHEN** a consumer of the storage assembly references `DevBitsLab.Mcp.SourceGraph.Storage.GraphStoreCorruptedException`
- **THEN** the type is publicly accessible, derives from `Exception`, and exposes `ScopeId : string` and `InnerSqliteException : SqliteException` properties; the constructor signature is `(string scopeId, SqliteException inner)`

#### Scenario: SQLITE_CORRUPT propagates as raw SqliteException
- **GIVEN** a `SqliteGraphStore` whose underlying file has been physically corrupted (random bytes overwritten at a SQLite page boundary)
- **WHEN** any read method (e.g. `FindSymbolsAsync`) is called
- **THEN** the method throws `SqliteException` with `SqliteErrorCode == 11`; the exception is NOT wrapped by the storage layer; the dispatch layer's `CorruptionGuard.IsCorruptionError` recognises it and routes to the verification path

#### Scenario: Other SQLite errors propagate unchanged
- **GIVEN** a `SqliteGraphStore` whose underlying connection raises `SqliteException { SqliteErrorCode = 5 (SQLITE_BUSY) }` on a write
- **WHEN** the call surfaces the exception
- **THEN** the original `SqliteException` propagates to the caller; `CorruptionGuard.IsCorruptionError` returns `false` for this exception so the dispatch layer does NOT run the verification path

### Requirement: Reactive integrity check on corruption suspicion

`ScopedExecution` SHALL catch any exception flagged by `CorruptionGuard.IsCorruptionError` (raw `SqliteException` with `SqliteErrorCode is 11 or 26`, or `GraphStoreCorruptedException`) from any tool body before propagating it. On catch, it SHALL run `IGraphStore.IntegrityCheckAsync` (which executes `PRAGMA integrity_check` AND the FTS5 integrity-check) on the affected scope's store and dispatch on the result:

- **Integrity check returned `"ok"`** (false alarm — the corruption error was transient): emit a heal event with `kind = "corruption-suspected-but-clean"`, `ok = true`, `details = "integrity_check passed; treating as transient"`. Rethrow the original exception so the agent's call still fails. Do NOT mark the scope `degraded`.

- **Integrity check returned a non-`"ok"` string** (corruption confirmed): emit a heal event with `kind = "corruption-detected"`, `ok = true`, `details = $"integrity_check failed: {result}"`. Mark the scope `degraded` with `status_message = $"corruption detected: {result}; call repair_scope mode=rebuild"`. Rethrow the original exception. If the autonomous-rebuild env var is enabled (per `Autonomous corrupt-DB rebuild gated by env var` in the `mcp-tools` capability), additionally fire the rebuild on a background task before rethrow.

- **Integrity check itself threw** (the DB is so broken the check can't complete): log at warning level. Emit a heal event with `kind = "corruption-detected"`, `ok = false`, `details = $"integrity_check itself failed: {ex.Message}"`. Mark the scope `degraded` with the same details. Rethrow the original exception.

The dispatch SHALL execute synchronously inside the `ScopedExecution` catch — the verification adds wall-clock to the failed call's response time (typically single-digit seconds for the integrity check), but the agent already saw the call fail; the structured `degraded` state is what subsequent calls benefit from.

Subsequent tool calls against a scope that this dispatch marked `degraded` SHALL hit the existing `degraded` short-circuit in `ScopedExecution.WaitForReadyAsync` and return the structured diagnostic without contacting SQLite again.

#### Scenario: False alarm — clean integrity check after suspicion
- **GIVEN** a tool call throws a corruption-flagged exception for scope `backend`, but `IntegrityCheckAsync` against `backend`'s DB returns `"ok"`
- **WHEN** `ScopedExecution` catches the exception and runs verification
- **THEN** `heals.jsonl` contains one line with `kind = "corruption-suspected-but-clean"`, `scope = "backend"`, `ok = true`; the `backend` registry row is NOT modified (still `"ok"`); the original exception propagates to the agent; the next tool call against `backend` is dispatched normally (no degraded short-circuit)

#### Scenario: Confirmed corruption marks scope degraded
- **GIVEN** a tool call throws a corruption-flagged exception for scope `backend` and `IntegrityCheckAsync` returns the string `"*** in database main *** Page 42: invalid header"`
- **WHEN** `ScopedExecution` catches and verifies
- **THEN** `heals.jsonl` contains one line with `kind = "corruption-detected"`, `ok = true`, `details = "integrity_check failed: *** in database main *** Page 42: invalid header"`; the `backend` registry row is updated to `Status = "degraded"` with `StatusMessage = "corruption detected: *** in database main *** Page 42: invalid header; call repair_scope mode=rebuild"`; the original exception propagates; the next tool call returns the degraded short-circuit without touching SQLite

#### Scenario: Integrity check itself fails
- **GIVEN** a tool call throws a corruption-flagged exception and `IntegrityCheckAsync` itself throws (e.g. file unreadable)
- **WHEN** `ScopedExecution` catches and the verification call also throws
- **THEN** `heals.jsonl` contains one line with `kind = "corruption-detected"`, `ok = false`, `details` carrying the verification exception's message; the registry row is marked `degraded` with the same details; the original exception propagates to the agent

