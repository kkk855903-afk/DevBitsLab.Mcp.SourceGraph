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

### Requirement: OpenTelemetry-compatible ActivitySource per tool call
The server SHALL declare an `ActivitySource` named `DevBitsLab.Mcp.SourceGraph` and SHALL open one `Activity` from that source for every MCP tool invocation that flows through `ToolMetrics.TrackAsync` or `ToolMetrics.TrackSync`. The activity name SHALL be `mcp.tool <toolName>` and the kind SHALL be `ActivityKind.Server`.

The activity SHALL carry the tags `mcp.tool.name` (string, the tool name) and `mcp.tool.scope` (optional string, the value of the `scope` argument when present). On exception the activity status SHALL be set to `ActivityStatusCode.Error` with the exception message, and a `exception.type` tag SHALL carry the fully-qualified exception type name.

The activity SHALL be a no-op when no `ActivityListener` subscribes to the source — `StartActivity` returns null, the using-block disposes a null reference, and the instrumentation cost is bounded to a single null check.

#### Scenario: Listener captures a span per successful tool call
- **WHEN** an `ActivityListener` configured for source name `DevBitsLab.Mcp.SourceGraph` is registered, and a tool wrapped in `ToolMetrics.TrackAsync` runs successfully
- **THEN** the listener receives one `Activity` whose `OperationName` starts with `mcp.tool ` and whose `Status` is `Unset`, and whose tags include `mcp.tool.name` matching the tool name

#### Scenario: Listener captures error status on a failing call
- **WHEN** a wrapped tool body throws and the listener is configured as above
- **THEN** the captured `Activity.Status` is `ActivityStatusCode.Error` and the tag `exception.type` matches the thrown exception's `GetType().FullName`

#### Scenario: No listener — zero-cost
- **WHEN** no `ActivityListener` is registered for the source
- **THEN** `Telemetry.ActivitySource.StartActivity(...)` returns null, the wrapped tool body still executes, and the existing in-memory counters / JSONL line are still produced unchanged

### Requirement: OpenTelemetry-compatible Meter exposing per-tool instruments
The server SHALL declare a `Meter` named `DevBitsLab.Mcp.SourceGraph` exposing four instruments:

- `sourcegraph.tool.calls` — `Counter<long>`, unit `{call}` — incremented once per successful or failed tool call.
- `sourcegraph.tool.errors` — `Counter<long>`, unit `{call}` — incremented once per failed tool call (in addition to `.calls`).
- `sourcegraph.tool.duration` — `Histogram<double>`, unit `ms` — recorded once per call with the elapsed wall-clock milliseconds.
- `sourcegraph.tool.response_size` — `Histogram<long>`, unit `By` — recorded once per call with the response payload size in characters (treated as bytes for the unit).

Every sample SHALL carry tags `mcp.tool` (string, tool name), `mcp.tool.ok` (boolean, `true` when the call returned, `false` when it threw), and `mcp.tool.scope` (string, present only when the tool's args carry a non-empty `scope` field).

The meter / instrument names are public surface. They SHALL match the names listed above byte-for-byte; a rename is a breaking change.

#### Scenario: MeterListener captures a successful call
- **WHEN** a `MeterListener` configured to record measurements on `DevBitsLab.Mcp.SourceGraph` instruments is started, and a wrapped tool returns successfully in 12 ms with a 480-character response
- **THEN** the listener receives:
  - one sample of `1` on `sourcegraph.tool.calls` with tag `mcp.tool.ok=true`,
  - zero samples on `sourcegraph.tool.errors`,
  - one sample of `12` (±jitter) on `sourcegraph.tool.duration`,
  - one sample of `480` on `sourcegraph.tool.response_size`.

#### Scenario: MeterListener captures a failed call
- **WHEN** a wrapped tool throws after 8 ms
- **THEN** the listener receives one sample on `sourcegraph.tool.calls` and one on `sourcegraph.tool.errors`, both tagged `mcp.tool.ok=false`, plus a duration sample of `8` (±jitter), plus a `response_size` sample of `0`

#### Scenario: Scope tag attached when args carry a scope
- **WHEN** a tool is invoked with an args object containing `scope = "backend"`
- **THEN** every metric sample emitted for that call carries `mcp.tool.scope=backend` in addition to `mcp.tool` and `mcp.tool.ok`

#### Scenario: Scope tag omitted when args have no scope
- **WHEN** a tool is invoked with an args object that has no `scope` property, or whose `scope` property is empty
- **THEN** the emitted samples carry only `mcp.tool` and `mcp.tool.ok` — no `mcp.tool.scope` tag is present

#### Scenario: No listener — zero-cost
- **WHEN** no `MeterListener` subscribes to the meter
- **THEN** the calls to `Counter<long>.Add` and `Histogram<>.Record` complete with bounded overhead (no allocation, no buffering), and the existing JSONL line / in-memory counters / `usage_stats` payload remain unaffected

### Requirement: OpenTelemetry signals coexist with the existing JSONL log and usage_stats tool
Adding the `ActivitySource` / `Meter` signals SHALL NOT alter the JSONL log line shape, the in-memory `ToolStats` aggregation, the per-scope counter exposed via `ToolMetrics.ScopeSnapshot`, or the markdown payload returned by the `usage_stats` MCP tool. The three observability surfaces are complementary — JSONL for offline archival, in-memory for `usage_stats` introspection, OTel for live external scraping.

#### Scenario: Existing JSONL line unchanged
- **WHEN** any wrapped tool fires after `ToolMetrics.Configure(<path>)` ran on startup, with or without an OTel listener attached
- **THEN** the appended JSONL line conforms to the same `{ts, tool, ok, ms, response_len, scope, args}` shape declared by the existing requirement, with no new fields injected by the OTel wiring

### Requirement: Persistent heal-event JSONL log

The server SHALL append one JSON-line entry to `<repo>/.sourcegraph/heals.jsonl` for every heal event emitted by any subsystem (boot-time scope reconciliation in `add-scope-health-surface`; in future changes also bounded retries, repair-tool invocations, corruption detections, and embeddings prunes).

The line shape SHALL be:
```json
{"ts":"<ISO8601>","kind":"<kebab-case>","scope":"<scope id>","ok":true|false,"ms":<number>,"details":"<free-form>"}
```

Where:
- `kind` is a short kebab-case identifier ending in a present-tense verb (`orphan-db-archived`, `missing-db-detected`, `stuck-indexing-detected`, …). New heal kinds are added by future changes; the field is open-ended.
- `scope` is the scope id the heal pertains to (`"*"` is permissible for cross-scope heals).
- `ok` is `true` when the heal action succeeded (or for pure detections, `true` when the detection completed without error).
- `ms` is the wall-clock duration of the heal action in milliseconds (zero for pure detections).
- `details` is optional free-form prose; absent when no further context is needed.

Writes SHALL be best-effort: an IO failure on append SHALL be logged at debug level but SHALL NOT throw to the caller (matches the existing `usage.jsonl` contract). The log file SHALL be created lazily on first append and SHALL NOT be auto-rotated in this revision.

The log path SHALL be configurable through `HealLog.Configure(string? logPath)`. When `Configure` is never called or is called with `null`, the log SHALL be a silent no-op.

#### Scenario: Heal event appends one line
- **GIVEN** `HealLog.Configure(<dbDir>/heals.jsonl)` was called on startup
- **WHEN** any subsystem calls `HealLog.Append(kind: "orphan-db-archived", scope: "stale", ok: true, ms: 12, details: "moved to orphans/stale-2026-05-10T14-22-08Z.db")`
- **THEN** `<dbDir>/heals.jsonl` contains a JSON object with all five fields, the `ts` field is an ISO-8601 timestamp within ±5 seconds of `DateTimeOffset.UtcNow`, and the line ends in a single `\n`

#### Scenario: Write failure is swallowed
- **GIVEN** `HealLog.Configure(<read-only dir>/heals.jsonl)` was called and the configured path's parent directory cannot be written
- **WHEN** `HealLog.Append(...)` is called
- **THEN** the call returns without throwing; no exception surfaces to the caller; the in-process metric and Counter still fire (per `OpenTelemetry Counter for heal events`)

#### Scenario: No-op when not configured
- **GIVEN** `HealLog.Configure(null)` (or `Configure` was never called)
- **WHEN** `HealLog.Append(...)` is called
- **THEN** no file is created on disk; the call is a no-op; the Counter is still incremented (the metric is independent of the log)

### Requirement: OpenTelemetry Counter for heal events

The server's existing `Meter` (`DevBitsLab.Mcp.SourceGraph`) SHALL expose one additional instrument:

- `sourcegraph.heal.fired` — `Counter<long>`, unit `{event}` — incremented once per call to `HealLog.Append`.

Each sample SHALL carry tags `kind` (string, the heal kind), `scope` (string, scope id), and `ok` (boolean, the success flag passed to `Append`).

The instrument name is public surface; a rename is a breaking change.

The instrument SHALL be zero-cost when no `MeterListener` subscribes to the meter (matches the existing `sourcegraph.tool.*` instruments; `Counter<long>.Add` completes with bounded overhead and no allocation in the no-listener case).

#### Scenario: MeterListener captures a heal increment
- **GIVEN** a `MeterListener` configured to record measurements on `DevBitsLab.Mcp.SourceGraph` instruments is started, and `HealLog.Configure(...)` has been called
- **WHEN** `HealLog.Append(kind: "stuck-indexing-detected", scope: "frontend", ok: true, ms: 0)` fires
- **THEN** the listener receives one sample of `1` on `sourcegraph.heal.fired` with tags `kind=stuck-indexing-detected`, `scope=frontend`, `ok=true`

#### Scenario: Heal counter coexists with tool counters unchanged
- **WHEN** any tool body wrapped in `ToolMetrics.TrackAsync` runs alongside heal events
- **THEN** the existing `sourcegraph.tool.calls` / `.errors` / `.duration` / `.response_size` instruments emit identical samples to today's behaviour; the new `sourcegraph.heal.fired` instrument is independent

