## Context

After Phase 1 + Phase 2 land, every category of degraded state has a remediation path *except* corruption discovered while serving a tool call. The flow today:

```
agent calls find_references
  ↓
SqliteGraphStore.FindReferencesAsync
  ↓
SqliteException { SqliteErrorCode = 11 (SQLITE_CORRUPT) } thrown
  ↓
propagates up through ScopedExecution
  ↓
MCP error response to agent
  ↓
NEXT call from agent → same error, again
  ↓ (repeat indefinitely until process restart)
```

The scope is not marked `degraded`. There's no diagnostic about *what* is corrupted. The agent has no idea whether to retry, restart, or escalate.

The fix is structural: classify corruption errors at the storage boundary (typed exception), catch them at the dispatch boundary (`ScopedExecution`), verify with `PRAGMA integrity_check`, mark `degraded` so subsequent calls short-circuit cleanly. The autonomous rebuild is an opt-in extension on top of detection — it's the action a user might want, not the action the system should always take.

The embeddings prune on cold-index completion is technically separate (it doesn't involve corruption) but ships in this change because it's the same shape — autonomous, low-risk, observable — and Phase 2 introduced the helper. Bundling it here keeps the action-layer scope of this change coherent.

## Goals / Non-Goals

**Goals:**

- Convert "corrupt DB returns the same opaque error on every call" into "corrupt DB is detected, scope is marked degraded with a clear message after one failed call".
- Add an opt-in env var that escalates detection to autonomous rebuild for users who want it (CI, ephemeral dev containers).
- Autonomously clean up embeddings rows orphaned by symbol deletions, since the cleanup is cheap, reversible, and the trigger is unambiguous.
- Reuse Phase 1's heal log + Counter and Phase 2's orphans-archive convention for every action taken.

**Non-Goals:**

- Periodic background corruption sweeps. Detection is reactive (on suspicion via SQLite error code), not scheduled. A user who wants proactive checks calls `verify_scope`.
- Repair of corrupted DBs without rebuilding. SQLite's `REINDEX` and similar are insufficient for actual page-level corruption; the only safe repair is "rebuild from sources". Trying to be clever risks data loss.
- Detection of FTS5-specific corruption that doesn't surface as `SQLITE_CORRUPT`. The FTS5 integrity-check (`INSERT INTO symbols_fts(symbols_fts) VALUES('integrity-check')`) is run as part of the `IntegrityCheckAsync` helper and surfaces FTS-specific issues; reactive triggering on FTS-only failures (where the main DB is fine) is unusual enough to defer.
- Autonomous rebuild for *non*-corruption degraded states (missing DB, stuck `indexing`, workspace-open failure). Phase 2's `repair_scope` is the agent-driven recovery for those; auto-rebuilding any degraded scope is one step too aggressive.

## Decisions

### Decision 1 — Typed exception at the storage boundary

The wrapping pattern:

```csharp
private static async Task<T> WrapCorruption<T>(Func<Task<T>> body, string scopeId)
{
    try { return await body().ConfigureAwait(false); }
    catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
    {
        throw new GraphStoreCorruptedException(scopeId, ex);
    }
}
```

Applied at every `IGraphStore` public method. The alternative (let `SqliteException` propagate; classify in `ScopedExecution`) is uglier because it forces the dispatch layer to know about SQLite-specific error codes.

The exception carries `ScopeId` because by the time `ScopedExecution` catches it, the call's scope context is on the stack but discoverable via the exception is easier than threading an additional argument through the catch.

### Decision 2 — Reactive verification, not eager

We could call `IntegrityCheckAsync` periodically (every N tool calls, every M minutes) — but periodic checks on a 100MB DB cost seconds and surface no new state most of the time. Reacting to actual `SQLITE_CORRUPT` is cheaper and has a clearer trigger semantic.

The downside: the *first* call after corruption returns an opaque error. That's acceptable — the *second* call returns the structured `degraded` short-circuit.

### Decision 3 — False-alarm path: clean integrity check after suspicion

`SQLITE_CORRUPT` has been observed (rarely) on:
- A `VACUUM` running concurrently with reads (interrupted, the read sees a transitional page).
- An OS-level transient I/O error misclassified by SQLite as corruption.
- A reader hitting a page during a checkpoint window with a faulty disk controller.

The integrity check is the arbiter. When it passes, we *don't* clear the agent's failure (the original call still throws), but we *don't* mark the scope `degraded` either. The next call probably succeeds. Heal kind: `corruption-suspected-but-clean` (with `ok = true` because the detection logic worked, even though the scope itself wasn't actually broken).

### Decision 4 — Autonomous rebuild gated by env var (off by default)

The risk profile of an autonomous rebuild on a misclassified corruption:

- **If the integrity check is right and the DB is broken**: rebuild is a clean win — no agent action needed, scope back to `ok` in minutes.
- **If the integrity check is wrong (false positive)**: rebuild silently destroys an index that took an hour to build on a large solution. User notices when the next call is slow because the cold-index is running again.

False positives on `PRAGMA integrity_check` are extremely rare (the check reads every page and verifies cross-references; a false positive would imply a bug in SQLite itself). But the consequence is severe enough that "off by default" is the right stance. Users for whom rebuild cost is low (CI re-runs, dev containers, small repos) opt in via env var; everyone else gets detection only.

The env var name (`SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS`) is verbose on purpose — short flags get accidentally enabled.

### Decision 5 — Autonomous rebuild is fire-and-forget

When the env var is enabled and corruption is confirmed, the rebuild runs on a background `Task.Run` — the agent's failed call returns immediately with the original error. Why:

- The agent's call is already going to fail (the corrupt DB can't serve it). Holding the response open while the rebuild runs (potentially minutes) wastes the agent's request budget.
- The agent can poll `verify_scope` or call `list_scopes` to check whether the rebuild completed.
- The fire-and-forget semantic is compatible with the eventual goal of a `tools/list_changed` notification when the rebuild finishes (out of scope here, but the architecture is consistent).

The trade-off: the agent doesn't see a "rebuilt successfully" message inline. Acceptable because the rebuild is expected to take longer than a single tool call's natural lifetime.

### Decision 6 — Embeddings prune is autonomous, no env var gate

Pruning orphan embeddings is unambiguous:

- The trigger is "cold-index just completed and there may now be embeddings rows for symbols that don't exist".
- The action is one DELETE statement.
- The cost of being wrong (deleting a row that's still valid) is zero — embeddings regenerate on next semantic_search call.

No env var gate; just runs at the end of every successful `ColdIndexAsync`. The heal event fires only when the prune count > 0, to avoid log noise on cold-indexes that produce no orphans (most of them).

### Decision 7 — Two new orphans archive discriminators

Phase 1 used `orphans/<id>-<ts>.db` for boot-time orphans. Phase 2 used `orphans/<id>-rebuild-<ts>.db` for `repair_scope mode=rebuild`. Phase 3 uses `orphans/<id>-corrupt-<ts>.db` for autonomous-rebuild archives.

The discriminator (`-rebuild-` vs `-corrupt-`) tells the user *why* the file ended up in orphans. Convention going forward: any future archive reason gets its own discriminator (e.g., `-stale-` for hypothetical "scope removed from config" archives).

## Risks / Trade-offs

- **[Risk] Autonomous rebuild masks a real disk-failure problem.** A user whose disk is silently flipping bits sees scopes spontaneously recover, never realizes they have a hardware issue. → Mitigation: every autonomous rebuild emits a heal event + Counter increment. A user (or their monitoring) seeing `sourcegraph.heal.fired{kind="corrupt-db-rebuilt"}` count climbing has an unambiguous signal. The env var being opt-in also means users who care about hardware diagnostics don't enable it.

- **[Risk] Reactive verification's `IntegrityCheckAsync` itself fails on a deeply corrupt DB.** → Mitigation: wrap the verification call in its own try/catch; on failure, log + emit heal `corruption-detected` with `ok = false` and `details = "integrity_check itself failed: <message>"`, mark degraded with the same message. The agent's tool call still fails (with the original `GraphStoreCorruptedException`); the verification's failure is recorded but doesn't escalate.

- **[Risk] Fire-and-forget rebuild leaks if the host shuts down mid-rebuild.** → Mitigation: register the background `Task` with the host's lifetime cancellation token; on shutdown, the rebuild gets a cancellation and exits cleanly (the partial new DB is left in `scopes/`, the next boot's stuck-`indexing` detection (Phase 1) catches it).

- **[Risk] Embeddings prune races with concurrent semantic_search.** → Mitigation: SQLite's transaction semantics make the DELETE atomic; a concurrent reader sees either the pre- or post-prune state, never a half-deleted row. No additional locking needed.

- **[Trade-off] No way to distinguish "transient SQLITE_CORRUPT" from "permanent" without running the integrity check.** A user might want a "skip the verification, treat any SQLITE_CORRUPT as definitive" mode for performance. → Accepted. Skipping verification means false positives mark scopes degraded; explicit verification is the right default. The performance cost (one integrity_check per detected corruption) is bounded — corruption is rare.

- **[Trade-off] No surface to query "is autonomous rebuild enabled" from a tool.** → Accepted. The env var is logged at startup; users who care can check the logs. A future change can add it to `usage_stats` or a new `server_info` tool.

## Migration Plan

Land in five small commits:

1. `GraphStoreCorruptedException` type + `SqliteGraphStore.WrapCorruption<T>` helper applied to every public method. Existing `SqliteGraphStoreTests` get a corruption-injection test.
2. `ScopedExecution` reactive verification + heal events. `CorruptionDetectionTests` + `CorruptionFalseAlarmTests`.
3. Env var read in `Program.cs` + plumbing into `ScopedExecution`. Logging at startup. No behavior change without the env var set.
4. Autonomous rebuild path + `corrupt-db-rebuild-started` / `corrupt-db-rebuilt` heal kinds. `AutonomousRebuildTests`.
5. Embeddings prune in `ScopeHost.ColdIndexAsync` + `embeddings-pruned` heal kind. `EmbeddingsPruneOnColdIndexTests`. README + CLAUDE.md docs.

**Rollback**: revert each commit independently. The env var is the riskiest surface; reverting commit 4 disables autonomous rebuild even when the env var is set (the fire-and-forget call site is gone). Reverting commit 1 removes the typed exception and reverts to today's `SqliteException`-propagation behavior.

## Open Questions

- **Should the autonomous-rebuild env var also accept a per-scope filter?** E.g., `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS=frontend,backend` to enable only on those scopes. Lean: not in this change. Whole-server boolean is enough; per-scope granularity adds parsing + a feature with no clear use case yet.

- **Should we emit a `notifications/resources/list_changed` after a corrupt-db rebuild succeeds?** The MCP protocol supports notifying clients when server-side state changes; an agent could re-issue a previously-failed call after a rebuild completes. Lean: not in this change. The protocol surface for this is fiddly and the failed call is one tool call; the agent can naturally retry.

- **Should `embeddings-pruned` also fire on `repair_scope mode=minimal`?** `minimal` already calls `PruneOrphanedAsync`. Lean: yes — emit `embeddings-pruned` from inside `PruneOrphanedAsync`'s caller (whichever path) when count > 0. Centralise the emission logic in a small helper to avoid duplication.
