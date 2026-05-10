## ADDED Requirements

### Requirement: Autonomous corrupt-DB rebuild gated by env var

When the environment variable `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS` is set to a truthy value (`"1"`, `"true"`, `"yes"`, case-insensitive), the server SHALL autonomously rebuild a scope's DB whenever `ScopedExecution`'s reactive verification confirms corruption (per the `storage` capability's `Reactive integrity check on corruption suspicion` requirement). When the variable is unset or any other value, autonomous rebuild SHALL NOT fire — corruption detection stops at marking the scope `degraded`.

When the env var is enabled and the autonomous rebuild fires, the sequence SHALL be:

1. Emit a heal event with `kind = "corrupt-db-rebuild-started"`, `ok = true`, `details = "fire-and-forget rebuild kicked off"`. The agent's failed tool call returns immediately with the original `GraphStoreCorruptedException`; the rebuild does NOT block the response.
2. Schedule a background task that:
   - Archives the corrupt DB to `<repo>/.sourcegraph/orphans/<id>-corrupt-<utc-iso>.db` (using the same orphans-directory convention introduced in `add-scope-health-surface`, with the `-corrupt-` discriminator distinguishing it from `-rebuild-` and bare `<id>-<ts>.db`).
   - Drops the scope's DB file and runs a fresh cold-index from `.sourcegraph.json` (the same path as `repair_scope mode=rebuild`).
   - On completion, emits a heal event with `kind = "corrupt-db-rebuilt"`, `ok = (after_status == "ok")`, `ms = <wall-clock elapsed>`, `details = $"after_status={after_status}"`.
   - On exception during the rebuild, emits the same heal kind with `ok = false` and `details` carrying the failure message.

The background task SHALL be tied to the host's lifetime cancellation token; on shutdown, the rebuild gets cooperative cancellation. A partially-completed rebuild leaves a fresh-but-incomplete DB in `scopes/`; the next boot's stuck-`indexing` detection (per `add-scope-health-surface`) catches it.

The env var status SHALL be logged at info level on startup (`"Autonomous corrupt-DB rebuild is ENABLED via SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS"`) when enabled, and SHALL NOT be logged when disabled (the default has no signal).

#### Scenario: Env var enabled — autonomous rebuild fires
- **GIVEN** `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS=1` is set on server startup AND a scope `backend` whose DB is physically corrupted
- **WHEN** any tool call against `backend` triggers reactive verification (per the `storage` capability) and the integrity check confirms corruption
- **THEN** `heals.jsonl` contains, in order: one `corruption-detected` line and one `corrupt-db-rebuild-started` line; the agent's tool call returns immediately with the original exception; within bounded wall-clock time (test wait, e.g. 30s), `heals.jsonl` gains a `corrupt-db-rebuilt` line with `ok = true`; `<repo>/.sourcegraph/orphans/backend-corrupt-<utc-iso>.db` exists with the original (corrupted) byte content; `<repo>/.sourcegraph/scopes/backend.db` is fresh; the `backend` registry row is `Status = "ok"`; subsequent tool calls against `backend` succeed

#### Scenario: Env var unset — no autonomous rebuild
- **GIVEN** `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS` is unset (or set to `"0"` / `"false"` / `""`) AND a scope `backend` whose DB is physically corrupted
- **WHEN** the same corruption-triggering call as above is made
- **THEN** `heals.jsonl` contains one `corruption-detected` line; NO `corrupt-db-rebuild-started` line is written; the `backend` registry row is `Status = "degraded"`; subsequent calls return the degraded short-circuit; recovery requires an explicit `repair_scope mode=rebuild` call from the agent

#### Scenario: Background rebuild interrupted by shutdown
- **GIVEN** the env var is enabled AND an autonomous rebuild is in flight (cold-indexing) when the host receives shutdown
- **WHEN** the host's cancellation token fires
- **THEN** the rebuild's `Task.Run` body exits via `OperationCanceledException`; a `corrupt-db-rebuilt` heal line is written with `ok = false` and `details = "rebuild cancelled by host shutdown"`; the partially-built `scopes/<id>.db` is left in place; the next boot's stuck-`indexing` detection catches it (per `add-scope-health-surface`)

### Requirement: Autonomous embeddings prune on cold-index completion

`ScopeHost.ColdIndexAsync` SHALL call `IEmbeddingsStore.PruneOrphanedAsync()` after the cold-index reaches `Status = "ok"`, removing rows from `symbol_embeddings` whose `symbol_id` no longer exists in the `symbols` table. The same prune call SHALL fire from the `repair_scope mode=minimal` path (per `add-scope-repair-tools`), centralised in a small helper to avoid duplication.

When the prune count > 0, the caller SHALL emit a heal event:
- `kind = "embeddings-pruned"`
- `ok = true`
- `ms = <wall-clock elapsed>`
- `details = $"removed {count} orphan rows"`

When the count == 0, no heal event SHALL be emitted (zero-noise convention; cold-indexes that produce no orphans are the common case).

When `PruneOrphanedAsync` itself throws, the caller SHALL log at warning level and emit a heal event with `kind = "embeddings-pruned"`, `ok = false`, `details = ex.Message`. The cold-index outcome (`ok` status) SHALL NOT be reverted on prune failure; the prune is best-effort.

#### Scenario: Cold-index produces orphans, prune fires, heal recorded
- **GIVEN** a scope `backend` whose cold-index just completed and whose `symbol_embeddings` table has 5 rows referencing `symbol_id` values that no longer exist in `symbols` (e.g. after a refactor that deleted the corresponding source files)
- **WHEN** `ColdIndexAsync` completes the post-index prune step
- **THEN** the 5 orphan rows are deleted from `symbol_embeddings`; `heals.jsonl` contains one line with `kind = "embeddings-pruned"`, `scope = "backend"`, `ok = true`, `details = "removed 5 orphan rows"`; the `backend` registry row remains `Status = "ok"`

#### Scenario: Cold-index produces no orphans, no heal event
- **GIVEN** a scope whose cold-index just completed and whose `symbol_embeddings` table has zero orphan rows
- **WHEN** the prune step runs
- **THEN** `PruneOrphanedAsync` returns 0; no `embeddings-pruned` heal line is written; the registry row remains `Status = "ok"`

#### Scenario: Prune failure does not revert cold-index outcome
- **GIVEN** a scope whose cold-index just completed and `PruneOrphanedAsync` throws (e.g. embeddings store is in a broken state)
- **WHEN** the prune step catches the exception
- **THEN** `heals.jsonl` contains one line with `kind = "embeddings-pruned"`, `ok = false`, `details` carrying the exception message; the registry row remains `Status = "ok"`; subsequent tool calls against the scope succeed

#### Scenario: repair_scope minimal also emits the prune heal
- **WHEN** `repair_scope(scope = "backend", mode = "minimal")` runs against a scope with 3 orphan embeddings rows
- **THEN** the prune step within minimal mode emits one `embeddings-pruned` heal line with `details = "removed 3 orphan rows"`, in addition to the `repair-scope-invoked` heal line that the tool itself emits
