## ADDED Requirements

### Requirement: Watch `.sourcegraph.json` for scope-config changes
`ScopeConfigWatcher` SHALL watch `<repoRoot>/.sourcegraph.json` and emit a parsed `ScopeConfig` to the live diff-and-apply path on each successful save. Malformed saves SHALL be logged at info level and emit no event; the running scope set SHALL remain unchanged until the next valid save.

#### Scenario: Edit adds a new scope
- **WHEN** `.sourcegraph.json` is edited to add a new scope `frontend` while the server is running
- **THEN** within `debounceMs + cold-index time`, `list_scopes` reports the new scope (initially with `status: indexing`, then settling to `ok` or `degraded`); the existing scopes are not disturbed

#### Scenario: Edit removes an existing scope
- **WHEN** an existing scope `tools` is removed from `.sourcegraph.json`
- **THEN** within `debounceMs + grace period`, `list_scopes` no longer reports it; the scope's per-scope DB on disk is preserved (orphan cleanup is out of scope)

#### Scenario: Edit changes `default_scope`
- **WHEN** `default_scope` is changed from `backend` to `frontend`
- **THEN** the next tool call that omits the `scope` argument resolves to `frontend`; no scope is reindexed and no host is replaced

#### Scenario: Edit modifies a scope's project-set
- **WHEN** an existing scope's `solutions`, `projects`, `paths`, `exclude`, or `isolated` field changes
- **THEN** the running host for that scope is atomically replaced (the new host is registered before the old is unregistered) and the old host is disposed after a configurable grace period; other scopes are not disturbed

#### Scenario: Malformed save during edit
- **WHEN** `.sourcegraph.json` is saved with a JSON syntax error or schema violation
- **THEN** `ScopeConfigLoader.Load` throws `ScopeConfigException`, the watcher logs at info level, no event is emitted, and the live scope set is unchanged

#### Scenario: File is deleted
- **WHEN** `.sourcegraph.json` is deleted while the server is running
- **THEN** the watcher loads `ScopeConfigLoader.Synthesise(repoRoot, …)` and applies that as the new config; every scope except the synthesised `default` is removed; `default` is added if it wasn't already present

#### Scenario: File is renamed away from or into the repo root
- **WHEN** `.sourcegraph.json` is renamed (`git mv .sourcegraph.json other.json`, or its inverse `git mv other.json .sourcegraph.json`)
- **THEN** the watcher re-evaluates `<repoRoot>/.sourcegraph.json`'s presence and applies whichever config that resolves to (synthesised default if absent, parsed scope set if present), regardless of which direction the rename went

#### Scenario: `plugins[]` entry is added or removed
- **WHEN** the `plugins[]` array in `.sourcegraph.json` changes (add, remove, or modify a plugin entry)
- **THEN** the watcher logs a warning naming the change and stating that plugin changes require a server restart; no plugin is loaded or unloaded; the scope diff is still applied if any scopes also changed in the same save

### Requirement: Atomic-swap with deferred disposal during scope replacement
When a scope is replaced live (modified project-set), the swap into `ScopeRouter` SHALL be observably atomic: at every moment during the replacement, a concurrent `ScopeRouter.TryGet(id)` SHALL return either the old host or the new host, never null and never two different host instances in succession from a single observer. The displaced host's `DisposeAsync` SHALL be deferred by `LiveIndexConfig.ScopeReplaceGraceMs` (default 5000) so in-flight tool queries that already hold a reference to the old host can complete against its stores.

#### Scenario: Concurrent observers see a consistent host
- **WHEN** a thread is calling `ScopeRouter.TryGet("foo")` in a tight loop while another thread applies a modify delta for `foo`
- **THEN** every observation is either the old `ScopeHost` or the new `ScopeHost`; no observation returns `null`, and no observer transitions back from new to old

#### Scenario: In-flight query during scope replacement
- **WHEN** a tool call against scope `foo` has resolved its host but not yet completed its store reads, and `.sourcegraph.json` is saved with a modification to `foo`'s `solutions[]`
- **THEN** the tool call completes successfully against the old host (whose store has not yet been disposed); subsequent tool calls resolve to the new host
