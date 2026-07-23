## MODIFIED Requirements

### Requirement: Caller and callee enumeration
The server SHALL expose `list_callers` and `list_callees` tools that walk `calls` edges by default, with an optional `kind` parameter that accepts a kebab-case edge kind name or any future kind exposed by the active scope's plugins, to filter the edge kind walked. When an edge row carries a non-null `payload` JSON value, the rendered markdown SHALL include an indented `payload:` sub-line under the edge row, displaying up to the first five key/value pairs from the payload object; if more than five pairs are present, an `(N more)` suffix SHALL indicate the elision count.

#### Scenario: Edge with no payload renders unchanged
- **WHEN** `list_callers` returns an edge whose `payload` column is `NULL` (e.g. a built-in C# `calls` edge today)
- **THEN** the markdown for that row is exactly the pre-change output — no `payload:` sub-line, no behavioural difference

#### Scenario: Edge with payload renders sub-line
- **WHEN** `list_callers` returns an edge whose `payload` is `{"path":"User.Name","mode":"two-way","converter":"BoolToVisibility"}`
- **THEN** the markdown row is followed by an indented line of the form `    payload: { path: "User.Name", mode: "two-way", converter: "BoolToVisibility" }`

#### Scenario: Edge payload truncated when many keys
- **WHEN** `list_callers` returns an edge whose `payload` carries seven key/value pairs
- **THEN** the rendered `payload:` sub-line shows the first five keys and appends ` (2 more)` so the agent sees the truncation without inspecting the row separately

### Requirement: Neighborhood tool surfaces payload
The `neighborhood` tool SHALL render the same `payload:` sub-line under every edge row in its output, applying the same five-key cap and `(N more)` suffix rule as `list_callers` and `list_callees`.

#### Scenario: Neighborhood result with mixed payload presence
- **WHEN** `neighborhood` returns three edges, one with payload and two without
- **THEN** only the row with payload carries the indented `payload:` sub-line; the other two render exactly as before

## ADDED Requirements

### Requirement: Always-render-payload pattern is consistent across tools
Any MCP tool that renders per-edge result rows SHALL use the same indented `payload:` sub-line pattern, the same five-key cap, and the same `(N more)` suffix when payload truncation occurs. New tools MUST NOT invent alternative payload rendering shapes; the consistency lets agents and humans skim multi-tool output without re-learning the format.

#### Scenario: Future tool emits per-edge rows
- **WHEN** a new MCP tool that walks edges (e.g. an `inspect_edge` follow-up) renders results
- **THEN** its row format includes the same `payload:` sub-line shape with the same truncation rule, by reusing the shared rendering helper rather than implementing a parallel format
