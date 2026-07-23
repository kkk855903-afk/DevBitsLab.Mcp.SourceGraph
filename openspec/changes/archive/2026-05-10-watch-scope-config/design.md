## Context

`add-scoping` shipped a per-scope file/git watcher (`SolutionWatcher` x N hosts) but loaded `.sourcegraph.json` exactly once at startup ([Program.cs:101](src/DevBitsLab.Mcp.SourceGraph.Server/Program.cs:101)). The asymmetry is now the largest piece of "still requires a restart" friction in the project, and the underlying primitives are mature enough to close it without a rewrite.

The relevant existing pieces:

- [`SolutionWatcher`](src/DevBitsLab.Mcp.SourceGraph.Watcher/SolutionWatcher.cs) — debounced `FileSystemWatcher`, dual-watcher (cs + git/HEAD), `Channel`-based event stream, parse-tolerant for renames.
- [`LiveIndexService.PrepareScopeAsync`](src/DevBitsLab.Mcp.SourceGraph.Server/LiveIndexService.cs:147) — phase-1 bring-up: opens DB, opens embeddings, registers with router as `indexing`. Already carries the broad `try/catch` that lets one scope fail without taking down the host.
- [`LiveIndexService.RunInitialIndexAsync`](src/DevBitsLab.Mcp.SourceGraph.Server/LiveIndexService.cs:258) — phase-2 bring-up: opens solution, cold index, plugin analyzers, settles status to `ok`/`degraded`, calls `MarkReady`.
- [`LiveIndexService.StartWatcher`](src/DevBitsLab.Mcp.SourceGraph.Server/LiveIndexService.cs:342) — wires the per-scope `SolutionWatcher` to a long-running task that consumes its batches.
- [`ScopeHost.DisposeAsync`](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopeHost.cs:91) — orderly tear-down: stop embeddings drain, dispose watcher, dispose indexer, dispose store.
- [`ScopeRouter`](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopeRouter.cs) — process-wide registry under a single lock; `Register`, `TryGet`, `All`, `SetDefaultScope`. No `Unregister` yet.
- [`ScopeConfigLoader.Load`](src/DevBitsLab.Mcp.SourceGraph.Storage/ScopeConfig.cs:72) — already throws `ScopeConfigException` on every malformed-config path; the watcher consumes that boundary directly.

The compatibility constraint is hard: a server that never sees a `.sourcegraph.json` edit must behave identically to today.

## Goals / Non-Goals

**Goals:**
- Adding a scope to `.sourcegraph.json` brings up that scope without a restart, with the same `indexing → ok|degraded` lifecycle as a startup scope.
- Removing a scope tears down its host and frees its resources cleanly; the per-scope DB on disk is preserved (orphan cleanup is a separate concern, out of scope).
- Changing `default_scope` is a metadata-only flip — no scope is touched.
- Modifying a scope's project-set (`solutions`/`projects`/`paths`/`exclude`/`isolated`) reindexes only that scope. Other scopes are not disturbed.
- Malformed saves never tear down working scopes. The user can save half-typed JSON without losing query availability.
- The CLI subcommands (`scopes add`/`scopes remove`/`init-scopes`) remain pure file operations; the live propagation is a property of the watcher, not the CLI.

**Non-Goals:**
- Hot-reloading plugin assemblies. `plugins[]` deltas are logged-and-ignored. `AssemblyLoadContext` collectible-unload is a separate, larger problem.
- Live-watching solutions outside the scope's solution directory. Today `SolutionWatcher` only watches `Path.GetDirectoryName(solutionPath)`; a `paths`-kind scope rooted elsewhere is silently un-watched. That's a known but separate gap (logged in [ROADMAP.md](openspec/ROADMAP.md) as a follow-up — call it out here so the live-config feature isn't blamed for it).
- Atomic config-and-DB rename when a scope id changes. Renaming a scope is treated as remove + add; the user pays a re-cold-index cost.
- Cross-process notification. No filesystem signal beyond what mtime polling can pick up; specifically no Unix domain socket / IPC surface for the CLI to "ping" a running server.
- Watching `.sourcegraph.json` files that aren't at the resolved repo root. The path is fixed by `ScopeConfigLoader.FileName` at the same root the server was launched against.

## Decisions

### 1. New `ScopeConfigWatcher`, not an extension to `SolutionWatcher`

`SolutionWatcher` is per-scope and per-solution-directory. `.sourcegraph.json` lives at the repo root and is global to the process. Folding both into one class would force a "kind" enum on every event and complicate the existing solution/git debounce logic for no gain. Sibling class:

```
src/DevBitsLab.Mcp.SourceGraph.Watcher/
├── SolutionWatcher.cs        (existing, per-scope)
├── FileChangeBatch.cs        (existing)
└── ScopeConfigWatcher.cs     (new, single instance per process)
```

`ScopeConfigWatcher` shape:
- A single mtime-polling loop over `<repoRoot>/.sourcegraph.json`, observing presence + `File.GetLastWriteTimeUtc`. The poll interval doubles as the debounce window (default 200ms).
- The very first iteration fires unconditionally so the diff catches any save that landed between the server's startup-time `ScopeConfigLoader.Load` and the watcher actually starting (the watcher boots after the cold-index `WhenAll` settles, which can race with a config edit). The diff returns "no-op" when on-disk content matches what's already live.
- Subsequent iterations emit only on presence flips or mtime advances.
- Single `Channel<ScopeConfigChange>` exposed via `ReadAllAsync`.
- `IAsyncDisposable` with orderly shutdown (cancel CTS, await processor, `TryComplete` in `finally`).

**Why polling, not `FileSystemWatcher`** (changed during implementation): macOS's FSEventStream-backed `FileSystemWatcher` does not reliably deliver events for files at the *root* of the watched directory — only subdirectory events fire. `IncludeSubdirectories = true` doesn't help (events for `.sourcegraph.json` itself still don't fire on macOS, while sibling `.sourcegraph/` subtree events do). Polling at 200ms costs a `stat()` per tick, which is cheap, and "did the config change?" doesn't need sub-second latency. Renames in either direction (away from or into the repo root) are detected the same way — the mtime/presence check is symmetric. A separate `Deleted` code path is not needed; absence is just `exists == false` on the next poll.

### 2. Diff-and-apply lives on `LiveIndexService`

The router is the source of truth for "what scopes are live". The watcher emits a config snapshot; `LiveIndexService` diffs that snapshot against `_router.All()` and routes each delta through existing methods.

```
LiveIndexService.OnConfigChangedAsync(ScopeConfig newConfig, ct)
    1. var diff = ScopeDiff.Compute(currentScopes: _router.All().Select(h => h.Scope), newConfig.Scopes)
    2. if diff.DefaultScopeChanged: _router.SetDefaultScope(newConfig.DefaultScope)
    3. for each removed: _ = TearDownAsync(host, gracePeriod: TimeSpan.FromSeconds(5))
    4. for each added:   PrepareScopeAsync(scope, ct) → RunInitialIndexAsync(host, ct) → StartWatcher(host, ct)
    5. for each modified: TearDown(old) + bring-up(new), as a contiguous unit per scope
    6. log a one-line summary: "scope-config delta: +foo -bar ~baz default=qux"
```

`ScopeDiff` is a tiny pure helper (no I/O); reside it in `Server/Scoping/ScopeDiff.cs` next to `ScopeRouter`. Equality of two scopes is by `Scope.Id` first, then a structural comparison of `ProjectSet`, `Isolated`. A scope present in both with the same payload is *not* a "modified" — it's a no-op.

### 3. Add `ScopeRouter.Unregister(string id)` and `ScopeRouter.Replace(string id, ScopeHost newHost)`

Two missing router primitives. `Unregister` is symmetric with `Register` and used on the remove path:

```csharp
public bool Unregister(string id)
{
    lock (_lock) return _hosts.Remove(id);
}
```

`Replace` performs the swap atomically under a single lock acquisition, so no `TryGet` between the two stages ever sees an empty slot or a duplicate:

```csharp
public ScopeHost? Replace(string id, ScopeHost newHost)
{
    lock (_lock)
    {
        _hosts.TryGetValue(id, out var displaced);
        _hosts[id] = newHost;
        return displaced;
    }
}
```

Returning the displaced host avoids a separate `TryGet` on the caller side and keeps the displaced-host reference flow explicit. We considered relying on call ordering ("register new before unregister old"), but that's a contract a future refactor can silently break by re-ordering the two calls; the single-lock primitive eliminates the rule entirely.

### 4. Deferred disposal for the displaced host

The router swap is atomic, but a tool call that already resolved against the *old* host still holds a reference to its `Store`. Disposing the store mid-query is a race condition independent of the router contract.

Two options considered:

| Option | Pros | Cons |
|---|---|---|
| Refcount on `ScopeHost` | Correct under all races | New synchronisation surface; new bug class (forgotten release) |
| Deferred disposal | No new types; one timer | Brief window where a slow query is racing the dispose |

We pick **deferred disposal** with a 5-second grace window. After `Replace` returns the displaced host, a `Task.Delay(5s)` precedes `DisposeAsync` on it. Any in-flight query that grabbed the old host has 5 seconds to complete its store reads. Tool queries today are sub-second outside cold-index; 5 seconds is a generous safety margin and the failure mode (a single slow query erroring with "store disposed") is a one-shot retryable from the agent's side.

The grace period is configurable via `LiveIndexConfig.ScopeReplaceGraceMs` (default `5000`). Tests use a small value (e.g. `50ms`) to keep them fast.

The 5000 ms default is a defensible round number, not a measured upper bound. **Validation commitment**: once this ships, sample `usage.jsonl` for the 99th-percentile tool call duration over a representative window; if that p99 is >2 s the default rises, if it's <500 ms the default falls. Tracked as a follow-up rather than a blocker for this change.

### 5. Malformed-config tolerance

The watcher is parse-tolerant by construction: it loads `.sourcegraph.json` itself rather than emitting a "raw" event for `LiveIndexService` to parse. On parse failure:

```
ScopeConfigException ex →
    log.LogInformation(ex, "scope config save was malformed; ignoring (current scopes still active)")
    no event emitted
```

The current host set stays exactly as it was. The next valid save fires the next event. This avoids a class of "save-while-typing tears down everything" bugs that would otherwise hit users running formatters or LSP-driven JSON validation.

A purely whitespace-only save (same parsed config) is also a no-op: the diff returns an empty changeset and the watcher logs nothing.

### 6. `.sourcegraph.json` deletion = revert to synthesised default

A user who deletes `.sourcegraph.json` is back to the zero-config single-solution path. The watcher treats `Deleted` as "load the synthesised default" via `ScopeConfigLoader.Synthesise(repoRoot, …)`. All scopes except the synthesised `default` are removed; the `default` scope is added if it wasn't already there.

This preserves the symmetry: starting the server without the file produces scope `default`, so deleting the file at runtime should converge to the same state.

### 7. Plugin deltas: log and skip

`plugins[]` change at runtime is rare in practice and complex in implementation (collectible `AssemblyLoadContext`, in-flight analyzer state, tool registration). When the diff sees a `plugins[]` change:

```
log.LogWarning("scope-config plugins[] changed; the server is still running with the previous plugin set. Restart to apply plugin changes.")
```

The new plugin list is *not* persisted into runtime state — the next plugin diff is computed against the same baseline (the startup-time plugin list). This avoids the "every save logs a warning" noise. Stored on `LiveIndexService` as a private `IReadOnlyList<PluginRef> _startupPlugins`.

### 8. Watcher start-up timing

`ScopeConfigWatcher` is started inside `LiveIndexService.ExecuteAsync`, after the initial cold index of all startup scopes finishes. Two reasons:

- A config save during cold-indexing would race the very setup we're trying to bring up. Easier to start watching once the host is steady-state.
- The poll loop's first iteration emits unconditionally with the current on-disk state. So any save that landed during cold-indexing is still picked up via the diff (which returns "no-op" when the on-disk content matches what the server already loaded at startup). No edits are dropped on the floor.

### 9. CLI / running-server interaction

`scopes add` and `scopes remove` keep their pure-file-write semantics. They write `.sourcegraph.json`, the OS posts the file event, the running server (if any) picks it up. No IPC, no port, no socket. The CLI's exit message gains one line:

```
Wrote .sourcegraph.json. A running sourcegraph-mcp server will pick up the change automatically.
```

If no server is running, the line is harmless. If a server is running but somehow not watching (e.g. degraded for unrelated reasons), the CLI cannot tell — but the user can verify with `list_scopes` from the agent.

### 10. Removed scope = registry row is deleted, on-disk DB is preserved

When a scope is removed live, `IScopeRegistry.DeleteAsync(id)` drops its row from `_meta.db` entirely. We considered a tombstone (`status = "removed"`) but rejected it: nothing queries the registry for removed scopes, every `ListAsync` callsite would have to filter, and the tombstone set grows unboundedly across iterations. `.sourcegraph.json` is the source of truth — the registry should stay bounded by it.

The per-scope `<id>.db` file on disk is *not* deleted. Re-adding the same scope id later picks up the existing DB and skips the cold-index cost. Orphan cleanup of long-removed DBs is out of scope for this change; a future `sourcegraph-mcp scopes prune` CLI command can address it.

`IScopeRegistry.RemoveAsync(string id, CancellationToken ct)` already exists ([IScopeRegistry.cs:22](src/DevBitsLab.Mcp.SourceGraph.Storage/IScopeRegistry.cs:22)) and the SQLite implementation is a `DELETE FROM scopes WHERE id = @id;` — exactly the contract this change needs. It's idempotent (deleting a missing id is a no-op SQL DELETE), so the live tear-down doesn't have to check first. No new registry primitive required; we just use it.

### 11. Renames are handled symmetrically by the polling loop

Two rename directions are possible:

- `git mv .sourcegraph.json other.json` — the watched file is gone
- `git mv other.json .sourcegraph.json` — the watched file just appeared

The polling loop handles both naturally because it observes presence + mtime on every tick. A rename-away flips `exists` from `true` to `false` and emits a `Reverted` (synthesised default). A rename-into flips it from `false` to `true` and emits an `Updated`. A rename-and-rename-back round-trip leaves no stale state because both transitions are observed independently. No special "rename" code path is needed.

### 12. Out-of-band: the "paths-kind scope is un-watched" gap

Independent of this change, scopes declared with `paths: [...]` (csproj globs outside the solution dir) are not watched today: `LiveIndexService.StartWatcher` only watches `Path.GetDirectoryName(solutionPath)`, and a `paths`-kind scope has `solutionPath = null`, so the early return on line `345` skips it. This change does not fix that gap; closing the loop on it is tracked as a follow-up. Calling it out so a reviewer reading "we now have a complete watcher story" doesn't draw the wrong conclusion.

## Risks

**Editor save patterns.** Different editors save `.sourcegraph.json` differently — some atomic-rename (vim, modern VS Code), some write-through, some create temp `.sourcegraph.json~`/`.swp` siblings. The mtime-polling watcher sidesteps the kernel-event idiosyncrasies entirely: it just looks at the file as it exists on disk at each poll. Mitigation for partial writes: the parse-tolerance path (Decision 5) — anything that arrives mid-write fails parse and is ignored.

**Filesystem reliability on macOS / Linux.** Polling avoids the per-OS event-delivery quirks that motivated the pivot away from `FileSystemWatcher` in the first place (FSEventStream not delivering for files at the watched directory's root on macOS, inotify limits on Linux, etc.). The trade-off is up to one poll-interval (default 200ms) of latency before a change is observed, which is well below any human's edit cadence.

**Tear-down racing in-flight tool calls.** Mitigated by Decision 4 (deferred disposal). The remaining risk is a single tool call exceeding the 5-second grace window. In practice the only candidate is a slow `semantic_search` against a cold ONNX model load; we accept that as a one-shot retryable error.

**Live-watch interacting with `.git/HEAD` reload.** A branch switch already triggers a full per-scope reindex via `RoslynIndexer.ReloadAndIndexAllAsync`. If the new branch also has a different `.sourcegraph.json`, both events fire near-simultaneously. The order is:

1. `.git/HEAD` event → per-scope `ReloadAndIndexAllAsync` starts
2. `.sourcegraph.json` event → diff-and-apply runs

If the diff says "scope X is removed" while step 1 is mid-flight on scope X, the deferred-disposal grace window covers it; if the diff says "scope X's solutions changed" the modified path tears down + brings up, which short-circuits the in-flight reindex. We accept the small wasted work; the resulting state is correct.

**Test fixture brittleness.** The existing `tests/fixtures/MultiScope/` is a static fixture. Live-config tests need to mutate `.sourcegraph.json` during the test, which means each test has to write to a temp copy. We add a `MultiScopeFixture.CopyToTempAsync()` helper and use that consistently; the existing fixture stays read-only.

**Spec drift.** The `scoping` spec's "New scope creates a new file" scenario specifies *"and the server is restarted"*. Modifying the scenario without modifying the requirement title risks looking like a spec edit when it's an actual behavioural change. The delta uses `MODIFIED Requirement: Per-scope physical isolation` and re-states the full requirement so the diff is unambiguous.

## Migration Plan

None. This is purely additive at runtime: a server with no `.sourcegraph.json` edits sees byte-identical behaviour. No schema change, no on-disk format change, no MCP wire change.

The CLI message change in `scopes add` / `scopes remove` is one extra line of output; existing scripts that grep for the existing "Wrote …" line keep matching.

## Open Questions

- **Should `default_scope` changes be reflected in the response of in-flight tool calls?** The router's `DefaultScope` is read under lock at every tool dispatch, so changes are picked up on the next call automatically. No action needed; calling this out so a reviewer doesn't ask.

- **Should the watcher fire on first start (i.e. emit a synthetic event for the existing config)?** No — startup already loads the config via `ScopeConfigLoader.Load` and brings up scopes. A synthetic event would re-process an unchanged config, which the diff would correctly resolve to a no-op, but the warning-log noise on (the rare) malformed-startup-config path would shift its phrasing. Cleanest to keep startup loading and live watching separate code paths and let them converge naturally.

- **Does `ScopeHost.Ready` semantics survive `modify` correctly?** A `modify` constructs a fresh `ScopeHost` with its own `Ready` task. Anything that captured `oldHost.Ready` would await a task that completed long ago — fine for "is the scope ready" but stale for "is the scope's *current* data ready". We don't believe any caller awaits `Ready` more than once per resolution, but the answer hinges on every callsite re-resolving the host before each `await Ready`. Verified by [test 8.5b in tasks.md](./tasks.md) (a query post-modify must observe the new host's `Ready`, not a captured stale one). If a captured-`Ready` callsite turns up in the audit, the fix is local — re-resolve the host inside the await loop.
