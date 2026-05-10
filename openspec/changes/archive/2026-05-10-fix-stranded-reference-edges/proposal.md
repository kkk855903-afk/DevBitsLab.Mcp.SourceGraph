## Why

The Roslyn incremental indexer can leave a file in a "zombie" state where its outgoing references are cleared but never repopulated. Once a file is in this state, the SHA-skip optimisation in pass 1 (per the `Hydrated symbol/file maps reused across reindexes` scenario in `openspec/specs/indexing/spec.md`) keeps it stranded indefinitely — every subsequent index sees the SHA matches the stored value and skips both passes, preserving the empty-references state.

We reproduced this against `src/.../Tools/HistoryTools.cs` in the working tree:

| State | `find_references(ToolMetrics.TrackAsync)` hits in `GraphTools.cs` | …in `HistoryTools.cs` |
|---|---:|---:|
| Existing DB | 32 | **0** |
| `rm -rf .sourcegraph/scopes/*.db` + restart | 30 | **6** ✓ |

The file is fully indexed at the symbol level (`list_symbols_in_file` returns its 5 declared symbols). Only the call/ref edges from the file's body are missing — and the SHA-skip path in [RoslynIndexer.cs:345-358](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:345-358) prevents recovery on subsequent runs because the file's content-hash matches the stored hash.

The most likely trigger is the live-index pipeline (`LiveIndexService` + `SolutionWatcher`): a file edit fires `ClearFileOutgoingAsync` in pass 1, then pass 2 silently fails or never reaches `BulkInsertReferencesAsync` (transient compilation gap, partial parse during a half-saved file, watcher debounce edge case). The file's stored SHA is the post-edit value, the references table has no rows for that file, and the next index sees "unchanged + present in `_keysByFileId`" → skip both passes.

The bug is invisible to users until they call `find_references` against a symbol that should have call sites in the stranded file — at which point the missing rows look like the symbol was never referenced from there. Users who notice it have no documented recovery path beyond `rm -rf .sourcegraph` and a full re-index, which can take minutes for large repos.

## What Changes

- **Pass 1's "unchanged file" skip path gains a pass-2-intact integrity check.** Today the check is `unchanged && !fullReset && _keysByFileId.ContainsKey(fileId)` — match the SHA, in-memory map populated, skip both passes. After this change, the skip ALSO requires evidence that the file's pass-2 output is present in the store: at least one `refs` row whose `file_id = ?` OR at least one outgoing edge whose source symbol is declared in the file, when the file declares any symbols. Files that declare symbols but have zero refs AND zero outgoing edges fall through to pass 2 for re-walking.
- **A new `IGraphStore.HasOutgoingReferencesAsync(long fileId, CancellationToken ct)` storage method** returns true if at least one `refs` row exists for the given file OR at least one outgoing edge originates from a symbol declared in the file. Cheap query (an `EXISTS (refs by file_id) OR EXISTS (edges joined to symbols.file_id)` probe); fires once per unchanged file at index startup, runs against indexed columns. Checking edges in addition to refs avoids spurious re-walks of files that legitimately produce zero refs but emit edges from member signatures (`uses-type`, `inherits`, `implements-member`).
- **Counter-defence: pass 2's clear-then-walk dance is wrapped in a per-file try/catch.** When pass 2 throws partway through walking a file (parse error, transient compilation gap, etc.), the failure is logged with the file path and the indexer continues with the next file. The file's outgoing edges remain cleared this round — but the next index will re-attempt because `HasOutgoingReferencesAsync` returns false. The current behaviour silently aborts the entire pass 2 loop on the first thrown exception.
- **A regression test** that constructs the zombie state directly (insert symbols + file row, no references) and asserts that the next `IndexCoreAsync` call walks pass 2 for that file.
- **A diagnostic log line** (`info` level) when pass 1 forces pass 2 to run because of the integrity check: `"Re-walking references for {Path}: file SHA matches but no outgoing edges in store"`. Operators see a one-time recovery message; healthy installs never see it.

## Capabilities

### New Capabilities
<!-- None — this change tightens an existing capability's contract. -->

### Modified Capabilities

- `indexing`: The `Hydrated symbol/file maps reused across reindexes` requirement gains a "recovery" scenario: SHA-skip is allowed, but only when the file's outgoing references are demonstrably present. Files with declared symbols but zero outgoing-reference rows are re-walked. The existing scenario for "Server restart with an existing DB" picks up an additional clause documenting the integrity check.

## Impact

- **Code (small)**: One `if`-condition change in `IndexCoreAsync` pass 1 (the `unchanged` skip), one new `IGraphStore` method (`HasOutgoingReferencesAsync`), and a `try/catch` wrapping the pass-2 per-file body. ~30 lines of production code.
- **Spec**: One MODIFIED scenario on `indexing` capability documenting the integrity check.
- **Tests**: One new regression test in `IndexFixtureTests.cs` (or a new `StrandedReferenceEdgesRecoveryTests.cs` — design.md decides). Constructs the zombie state by inserting symbols + file row directly, calls `IndexCoreAsync`, and asserts pass 2 walked the file (verified by the presence of expected reference rows after).
- **Performance**: Each unchanged file gains one `EXISTS` query at index startup. The query hits an indexed `file_id` column; cost is sub-millisecond per file. For a 5000-file solution that's ~5s of additional startup time on cold start, dominated already by Roslyn workspace load.
- **Backwards compatibility**: Pure additive on the storage interface; existing implementations gain a default that returns `true` (maintains today's behaviour). The new integrity check only forces extra work when a real zombie file is detected — healthy installs see no behavioural change.
- **Recovery for users hit today**: After this change ships, users with stranded files don't need to wipe their DB — the next `serve` start (or next `LiveIndexService` re-walk) automatically detects and recovers. We document this in the change's release notes.
- **Out of scope**: Diagnosing the upstream cause (which silent failure in pass 2 produces the zombie state in the first place). The fix is defensive — it tolerates the failure and recovers — rather than identifying the originating bug. A follow-up change can investigate root causes if zombie files keep reappearing in production.
