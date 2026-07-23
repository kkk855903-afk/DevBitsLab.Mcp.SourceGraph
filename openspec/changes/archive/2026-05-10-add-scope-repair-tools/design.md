## Context

After `add-scope-health-surface` lands, the server can detect and surface six kinds of degraded state:

- Orphan DB files (auto-archived)
- Missing DB files (marked degraded; status_message hints `repair_scope`)
- Stuck `indexing` rows (marked degraded; status_message hints `repair_scope`)
- Per-scope cold-index failures (existing behavior; marked degraded with the exception message)
- Drift between disk and DB (reported by `verify_scope`)
- Corruption (reported by `verify_scope.integrity_check` ≠ `"ok"`)

The first three explicitly point at a `repair_scope` tool that doesn't exist yet — that's the gap this change fills, plus the drift-remediation surface and the bounded retry. Corruption recovery is left to Phase 3 (`add-corruption-recovery`) because it has tighter constraints (must not silently destroy data on a misclassified failure).

## Goals / Non-Goals

**Goals:**

- Convert "degraded forever within a process" into "degraded after up to 3 retries" for transient downstream failures.
- Give the agent two heavyweight repair primitives — `minimal` (cheap) and `rebuild` (nuclear) — gated behind explicit tool calls.
- Give the agent a drift-remediation tool that doesn't require a process restart.
- Reuse Phase 1's heal log + Counter for every action taken; no parallel observability surface.

**Non-Goals:**

- Autonomous rebuild on missing DB or stuck `indexing`. Phase 1 surfaced these; Phase 2 lets the agent fix them via `repair_scope` but does not auto-trigger. Same rationale as Phase 1: explicit-and-observable beats silent-and-clever.
- Periodic background drift sweeps. `reconcile_drift` runs only when the agent asks. Background sweeps eat I/O and would surprise users.
- Corruption-driven autonomous rebuild. Phase 3 territory — different constraints around when it's safe to nuke a DB.
- Per-file granular repair. `repair_scope` operates at scope grain. A future `reindex_files(scope, paths)` is conceivable but not in scope here.

## Decisions

### Decision 1 — Retry budget: 3 attempts, exponential backoff (1s / 5s / 25s)

The numbers come from the failure profile, not from a generic "retry 3 times" intuition:

| Failure | Typical resolution time | 1st backoff (1s) | 2nd backoff (5s) | 3rd backoff (25s) |
|---|---|---|---|---|
| `dotnet restore` racing | 1–10s | sometimes wins | usually wins | always wins |
| MSBuild SDK upgrade | 5–60s | rarely wins | sometimes wins | usually wins |
| Network-FS blip | 1–5s | usually wins | always wins | n/a |
| Real config error | never | wastes 1s | wastes 5s | wastes 25s — total 31s |

A 31-second worst-case wait before `degraded` is acceptable for cold-index startup, where the user is already waiting for indexing to complete (typically 10s–5min for real solutions). A linear "3 attempts of 5s each" wouldn't help the slowest case (MSBuild SDK upgrade) and would waste more wall-clock on the always-wins cases.

`OperationCanceledException` from any attempt skips the retry and rethrows immediately — cooperative shutdown is non-negotiable.

### Decision 2 — `repair_scope` modes: minimal vs rebuild

A binary mode keeps the tool surface understandable. Three or more modes invent gradations the user has to reason about; one mode forces every problem into the same hammer.

| Mode | What it does | When the agent calls it |
|---|---|---|
| `minimal` | Integrity check. If `"ok"`: prune orphan embeddings + re-attempt workspace open. If not: refuse, instruct `rebuild`. | Drift suspected, transient failure suspected, status is `degraded` from a recoverable cause (workspace race). |
| `rebuild` | Archive current DB to `orphans/<id>-rebuild-<ts>.db`, drop, cold-index from sources. | Corruption detected, repeated `minimal` fails, agent or user wants to nuke and start over. |

`minimal` is conservative-by-construction: it cannot destroy index state. The `archive-and-drop` step in `rebuild` preserves evidence the same way orphan reconciliation does (Phase 1 Decision 2) — users can `sqlite3 orphans/foo-rebuild-*.db` and inspect the old graph.

### Decision 3 — `reconcile_drift` is symmetric over add / change / remove

The watcher misses three event kinds when offline: file edits, file additions, file deletions. A drift tool that only handled one would be incomplete. The implementation is essentially: produce the on-disk file set + per-file SHA, produce the in-DB file set + per-file SHA, compute the symmetric difference and the SHA-mismatch set, dispatch to existing per-file paths.

The `max_files` cap defaults to 1000. The reasoning: 1000 file reads + SHA-256 + DB lookup runs in ~1s on a typical SSD; large enough to handle the common "watcher missed a few edits" case without an explicit cap argument; small enough to bound the worst case for an accidentally-huge scope. Hard cap of 50000 is the sanity backstop. Beyond 50000, the caller probably wants `repair_scope rebuild` instead.

The walk uses the same exclusion list as `RoslynIndexer` and `SolutionWatcher` (`obj/`, `bin/`, `.git/`, `.sourcegraph/`). Adding new exclusions here would create drift between the discovery rules; the existing list is the source of truth.

### Decision 4 — Both repair tools require a `scope` argument; no `"*"` fan-out

`scope = "*"` for `verify_scope` is harmless — read-only. For `repair_scope` and `reconcile_drift`, fan-out is dangerous: a `rebuild` against `"*"` is potentially "drop every scope DB at once". Forcing the caller to name a single scope makes the destructive intent explicit.

If the agent wants to repair multiple scopes, it iterates the result of `list_scopes` and calls `repair_scope` per scope. The orchestration cost is one extra agent turn; the safety win is meaningful.

### Decision 5 — `repair_scope rebuild` archives the existing DB unconditionally

Even on an explicit user request to rebuild, archiving the old DB to `orphans/` adds negligible cost (a `File.Move`) and preserves evidence. The agent doesn't need a flag to opt out; if the user wants the orphans directory pruned, they delete it manually.

This sets up Phase 3's autonomous corruption-rebuild path to use the same archive convention — `orphans/<id>-corrupt-<ts>.db` — so users have one mental model: anything serious that touches a scope DB leaves an inspectable copy.

### Decision 6 — Retry inside `LiveIndexService` is the only autonomous heal in this change

`repair_scope` and `reconcile_drift` are agent-driven (the agent decides when to fire); `workspace-open-retried` is autonomous (the server retries without asking). The asymmetry is by design:

- Workspace-open failure has a well-bounded transient profile and a small worst-case wait. Retry is unambiguously the right action.
- Repair / drift remediation can be expensive and are not always the right action ("the user explicitly disabled this scope; don't reindex it"). Agent context decides correctly; server cannot.

The single autonomous heal also forms the template for Phase 3's "integrity-check on suspicion" autonomous heal: bounded, observable, undoable.

### Decision 7 — `repair_scope` and `reconcile_drift` emit progress at coarse checkpoints

Both tools are slow by their nature (`rebuild` cold-indexes a scope; `reconcile_drift` walks the tree + hashes files). Emit `notifications/progress` per the existing convention so the chat UI shows a status line:

- `repair_scope minimal`: `"running integrity_check"` (0.0), `"pruning orphans"` (0.5), `"reopening workspace"` (0.8).
- `repair_scope rebuild`: `"archiving old DB"` (0.0), `"cold-indexing"` (0.1, the long step), `"finalising"` (0.95).
- `reconcile_drift`: `"walking source tree"` (0.0), `"comparing hashes"` (0.3), `"applying changes"` (0.7).

The 0.1 → 0.95 jump in `rebuild` is correct; the cold-index dominates wall-clock time. A future change could thread per-file progress from inside `RoslynIndexer.IndexAllAsync`, but threading is out of scope here.

## Risks / Trade-offs

- **[Risk] Retry storm on common-cause failure.** Three scopes share the same dotnet-upgrade-in-flight cause; each retries 3 times serially per scope, total wait = N × 31s. → Mitigation: scope bring-up is already concurrent (per `ScopeHost.ExecuteAsync`'s parallel `PrepareScopeAsync` calls), so the per-scope wall-clock waits overlap. Worst case is ~31s total, not N × 31s.

- **[Risk] `repair_scope rebuild` against an enormous solution takes minutes; the agent might time out before the response.** → Mitigation: progress notifications keep the chat UI alive. The MCP `tools/call` request itself doesn't have a server-imposed timeout; long calls return when complete. Agent-side timeouts (Claude Code's are generous) bound this case in practice; if it's a real problem, a future change could split `rebuild` into `start-rebuild` (returns immediately with a job id) + `rebuild-status` (poll). Out of scope here.

- **[Risk] `reconcile_drift` on a non-source directory (someone points the scope root at `~/`).** Walking the home directory + computing 100k SHA-256s would take minutes. → Mitigation: `max_files` cap (default 1000, hard cap 50000) bounds worst-case wall-clock. Beyond the cap, the tool returns `scanned_count = max_files, partial = true` so the agent knows it didn't finish the walk.

- **[Trade-off] No way to disable the retry budget.** A user who wants the old "fail-fast on workspace open" behavior is out of luck. → Accepted. The behavior change is bounded (worst case 31s before today's outcome); making it configurable adds a knob with no clear use case beyond "I want fail-fast for a CI smoke test", which can override via env var if needed in a future change.

- **[Trade-off] `repair_scope minimal` refuses corrupt DBs.** The agent has to escalate explicitly to `rebuild`. → Accepted. Auto-escalation hides the seriousness of corruption from the agent; explicit escalation makes the destructive step a deliberate choice.

## Migration Plan

Land in five small commits:

1. `LiveIndexService.RetryOpenAndIndexAsync` + bounded backoff loop. Existing call sites switch over. `WorkspaceOpenRetryTests` covers the contract.
2. `IEmbeddingsStore.PruneOrphanedAsync` + impl + unit test.
3. `RoslynIndexer.WalkSourceTreeAsync` (the read-only walker `reconcile_drift` reuses) + unit test.
4. `ScopeHost.TriggerRebuildAsync` (drives cold-index from a tool body) + `repair_scope` tool + `RepairScopeToolTests`.
5. `reconcile_drift` tool + `ReconcileDriftToolTests` + README/CLAUDE.md docs.

**Rollback**: revert each commit independently. The retry change is the most observable; if it causes problems, reverting it restores today's first-throw-wins behavior.

## Open Questions

- **Should `reconcile_drift` accept a `dry_run = true` flag?** The structured response already tells you what would change; a separate flag means returning the diff without applying. Lean: yes — adds three lines and gives the agent a "plan-then-execute" pattern. Will land as `dry_run` arg defaulting to `false` in this change.

- **Should the bounded-retry budget be configurable via env var?** Lean: not in this change. If demand emerges, `SOURCEGRAPH_WORKSPACE_OPEN_RETRY_ATTEMPTS` (default 3) and `..._BACKOFF_MS` (default `1000,5000,25000`) is a one-line follow-on.

- **Should `repair_scope rebuild` clear the embeddings store too?** Lean: yes — it's part of the "nuclear from sources" semantics. Embeddings regenerate on next semantic_search call (cold start) or via a future per-symbol embedding job. Clarified in the spec scenario.
