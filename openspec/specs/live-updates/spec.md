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
