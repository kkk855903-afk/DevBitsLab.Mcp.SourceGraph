## ADDED Requirements

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
