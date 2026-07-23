## Why

`add-scoping` shipped with hot-reconfiguration explicitly listed as a non-goal — its [design.md](openspec/changes/archive/2026-05-03-add-scoping/design.md) says *"Editing `.sourcegraph.json` requires a server restart in v1; live config-watch is a future change."* The scoping spec bakes the restart into a literal scenario clause: *"WHEN a new scope `frontend` is added to `.sourcegraph.json` **and the server is restarted**"* ([scoping/spec.md:30](openspec/specs/scoping/spec.md:30)).

This deferred work has now bitten daily workflows in three concrete ways:

1. **CLI / running-server desync.** [`sourcegraph-mcp scopes add foo --solution …`](src/DevBitsLab.Mcp.SourceGraph.Server/Cli/ScopesCli.cs:52) writes `.sourcegraph.json` and the per-repo registry, but a server already attached to the same workspace keeps the old world view until restart. Agents querying the new scope get *"Unknown scope(s): foo"* until the human notices.
2. **Pulled-in commits.** When a teammate commits a new scope to `.sourcegraph.json` and a collaborator pulls, every running editor with an MCP session needs a restart. The server already watches `.git/HEAD` for branch switches and re-indexes; not watching `.sourcegraph.json` is the missing half of "the file system is the source of truth."
3. **Iteration friction.** Authoring `.sourcegraph.json` is a tight edit-validate loop today (typo → restart → see error → fix → restart). The CLI scaffold helps; live reload makes it disappear.

The lifecycle infrastructure to do this already exists. [`LiveIndexService`](src/DevBitsLab.Mcp.SourceGraph.Server/LiveIndexService.cs) owns a per-scope add/index/teardown chain (`PrepareScopeAsync` → `RunInitialIndexAsync` → `StartWatcher` → `DisposeAsync`). What's missing is a watcher pointed at `.sourcegraph.json` plus a diff-and-apply step that drives the existing lifecycle in response. The new watcher uses mtime polling (the original plan was `FileSystemWatcher` — pivoted during implementation when macOS's FSEventStream backend turned out not to deliver events for files at the watched directory's root reliably).

## What Changes

- **New `ScopeConfigWatcher`** in `src/DevBitsLab.Mcp.SourceGraph.Watcher/`. Watches `<repo>/.sourcegraph.json` with the same debounce + ignore-noisy-events pattern as `SolutionWatcher`. Emits `ScopeConfigChange` events on the existing `Channel<T>` pattern.
- **Diff-and-apply on [`LiveIndexService`](src/DevBitsLab.Mcp.SourceGraph.Server/LiveIndexService.cs)**. On each emitted change, compute `(added, removed, modified, defaultScopeChanged)` against the live `_router.All()` set and route each delta through the existing per-scope bring-up/tear-down. Reuses `PrepareScopeAsync`, `RunInitialIndexAsync`, `StartWatcher`, and `ScopeHost.DisposeAsync` unchanged.
- **`ScopeRouter.Unregister(string id)`** and **`ScopeRouter.Replace(string id, ScopeHost newHost)`** — the missing primitives on the router. `Replace` performs the swap under a single lock acquisition so no `TryGet` ever observes a window where the scope is missing or duplicated.
- **Re-use the existing `IScopeRegistry.RemoveAsync`** for the live tear-down path. Removed scopes vacate their `_meta.db` row entirely (no tombstone). The on-disk `<id>.db` is preserved and serves as the implicit re-add cache.
- **Parse-tolerance.** A malformed save (mid-edit `.sourcegraph.json`, partial atomic-rename, JSON syntax error) logs a warning at `info` level and leaves the live host set untouched. The next valid save is what gets applied.
- **`default_scope` fast path.** When the only delta is `default_scope`, no scope is touched; the change is `_router.SetDefaultScope(newId)` and a single info log.
- **Plugin changes ignored at runtime.** A delta in the top-level `plugins[]` array is logged at `warn` level (*"plugin changes require restart"*) and skipped. Hot-loading `AssemblyLoadContext`-isolated plugins is a separate, much larger problem and stays out of scope.
- **Atomic-swap disposal for modified scopes.** When a scope's `solutions`/`projects`/`paths`/`exclude`/`isolated` changes, `ScopeRouter.Replace` swaps the registered host under a single lock; disposal of the displaced host is deferred by a short grace window so any in-flight tool query that already resolved against the old host can finish.
- **CLI alignment.** [`scopes add` / `scopes remove`](src/DevBitsLab.Mcp.SourceGraph.Server/Cli/ScopesCli.cs:52) gain a one-line print stating that a running server will pick up the change live; otherwise the CLI is unchanged (the file write already triggers the watcher).

## Capabilities

### New Capabilities

<!-- None — this change extends two existing capabilities. -->

### Modified Capabilities

- `live-updates`: gains a requirement covering `.sourcegraph.json` watching and the malformed-save tolerance. The capability now extends past "watch the working tree" to "watch the configuration that defines what the working tree means."
- `scoping`: the per-scope-physical-isolation scenario relaxes its restart precondition (a live add/remove also creates/removes the scope's DB). Adds a requirement spelling out the lifecycle on add / remove / modify / `default_scope` change, and an explicit non-requirement around plugin reload.

## Impact

- **Code**: One new file (`ScopeConfigWatcher.cs`, ~150 LOC, mirrors `SolutionWatcher` shape). One new method (`ScopeRouter.Unregister`, ~10 LOC). A `~80 LOC` diff-and-apply method on `LiveIndexService` plus its hosted-task wiring. No changes to indexer, storage, MCP tool surface, or wire format.
- **Tests**: New fixture `tests/fixtures/MultiScope/` is already there; reuse it. Add `LiveScopeConfigTests`: add scope, remove scope, change `default_scope`, malformed save, plugin entry change. Use a short debounce so tests don't sleep.
- **Spec**: Two delta specs (`scoping`, `live-updates`). One scenario in the existing `scoping` spec is modified (the restart precondition is relaxed); the rest stay as-is.
- **Memory / runtime**: One additional poll task running a `stat()` every 200ms (negligible). No memory growth proportional to scope count beyond what already happens at startup.
- **Public API / wire format**: None. `list_scopes` already reports `status: indexing | ok | degraded` — those values keep their meaning. The agent's view of "what scopes exist" simply becomes eventually-consistent with `.sourcegraph.json` rather than fixed at startup.
- **Documentation**: One section in `CLAUDE.md` under *Scopes (multi-solution monorepos)* noting that edits to `.sourcegraph.json` are now picked up live; one bullet under the CLI helpers noting the same.
- **Backward compatibility**: A user who never edits `.sourcegraph.json` while the server is running sees zero behavioural change. The watcher is dormant until a write event fires.
