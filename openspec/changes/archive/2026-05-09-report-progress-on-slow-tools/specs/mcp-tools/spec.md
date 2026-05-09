## ADDED Requirements

### Requirement: Progress notifications on slow tools
Tools whose work has multi-second tails on representative inputs SHALL accept an `IProgress<ProgressNotificationValue>` parameter and emit progress at coarse, named checkpoints during the call. Each emitted `ProgressNotificationValue` SHALL set `Total = 1.0`, a `Progress` value in the inclusive range `[0.0, 1.0]` that is monotonically increasing across the call, and a short imperative `Message` (e.g. `"encoding query"`, `"searching"`, `"querying"`) that contains no user-controlled substrings.

The set of tools opted in by this requirement is `semantic_search`, `impact_of_change`, and `module_summary`. Other tools MAY add the parameter in future changes when their measured latency justifies it.

When an MCP client did not include a `progressToken` on the originating `tools/call` request, the SDK SHALL inject a no-op `IProgress<ProgressNotificationValue>` instance so tool bodies that call `Report(...)` unconditionally incur no wire-level overhead.

#### Scenario: semantic_search emits encoding, searching, and formatting checkpoints
- **WHEN** an MCP client invokes `semantic_search(query = "...")` and includes a `progressToken` on the request
- **THEN** the server emits three `notifications/progress` messages over the call's lifetime, in order: `Progress = 0.0` with `Message = "encoding query"`, `Progress = 0.5` with `Message = "searching"`, and `Progress = 0.9` with `Message = "formatting results"`; the request's final `tools/call` response carries the search results as today

#### Scenario: impact_of_change emits a starting checkpoint
- **WHEN** an MCP client invokes `impact_of_change(symbol = "...", maxDepth = 6)` with a `progressToken` on the request
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins; the final response carries the impact set as today

#### Scenario: module_summary emits a starting checkpoint
- **WHEN** an MCP client invokes `module_summary(namespaceOrPath = "...")` with a `progressToken` on the request
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins

#### Scenario: No progress emitted when client did not opt in
- **WHEN** an MCP client invokes any of `semantic_search`, `impact_of_change`, `module_summary` WITHOUT a `progressToken` on the request
- **THEN** the server emits zero `notifications/progress` messages for the call; the tool result returns identically to today's behaviour

#### Scenario: Progress values are monotonically increasing
- **WHEN** any opted-in tool emits two or more progress notifications during a single call
- **THEN** each successive notification's `Progress` value is strictly greater than the previous notification's `Progress`, with all values in the closed interval `[0.0, 1.0]`

#### Scenario: Progress messages carry no user input
- **WHEN** any progress notification is emitted by an opted-in tool
- **THEN** its `Message` string is one of the documented short imperatives (`"encoding query"`, `"searching"`, `"formatting results"`, `"querying"`) and does not interpolate any caller-supplied argument value (symbol name, query text, file path, etc.) into the message
