## ADDED Requirements

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
