## Context

The Roslyn incremental indexer is multi-pass:

1. **Pass 1 — phase A** ([RoslynIndexer.cs:300-360](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:300-360)): for each document, compute SHA, compare to stored. If unchanged AND `_keysByFileId` already contains the fileId AND `!fullReset` → `continue` (skip both pass 1's symbol upsert AND pass 2's reference walk for this file). If changed → `ClearFileOutgoingAsync` + add to `docsByChangedFile`.

2. **Pass 1 — phase B–E**: declare symbols, populate annotations, persist test-framework values, etc. (For files that fell through phase A.)

3. **Pass 2** ([RoslynIndexer.cs:529-650](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:529-650)): for each document in `docsByChangedFile`, walk syntax → emit reference rows + edge rows.

The SHA-skip is correct under the assumption "if SHA matches and the file's symbols are in the in-memory map, then the file's references on disk are also intact." That assumption breaks when pass 2 ever fails midway: pass 1's `ClearFileOutgoingAsync` already cleared the file's references, but pass 2 didn't repopulate them. The next index sees SHA match + symbols hydrated → skip → references stay zero forever.

Reproduced today against `src/.../Tools/HistoryTools.cs`: 0 references in the existing DB, 6 references after a wipe and re-index. The file declares 5 symbols visible in `list_symbols_in_file` but contributes zero rows to `find_references` results — invisible-to-grep but present-as-symbol, the classic zombie shape.

Most likely originating event: `LiveIndexService` re-walking `HistoryTools.cs` partway through one of the recent edits (the polish-tool-output-markdown sweep, the leaf change), with pass 2 silently aborting before `BulkInsertReferencesAsync` (a transient compilation gap, a parse error on an in-progress save, an exception in a single-file walk that bubbled out of pass 2's loop). Diagnosing the originating event is hard: no log capture, no reproducer, possibly already-fixed by other refactors. The fix below is **defensive** — it ensures the indexer recovers from the zombie state on its own, regardless of how it got there.

## Goals / Non-Goals

**Goals:**

- Pass 1's "unchanged file" skip path verifies that the file's references are demonstrably intact before skipping pass 2. If a file declares symbols but has zero outgoing-reference rows, the skip is bypassed and pass 2 walks the file.
- Pass 2's per-file body is wrapped in try/catch so a single file's walk failure doesn't abort the entire pass-2 loop. Failed files log at warn-level with the path; the file's edges remain cleared this round and the next index re-attempts via the integrity check above.
- A regression test pins the recovery behaviour: build a zombie state in memory (insert symbols + file row, no references), call `IndexCoreAsync`, assert that the file is re-walked and references appear.
- A diagnostic log line surfaces every recovery so operators can see "this file was zombied; recovered" — useful for confirming the bug is rare, and for noticing if some upstream issue is producing zombies regularly.
- Existing healthy installs see no behavioural change. The `EXISTS` query is sub-millisecond per file; the integrity check fires once per unchanged file at index startup.

**Non-Goals:**

- Diagnosing the originating cause of the zombie state. We don't know exactly which sequence (live re-index race, transient compile error, exception in pass 2's walk) put `HistoryTools.cs` in this state, and reproducing it is hard. The fix is defensive — make the indexer self-correcting — rather than identifying and patching the originating bug.
- Making pass 2 atomic with pass 1's clear (option 2 from the discussion). That would require restructuring the pass-1/pass-2 flow into a single transaction, which touches many more code paths and isn't worth the complexity for a defensive fix.
- Storing a separate "references-extracted" content-hash alongside the file SHA (option 3 from the discussion). Adds storage state for a marginal correctness gain over the EXISTS check.
- Changing the wire-level shape of any tool response. Users who hit the bug will see their `find_references` results change after this fix lands (the missing rows reappear), but tool semantics are unchanged.

## Decisions

### Decision 1 — Integrity check on the unchanged-file skip path

The skip condition becomes:

```csharp
if (unchanged && !fullReset && _keysByFileId.ContainsKey(fileId)
    && (!_keysByFileId[fileId].Any()  // file declares no symbols → no refs expected
        || await _store.HasOutgoingReferencesAsync(fileId, ct).ConfigureAwait(false)))
{
    // DB and in-memory map are already consistent for this file — skip entirely.
    continue;
}
```

Files with declared symbols but zero outgoing refs fall through to pass 2 for re-walking. Files with no declared symbols don't need re-walking (an empty file has nothing to reference). The `HasOutgoingReferencesAsync` query is `SELECT EXISTS (SELECT 1 FROM symbol_references WHERE file_id = ? LIMIT 1)` against the existing index on `file_id`.

**Rationale**: catches the zombie state with a single cheap query per unchanged file. No restructuring of the pass-1/pass-2 flow; no new persistent state.

**Alternatives considered**:
- Restructure pass 2 to wrap pass 1's clear in a transaction. Cleaner, but touches the pass-1 flow much more invasively. Defer.
- Store a separate `references_extracted_at` column or hash. Adds persistent state for marginal correctness over the EXISTS check.
- Ship a "force re-walk" admin tool. Doesn't help users who don't know they have a zombie file.

### Decision 2 — Pass 2 per-file try/catch

The per-file body in pass 2's loop ([RoslynIndexer.cs:538+](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:538)) gets wrapped:

```csharp
try
{
    // existing per-file walk (tree, model, descendant nodes, refBatch, edgeBatch, BulkInsert*)
}
catch (OperationCanceledException) { throw; }
catch (Exception ex)
{
    _logger.LogWarning(ex,
        "Pass 2 walk failed for {Path}; file's outgoing edges remain cleared this round and will be re-attempted on the next index",
        path);
}
```

**Rationale**: stops one file's failure from killing the rest of pass 2. Combined with Decision 1's integrity check, the next index automatically re-attempts. Without this catch, a thrown exception in any file's walk silently terminates the loop and several files (whichever come after the failure in iteration order) end up zombied.

`OperationCanceledException` rethrows so user-driven cancellation works. Other exceptions log + continue.

### Decision 3 — Diagnostic log line on recovery

When the integrity check forces pass 2 to run for a file that would have been SHA-skipped:

```csharp
_logger.LogInformation(
    "Re-walking references for {Path}: file SHA matches but no outgoing edges in store (likely zombied by a prior incomplete indexing pass; recovering)",
    path);
```

**Rationale**: surfaces the fact that recovery happened without making it look like an error (info-level, not warn). Healthy installs never see this line; a one-time message after upgrade is expected as zombied files heal. Repeated messages on the same file across runs would indicate the originating bug is still firing — useful signal for the next investigation.

### Decision 4 — Test pattern: construct the zombie state directly

The regression test bypasses Roslyn entirely:

```csharp
[Fact]
public async Task IndexCoreAsync_zombieFile_isReWalked()
{
    // Arrange: build a zombie state. File row + symbols, but no symbol_references for that file.
    // Use direct store inserts to skip Roslyn's contribution.
    await store.UpsertFileAsync(zombiePath, sha, ts, isGenerated: false);
    await store.UpsertSymbolAsync(/* fileId from above, fqn, kind, ... */);
    // Deliberately do NOT insert any symbol_references for this fileId.

    // Act: rerun IndexCoreAsync — same SHA, file appears unchanged, but pass 2 should still walk.
    await indexer.IndexCoreAsync(documents, ct);

    // Assert: post-condition has at least one reference row for the zombie file.
    var refCount = await store.CountReferencesByFileAsync(zombiePath);
    refCount.Should().BeGreaterThan(0);
}
```

**Rationale**: precise reproduction of the zombie state without depending on a specific sequence of edits + watcher events to put a real file there. The assertion pins the integrity-check + re-walk behaviour; the originating cause is still unaddressed but the fix's recovery property is verified.

### Decision 5 — Storage interface change: pure addition

`IGraphStore` gains:

```csharp
/// <summary>
/// True when the store has at least one symbol_references row whose file_id matches
/// <paramref name="fileId"/>. Used by the indexer's pass-1 integrity check to detect
/// "zombied" files whose references were cleared but never repopulated.
/// </summary>
Task<bool> HasOutgoingReferencesAsync(long fileId, CancellationToken ct = default);
```

Default implementation in `IGraphStore` returns `Task.FromResult(true)` so existing implementations keep today's behaviour (skip every unchanged file, no integrity check). Real implementations (`SqliteGraphStore`) override with the EXISTS query.

**Rationale**: zero-impact addition for any in-tree or third-party `IGraphStore` implementations that don't immediately implement the override. The default biased toward "trust the SHA-skip" is the existing behaviour; only stores that opt into the check participate in the recovery.

## Risks / Trade-offs

- **[Risk] Performance regression on cold start.** Each unchanged file gains one EXISTS query. For a 5000-file solution: 5000 queries, ~0.5ms each on a warm SQLite cache → ~2.5s additional startup. Cold cache could be ~10s. Within the existing index startup time (10–60s for a non-trivial solution). Acceptable.

- **[Risk] False positives — files that legitimately have no outgoing references** (purely declarative files, partial classes whose body lives elsewhere, generated stubs). Re-walking them is harmless; pass 2 emits zero rows again, the EXISTS query returns false next time, infinite re-walk loop. → Mitigation: only force re-walk when the file declares symbols (`_keysByFileId[fileId].Any()`). A symbol-bearing file that consistently has zero outgoing references would still loop — but such files are rare in practice (every `using` is a reference, every type use, every method call), and the per-file cost of one wasted pass-2 walk is small (~10ms for a typical file).

- **[Risk] The originating cause keeps producing zombies faster than the fix recovers them.** → Mitigation: the info-level log line on every recovery makes the rate observable. If we see repeated recoveries on the same files, that's a signal to investigate the upstream cause. Current evidence is one anecdotal report (HistoryTools.cs) from a complex multi-edit session — likely an artefact of that specific cadence, not a routine occurrence.

- **[Risk] `try/catch` swallowing real bugs** (typo in the indexer, broken Roslyn API contract). → Mitigation: warn-level log records the exception type + message + stack trace. CI would catch a systematic regression because every test fixture would hit the catch on every run. Production catches the long-tail edge cases without the indexer falling over.

- **[Trade-off] Defensive vs. root-cause fix.** This change makes the indexer self-correcting; it doesn't identify why pass 2 ever fails partway through. Accepted — defensive correctness is more valuable for this class of bug than knowing the originating event, especially since the originating event is hard to reproduce.

## Migration Plan

This is a code-only change with no data migration:

1. **Land the storage method first** — `IGraphStore.HasOutgoingReferencesAsync` with the default `Task.FromResult(true)` body. CI green; no behaviour change yet.
2. **Override in `SqliteGraphStore`** with the EXISTS query. CI green; the method works but no callers yet.
3. **Update pass 1's unchanged-file skip path** to call `HasOutgoingReferencesAsync` and use the result. Land the diagnostic log line.
4. **Wrap pass 2's per-file walk in try/catch.** Land the warn-level log line.
5. **Add the regression test.** Verify it fails before the fix and passes after.
6. **Documentation note in `README.md`** under "Resource limits and tunables" or as a brief "Recovery from incomplete indexing" subsection: mention that the indexer self-heals from incomplete prior passes on the next start; no operator action needed.

**Rollback strategy**: revert the per-step commits in reverse order. The storage method's default body is a no-op for installs that don't ship the override, and the integrity check defaults to the existing behaviour when the EXISTS query returns true.

## Open Questions

- **Should the diagnostic log line carry a count** (`Re-walking references for {N} zombied file(s) since startup`) instead of one line per file? Per-file is more useful for debugging; aggregate is friendlier in stable steady-state. Probably keep per-file for now; aggregate later if log volume becomes a complaint.
- **Should pass 2's catch handler also count failures and emit a metric?** OTel `mcp.indexer.pass2_failures` would surface the rate of originating failures distinct from the count of recoveries. Worth doing if the metric story stays consistent. Defer; the change is already big enough.
- **`HasOutgoingReferencesAsync` vs. an aggregate `GetIntegrityStateAsync` returning multiple checks** (refs, annotations, edges). The integrity check could expand later. For now, refs are the only known zombie surface. Single method is sufficient; refactor if more checks land.
