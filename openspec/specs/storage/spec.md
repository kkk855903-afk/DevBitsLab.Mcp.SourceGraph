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
