## Why

After Phase 1 (`add-scope-health-surface`) and Phase 2 (`add-scope-repair-tools`), the server can:

- Detect orphan DBs, missing DBs, and stuck `indexing` rows on boot.
- Surface drift, integrity-check status, and row counts via `verify_scope`.
- Recover from transient workspace-open failures via bounded retry.
- Repair scopes via agent-driven `repair_scope` (`minimal` or `rebuild`) and `reconcile_drift`.

The remaining failure mode is **physical SQLite corruption discovered mid-call**. SQLite returns error code 11 (`SQLITE_CORRUPT`) when it reads a page that fails its checksum, or 26 (`SQLITE_NOTADB`) when the file header is malformed. Today these errors propagate to the tool body as a generic `SqliteException`, the call returns an MCP error, and the scope keeps trying to serve subsequent calls — every one of which fails the same way until the process restarts.

The agent has no way to discriminate "this is a transient lock issue" from "this DB is permanently broken" — both surface as the same opaque error. The user sees a tool that "doesn't work" with no actionable diagnostic.

This change does three things:

1. **Detect corruption on suspicion.** When any storage call surfaces `SQLITE_CORRUPT` or `SQLITE_NOTADB`, run `PRAGMA integrity_check` and the FTS5 integrity-check. If either fails, mark the scope `degraded` with the failure message and emit a heal event. The scope stops accepting subsequent tool calls until repaired (the existing `degraded` short-circuit kicks in). Detection is autonomous and unconditional; this is the safe-and-obvious case.

2. **Autonomously prune orphan embeddings on cold-index completion.** `IEmbeddingsStore.PruneOrphanedAsync` (introduced in Phase 2) gets called automatically at the end of every successful `ColdIndexAsync`, removing embeddings rows whose `symbol_id` no longer exists. Cheap (one DELETE), reversible (embeddings regenerate), and high-frequency (every cold-index ends in a possible mismatch). No env var gate.

3. **Gate autonomous DB rebuild behind `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS=1`.** When enabled, a corruption detection automatically triggers the same archive-and-rebuild path as `repair_scope mode=rebuild`: archive to `orphans/<id>-corrupt-<utc-iso>.db`, drop, cold-index from sources. When disabled (the default), corruption detection stops at marking the scope `degraded`, and the agent uses `repair_scope mode=rebuild` to recover. The opt-in gate exists because silent autonomous DB destruction is the most dangerous heal — a misclassified corruption (race with `VACUUM`, transient OS-level read error) would silently throw away accumulated index state.

The gating split — autonomous *detection* always on, autonomous *rebuild* opt-in only — is the deliberate stance: the system is forthright about what's wrong without being presumptuous about how to fix it. The autonomous rebuild remains available for users who prefer the system to self-recover (CI environments, ephemeral dev containers where re-indexing is cheap), but never surprises a user whose scope took an hour to cold-index in the first place.

## What Changes

- **Storage exception classification**. Wrap `SqliteGraphStore` operations to translate SQLite error codes 11 (`SQLITE_CORRUPT`) and 26 (`SQLITE_NOTADB`) into a typed `GraphStoreCorruptedException` carrying the original SQLite message + the scope id. The wrapping happens at the `IGraphStore` boundary, so all callers (tool bodies, indexer, embeddings store) see the typed exception rather than a generic `SqliteException`.

- **Reactive corruption verification in `ScopedExecution`**. When any tool call body throws `GraphStoreCorruptedException`, `ScopedExecution` catches it before propagating, runs `IntegrityCheckAsync` on the scope's store, and:
  - If integrity check passes (transient false alarm): logs a warning, emits heal `corruption-suspected-but-clean` (`ok = true`), rethrows the original exception so the call still fails (the agent sees the failure once; subsequent calls succeed).
  - If integrity check fails: marks the scope `degraded` with `status_message = "corruption detected: <integrity_check result>; call repair_scope mode=rebuild"`, emits heal `corruption-detected` (`ok = true` — detection succeeded), rethrows.
  - If `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS=1` is set AND integrity check failed: kicks off the archive-and-rebuild path on a fire-and-forget Task (the original tool call still fails — the agent doesn't wait for the rebuild). Emits heal `corrupt-db-rebuild-started`. When the background rebuild completes, emits `corrupt-db-rebuilt` (`ok = (after_status == "ok")`).

- **Autonomous embeddings prune on cold-index completion**. In `ScopeHost.ColdIndexAsync`, after the cold-index reaches `ok`, call `IEmbeddingsStore.PruneOrphanedAsync()` (best-effort). On any pruned-row count > 0, emit heal `embeddings-pruned` with `details = "removed N orphan rows"`. On a count of 0, no heal event (zero-noise convention).

- **`SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS` env var** (boolean: `1` / `true` / `yes` enables, anything else including unset disables). Read once at startup; logged at info level if enabled (`"Autonomous corrupt-DB rebuild is ENABLED via SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS"`).

## Capabilities

### Modified Capabilities

- `storage`: Adds two requirements — `Typed exception for SQLite corruption` and `Reactive integrity check on corruption suspicion`.
- `mcp-tools`: Adds one requirement — `Autonomous corrupt-DB rebuild gated by env var` (the rebuild is internally a `repair_scope mode=rebuild` path; the requirement specifies the env-var gate, the heal-event sequence, and the fire-and-forget semantics).

## Impact

- **Code (small-medium)**:
  - `Storage/QueryGraphExceptions.cs` (~20 lines added): new `GraphStoreCorruptedException` type carrying `ScopeId` and the original `SqliteException`.
  - `Storage/SqliteGraphStore.cs` (~30 lines): `[try/catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { throw new GraphStoreCorruptedException(...) }]` wrapper around every public method. Cleanest implementation: a `WrapCorruption<T>(Func<Task<T>> body)` helper used at every entry point.
  - `Server/Scoping/ScopedExecution.cs` (~50 lines): catch `GraphStoreCorruptedException` before propagating; run integrity check; mark degraded; conditionally kick off rebuild.
  - `Server/Scoping/ScopeHost.cs` (~10 lines): call `PruneOrphanedAsync` after `ColdIndexAsync` reaches `ok`; emit heal on count > 0.
  - `Server/Program.cs` (~5 lines): read `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS` env var at startup; log if enabled; pass the boolean into `ScopedExecution`'s constructor (or as a static).
- **Spec**: 3 new requirements (2 `storage`, 1 `mcp-tools`), each with 3-5 scenarios.
- **Tests**:
  - `CorruptionDetectionTests.cs` — fixture that corrupts a DB mid-test (write garbage bytes at a known SQLite page offset); assert tool calls throw `GraphStoreCorruptedException`; assert `ScopedExecution` runs integrity check and marks degraded; assert heal `corruption-detected` written with `ok = true`.
  - `CorruptionFalseAlarmTests.cs` — fixture that simulates a transient corruption error (mock `SqliteException` with code 11) followed by a clean integrity check; assert `corruption-suspected-but-clean` heal written and the call still fails (the original exception rethrows).
  - `AutonomousRebuildTests.cs` — env var enabled; corrupted DB; assert rebuild fires on background task; assert `orphans/<id>-corrupt-<ts>.db` exists with the corrupted bytes; assert post-rebuild scope is `ok`; two heal events: `corrupt-db-rebuild-started` then `corrupt-db-rebuilt`.
  - `EmbeddingsPruneOnColdIndexTests.cs` — fixture with cold-index that produces orphan embeddings; assert prune fires and heal `embeddings-pruned` written with the row count.
- **Public API**: One new env var (`SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS`). Three new heal kinds (`corruption-suspected-but-clean`, `corruption-detected`, `corrupt-db-rebuild-started`, `corrupt-db-rebuilt`, `embeddings-pruned`). One new exception type (`GraphStoreCorruptedException`) — internal-facing; not exposed on any tool result.
- **Backward compatibility**: Pure additive in default mode. With the env var unset (default), today's behavior is preserved except that scopes hitting corruption are now `degraded` with a discriminative message (instead of returning the same opaque error every call). Embeddings prune on cold-index is a one-shot DELETE; no observable change for users without embeddings rows.
- **Documentation**: README + CLAUDE.md gain a "Corruption recovery" subsection explaining the env var, the heal events, and how `repair_scope mode=rebuild` interplays with autonomous rebuild.
- **Depends on**: `add-scope-health-surface` (for `HealLog`, the Counter, the orphans dir convention) and `add-scope-repair-tools` (for `IEmbeddingsStore.PruneOrphanedAsync`, `ScopeHost.TriggerRebuildAsync`, the orphans archive convention extended for the `corrupt` discriminator).
