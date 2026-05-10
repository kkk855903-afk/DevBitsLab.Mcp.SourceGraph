## ADDED Requirements

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
