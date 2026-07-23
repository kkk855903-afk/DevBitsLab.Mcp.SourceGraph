## Why

`ToolMetrics` already records every MCP tool call into in-memory counters and a JSONL audit log, and surfaces a `usage_stats` MCP tool to read the counters back. That's enough for a single-developer workflow but it doesn't reach a real telemetry pipeline.

Enterprise consumers running this server inside their build/CI infrastructure (or alongside other agent tools) want to scrape latency / error / call-rate without parsing a JSONL file out-of-band. The `System.Diagnostics` shape — `ActivitySource` for spans, `Meter` for counters/histograms — is the standard .NET idiom and the natural input to either an OpenTelemetry exporter or `dotnet-counters`.

The signals are zero-cost when nobody is listening: `ActivitySource.StartActivity` returns `null` outside an instrumented host, and `Meter` instruments increment a single field rather than buffering samples. So enabling them by default doesn't tax users who don't care.

## What Changes

- **New `Telemetry` static class** (`Server/Observability/Telemetry.cs`) exposing one `ActivitySource` and one `Meter`, both named `DevBitsLab.Mcp.SourceGraph`, plus four instruments: `sourcegraph.tool.calls` (counter), `sourcegraph.tool.errors` (counter), `sourcegraph.tool.duration` (ms histogram), `sourcegraph.tool.response_size` (bytes histogram).
- **`ToolMetrics.TrackAsync` / `TrackSync` wrap the tool body in an Activity span** with kind `Server`, name `mcp.tool <toolName>`, tags `mcp.tool.name` / `mcp.tool.scope`, and OK / error status; on completion they record the duration + response-size samples and increment the call (and on failure, error) counter, with the same tags as the histogram samples.
- **No change to existing observability surfaces**: the JSONL audit log, in-memory counters, and `usage_stats` MCP tool all keep working with the exact same payload shape.
- **README "Observability" section grows a third bullet** describing the OpenTelemetry signals, including a `dotnet-counters` invocation.

## Capabilities

### Modified Capabilities

- `observability`: new ADDED requirement covering the `ActivitySource` + `Meter` signals. Existing requirements for the JSONL log, in-memory counters, and `usage_stats` MCP tool remain unchanged.

## Impact

- **Behaviour**: zero for users who don't attach an OTel listener or `dotnet-counters` session. The instruments are registered at process start, but the runtime path through `Activity.Current` and the unconfigured meter is essentially branch-free.
- **Token cost**: zero. Signals are out-of-band of MCP wire traffic.
- **Test surface**: small. The `ToolMetrics.Track*` integration is exercised by every existing tool test; an extra unit test verifying the `ActivitySource`/`Meter` names and counter contracts is sufficient. We don't need to spin up a real OTel SDK to validate the shape — `MeterListener` and `ActivityListener` from the BCL are enough.
- **Compatibility**: additive. No public API on `ToolMetrics` or the MCP wire protocol changes.
