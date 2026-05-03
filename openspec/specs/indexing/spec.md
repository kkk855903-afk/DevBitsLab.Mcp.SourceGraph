# Indexing

## Purpose

Provide a Roslyn-backed indexer that turns a .NET solution into a queryable
code graph (symbols, references, calls/inherits/implements edges) and keeps
its in-memory maps and on-disk store consistent across cold runs and live
edits.

## Requirements

### Requirement: Cold index of a solution
The indexer SHALL walk every C# document in the loaded solution exactly once
and persist its declarations, references, and edges to the graph store.

#### Scenario: Index a fresh solution end-to-end
- **WHEN** `sourcegraph-mcp index <solution>` is invoked against a solution
  whose graph DB is empty or absent
- **THEN** every regular `.cs` document with `File.Exists(path) == true` is
  walked, an `IndexResult` is returned with `FilesIndexed > 0`,
  `SymbolsIndexed > 0`, and the totals reported by `GetStatsAsync` match the
  per-pass counts

### Requirement: Stable symbol identifiers across edits
The indexer SHALL upsert symbols by canonical key (Roslyn
`DocumentationCommentId`) so the integer `id` remains stable across edits and
incoming refs from other files do not get orphaned.

#### Scenario: Edit a defining file
- **WHEN** a file containing symbol `S` is edited and the live indexer
  reprocesses it
- **THEN** `S`'s row in `symbols` is updated in place via
  `INSERT … ON CONFLICT(canonical_key) DO UPDATE`, its integer `id` stays the
  same, and refs from other unchanged files that target `S.id` remain valid

#### Scenario: Remove a symbol from its source file
- **WHEN** a previously indexed symbol is no longer declared anywhere in its
  file across all per-project iterations
- **THEN** `DeleteSymbolsForFileNotInAsync` removes that symbol row and the
  refs/edges that targeted it in the same transaction

### Requirement: Hydrate in-memory maps from the store on startup
The indexer SHALL populate `_symbolIdByKey`, `_keysByFileId`, and
`_fileIdByPath` from the existing graph DB on the first `IndexCoreAsync` call
in a process (or after `fullReset`).

#### Scenario: Server restart with an existing DB
- **WHEN** `sourcegraph-mcp serve` starts against a solution whose
  `.sourcegraph/graph.db` was populated by a prior cold index
- **THEN** the indexer reads every `(canonical_key, id, file_id)` from
  `symbols` and `(path, id)` from `files`, logs
  `"Hydrated N symbol(s) and M file(s) from graph store"`, and every file
  whose SHA matches the stored value is skipped in pass 1

### Requirement: Multi-target and linked-file iterations don't double-count
The indexer SHALL emit refs and edges from at most one document per fileId
even when the loaded solution exposes the same source path multiple times
(multi-target frameworks, linked files, shared projects).

#### Scenario: A file targeted by multiple TFMs
- **WHEN** the solution multi-targets such that path `P` produces N
  documents
- **THEN** pass 1 accumulates the union of declared canonical keys across
  all N iterations before reconciling, and pass 2 walks exactly one of the N
  documents

### Requirement: Robust file reads against editor save races
The indexer SHALL skip a file gracefully and rely on the next watcher event
when a transient `IOException` interrupts the file read (e.g., a 0-byte view
during an editor save).

#### Scenario: Read fails mid-batch
- **WHEN** `File.ReadAllBytesAsync` or `File.ReadAllTextAsync` throws
  `IOException` while building the changed-file batch
- **THEN** the path is logged at debug, omitted from the current batch, and
  no partial state is committed; the next FSW event for that path retries
  the read
