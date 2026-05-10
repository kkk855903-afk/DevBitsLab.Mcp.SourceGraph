## ADDED Requirements

### Requirement: Boot-time orphan DB reconciliation

On startup, before per-scope bring-up, the host SHALL cross-reference three sets — configured scopes (from `.sourcegraph.json`), registry rows (from `_meta.db`), and per-scope DB files (under `<repo>/.sourcegraph/scopes/`) — and take corrective action for asymmetries.

For each `id` whose scope DB file exists under `scopes/<id>.db` but no registry row exists with that id, the host SHALL move the file to `<repo>/.sourcegraph/orphans/<id>-<utc-iso>.db` (with `:` replaced by `-` for cross-filesystem safety) and emit a heal event with `kind = "orphan-db-archived"`. The orphans directory SHALL be created lazily. The original DB file SHALL be moved, not deleted, so the user can inspect it.

For each registry row pointing at a DB file that does not exist on disk, the host SHALL upsert the registry row with `Status = "degraded"` and `StatusMessage = "scope DB file missing — call repair_scope or restart"` and emit a heal event with `kind = "missing-db-detected"`. The host SHALL NOT autonomously rebuild the missing scope; rebuild is the responsibility of `repair_scope` (introduced in `add-scope-repair-tools`) or process restart.

The reconciliation pass SHALL be best-effort: an `IOException` on the move or upsert SHALL be logged at warning level and emitted as a heal event with `ok = false`, but SHALL NOT prevent the rest of the boot sequence from completing. A file that disappears between the directory listing and the move attempt (delete-during-listing race) SHALL be treated as a no-op with no heal event emitted.

#### Scenario: Orphan DB file is archived
- **GIVEN** `<repo>/.sourcegraph/scopes/stale.db` exists on disk and `_meta.db` has no registry row with `id = "stale"`
- **WHEN** the host runs `ReconcileOnBootAsync` during startup
- **THEN** `<repo>/.sourcegraph/scopes/stale.db` no longer exists; `<repo>/.sourcegraph/orphans/stale-<utc-iso>.db` exists with the same byte content; `<repo>/.sourcegraph/heals.jsonl` contains one line with `kind = "orphan-db-archived"`, `scope = "stale"`, `ok = true`

#### Scenario: Missing DB file marks scope degraded
- **GIVEN** `_meta.db` has a registry row `id = "backend"` with `Status = "ok"` and `<repo>/.sourcegraph/scopes/backend.db` does not exist
- **WHEN** the host runs `ReconcileOnBootAsync` during startup
- **THEN** the registry row for `backend` is updated to `Status = "degraded"` with `StatusMessage = "scope DB file missing — call repair_scope or restart"`; `heals.jsonl` contains one line with `kind = "missing-db-detected"`, `scope = "backend"`, `ok = true`; the host does NOT cold-index `backend` in this boot

#### Scenario: Reconciliation tolerates IOException
- **GIVEN** the orphans directory cannot be created (e.g., parent directory is read-only) and an orphan DB file is detected
- **WHEN** `ReconcileOnBootAsync` attempts the archive
- **THEN** the failure is logged at warning level; `heals.jsonl` contains one line with `kind = "orphan-db-archived"`, `ok = false`, and a `details` field carrying the exception message; the boot sequence continues to per-scope bring-up

#### Scenario: Race-deleted file is silently skipped
- **GIVEN** the directory listing reports `scopes/ghost.db` but the file is deleted by another process before the move attempt
- **WHEN** `ReconcileOnBootAsync` calls `File.Move` and gets `FileNotFoundException`
- **THEN** the host treats the case as a no-op; no `heals.jsonl` line is written for this id

### Requirement: Boot-time stuck-`indexing` detection

On startup, after orphan reconciliation but before per-scope bring-up, the host SHALL inspect every registry row whose `Status = "indexing"` and whose `LastIndexedAt` is older than the current process's start time. Such rows represent scopes whose previous-process cold-index was interrupted (OOM, Ctrl-C, IDE crash) and must be discriminated from in-flight indexes started by the current process.

For each matching row, the host SHALL upsert it with `Status = "degraded"` and `StatusMessage = "previous index interrupted — call repair_scope or restart"` and emit a heal event with `kind = "stuck-indexing-detected"`. The host SHALL NOT autonomously resume or rebuild the index; recovery is the responsibility of `repair_scope` or process restart.

A scope whose registry row carries `Status = "indexing"` AND whose `LastIndexedAt` is after the process start time SHALL NOT be touched — it is owned by the current process's bring-up sequence and will resolve to `ok` or `degraded` through the existing `PrepareScopeAsync` / `ColdIndexAsync` paths.

#### Scenario: Stuck `indexing` row from a prior process is marked degraded
- **GIVEN** `_meta.db` has a registry row `id = "frontend"` with `Status = "indexing"` and `LastIndexedAt = 2026-05-09T10:00:00Z`, and the current process started at `2026-05-09T11:00:00Z`
- **WHEN** the host runs the stuck-indexing detection pass
- **THEN** the registry row for `frontend` is updated to `Status = "degraded"` with `StatusMessage = "previous index interrupted — call repair_scope or restart"`; `heals.jsonl` contains one line with `kind = "stuck-indexing-detected"`, `scope = "frontend"`, `ok = true`

#### Scenario: In-flight `indexing` row from the current process is left alone
- **GIVEN** the current process started at `2026-05-09T11:00:00Z`, and during bring-up `PrepareScopeAsync` has just upserted a row `id = "frontend"` with `Status = "indexing"` and `LastIndexedAt = 2026-05-09T11:00:05Z`
- **WHEN** the host's stuck-indexing detection pass runs (in this scenario, hypothetically re-running mid-boot)
- **THEN** the `frontend` row is not modified; no heal event is emitted for it
