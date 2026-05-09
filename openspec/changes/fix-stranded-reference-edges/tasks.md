## 1. Storage interface: `HasOutgoingReferencesAsync`

- [ ] 1.1 Add `Task<bool> HasOutgoingReferencesAsync(long fileId, CancellationToken ct = default)` to `src/DevBitsLab.Mcp.SourceGraph.Storage/IGraphStore.cs` with a default body of `Task.FromResult(true)`. XML doc: explains the integrity-check use case so future store implementations know the contract.
- [ ] 1.2 Override the method in `src/DevBitsLab.Mcp.SourceGraph.Storage/SqliteGraphStore.cs` with `SELECT EXISTS (SELECT 1 FROM symbol_references WHERE file_id = ? LIMIT 1)`. The query hits the existing index on `file_id` (verify in `Schema.cs` or via `EXPLAIN QUERY PLAN` — there should already be one).
- [ ] 1.3 Build clean (`dotnet build`). No callers yet — this is a no-op pass.

## 2. Indexer: integrity check on the unchanged-file skip path

- [ ] 2.1 In `src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs`, locate the pass-1 unchanged-file skip block (~line 345). Extend the skip condition to call `HasOutgoingReferencesAsync(fileId, ct)` when the file has declared symbols (`_keysByFileId.TryGetValue(fileId, out var keys) && keys.Count > 0`). Skip only when the EXISTS query returns true OR the file has no declared symbols.
- [ ] 2.2 When the integrity check forces a re-walk (`unchanged && symbol-bearing && no refs in store`), emit an info-level log line: `"Re-walking references for {Path}: file SHA matches but no outgoing edges in store (likely zombied by a prior incomplete indexing pass; recovering)"`. Use the structured logging conventions already in `RoslynIndexer.cs` (`_logger.LogInformation(...)`).
- [ ] 2.3 Confirm the existing unchanged-file skip path's `continue` is reached when the EXISTS query returns true — i.e. the integrity check is purely additive, doesn't change behaviour for healthy files.

## 3. Indexer: pass-2 per-file try/catch

- [ ] 3.1 In `RoslynIndexer.cs`, locate pass 2's per-file walk loop (~line 538). Wrap the body in a try/catch:
  - `OperationCanceledException` rethrows.
  - Other exceptions log at warn level with `_logger.LogWarning(ex, "Pass 2 walk failed for {Path}; file's outgoing edges remain cleared this round and will be re-attempted on the next index", path)`.
- [ ] 3.2 Verify the existing `BulkInsertReferencesAsync` and `BulkInsertEdgesAsync` calls remain inside the try block so a partial walk doesn't commit half a file's references.

## 4. Regression test

- [ ] 4.1 Add `tests/DevBitsLab.Mcp.SourceGraph.Tests/StrandedReferenceEdgesRecoveryTests.cs`. Test pattern:
  - Cold-index `tests/fixtures/Sample.sln` once via `RoslynIndexer.IndexSolutionOnceAsync` (same harness as `TabularRenderingTests` / `ProgressReportingTests`).
  - Pick one indexed file (e.g. `Calculator.cs`).
  - Construct the zombie state by directly deleting the file's `symbol_references` rows via the store (or a new test-only helper if needed).
  - Verify the file is in the zombie state: `find_references` against a symbol that lives in `Calculator.cs` should miss those call sites.
  - Run `IndexCoreAsync` (or `ReloadAndIndexAllAsync`) again with the on-disk file unchanged.
  - Assert that the references reappear.
- [ ] 4.2 Negative-path test: a file with declared symbols and existing references should NOT trigger the re-walk path. Verify by counting pass-2-walk events through a logging capture, or by asserting the count of `symbol_references` rows didn't change after a no-op re-index.
- [ ] 4.3 Pass-2 catch test: arrange a per-file walk failure (mock store that throws on `BulkInsertReferencesAsync` for one file), confirm the loop continues for subsequent files, and the warn-level log line is emitted with the failed file's path.

## 5. Verification + spec sync

- [ ] 5.1 `dotnet build` clean.
- [ ] 5.2 `dotnet test` — full suite green, including the new regression tests.
- [ ] 5.3 End-to-end smoke: re-create the zombie state on `tests/fixtures/Sample.sln` (delete a file's references via SQL), restart `sourcegraph-mcp serve`, drive a real `find_references` for a symbol in that file, confirm the call sites reappear.
- [ ] 5.4 Run `openspec validate fix-stranded-reference-edges --strict`.

## 6. Documentation

- [ ] 6.1 In `README.md`, add a brief "Recovery from incomplete indexing" subsection (or extend an existing section) noting that the indexer self-heals from incomplete prior passes on the next start; no operator action needed. Mention that the recovery emits an info-level log line on the affected files.

## 7. Spec sync (archive)

- [ ] 7.1 Run `openspec archive fix-stranded-reference-edges --yes`. Confirm 2 ADDED requirements (Self-heal stranded reference edges, Pass 2 file-walk failures don't abort the loop) and 1 MODIFIED scenario (Server restart with an existing DB) land in `openspec/specs/indexing/spec.md` cleanly.
