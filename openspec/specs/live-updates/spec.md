# Live Updates

## Purpose

Keep the code graph in sync with the developer's working tree by watching for
filesystem changes (edits, creates, deletes, renames) and git branch
switches, then triggering incremental reindexing through the indexer.
## Requirements
### Requirement: Watch C# files under the solution root
`SolutionWatcher` SHALL emit a debounced batch of changed `.cs` paths under
the solution directory while ignoring `obj/`, `bin/`, `.git/`, and
`.sourcegraph/` subtrees.

#### Scenario: Edit a .cs file
- **WHEN** a `.cs` file under the solution root is created, modified,
  deleted, or renamed
- **THEN** within `debounce` time (default 200ms), a `FileChangeBatch` with
  `Reason = FileSystemEvent` and the affected paths is yielded by
  `ReadAllAsync`

#### Scenario: Build artifact churn is filtered out
- **WHEN** a file under `.../obj/...`, `.../bin/...`, `.../.git/...`, or
  `.../.sourcegraph/...` changes
- **THEN** the event is dropped at `ShouldIgnore` and never reaches the
  pending set

### Requirement: Detect git HEAD changes in checkouts and worktrees
`SolutionWatcher` SHALL watch the active `HEAD` file for the solution's
working tree, supporting both standard checkouts (where `<root>/.git` is a
directory) and worktrees (where `<root>/.git` is a file containing
`gitdir: <main>/.git/worktrees/<name>`).

#### Scenario: Branch switch in a normal checkout
- **WHEN** `<root>/.git` is a directory and `git checkout` updates
  `<root>/.git/HEAD`
- **THEN** a `FileChangeBatch` with `Reason = GitHeadChanged` is emitted

#### Scenario: Branch switch in a git worktree
- **WHEN** `<root>/.git` is a file pointing at
  `<main>/.git/worktrees/<name>` and `git checkout` updates
  `<main>/.git/worktrees/<name>/HEAD`
- **THEN** `ResolveGitHeadDir` returns the worktree's gitdir, a watcher is
  installed there, and a `GitHeadChanged` batch is emitted on update

### Requirement: Branch switch triggers a full reindex
`LiveIndexService` SHALL call `RoslynIndexer.ReloadAndIndexAllAsync` when it
receives a `GitHeadChanged` batch.

#### Scenario: Consume a GitHeadChanged batch
- **WHEN** the watcher emits `FileChangeBatch { Reason = GitHeadChanged }`
- **THEN** the service logs `"Git HEAD changed; running full reindex"`,
  reopens the workspace, runs `IndexAllAsync`, and logs the elapsed time

### Requirement: File-change batches drive incremental reindex
`LiveIndexService` SHALL pass `FileSystemEvent` batches to
`RoslynIndexer.IndexChangedFilesAsync` and log the resulting file count and
elapsed time.

#### Scenario: Edit triggers incremental reindex
- **WHEN** a `FileSystemEvent` batch arrives with one or more solution paths
- **THEN** the service awaits `IndexChangedFilesAsync(paths)` and logs
  `"Reindexed {N} changed file(s) in {Elapsed}"`

### Requirement: Initial-index errors don't crash the host
`LiveIndexService` SHALL log and recover from an initial-index failure
without aborting the MCP host process.

#### Scenario: OpenAsync or IndexAllAsync throws on startup
- **WHEN** the initial `OpenAsync` / `IndexAllAsync` throws (workspace
  failure, etc.)
- **THEN** the service logs `"Initial indexing failed; live updates will
  not run"` at error level, does not start the watcher, and returns from
  `ExecuteAsync` without crashing the process

### Requirement: Per-scope indexing progress source
`LiveIndexService` SHALL expose a per-scope `IIndexingProgressSource` whose `Reported` event fires at coarse phase checkpoints during initial indexing: `opening workspace` (Progress = 0.0), `indexing` (Progress = 0.5), and `ready` (Progress = 1.0). Each emission SHALL set `Total = 1.0`. Messages SHALL be drawn from the documented set above with no interpolated values; no file paths, symbol names, or other user-controlled substrings.

The progress source SHALL fire `Reported` only while initial indexing is in progress for that scope; after `ready` is emitted, it SHALL set its `IsReady` flag to `true` and stop emitting. Subsequent re-indexes (file-watcher driven) SHALL NOT emit through the source — the source's contract is "first index only."

Per-document progress (e.g. `pass 1: <N>/<M> files`) is intentionally out of scope for v1: `RoslynIndexer.IndexAllAsync` does not currently expose a per-document callback to outside callers, and adding one is its own change. The coarse three-event taxonomy is sufficient to remove the silent-spinner anti-feel; future revisions may extend the taxonomy when the indexer surface gains the hook.

#### Scenario: Cold-start emissions follow the documented phase taxonomy
- **WHEN** `LiveIndexService` starts a fresh index for any scope
- **THEN** the scope's progress source fires `Reported` exactly three times in order: `Message = "opening workspace"` with `Progress = 0.0`, `Message = "indexing"` with `Progress = 0.5`, and `Message = "ready"` with `Progress = 1.0`

#### Scenario: Progress fractions are monotonically increasing
- **WHEN** a scope's progress source emits any sequence of two or more events
- **THEN** each successive event's `Progress` value is strictly greater than its predecessor, with all values in `[0.0, 1.0]`

#### Scenario: Source stops emitting after ready
- **WHEN** a scope's `IsReady` flag is `true` and a file change triggers an incremental re-index via `IndexChangedFilesAsync`
- **THEN** the progress source emits zero new `Reported` events for that incremental pass; observable progress for incremental indexing is left to a future, separate change

#### Scenario: Messages contain no user input
- **WHEN** any `Reported` event fires
- **THEN** its `Message` is exactly one of `"opening workspace"`, `"indexing"`, or `"ready"`; no caller-supplied substring is interpolated

### Requirement: Cold-start progress forwarding on tool calls
The MCP tool-call wrapper SHALL, before awaiting `ScopeHost.Ready` on a tool call whose scope has not yet completed initial indexing, subscribe to that scope's `IIndexingProgressSource` and forward each emitted `ProgressNotificationValue` to the call's injected `IProgress<ProgressNotificationValue>`. The wrapper SHALL unsubscribe in a `finally` block whether the await completes normally, throws, or is cancelled. The wrapper SHALL skip the subscribe / forward / unsubscribe path entirely when `ScopeHost.Ready.IsCompleted` is already `true` (warm path).

#### Scenario: Cold-start tool call with progressToken sees phase progress
- **WHEN** an MCP client issues `find_definition(symbol = "Calculator")` against a freshly-started server whose scope has not yet finished initial indexing, and the request includes a `progressToken`
- **THEN** the server emits multiple `notifications/progress` messages tagged with that token — one per progress-source event — until the scope reaches `ready`, at which point the underlying `find_definition` runs and the final `tools/call` response carries the result; the messages match the patterns documented in "Per-scope indexing progress source" above

#### Scenario: Cold-start tool call without progressToken emits no progress
- **WHEN** an MCP client issues `find_definition` during cold-start without a `progressToken`
- **THEN** the server emits zero `notifications/progress` messages; the wrapper still subscribes (the no-op `IProgress` instance the SDK injects swallows the calls) and the tool result returns as today

#### Scenario: Warm-path tool call does not subscribe
- **WHEN** an MCP client issues `find_definition` after the scope's `Ready` has already completed
- **THEN** the wrapper observes `IsCompleted = true`, skips the progress-source subscription entirely, and the tool runs without any subscribe / unsubscribe overhead

#### Scenario: Cancelled cold-start call tears down subscription
- **WHEN** a client cancels a `tools/call` mid-cold-start (sends `notifications/cancelled` while `Ready` is still pending)
- **THEN** the wrapper's `finally` block unsubscribes from the progress source; subsequent progress emissions for the still-running indexing pass do not invoke the cancelled call's injected `IProgress`

<!-- Server-startup `notifications/message` requirement deferred to a follow-up change.
     Scope: needs a hook on the SDK's IMcpServer instance (which is constructed by the host
     after LiveIndexService starts) plus a way to emit notifications/message frames before
     any tools/call has arrived. v1 of this change ships the per-scope progress source +
     tool-call wrapper forwarding; the wire-level startup signal can be added once we have
     a clean handle on the IMcpServer. The existing stderr `ILogger` lifecycle output is
     unchanged. -->

### Requirement: Bounded retry on initial workspace open

`LiveIndexService` SHALL wrap the workspace-open + initial-index sequence in a bounded retry loop of up to 4 attempts (1 initial + 3 retries), with exponential backoff `[1000ms, 5000ms, 25000ms]` between attempts. The first attempt runs immediately; subsequent attempts run after the corresponding backoff delay.

A retry SHALL fire when any attempt throws an exception other than `OperationCanceledException`. `OperationCanceledException` SHALL be rethrown immediately without retry — cooperative shutdown wins.

When an attempt N > 1 succeeds, the service SHALL emit a heal event with `kind = "workspace-open-retried"`, `ok = true`, `details = "succeeded on attempt N"`. When all 4 attempts fail, the service SHALL emit a heal event with `kind = "workspace-open-retried"`, `ok = false`, `details = "all 4 attempts failed: <last exception message>"`, then proceed with today's path: log at error level and mark the scope `degraded` with the original exception's message.

The retry SHALL NOT fire on watcher-driven incremental reindexes (`IndexChangedFilesAsync`) — only on the cold-index path. Steady-state reindex failures continue to follow the existing per-batch logging path (`"Scope `{Id}`: failed to apply change batch"`).

#### Scenario: Transient workspace failure recovers on second attempt
- **GIVEN** `RoslynIndexer.OpenAsync` throws `IOException("dotnet restore in progress")` on the first attempt and succeeds on the second
- **WHEN** the cold-index path fires
- **THEN** the second attempt's success is recorded; total wall-clock elapsed is at least 1000ms (the 1s backoff); `heals.jsonl` contains one line with `kind = "workspace-open-retried"`, `ok = true`, `details = "succeeded on attempt 2"`; the scope is marked `ok` in the registry; no other heal event is emitted

#### Scenario: All retries exhausted, scope marked degraded
- **GIVEN** `RoslynIndexer.OpenAsync` throws on all 4 attempts with the same exception message
- **WHEN** the cold-index path fires
- **THEN** total wall-clock elapsed is at least 31000ms (1s + 5s + 25s backoffs); `heals.jsonl` contains one line with `kind = "workspace-open-retried"`, `ok = false`, `details = "all 4 attempts failed: <message>"`; the scope is marked `degraded` with the exception message in the registry's `status_message`; the host stays up

#### Scenario: Cancellation during backoff is honoured immediately
- **GIVEN** the workspace open throws on the first attempt and the cancellation token is signalled mid-backoff (during the 1s wait)
- **WHEN** the await completes with `OperationCanceledException`
- **THEN** the retry loop exits immediately without a third attempt; no heal event is written; the scope's status is left at whatever the calling path sets on cancellation (typically not modified)

#### Scenario: Steady-state reindex failure is unchanged
- **GIVEN** the cold-index succeeded (or the bounded-retry path is not active because the scope is already past initial bring-up)
- **WHEN** a watcher-driven `IndexChangedFilesAsync` call throws an `IOException` for one batch
- **THEN** the failure is logged at error level via the existing `"Scope `{Id}`: failed to apply change batch"` path; no retry is attempted at the steady-state layer; no heal event is written; the scope remains `ok` (the next batch may succeed)

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
