## ADDED Requirements

### Requirement: repair_scope tool

The server SHALL expose a `repair_scope` MCP tool that takes a destructive (or potentially destructive) action against a single named scope.

The tool SHALL accept:
- `scope` (string, required) — a single scope id; `"*"` and comma-separated lists SHALL be rejected with a structured `bad_scope` diagnostic. Repair acts at scope grain; the destructive intent must be explicit and singular.
- `mode` (string, default `"minimal"`) — one of `"minimal"` or `"rebuild"`. Other values SHALL be rejected with a structured `bad_argument` diagnostic.

The tool SHALL ship `structuredContent` of shape:
```
{ scope: string, mode: string, before_status: string, after_status: string, elapsed_ms: long, message: string }
```

`minimal` mode SHALL:
1. Capture `before_status` from the registry.
2. Run `IGraphStore.IntegrityCheckAsync`. If the result is not `"ok"`, return immediately with `after_status = before_status`, `message = "integrity_check failed: <result>; call repair_scope mode=rebuild"`. No mutation occurs.
3. Otherwise, call `IEmbeddingsStore.PruneOrphanedAsync()` (returning a count of pruned rows for the message).
4. Re-run the bounded-retry workspace-open path against the scope (per `Bounded retry on initial workspace open`). 
5. Return with `after_status` from the post-retry registry row, `message = "ok; pruned {N} orphan embeddings; reopened workspace"` (or "...workspace open failed after retries" on final failure).

`rebuild` mode SHALL:
1. Capture `before_status`.
2. Move the scope's DB file from `<repo>/.sourcegraph/scopes/<id>.db` to `<repo>/.sourcegraph/orphans/<id>-rebuild-<utc-iso>.db` (with `:` replaced by `-`). The orphans directory SHALL be created lazily. If the scope DB file does not exist (e.g., missing-DB degraded), skip the archive step.
3. Drop the scope's `IGraphStore` instance and any cached embeddings store; the next operation creates fresh ones.
4. Run a full cold-index for the scope from its `.sourcegraph.json` configuration (the same path as boot-time bring-up).
5. Return with `after_status` reflecting the post-cold-index registry row, `message = "rebuilt; archived previous DB to orphans/{filename}; new symbol_count={N}"`.

Both modes SHALL emit a heal event:
- `kind = "repair-scope-invoked"`
- `ok = (after_status == "ok")`
- `ms = elapsed_ms`
- `details = "mode={mode}; ..."` carrying the same `message` text as the structured response.

Both modes SHALL emit `notifications/progress` per the existing `Progress notifications on slow tools` requirement at the documented checkpoints (minimal: `"running integrity_check"` 0.0, `"pruning orphans"` 0.5, `"reopening workspace"` 0.8; rebuild: `"archiving old DB"` 0.0, `"cold-indexing"` 0.1, `"finalising"` 0.95).

Both modes SHALL be idempotent: calling `minimal` against an already-healthy scope is a no-op (re-runs integrity check + zero-row prune + a no-op workspace reopen); calling `rebuild` twice produces two archive files and two cold-indexes (both end in `ok`).

#### Scenario: minimal on healthy scope is a no-op
- **GIVEN** scope `backend` with `status = "ok"`, integrity_check returns `"ok"`, and zero orphan embeddings rows
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "minimal")`
- **THEN** `before_status = after_status = "ok"`; `message` mentions "ok; pruned 0 orphan embeddings; reopened workspace"; `heals.jsonl` contains one `repair-scope-invoked` line with `ok = true`; no DB row in `symbols` / `refs` / `edges` / `files` is touched

#### Scenario: minimal on corrupted scope refuses
- **GIVEN** scope `backend` whose `IntegrityCheckAsync` returns a non-`"ok"` string
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "minimal")`
- **THEN** `after_status = before_status` (no mutation); `message` includes the substring "call repair_scope mode=rebuild"; `heals.jsonl` contains one `repair-scope-invoked` line with `ok = false` and `details` carrying the integrity-check failure; the DB file is unchanged on disk

#### Scenario: rebuild archives and reindexes
- **GIVEN** scope `backend` exists with a populated DB at `<repo>/.sourcegraph/scopes/backend.db`
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "rebuild")`
- **THEN** an archive file `<repo>/.sourcegraph/orphans/backend-rebuild-<utc-iso>.db` exists with the original byte content; `<repo>/.sourcegraph/scopes/backend.db` exists as a fresh DB at `Schema.Version`; `after_status = "ok"` (assuming the cold-index succeeds); `heals.jsonl` contains one `repair-scope-invoked` line with `ok = true`, `details` matching the message

#### Scenario: rebuild on missing-DB scope skips archive but cold-indexes
- **GIVEN** scope `tools` whose registry row carries `status = "degraded"`, `status_message = "scope DB file missing — call repair_scope or restart"`, and no `<repo>/.sourcegraph/scopes/tools.db` exists
- **WHEN** the agent invokes `repair_scope(scope = "tools", mode = "rebuild")`
- **THEN** no archive file is created (nothing to archive); a fresh `tools.db` is created and cold-indexed; `after_status = "ok"`; `heals.jsonl` contains one `repair-scope-invoked` line

#### Scenario: scope = "*" rejected
- **WHEN** the agent invokes `repair_scope(scope = "*", mode = "minimal")`
- **THEN** the response is a structured `bad_scope` diagnostic (matching the established convention); no mutation occurs; no heal event is written

#### Scenario: invalid mode rejected
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "nuke")`
- **THEN** the response is a structured `bad_argument` diagnostic; no mutation occurs; no heal event is written

### Requirement: reconcile_drift tool

The server SHALL expose a `reconcile_drift` MCP tool that walks a single scope's source tree, compares each file's on-disk SHA-256 to the DB's `content_sha256`, and applies the symmetric difference (reindex changed, index added, remove vanished).

The tool SHALL accept:
- `scope` (string, required) — single scope id; `"*"` and comma-separated lists SHALL be rejected with a structured `bad_scope` diagnostic.
- `max_files` (int, default `1000`, hard cap `50000`) — caps the walk; values above the cap SHALL be silently clamped.
- `dry_run` (bool, default `false`) — when `true`, computes the diff without applying it.

The walk SHALL use the same exclusion list as `SolutionWatcher.ShouldIgnore` (`obj/`, `bin/`, `.git/`, `.sourcegraph/`).

The tool SHALL ship `structuredContent` of shape:
```
{ scope: string, scanned_count: int, reindexed_count: int, added_count: int, removed_count: int, unchanged_count: int, partial: bool, dry_run: bool, elapsed_ms: long }
```

Where `partial = true` indicates the walk hit `max_files` and stopped before scanning every file under the root.

When `dry_run = false`, the tool SHALL dispatch the changed and added paths to `RoslynIndexer.IndexChangedFilesAsync` (the same path the watcher uses) and remove the vanished paths via the existing per-file delete path.

When `dry_run = true`, the tool SHALL compute the diff and return it without invoking the indexer or the delete path; no DB row is touched; no heal event is emitted.

When `dry_run = false`, the tool SHALL emit a heal event:
- `kind = "reconcile-drift-invoked"`
- `ok = true` (drift reconciliation does not have a "failed" semantic — partial is reported via the `partial` field, not via `ok`)
- `details = "scanned={N}, reindexed={M}, added={A}, removed={R}, unchanged={U}"`

The tool SHALL emit `notifications/progress` at three checkpoints: `"walking source tree"` (0.0), `"comparing hashes"` (0.3), `"applying changes"` (0.7) — the third checkpoint omitted when `dry_run = true`.

#### Scenario: Reconcile picks up watcher-missed edits, additions, and deletions
- **GIVEN** a scope `backend` with 10 indexed files; while the server was offline, file `A.cs` was edited (SHA changed), file `B.cs` was added, file `C.cs` was deleted
- **WHEN** the agent invokes `reconcile_drift(scope = "backend")` with default args
- **THEN** the response carries `scanned_count = 10` (the 9 remaining + the new B.cs), `reindexed_count = 1` (A.cs), `added_count = 1` (B.cs), `removed_count = 1` (C.cs), `unchanged_count = 8`, `partial = false`, `dry_run = false`; the DB now has rows for A.cs (with the new SHA), B.cs (newly inserted), and no row for C.cs; `heals.jsonl` contains one `reconcile-drift-invoked` line

#### Scenario: dry_run reports diff without applying
- **GIVEN** the same drift scenario as above
- **WHEN** the agent invokes `reconcile_drift(scope = "backend", dry_run = true)`
- **THEN** the response carries the same counts as above (1/1/1/8); the DB is NOT mutated (A.cs still has the old SHA, B.cs has no row, C.cs row still exists); no `heals.jsonl` line is written

#### Scenario: max_files cap returns partial = true
- **GIVEN** a scope whose root contains 5000 source files and `max_files = 100`
- **WHEN** the agent invokes `reconcile_drift(scope = "backend", max_files = 100)`
- **THEN** `scanned_count = 100`, `partial = true`; only the first-walked 100 files are compared; the response message hints "increase max_files to scan all"; no error

#### Scenario: scope = "*" rejected
- **WHEN** the agent invokes `reconcile_drift(scope = "*")`
- **THEN** the response is a structured `bad_scope` diagnostic; no mutation occurs
