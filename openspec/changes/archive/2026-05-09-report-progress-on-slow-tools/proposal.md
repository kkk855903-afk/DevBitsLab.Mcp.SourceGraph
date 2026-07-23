## Why

Most of our tools are sub-100ms — `find_definition`, `find_references`, `search_symbols`, etc. all hit indexed SQLite + FTS5 tables and return inside one tick. But a few tool calls have legitimately multi-second tails:

- **`semantic_search` on the first call after server start** — the singleton `JinaCodeEmbeddingGenerator` is lazily instantiated by DI, which means the first request triggers a fresh ONNX model load (typically 3–5 seconds depending on disk + CPU). Subsequent calls reuse the loaded model and are sub-second.
- **`impact_of_change` with deep `maxDepth`** on large graphs — the recursive CTE that walks the call edges can easily run for several seconds when `maxDepth ≥ 6` and the inbound fan-in is wide.
- **`module_summary`** on namespaces containing thousands of symbols — the in-degree aggregation crosses the same wide join.

Today these tools render as silent stalls in the chat UI. The agent and the human reading along see the request fire and then nothing until the response arrives. There's no signal that work is happening, what stage it's in, or how much remains. The MCP protocol's `notifications/progress` mechanism exists exactly for this: a tool can report intermediate progress, and any client that opted in via a `progressToken` on the request renders it (chat panels typically as a status line, terminal clients as a pulsing indicator).

The SDK spike confirmed this is straightforward: an `IProgress<ProgressNotificationValue>` parameter on a tool method is auto-injected by the SDK, exactly like `CancellationToken`. When the client didn't pass a `progressToken`, the injected reporter is a no-op — zero cost for clients that don't ask for progress.

This change is narrow on purpose: introduce the *mechanism* on the slowest tool (`semantic_search`) so the contract is established, then let future changes opt other tools in as their slowness profile becomes clear. Establishing the pattern now keeps follow-on adds short and uniform.

## What Changes

- **`SemanticSearchAsync` accepts an `IProgress<ProgressNotificationValue>` parameter** that the SDK auto-injects. The body emits progress at three coarse checkpoints:
  1. Before encoding the query (often the slow step on cold start because the encoder loads the ONNX model lazily).
  2. After encoding, before the vector-similarity search.
  3. After the search completes, before formatting the response.
  Each `ProgressNotificationValue` carries `Progress` (a monotonically increasing double in 0..1), `Total = 1.0` (so the wire indicates completion proportion), and a short human-readable `Message` (e.g. `"encoding query"`, `"searching"`, `"formatting results"`).
- **`Format.Progress` helper** wraps the boilerplate of building `ProgressNotificationValue` objects so individual tool methods don't repeat themselves. Lives next to the existing `Format.*` helpers.
- **No behavioral change for clients that didn't request progress** — the SDK silently no-ops `progress.Report(...)` when the request didn't include a `progressToken`. The wire-level fast path is unchanged.
- **No new dependencies, no SDK version bump.** The `IProgress<ProgressNotificationValue>` injection is supported by the `ModelContextProtocol` 1.2.0 SDK we already ship.
- **`impact_of_change` and `module_summary` get the parameter** but emit progress only at the start (`"running query"`) and end. They become genuine progress citizens once a future change adds per-depth or per-batch checkpoints; the IProgress acceptance is the one-line foundation.
- **Documentation update** in `README.md` and `CLAUDE.md`: clients can opt into progress by sending a `progressToken` in their `tools/call` request — the protocol-level mechanism, with no source-graph-specific configuration.

## Capabilities

### New Capabilities
<!-- None — the protocol-level progress feature is already part of MCP; this change just opts in. -->

### Modified Capabilities

- `mcp-tools`: Adds a new requirement, `Progress notifications on slow tools`, scoped to `semantic_search`, `impact_of_change`, and `module_summary`. The requirement specifies which tools accept `IProgress<ProgressNotificationValue>`, what their progress checkpoints are, and the no-op contract for clients that didn't request progress.

## Impact

- **Code (small)**: `Tools/GraphTools.cs` — three method signatures gain an `IProgress<ProgressNotificationValue> progress` parameter (positioned before `CancellationToken ct = default`); their bodies call `progress.Report(...)` at the documented checkpoints. New `Format.Progress(...)` helper. No type changes elsewhere.
- **Spec**: 1 new requirement in `openspec/specs/mcp-tools/spec.md` with three scenarios (`semantic_search` checkpoints, `impact_of_change` checkpoints, no-op when client didn't request progress).
- **Tests**: New `ProgressReportingTests.cs` exercises the three converted tools with a fake `IProgress<ProgressNotificationValue>` reporter that captures emitted progress values; assertions check that the progress sequence has the expected checkpoint count and that `Progress` increases monotonically. A negative test confirms the chokepoint behaviour: when the parameter is `IProgress.NoOp` (or `null`?), no report is captured.
- **Public API / dependencies**: None. The MCP SDK's `IProgress<ProgressNotificationValue>` injection is part of 1.2.0. No new NuGet refs.
- **Token cost**: Zero on the wire when client didn't request progress (SDK no-ops). When client did request progress, each checkpoint adds a single `notifications/progress` message (~50 bytes JSON-RPC overhead, ~100 bytes total per checkpoint). Three checkpoints per `semantic_search` call ≈ 300 bytes, dominated by the response body anyway.
- **Plugin compatibility**: Plugin tools registered via `IToolRegistry.AddTool` keep their existing `Delegate` signatures. If a plugin author wants progress reporting, they add an `IProgress<ProgressNotificationValue>` parameter to their handler and the SDK injects it the same way it does for built-in tools.
- **Backward compatibility**: Pure additive. Tool wire format unchanged, tool result shape unchanged. Older clients that don't support `progressToken` simply don't request progress and see today's silent-then-result behaviour, unchanged.
- **Documentation**: README + CLAUDE.md notes about which tools emit progress and the client-side opt-in via `progressToken`.
