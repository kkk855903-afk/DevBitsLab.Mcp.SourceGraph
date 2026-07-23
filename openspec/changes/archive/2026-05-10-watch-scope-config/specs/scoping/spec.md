## MODIFIED Requirements

### Requirement: Per-scope physical isolation
Each scope SHALL persist its graph in `<repo>/.sourcegraph/scopes/<id>.db`; a separate `<repo>/.sourcegraph/_meta.db` SHALL hold the `scopes` registry. A new scope's per-scope DB SHALL be created on its first index, whether the scope was added at startup or live via a `.sourcegraph.json` edit.

#### Scenario: New scope creates a new file (restart path)
- **WHEN** a new scope `frontend` is added to `.sourcegraph.json` and the server is restarted
- **THEN** `.sourcegraph/scopes/frontend.db` is created on first index, distinct from any other scope's DB

#### Scenario: New scope creates a new file (live path)
- **WHEN** a new scope `frontend` is added to `.sourcegraph.json` while the server is running
- **THEN** within the watcher's debounce + cold-index window, `.sourcegraph/scopes/frontend.db` is created and `list_scopes` reports the new scope; no other scope's DB is touched

## ADDED Requirements

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
