# Observability

## Purpose

Make tool-call activity visible at runtime and persist it for offline
analysis so a user can confirm whether their MCP server is actually being
used by an agent (vs the agent silently falling back to `Grep` + `Read`).

## Requirements

### Requirement: In-process per-tool counters
`ToolMetrics` SHALL maintain in-memory counters per tool name (count,
errors, total/max latency, total response size, last-called time) updated
from a wrapper around every tool body.

#### Scenario: Record a successful call
- **WHEN** a tool body wrapped in `ToolMetrics.TrackAsync(name, args, fn)`
  completes without throwing
- **THEN** the counter for `name` is incremented, latency is added to the
  total and compared to `_maxMs`, the response length is added to the
  running total, `_lastCalled` is updated to now

#### Scenario: Record a failed call
- **WHEN** the wrapped body throws
- **THEN** the counter and error count for `name` are incremented and the
  exception is rethrown to the SDK so the client receives an MCP error
  response

### Requirement: Persistent JSONL log
`ToolMetrics` SHALL append a JSON-line entry per tool call to
`<dbDir>/usage.jsonl` whenever `Configure` was called with that path.

#### Scenario: Log a call to JSONL
- **WHEN** any tool fires after `ToolMetrics.Configure(<path>)` ran on
  startup
- **THEN** a line of the form
  `{"ts":"…","tool":"…","ok":true|false,"ms":…,"response_len":…,"args":…}`
  is appended atomically to `<path>` (best-effort: write failures are
  swallowed and never surface to the agent)

### Requirement: usage_stats MCP tool
The server SHALL expose a `usage_stats` MCP tool that returns a markdown
table summarising every tool that has fired in the current process plus a
reference to the JSONL log path.

#### Scenario: Inspect tool usage mid-session
- **WHEN** an agent invokes `usage_stats()`
- **THEN** the response includes the process uptime, a one-row-per-tool
  table (count, errors, avg ms, max ms, avg resp size, last-called age), and
  a footer pointing at `<dbDir>/usage.jsonl`

#### Scenario: No calls yet
- **WHEN** `usage_stats()` is invoked and no other tool has fired in this
  process
- **THEN** the response shows the uptime line and `"No tool calls recorded
  yet."`
