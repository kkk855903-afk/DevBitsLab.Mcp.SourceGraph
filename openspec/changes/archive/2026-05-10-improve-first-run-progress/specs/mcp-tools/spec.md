## MODIFIED Requirements

### Requirement: Progress notifications on slow tools
Tools whose work has multi-second tails on representative inputs SHALL accept an `IProgress<ProgressNotificationValue>` parameter and emit progress at coarse, named checkpoints during the call. Each emitted `ProgressNotificationValue` SHALL set `Total = 1.0`, a `Progress` value in the inclusive range `[0.0, 1.0]` that is monotonically increasing across the call, and a `Message` drawn from one of the following structural shapes (no caller-supplied substrings interpolated):

- Short imperatives: `"encoding query"`, `"searching"`, `"formatting results"`, `"querying"`.
- Indexing phase markers: `"opening workspace"`, `"indexing"`, `"ready"`.

The set of tools opted in by this requirement at the per-tool level is `semantic_search`, `impact_of_change`, `module_summary`, and `find_definition`. **In addition**, every MCP `tools/call` to one of those tools whose dispatch awaits `ScopeHost.Ready` because the targeted scope has not yet completed initial indexing SHALL forward the per-scope indexing progress source's events as `notifications/progress` for the duration of that wait. Other tools MAY add per-tool `IProgress` parameters in future changes when their measured latency justifies it; until they do, cold-start time is silent for those tool calls (today's behaviour).

When an MCP client did not include a `progressToken` on the originating `tools/call` request, the SDK SHALL inject a no-op `IProgress<ProgressNotificationValue>` instance so tool bodies (and the cold-start wrapper) that call `Report(...)` unconditionally incur no wire-level overhead.

#### Scenario: semantic_search emits encoding, searching, and formatting checkpoints
- **WHEN** an MCP client invokes `semantic_search(query = "...")` with a `progressToken`
- **THEN** the server emits three `notifications/progress` messages over the call's lifetime, in order: `Progress = 0.0` with `Message = "encoding query"`, `Progress = 0.5` with `Message = "searching"`, and `Progress = 0.9` with `Message = "formatting results"`

#### Scenario: impact_of_change emits a starting checkpoint
- **WHEN** an MCP client invokes `impact_of_change(symbol = "...", maxDepth = 6)` with a `progressToken`
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins

#### Scenario: module_summary emits a starting checkpoint
- **WHEN** an MCP client invokes `module_summary(namespaceOrPath = "...")` with a `progressToken`
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins

#### Scenario: Cold-start tool call forwards indexing-phase progress
- **WHEN** an MCP client invokes a progress-aware tool (e.g. `find_definition`) with a `progressToken` against a scope whose initial indexing is still running
- **THEN** the server emits a sequence of `notifications/progress` messages drawn from the indexing phase markers (`"opening workspace"`, `"indexing"`, `"ready"`) for the duration of the cold-start wait; once `Ready`, the underlying tool's own checkpoints (if any) emit normally

#### Scenario: No progress emitted when client did not opt in
- **WHEN** any `tools/call` invocation arrives without a `progressToken`, whether against a slow tool or during cold-start
- **THEN** the server emits zero `notifications/progress` messages; the tool result returns identically to today's behaviour

#### Scenario: Progress values are monotonically increasing
- **WHEN** any opted-in tool or cold-start wait emits two or more progress notifications during a single call
- **THEN** each successive notification's `Progress` value is strictly greater than the previous notification's `Progress`, with all values in the closed interval `[0.0, 1.0]`

#### Scenario: Progress messages carry no user input
- **WHEN** any progress notification is emitted by an opted-in tool or by the cold-start forwarder
- **THEN** its `Message` matches one of the documented structural shapes — short imperative or indexing phase marker — and no caller-supplied substring (symbol name, query text, file path) appears in any `Message`
