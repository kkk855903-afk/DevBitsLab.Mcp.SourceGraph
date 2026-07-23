## Context

`ToolMetrics` is the single chokepoint every MCP tool call passes through. It already does three observability jobs — in-memory aggregation, JSONL audit, and the `usage_stats` MCP tool — but nothing in that pipeline can be picked up by a metrics agent or a tracing backend without parsing the JSONL out-of-band.

The .NET BCL ships first-class APIs for both signals (`System.Diagnostics.ActivitySource`, `System.Diagnostics.Metrics.Meter`). They're the same APIs the OpenTelemetry SDK consumes via `AddSource(...)` / `AddMeter(...)`, and the same APIs `dotnet-counters` reads. Wiring the existing chokepoint to emit them is mechanical and costs no allocations on the cold path.

## Goals / Non-Goals

**Goals:**

- Every MCP tool call emits one Activity span (when listened to) and four metric samples (calls, errors, duration, response size).
- Metric / span names follow OpenTelemetry semantic-convention shape so dashboards built against generic agent dashboards light up without custom processors.
- Zero-cost path when no listener is attached.
- No regression to the existing JSONL log or `usage_stats` MCP tool.

**Non-Goals:**

- Bundling the OpenTelemetry SDK or any specific exporter. Consumers attach their own.
- Health-check endpoints. Out of scope; the server is stdio-only.
- Tracing the indexing pipeline or scope-router internals. This change is scoped to the MCP-tool boundary; deeper instrumentation can land later if needed.
- Configurable instrument names. The single advertised name `DevBitsLab.Mcp.SourceGraph` is part of the public surface; renaming it later would break consumers' configuration.

## Decisions

**1. One `ActivitySource` and one `Meter`, both named `DevBitsLab.Mcp.SourceGraph`.**

The convention in OpenTelemetry instrumentation libraries is "one source per logical component"; we have one logical component (the MCP server) so one source / one meter is correct. Versioning the source/meter from `AssemblyInformationalVersionAttribute` so dashboards can group samples by build.

**2. Activity name: `mcp.tool <toolName>`. Kind: `Server`.**

Mirrors RPC-style activity names (`{rpc.system} {rpc.service}/{rpc.method}`). The connected client is the *caller*; we are the *server*. This lets distributed-tracing backends correlate inbound MCP requests with whatever called them when the wire protocol grows trace propagation.

**3. Metric names: `sourcegraph.tool.calls`, `.errors`, `.duration`, `.response_size`.**

Lowercase, dot-separated, semantic-convention-flavoured. Units: `{call}` for counters, `ms` for duration, `By` for response size. Tags: `mcp.tool` (name), `mcp.tool.ok` (bool), `mcp.tool.scope` (optional string).

**4. Emit unconditionally; don't gate on a config flag.**

Unattached `Activity` and `Meter` calls are essentially free. Adding a flag would just add a branch in the hot path and a knob in the CLI that nobody flips off. Users who want strict silence already get it because no exporter is registered by default.

**5. Don't extend the JSONL schema.**

The JSONL log is for offline analysis and historical archival; OTel signals are for live scraping. They're complementary surfaces, and the JSONL line shape is implicitly part of the spec. Changing it would force everyone parsing those logs to update.

## Risks / Trade-offs

- **Instrument names are now public surface.** Renaming `sourcegraph.tool.calls` later would break dashboards. We accept that — the names are well-formed and follow convention; we'd rather pin them now than rename them later.
- **Tag cardinality.** `mcp.tool` is bounded (~25 tools); `mcp.tool.scope` is bounded by the user's `.sourcegraph.json` (~handful per repo); `mcp.tool.ok` is two values. Cardinality is fine.
- **No instrumented hosting bundle.** A user has to wire the OTel SDK themselves to actually see the signals. We accept that — bundling the SDK would force a heavy dependency on every consumer; the README covers the ~3-line wiring snippet.
