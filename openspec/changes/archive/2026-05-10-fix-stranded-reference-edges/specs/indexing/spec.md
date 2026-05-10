## ADDED Requirements

### Requirement: Self-heal stranded reference edges
The indexer SHALL detect and recover from a "zombie" file state where pass 1's `ClearFileOutgoingAsync` cleared a file's outgoing refs/edges but pass 2's reference walk did not repopulate them. On every `IndexCoreAsync` call, the pass-1 unchanged-file skip path SHALL bypass the skip when the file declares one or more symbols but the store reports zero outgoing pass-2 artifacts (refs AND edges) for that file. The bypassed file SHALL be re-walked in pass 2 so its refs/edges are regenerated.

The integrity check SHALL be implemented via a new storage method `IGraphStore.HasOutgoingReferencesAsync(long fileId, CancellationToken ct)` that returns `true` when at least one outgoing-reference row exists for the given file OR at least one outgoing edge originates from a symbol declared in that file. Checking edges as well as refs avoids spurious re-walks of files that legitimately produce zero refs but emit edges from member signatures (`uses-type`, `inherits`, `implements-member`). Default implementation SHALL return `true` so existing storage implementations preserve today's behaviour.

#### Scenario: Stranded file is re-walked on next index
- **GIVEN** a file `F` whose row, declared symbols, and content SHA exist in the store, but for which `refs.file_id = F.id` has zero rows AND no edges originate from symbols declared in `F`
- **WHEN** `IndexCoreAsync` runs against a workspace containing `F` whose on-disk SHA matches the stored SHA (no edit since last index)
- **THEN** pass 1's "unchanged file" skip is bypassed for `F` (because `HasOutgoingReferencesAsync(F.id) == false` while `_keysByFileId[F.id].Any() == true`), pass 2 walks `F`, and at least one outgoing-reference row appears for `F` after the call returns

#### Scenario: Healthy unchanged file is still skipped
- **GIVEN** a file `F` with declared symbols and at least one outgoing-reference row OR at least one outgoing edge from a symbol declared in `F`
- **WHEN** `IndexCoreAsync` runs against a workspace containing `F` whose on-disk SHA matches the stored SHA
- **THEN** pass 1's "unchanged file" skip applies as today; pass 2 does NOT walk `F`; the EXISTS-style integrity check fires once with negligible cost

#### Scenario: Symbol-less file does not loop on the integrity check
- **GIVEN** a file `F` with no declared symbols (an empty file or one containing only `using` directives)
- **WHEN** `IndexCoreAsync` runs against a workspace containing `F` whose SHA matches the stored SHA
- **THEN** the integrity check's "file declares symbols" guard short-circuits — `_keysByFileId[F.id]` is empty so the check doesn't fire — and pass 2 does not re-walk; the file is skipped as today

#### Scenario: Recovery is logged
- **WHEN** the integrity check forces pass 2 to walk a file that would have been SHA-skipped
- **THEN** the indexer emits an info-level log entry of the form `"Re-walking references for {Path}: file SHA matches but no outgoing references in store …"` so operators can observe recoveries; healthy installs never see this line

### Requirement: Pass 2 file-walk failures don't abort the loop
The indexer SHALL wrap each per-file body of pass 2's reference walk in a try/catch so that an exception thrown while walking one file does not abort pass 2 for the remaining files. Cancellation (`OperationCanceledException`) SHALL still propagate. Other exceptions SHALL be logged at warn level with the file path and exception detail; the failed file's outgoing edges remain cleared this round and will be re-walked on the next index via the integrity check above.

#### Scenario: One file's walk throws; other files' walks complete
- **GIVEN** a pass-2 batch of three changed files where the second file's syntax tree triggers an exception during the descendant-node walk (e.g. a transient compilation gap, a symbol-resolution failure)
- **WHEN** pass 2 iterates the three files
- **THEN** the first file's references are inserted, the second file's exception is caught and logged at warn level with the file path, and the third file's references are inserted; pass 2 completes without rethrowing

#### Scenario: Cancellation propagates
- **WHEN** pass 2 is iterating files and the supplied `CancellationToken` is signaled, raising `OperationCanceledException` in a per-file body
- **THEN** the catch handler rethrows so the cancellation surfaces to the caller; partial state from earlier files in the batch is left as-is

## MODIFIED Requirements

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
  whose SHA matches the stored value AND has either zero declared symbols
  or at least one outgoing-reference row in the store is skipped in pass 1
  (per the self-heal integrity check); files that match the SHA but have
  declared symbols with zero outgoing references are bypassed and re-walked
