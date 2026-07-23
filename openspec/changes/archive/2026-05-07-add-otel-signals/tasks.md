## 1. Telemetry surface

- [x] 1.1 Add `Server/Observability/Telemetry.cs` with one `ActivitySource("DevBitsLab.Mcp.SourceGraph", <asm version>)` and one `Meter("DevBitsLab.Mcp.SourceGraph", <asm version>)`. Read the version from `AssemblyInformationalVersionAttribute` with a `0.0.0` fallback.
- [x] 1.2 Define four instruments on the meter: `sourcegraph.tool.calls` (Counter<long>, unit `{call}`), `sourcegraph.tool.errors` (Counter<long>, unit `{call}`), `sourcegraph.tool.duration` (Histogram<double>, unit `ms`), `sourcegraph.tool.response_size` (Histogram<long>, unit `By`).
- [x] 1.3 Mark the type `public static` so the names are stable for downstream consumers configuring `AddSource(Telemetry.Name)` / `AddMeter(Telemetry.Name)`.

## 2. Wire into ToolMetrics

- [x] 2.1 In `ToolMetrics.TrackAsync`, open an `Activity` via `Telemetry.ActivitySource.StartActivity($"mcp.tool {toolName}", ActivityKind.Server)` before invoking the body. Attach tags `mcp.tool.name` and `mcp.tool.scope` (the latter resolved through the existing `ExtractScope(args)` reflection path).
- [x] 2.2 On exception, set `Activity.Status` to `ActivityStatusCode.Error` with the exception message and record `exception.type` as a tag, then rethrow.
- [x] 2.3 On completion, record `mcp.tool.response_bytes` as a tag (UTF-8 byte count of the response — matches the `sourcegraph.tool.response_size` histogram's `By` unit). The tag was originally drafted as `response_chars`; renamed during PR review to align unit and value.
- [x] 2.4 Mirror the same wiring in `ToolMetrics.TrackSync`.
- [x] 2.5 Inside `ToolMetrics.Record`, after the existing in-memory aggregation + JSONL append, emit one sample on each instrument with tags `{ mcp.tool, mcp.tool.ok, mcp.tool.scope (optional) }`. Use `TagList` to avoid intermediate allocations.

## 3. Documentation

- [x] 3.1 Update `README.md` "Observability" section to enumerate the three signal surfaces (JSONL, `usage_stats`, OpenTelemetry) and include the instrument names and a `dotnet-counters` invocation.
- [x] 3.2 Note in `docs/ARCHITECTURE.md` that every wrapped tool call emits an OTel span / counter sample alongside the existing JSONL line, so future maintainers don't accidentally duplicate the wiring.

## 4. Tests

- [x] 4.1 Unit test: subscribe an `ActivityListener` to `DevBitsLab.Mcp.SourceGraph` and assert that one `Activity` named `mcp.tool <name>` with tags `mcp.tool.name=<name>` is captured per `Track*` call. (`TelemetrySignalTests.TrackAsync_withActivityListener_capturesOneServerActivity` plus the failure-status companion.)
- [x] 4.2 Unit test: subscribe a `MeterListener` to the same name and assert that a successful call emits exactly one sample on `sourcegraph.tool.calls` and zero on `sourcegraph.tool.errors`; a failed call emits one on each. (`TelemetrySignalTests.TrackAsync_withMeterListener_emitsCallsAndDurationOnSuccess_butNoErrors` + `..._emitsBothCallsAndErrorsOnFailure`; scope-tag presence/absence covered by `..._withScopeArg_attachesScopeTagToEverySample` + `..._withoutScopeArg_omitsScopeTag`.)
- [x] 4.3 Unit test: with no listeners attached, `Track*` runs without throwing and surfaces the body's return value unchanged (sanity check — the no-listener cost path is the same shape as pre-change). (`TelemetrySignalTests.TrackAsync_withoutAnyListeners_runsAndReturnsBodyResult`.) Plus a name-stability assertion (`Telemetry_exposesPublicNameMatchingTheSpec`) so a future rename of `Telemetry.Name` fails CI loudly rather than silently breaking downstream OTel configurations.

## 5. Update specs

- [ ] 5.1 On archive, sync delta into `openspec/specs/observability/spec.md` (ADDED requirement: OpenTelemetry signals).
