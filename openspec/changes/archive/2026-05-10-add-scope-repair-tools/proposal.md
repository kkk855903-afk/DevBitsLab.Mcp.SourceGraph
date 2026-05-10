## Why

`add-scope-health-surface` made scope failure modes *observable*: orphan DBs are archived, missing-DB and stuck-`indexing` rows are marked `degraded` with a discriminative message, and `verify_scope` exposes row counts, integrity-check, and a drift sample. What it deliberately did *not* do is take any *action* on the surfaced state.

That leaves two concrete user pains unresolved:

1. **Transient downstream failures** mark a scope `degraded` permanently within a single process. The most common case: `dotnet restore` racing with cold-index, MSBuild SDK in the middle of an upgrade, network-FS hiccup. A single retry would convert "degraded forever" into "ok after 30 seconds", but today's `LiveIndexService` has no retry — first throw wins.

2. **`verify_scope` reports drift but the agent has no way to fix it short of restarting the server.** The watcher only observes events that happen while it's running; files moved/deleted/added between server runs are silently absent from the index. Users notice when `find_references` returns nothing for a symbol they can grep for in a file the watcher missed.

This change introduces the **action layer** on top of Phase 1's observability:

- A bounded retry on workspace open inside `LiveIndexService` so transient failures get up to three free shots before the scope lands in `degraded`.
- An agent-driven `repair_scope` tool with two modes (`minimal` for cheap fixes, `rebuild` for nuclear-from-sources reset).
- An agent-driven `reconcile_drift` tool that walks the source tree, compares to the `files` table, and reindexes drifted files / removes vanished ones.

Agent-driven (rather than autonomous) is deliberate for the heavyweight actions. The agent has tone-of-voice context the server doesn't ("user said the index looks stale", "we just merged a giant refactor", "find_references is returning nothing"); a tool that fires only when the agent asks lets the agent compose `verify_scope` → `reconcile_drift` → `verify_scope` without the server second-guessing.

The bounded retry on workspace open is the one autonomous behavior added in this change. It's safe because: (a) it only fires on cold-index startup, not in steady state; (b) the failure surface (workspace open) is well-bounded and historically transient; (c) the cap of 3 attempts with exponential backoff (1s, 5s, 25s) bounds the total wait at ~31s in the worst case before today's `degraded` outcome.

## What Changes

- **`LiveIndexService` cold-index path retries the workspace-open + initial-index sequence up to three times** with exponential backoff (1s, 5s, 25s) before marking the scope `degraded`. Each retry attempt is logged at warning level and a heal event `kind = "workspace-open-retried"` is emitted on success (carrying `details = "succeeded on attempt N"`); a final failure after attempt 3 follows today's path (mark `degraded`, persist registry row) plus emits a heal event `kind = "workspace-open-retried"` with `ok = false`. `OperationCanceledException` is rethrown from any attempt without retry — cooperative shutdown wins.

- **`repair_scope` MCP tool**. Args: `scope` (string, required), `mode` (string, default `"minimal"`, one of `"minimal" | "rebuild"`). 
  - `minimal`: run integrity check; if non-`ok`, refuse and instruct caller to use `rebuild`. Otherwise prune orphan `symbol_embeddings` rows whose `symbol_id` no longer exists in `symbols`. Re-attempt workspace open on the scope (using the same bounded-retry path as cold-index). Emit heal event `repair-scope-invoked` with `details = "mode=minimal"`.
  - `rebuild`: archive the current scope DB to `<repo>/.sourcegraph/orphans/<id>-rebuild-<utc-iso>.db` (preserving the user's evidence even when the rebuild is intentional), drop the scope file, run a fresh cold-index from `.sourcegraph.json`. Returns when the cold-index reaches `ok` or `degraded`. Emit heal event `repair-scope-invoked` with `details = "mode=rebuild, archived to orphans/…"`.
  - Both modes return `structuredContent` with `before_status`, `after_status`, `elapsed_ms`, `mode`, and a free-form `message`.
  - Both modes are idempotent — calling `minimal` twice in a row is a no-op the second time; calling `rebuild` twice produces two archive files (both preserved).

- **`reconcile_drift` MCP tool**. Args: `scope` (string, required), `max_files` (int, default 1000, capped at 50000). Walks the source tree under the scope's root (using the same `RoslynIndexer` file-discovery rules — same exclusion of `obj/`, `bin/`, `.git/`, `.sourcegraph/`), compares each discovered file's on-disk SHA-256 to the DB's `content_sha256`, and:
  - Files whose SHA differs are queued for incremental reindex (calls into `RoslynIndexer.IndexChangedFilesAsync`, the same path the watcher uses).
  - Files in the DB whose paths no longer exist on disk are removed via the existing per-file delete path.
  - Files on disk that aren't in the DB at all (added between server runs) are queued for indexing.
  Returns `structuredContent` with `scanned_count`, `reindexed_count`, `added_count`, `removed_count`, `unchanged_count`, `elapsed_ms`. Emits heal event `reconcile-drift-invoked` with `details = "scanned=N, reindexed=M, added=A, removed=R"`.
  Emits `notifications/progress` at three checkpoints: `"walking source tree"` (0.0), `"comparing hashes"` (0.3), `"applying changes"` (0.7).

## Capabilities

### Modified Capabilities

- `live-updates`: Adds requirement `Bounded retry on initial workspace open`. The existing `Initial-index errors don't crash the host` requirement is preserved; the retry inserts ahead of the eventual `degraded` outcome.

- `mcp-tools`: Adds two requirements — `repair_scope tool` and `reconcile_drift tool` — both with structured outcomes and progress reporting.

## Impact

- **Code (medium)**:
  - `Server/LiveIndexService.cs` (~50 lines): `RetryOpenAndIndexAsync` helper wrapping the existing `OpenAsync` + `IndexAllAsync` calls in a 3-attempt loop with exponential backoff. Replaces the direct call sites in `ColdIndexAsync` and `PrepareScopeAsync` paths.
  - `Server/Tools/ScopeTools.cs` (~200 lines): `RepairScopeAsync` + `ReconcileDriftAsync` tool bodies and result types.
  - `Server/Scoping/ScopeHost.cs` (~30 lines): expose `TriggerRebuildAsync(scope_id)` on the host so `repair_scope` can drive the cold-index path without re-implementing it.
  - `Storage/SqliteEmbeddingsStore.cs` (~20 lines): `PruneOrphanedAsync()` method — `DELETE FROM symbol_embeddings WHERE rowid NOT IN (SELECT id FROM symbols)`. (No-op when embeddings store isn't wired up.)
  - `Indexing/RoslynIndexer.cs` (~30 lines): expose `WalkSourceTreeAsync(scope, max_files)` returning `(path, on_disk_sha)` pairs without indexing — `reconcile_drift` reuses this for the comparison pass.
- **Spec**: 3 new requirements (1 `live-updates`, 2 `mcp-tools`), each with 3-5 scenarios.
- **Tests**:
  - `WorkspaceOpenRetryTests.cs` — fake workspace that throws on attempts 1+2 and succeeds on attempt 3; assert exactly 3 attempts, total elapsed ≥ 6s and < 35s, one `workspace-open-retried` heal line with `ok = true`.
  - `RepairScopeToolTests.cs` — `minimal` path on a healthy scope is a no-op; `minimal` path on a corrupt scope refuses; `rebuild` path archives the old DB and produces a fresh one with non-zero counts.
  - `ReconcileDriftToolTests.cs` — fixture with one file edited, one added, one removed; assert `reindexed_count = 1`, `added_count = 1`, `removed_count = 1`; assert no other rows in the DB are touched.
- **Public API**: Two new MCP tools (`repair_scope`, `reconcile_drift`). Two new heal kinds (`workspace-open-retried`, `repair-scope-invoked`, `reconcile-drift-invoked`).
- **Backward compatibility**: Pure additive on the tool surface. The retry inside `LiveIndexService` changes startup timing under failure conditions (today: fail in <1s; new: fail in up to ~31s) — flagged as a behavior change in the design doc but not a breaking-change in any contract sense (the eventual outcome is the same `degraded` state).
- **Documentation**: README + CLAUDE.md gain a "self-healing toolkit" subsection describing when to call which tool.
- **Depends on**: `add-scope-health-surface` must land first so `HealLog`, the `sourcegraph.heal.fired` Counter, and the orphans-directory convention exist.
