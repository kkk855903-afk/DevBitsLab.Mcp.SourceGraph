# Storage

## Purpose

Persist the code graph (files, symbols, refs, edges) in a single SQLite file
with FTS5 full-text search, and expose query and write operations to the rest
of the system through `IGraphStore`.

## Requirements

### Requirement: Self-applying schema migrations
`SqliteGraphStore` SHALL apply the bundled schema on connect; if the on-disk
version is below `Schema.Version`, all data tables and triggers SHALL be
dropped and recreated from the embedded SQL.

#### Scenario: Open a DB on an older schema
- **WHEN** the `schema_version` table reports a value less than
  `Schema.Version` (currently `4`)
- **THEN** `EnsureSchemaAsync` runs `Schema.DropAll`, applies `Schema.V1` and
  `Schema.V2` from scratch, inserts the new version row, and logs
  `"On-disk graph schema is vOLD; rebuilding to vNEW"`

### Requirement: Stable symbol id by canonical key
`UpsertSymbolAsync` SHALL preserve a symbol's row id across successive calls
that share the same canonical key, updating the other columns in place.

#### Scenario: Same key, new line/col
- **WHEN** a symbol with canonical key `K` is upserted twice with different
  `start_line` / `signature` values
- **THEN** the row id returned by the second call equals the first; the
  row's other columns reflect the latest call (last-write-wins on
  `name`, `fqn`, `kind`, `file_id`, `start_line`, etc.)

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
`file_id = fileId` and the edges whose `src` lives in that file, leaving
the file's symbols and any incoming refs/edges intact.

#### Scenario: Reset a file's outgoing data before reindex
- **WHEN** `ClearFileOutgoingAsync(F)` is called as the first step of a live
  reindex of file `F`
- **THEN** all rows in `refs` with `file_id = F` are removed, all rows in
  `edges` whose `src` matches `(SELECT id FROM symbols WHERE file_id = F)`
  are removed, and no rows in `symbols` are touched

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

### Requirement: Attributes table with FTS over arguments
The schema SHALL include an `attributes(id, symbol_id, name, full_name, args_json, attribute_symbol_id)` table indexed on `(symbol_id)`, `(name)`, and `(attribute_symbol_id)`, plus an `attributes_fts` virtual table tokenising the synthesised `args_text` column.

#### Scenario: Trigram match on argument text
- **WHEN** `attributes_fts MATCH 'users-list'` is queried against a row whose `args_json` contains `"/api/users-list"`
- **THEN** that row is returned

### Requirement: find_by_attribute query API
`IGraphStore` SHALL expose `FindByAttributeAsync(name, argSubstring?, kindFilter?, limit)` returning `SymbolHit` rows whose attached attributes match the criteria.

#### Scenario: Strict name match plus wildcard arg match
- **WHEN** `FindByAttributeAsync("HttpGet", "users", null, 50)` is called against a graph with `[HttpGet("/api/users")]` on method `M`
- **THEN** `M` is in the result set

#### Scenario: Name match without arg constraint
- **WHEN** `FindByAttributeAsync("Obsolete", null, null, 50)` is called
- **THEN** every symbol carrying any `[Obsolete]` (with or without args) is returned

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
