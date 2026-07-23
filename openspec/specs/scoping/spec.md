# Scoping

## Purpose

Decompose a repository into named, user-defined indexable units ("scopes")
so multi-solution monorepos, large solutions, and isolation needs (vendor
code, generated code, test fixtures) can be queried separately or together
from one MCP server. Each scope owns a SQLite file under
`<repo>/.sourcegraph/scopes/<id>.db` and is registered in a small
`_meta.db`; queries fan out across one or more scopes and merge results by
canonical key with the originating scope tagged on every row.
## Requirements
### Requirement: Scope as first-class entity
The system SHALL model a scope as `(id, name, root, project_set, isolated, last_indexed_at)` where `id` is a kebab-case slug unique within the repo, `project_set` is one of `solutions[]`, `projects[]`, or `paths[]` (csproj globs), and `isolated` defaults to `false`.

#### Scenario: Synthesise a default scope
- **WHEN** a server starts in a repo with no `.sourcegraph.json` and exactly one `.slnx` discovered at the root
- **THEN** an in-memory scope `{ id: "default", solutions: ["<discovered>"], isolated: false }` is registered and used for every query whose `scope` is omitted

#### Scenario: Read scopes from config
- **WHEN** `.sourcegraph.json` lists three scopes (one solutions-based, one paths-based, one isolated)
- **THEN** all three appear in the registry and `list_scopes` reports each with the right kind, isolation flag, and root

### Requirement: Per-scope physical isolation
Each scope SHALL persist its graph in `<repo>/.sourcegraph/scopes/<id>.db`; a separate `<repo>/.sourcegraph/_meta.db` SHALL hold the `scopes` registry. A new scope's per-scope DB SHALL be created on its first index, whether the scope was added at startup or live via a `.sourcegraph.json` edit.

#### Scenario: New scope creates a new file (restart path)
- **WHEN** a new scope `frontend` is added to `.sourcegraph.json` and the server is restarted
- **THEN** `.sourcegraph/scopes/frontend.db` is created on first index, distinct from any other scope's DB

#### Scenario: New scope creates a new file (live path)
- **WHEN** a new scope `frontend` is added to `.sourcegraph.json` while the server is running
- **THEN** within the watcher's debounce + cold-index window, `.sourcegraph/scopes/frontend.db` is created and `list_scopes` reports the new scope; no other scope's DB is touched

### Requirement: One-shot migration from single-DB layout
On startup, if a legacy `<repo>/.sourcegraph/graph.db` exists and `<repo>/.sourcegraph/scopes/default.db` does not, the system SHALL atomically move the legacy file to the new location.

#### Scenario: Existing user upgrades
- **WHEN** a v0.1.x graph.db is present at startup of the new server
- **THEN** the file is renamed to `scopes/default.db`, no data is lost, and the synthesised `default` scope opens it without re-indexing

### Requirement: Cross-scope query fan-out and merge
Queries that target multiple scopes SHALL execute per-scope and merge results in process by grouping on `canonical_key`; rows attributed to multiple scopes appear once with `scope` listing every scope they came from.

#### Scenario: Shared library appears in two scopes
- **WHEN** scopes `frontend` and `backend` both index a shared library that defines symbol `Foo` with the same canonical key
- **THEN** a `find_definition(symbol = "Foo", scope = "*")` query returns one row whose `scope` field is `["backend", "frontend"]` (sorted)

### Requirement: Isolation flag affects fan-out default
When a scope has `isolated: true`, it SHALL be excluded from `scope = "*"` fan-out by default; it is only queried when listed explicitly in `scope = ["vendor"]`.

#### Scenario: Vendor scope opt-out
- **WHEN** `find_references(symbol = "AuthService", scope = "*")` runs against a config with `frontend, backend, vendor (isolated)`
- **THEN** results come from `frontend` and `backend` only; rows from `vendor` are excluded unless `scope` explicitly includes `"vendor"`

### Requirement: Partial scope reports per-project failures
A scope whose cold-index completed and produced symbols for at least one project, but where one or more projects or files failed, SHALL be marked with status `partial`. A partial scope SHALL be queryable: tools targeting `scope = "<id>"` against a partial scope SHALL return whatever symbols were indexed (best-effort); `scope = "*"` fan-out SHALL include partial scopes alongside `ok` scopes (and SHALL also reach `degraded` scopes per the existing `Degraded scope doesn't crash the host` requirement — those contribute a `"scope is degraded: <error>"` block to the merged response instead of running the query). The registry SHALL persist the failure lists alongside the scope row so `list_scopes` returns accurate failure detail even after a server restart that hasn't yet re-triggered indexing.

A scope status SHALL be:
- `ok` — every project and file indexed cleanly; `failed_projects` and `failed_files` are empty
- `indexing` — cold index in progress
- `partial` — at least one project produced symbols and at least one project or file failed; `failed_projects` and/or `failed_files` are non-empty
- `degraded` — workspace failed to open, OR every project failed (zero files indexed), OR an unanticipated exception escaped to the scope-level safety net; tools return `"scope is degraded: <error>"`

#### Scenario: Solution with one bad project lands `partial`, not `degraded`
- **GIVEN** a solution containing two projects where one fails to compile
- **WHEN** `LiveIndexService` cold-indexes the scope
- **THEN** the scope's status is `partial` (not `degraded`); `list_scopes` reports `failed_projects` containing the failed project's name and reason; tools targeting the scope return symbols from the working project; `scope = "*"` fan-out includes this scope's results

#### Scenario: Partial-scope failure lists survive restart
- **GIVEN** a scope previously cold-indexed to `partial` status with one entry in `failed_projects`
- **WHEN** the server is restarted and `list_scopes` is invoked before any re-index runs
- **THEN** the partial status, the failed project's name, and the reason are returned from the persisted registry row — operators see accurate failure detail without waiting for a re-index

#### Scenario: All-projects-fail scope is `degraded`, not `partial`
- **GIVEN** a solution where every project's compilation fails
- **WHEN** `LiveIndexService` cold-indexes the scope
- **THEN** the scope's status is `degraded` (because zero files were indexed); `failed_projects` enumerates every project; tools targeting the scope return the existing degraded-scope error message; `scope = "*"` reaches the scope as today (per `Degraded scope doesn't crash the host`) and contributes the per-scope error block to the merged response without breaking the call

### Requirement: Degraded scope doesn't crash the host
If a scope's initial index fails with no recoverable output (workspace error, missing solution, every project failed to compile, or an unanticipated exception escaped to the scope-level safety net), the registry SHALL mark that scope as `degraded`; queries against it return an empty result with a status note, while every other scope continues to serve. A scope with at least one project that produced symbols SHALL be marked `partial` instead — `degraded` is reserved for the no-recoverable-output case.

#### Scenario: Bad solution path
- **WHEN** `.sourcegraph.json` lists a `tools.slnx` that fails to load
- **THEN** `list_scopes` reports `tools` with `status: degraded` and an error message; queries with `scope = "tools"` return `"scope is degraded: <error>"`; queries with `scope = "*"` succeed against the healthy scopes

#### Scenario: Boundary between degraded and partial
- **GIVEN** a solution that opens successfully but where every project's compilation fails
- **WHEN** `LiveIndexService` cold-indexes the scope
- **THEN** the scope is `degraded` (not `partial`) because zero files were indexed; the `failed_projects` list still enumerates every project so operators see why every project failed

### Requirement: list_scopes tool
The server SHALL expose a `list_scopes` tool that returns each scope's id, name, root, project count, last-indexed timestamp, isolation flag, status, and (when non-empty) the lists of failed projects and failed files.

The structured output schema SHALL include:
- `failed_projects: { name: string, reason: string }[]` — projects whose compilation could not be obtained during the most recent cold index
- `failed_files: { path: string, reason: string }[]` — files whose Pass 1 walk threw during the most recent cold index

Both arrays SHALL be omitted when empty (or rendered as empty arrays — the JSON shape is consistent), so healthy scopes' output is unchanged from the prior contract. The markdown rendering SHALL surface the failure detail (e.g., as a sub-list under the affected scope's row) when the arrays are non-empty so operators reading the human-friendly output see the failure attribution without needing to inspect `structuredContent`.

#### Scenario: Discover available scopes
- **WHEN** the agent invokes `list_scopes()`
- **THEN** the response is a markdown table with one row per registered scope; healthy scopes show only id, name, root, project count, last-indexed timestamp, isolation flag, and `status: ok` — the failure-list columns are suppressed

#### Scenario: List a partial scope
- **GIVEN** a scope `backend` with `status: partial` whose `failed_projects` contains `Legacy.WebForms` (reason: `compilation null`)
- **WHEN** `list_scopes` is invoked
- **THEN** the markdown row for `backend` shows `status: partial` and a sub-list (or column) carrying `Legacy.WebForms — compilation null`; the `structuredContent.failed_projects` array contains exactly one entry with `name: "Legacy.WebForms"` and a non-empty `reason`; `failed_files` is empty

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

### Requirement: Scope `language` field
A scope entry in `.sourcegraph.json` MAY carry an optional `language` field whose value is a kebab-case string identifying the scope's primary language (e.g. `"typescript"`, `"python"`, `"go"`). The loader SHALL accept any kebab-case value and SHALL NOT enforce a closed list at this version. When present, `scopes info <name>` surfaces the value; when absent, it renders `(unset)`. (`scopes list` does not display the field at this version — `scopes info` is the dedicated surface for per-scope metadata.)

#### Scenario: Loader accepts a kebab-case language
- **WHEN** a scope declares `"language": "typescript"`
- **THEN** `ScopeConfigLoader.Load` succeeds, the resulting `Scope` (or sister runtime config) carries the value, and `scopes info` renders `Language: typescript`

#### Scenario: Loader rejects a non-kebab-case language
- **WHEN** a scope declares `"language": "TypeScript"` or `"language": "type_script"` or `"language": ""`
- **THEN** the loader SHALL throw `ScopeConfigException` identifying the offending value and the scope name

#### Scenario: Loader accepts an unknown-but-kebab-case language
- **WHEN** a scope declares `"language": "phyton"` (a typo)
- **THEN** the loader SHALL succeed; the value is surfaced verbatim. Mis-routing is a soft-registry concern surfaced via diagnostics, not a load-time failure.

### Requirement: Scope `enrichment` field (forward-declared)
A scope entry MAY carry an optional `enrichment` object with a single nested `lsp` field. The `lsp` field SHALL declare a `command` (non-empty string) and an optional `args` (string array, defaulting to `[]`). The loader SHALL parse and validate the shape; the host SHALL surface the configuration via `scopes info` but SHALL NOT consume it at this version.

#### Scenario: Loader round-trips the enrichment block
- **WHEN** a scope declares `"enrichment": { "lsp": { "command": "typescript-language-server", "args": ["--stdio"] } }`
- **THEN** `Save(Load(...))` reproduces the same JSON, the `Scope` exposes the typed config, and `scopes info` renders the `Enrichment` section with `(no consumer at this version)` annotation

#### Scenario: Loader rejects an empty `command`
- **WHEN** a scope declares `"enrichment": { "lsp": { "command": "" } }` or omits `command` entirely
- **THEN** the loader SHALL throw `ScopeConfigException` identifying the offending scope and the missing/empty `command` field

#### Scenario: Loader rejects unknown enrichment keys at v1
- **WHEN** a scope declares `"enrichment": { "lsp": {...}, "embeddings": {...} }`
- **THEN** the loader SHALL throw `ScopeConfigException` reporting `embeddings` as an unknown enrichment key. Future enrichment kinds (embeddings, static analysis) are reserved-but-rejected at this SDK version, mirroring the canonical-key scheme posture; later changes may lift them.

#### Scenario: Inert enrichment annotated in `scopes info`
- **WHEN** a user sets `enrichment.lsp` and runs `scopes info <name>`
- **THEN** the output SHALL show the configured command and args, plus an explanatory annotation that no plugin claims this enrichment at the current version, so the operator does not assume the LSP is being launched

### Requirement: Live scope lifecycle from config edits
The system SHALL bring up, tear down, and replace per-scope hosts in response to validated `.sourcegraph.json` saves observed by `ScopeConfigWatcher`, without restarting the server. The lifecycle stages SHALL match the startup path: a new scope passes through `indexing → ok | degraded` and signals `ScopeHost.Ready` exactly as a startup scope does.

#### Scenario: Live add goes through full lifecycle
- **WHEN** an `add` delta is applied for a new scope
- **THEN** `LiveIndexService` calls the same `PrepareScopeAsync` → `RunInitialIndexAsync` → `StartWatcher` chain as for a startup scope, the scope's `status` transitions through `indexing` to `ok` or `degraded`, and `ScopeHost.Ready` completes once the cold index settles

#### Scenario: Live remove disposes cleanly
- **WHEN** a `remove` delta is applied for an existing scope
- **THEN** `ScopeRouter.Unregister` removes the scope from the router, the per-scope `SolutionWatcher` is disposed, the embeddings drain is stopped, the indexer + store are disposed; the per-scope DB on disk is *not* deleted

#### Scenario: Live modify atomically replaces the host
- **WHEN** a `modify` delta is applied (a scope's `solutions`/`projects`/`paths`/`exclude`/`isolated` changed)
- **THEN** a fresh `ScopeHost` is constructed and the router swap is observably atomic — no concurrent `TryGet(id)` ever returns null during the replacement; the displaced host is disposed after the configured grace period; the new host is brought up via `RunInitialIndexAsync` exactly as a startup scope is

#### Scenario: Live remove deletes the registry row
- **WHEN** a `remove` delta is applied
- **THEN** the scope's row in `_meta.db` is deleted (no tombstone); the per-scope `<id>.db` file on disk is preserved; subsequent `list_scopes` does not report the scope, and re-adding the same scope id later picks up the existing on-disk DB without a cold reindex

#### Scenario: Live default-scope change is metadata-only
- **WHEN** only the `default_scope` field changed
- **THEN** `ScopeRouter.SetDefaultScope` is called with the new id and no scope's data is touched; no scope is reindexed and no host is replaced

### Requirement: Plugin changes are not live-reloadable
The system SHALL NOT load, unload, or reconfigure plugins in response to a `.sourcegraph.json` edit. The plugin set established at server startup SHALL be the plugin set the server runs with for its entire lifetime. A change to the top-level `plugins[]` array detected by `ScopeConfigWatcher` SHALL be logged at warn level with a message stating that a server restart is required to apply the change.

#### Scenario: Adding a plugin entry at runtime is non-effective
- **WHEN** a new entry is added to the top-level `plugins[]` array of `.sourcegraph.json` while the server is running
- **THEN** the running plugin set is unchanged, no `AssemblyLoadContext` is created, no analyzer is loaded, and a single warn-level log entry is emitted naming the change and instructing the user to restart

#### Scenario: Plugin and scope change in the same save
- **WHEN** a single `.sourcegraph.json` save adds a new scope *and* adds a new plugin entry
- **THEN** the scope diff is applied normally (new scope brought up live), the plugin diff is logged-and-skipped, and the running plugin set is unchanged
