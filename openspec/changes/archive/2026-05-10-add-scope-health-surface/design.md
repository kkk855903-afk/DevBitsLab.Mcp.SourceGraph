## Context

The server already has a per-scope quarantine mechanism: `ScopeHost.PrepareScopeAsync` and `ColdIndexAsync` each catch broadly, set `host.Status = "degraded"`, persist the registry row, and let the host stay up to serve other scopes. That's good — but it only covers failures *during* per-scope bring-up. Three pre-existing on-disk states bypass it:

1. A scope DB file that exists with no registry row (config edit removed the scope, file was left behind).
2. A registry row pointing at a DB file that no longer exists (user deleted it; or filesystem corruption ate it).
3. A registry row stuck at `status='indexing'` from a previous process that died before completion.

None of the three trigger a `PrepareScopeAsync` failure — they're orthogonal to it. The first two never reach `PrepareScopeAsync` at all; the third lands in `WaitForReadyAsync` waiting on a signal that will never fire because no process is indexing.

The fix shape is uniform: **a boot-time reconciliation pass before per-scope bring-up**, plus a **structured heal-event log** so every reconciliation action is observable. The reconciliation pass is small (filesystem listing + registry query); the log is the substrate every later phase reuses.

## Goals / Non-Goals

**Goals:**

- Detect orphan DB files, missing DB files, and stuck `indexing` rows on boot, before any tool call lands.
- Surface every detection action through both the heal-event JSONL log and a Meter Counter.
- Expose a read-only `verify_scope` tool that gives the agent a structured health snapshot without mutating any state.
- Keep the substrate generic so Phase 2 + Phase 3 reuse the same log path and metric.

**Non-Goals:**

- Autonomous rebuild on missing DB or stuck `indexing`. Phase 2 (`repair_scope`) and Phase 3 (`integrity-check on suspicion`) own those decisions; surfacing-without-acting in Phase 1 keeps the blast radius small.
- A periodic background sweep. The reconciliation pass runs once at boot; subsequent state changes go through the existing watcher / scope-host paths.
- Drift remediation. `verify_scope` *reports* drift via the 20-file sample but doesn't reindex anything. Phase 2's `reconcile_drift` tool is the action surface.
- A new `ScopeStatus` value. The existing three-state `ok | degraded | indexing` is sufficient; missing-DB + stuck-`indexing` both fold into `degraded` with a discriminative `status_message`.

## Decisions

### Decision 1 — Reconciliation runs once at boot, not periodically

The three failure modes this change targets all happen *between* server runs (config edit, manual file delete, prior crash) — there's no live source for them while the server is up. A single boot-time pass catches every case at the only moment that matters; periodic polling adds I/O and surprise without finding new state.

If a future need emerges (e.g., a deployment that hot-swaps DB files via symlinks under a running server), a `reconcile_now` tool or a SIGUSR1-style trigger is a one-line follow-on. Not in scope here.

### Decision 2 — Orphan DBs are archived, not deleted

```
Before:                                  After:
.sourcegraph/                            .sourcegraph/
├── _meta.db                             ├── _meta.db
├── scopes/                              ├── scopes/
│   ├── frontend.db                      │   └── backend.db
│   ├── backend.db                       └── orphans/
│   └── stale-experiment.db ←orphan          └── stale-experiment-2026-05-10T14-22-08Z.db
```

Move-not-delete preserves the user's evidence. SQLite DBs are not large compared to the source they index (a 100k-LOC C# repo produces ~10–50 MB), so retention cost is bounded. Users who want to free space can `rm -rf .sourcegraph/orphans/` themselves; we don't auto-prune in this change. (Phase 3 may add a TTL-based prune; not in scope here.)

The archive filename uses ISO-8601 with `:` replaced by `-` for cross-filesystem safety (Windows + macOS NTFS / SMB shares reject `:` in filenames).

### Decision 3 — Missing DB and stuck `indexing` are surfaced as `degraded`, not `unrecoverable` or a new status

The existing three-state `ok | degraded | indexing` is what every existing tool already understands (`ScopedExecution.WaitForReadyAsync` short-circuits `degraded`; `list_scopes` renders the `status_message`). Adding a new state would force every consumer to learn it. A discriminative `status_message` is enough:

- `"scope DB file missing — call repair_scope or restart"`
- `"previous index interrupted — call repair_scope or restart"`

The agent can branch on the message text without a new enum case. (Phase 2 introduces `repair_scope`, which the message hints at; until Phase 2 ships, the user-facing fallback is "restart the server" — fine because today's behavior is "this state is invisible".)

### Decision 4 — `verify_scope` exposes raw counts and a 20-file drift sample, not a graded "health score"

A scoring rubric would invent meaning ("80/100 = healthy?") that doesn't map to any concrete user action. Raw numbers + a small drift sample let the agent decide what's anomalous in context:

- `row_counts.symbols == 0` on a non-empty scope → broken cold index
- `drift_sample.changed > 5/20` → watcher missed events; user should call `reconcile_drift` (Phase 2)
- `integrity_check != "ok"` → corruption; Phase 3 territory

The 20-file sample size is tunable but defaults to a number that runs in under 100ms even on a cold disk: 20 file reads + 20 SHA-256 computations ≈ 50ms on a typical SSD; the SQLite lookups for the stored `content_sha256` values are sub-millisecond. The sample is uniform-random over the `files` table (no stratification); 20 is enough to surface "lots of drift" without being enough to surface "one file changed".

### Decision 5 — Heal log path: `<repo>/.sourcegraph/heals.jsonl`, separate from `usage.jsonl`

Two reasons not to fold heal events into `usage.jsonl`:

- **Schema clarity** — `usage.jsonl` records *tool calls* (one line per `tools/call`). Heal events aren't tool calls; they're internal state transitions. Mixing them complicates downstream parsing.
- **Volume profile** — `usage.jsonl` can grow to thousands of lines per session; heal events typically tens. A separate file keeps heal events scannable by `cat`.

Both files share the `<repo>/.sourcegraph/` directory and the same lazy-create + best-effort-write contract; the only difference is path and shape.

### Decision 6 — Heal kinds are typed strings, not enums

The kinds in Phase 1: `orphan-db-archived`, `missing-db-detected`, `stuck-indexing-detected`. Phase 2 adds `workspace-open-retried`, `repair-scope-invoked`, `reconcile-drift-invoked`. Phase 3 adds `corruption-detected`, `corrupt-db-rebuilt`, `embeddings-pruned`. Keeping them as TEXT in the JSONL + as a tag string on the metric means a new heal kind is a one-line addition, no enum + serializer round-trip. Convention: kebab-case, present-tense verb at the end (`-archived`, `-detected`, `-rebuilt`).

### Decision 7 — `verify_scope` does NOT register itself as a heal event

Reads aren't heals. `verify_scope` calls `usage.jsonl` like every other tool (via `ToolMetrics.TrackAsync`); no `heals.jsonl` line. The heal log records *state changes*, not introspection.

## Risks / Trade-offs

- **[Risk] Reconciliation race with a slow filesystem** — listing `scopes/*.db` while a deletion completes mid-scan could see a phantom file. → Mitigation: re-stat each candidate file after listing; if the file disappeared, treat as a no-op for this boot. Worst case: one extra log line on next boot.

- **[Risk] Drift sample misleads on small scopes** — a scope with only 5 files samples all 5; one drift = "20% drift", which sounds dire. → Mitigation: report `sampled` and `total_files` so the caller can interpret. Don't compute a percentage in the structured output; let the agent compute or render as a fraction.

- **[Risk] PRAGMA integrity_check on a 100MB DB takes seconds** — `verify_scope` becomes a slow tool. → Mitigation: emit `notifications/progress` (the mechanism shipped in `report-progress-on-slow-tools`) at three checkpoints (`"reading row counts"`, `"running integrity_check"`, `"sampling drift"`). For very large DBs, integrity_check still bounds at single-digit seconds; acceptable for an agent-triggered diagnostic.

- **[Trade-off] No autonomous rebuild on missing DB even though it's the obvious right action** — sometimes the user wants the missing DB to *be* the trigger for a rebuild. → Accepted. This change's stance is detection-only; Phase 2's `repair_scope` is one tool call away. The cost of a wrong autonomous rebuild (silent loss of accumulated index state) outweighs the benefit of saving one tool call. The user-facing message names the next step.

- **[Trade-off] Heal log + metric add a sixth observability surface to the existing five (`usage.jsonl`, in-memory `ToolStats`, `ToolMetrics.ScopeSnapshot`, ActivitySource, Meter)** — more places to look. → Accepted. The substrate is necessary for Phase 2/3 to be observable; centralising it in a `HealLog` helper means future heal kinds add one line, not five.

## Migration Plan

Pure additive; no data migration. Land in five small commits:

1. `HealLog.cs` + `Telemetry.HealCounter` + Program wiring. No callers yet. CI green.
2. `IGraphStore.RowCountsAsync` + `IntegrityCheckAsync` + impls. Unit-tested in `SqliteGraphStoreTests`.
3. `ScopeHost.ReconcileOnBootAsync` + the three heal kinds. `ScopeReconciliationOnBootTests` covers all three branches.
4. `verify_scope` tool + `VerifyScopeResult` structuredContent type. `VerifyScopeToolTests` covers happy path, degraded path, drift sample.
5. README + CLAUDE.md docs.

**Rollback**: revert each commit independently. The substrate (`HealLog`, `IntegrityCheckAsync`) is harmless if its callers are reverted; the new tool is a single registration in `Program.cs`.

## Open Questions

- **Should `verify_scope` accept `scope = "*"` and fan out, or one scope per call?** Lean: yes, fan out, returning one structured row per scope. Matches `list_scopes` shape. Default `scope` = `"*"` so the no-args call gives a system-wide health view.

- **Should we cap `heals.jsonl` size?** Phase 1: no — heal volume is naturally low. If Phase 2/3 produce noisier streams (e.g., bounded retry firing on every transient I/O hiccup), revisit with a rotation policy then.

- **Should the orphan archive use a different extension to make it un-attachable by accident?** Lean: keep `.db` so the user can `sqlite3 orphans/foo.db` and inspect. The directory name (`orphans/`) is the discriminator.
