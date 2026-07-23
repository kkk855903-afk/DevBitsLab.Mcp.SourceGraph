## Why

The first time an MCP client connects to a freshly-started `sourcegraph-mcp serve`, two things can take 10–60 seconds and offer no visible feedback:

1. **Cold-start indexing.** The first `tools/call` from any client awaits `ScopeHost.Ready` while the indexer reads the solution, walks every `.cs` file, and writes symbols / refs / edges to SQLite. Today the call blocks silently; the agent's chat panel shows a spinner and the human reading along has no idea whether it's making progress or hung.
2. **Embedding model download.** The first `semantic_search` call after a fresh install pulls ~480 MB of ONNX model weights from HuggingFace before encoding can begin. Today the existing `notifications/progress` instrumentation on `semantic_search` emits a single `"encoding query"` checkpoint *before* the download starts; the user sees `0%` for several minutes while the actual work — the download — is invisible.

The `notifications/progress` flow exists in MCP and is already wired into three slow tools (`semantic_search`, `impact_of_change`, `module_summary`) by the [`add `report-progress-on-slow-tools`](../../specs/mcp-tools/spec.md) requirement. The infrastructure is there; the cold-start and download paths just don't use it yet.

This change closes both gaps without adding any new wire mechanism.

## What Changes

- **Cold-start awareness in tool-call wrappers.** When a `tools/call` arrives at a scope whose first index hasn't finished, the wrapper that today silently `await`s `ScopeHost.Ready` SHALL instead subscribe to a per-scope `IIndexingProgressSource` exposed by `LiveIndexService`, forward each emitted progress event as a `notifications/progress` message tagged with the originating request's `progressToken` (when supplied), and only return the underlying tool's result after `Ready` completes. Clients that didn't supply a `progressToken` see no notifications and behave exactly as today.
- **Per-scope `IIndexingProgressSource` on `LiveIndexService`.** The hosted service that drives initial indexing SHALL emit progress at three coarse phase checkpoints: `opening workspace` (0.0), `indexing` (0.5), and `ready` (1.0). Per-document granularity is deferred — `RoslynIndexer.IndexAllAsync` doesn't expose a per-document callback today, and adding one is its own change.
- **`logging/message` notifications at server start are deferred.** The original proposal listed this as a goal; it requires hooking the SDK's `IMcpServer` instance (constructed by the host *after* `LiveIndexService` starts) to emit notifications before any `tools/call` has arrived. That integration is its own piece of work. v1 of this change ships the per-scope progress source + tool-call wrapper forwarding; the existing stderr `ILogger` lifecycle output is unchanged.
- **`find_definition` becomes progress-aware.** Adds an `IProgress<ProgressNotificationValue>` parameter to `FindDefinitionAsync` and threads it through `ScopedExecution.RunAsync` — making it the canonical poster-child for cold-start progress (the most likely first-call any user makes). Existing IProgress-aware tools (`semantic_search`, `impact_of_change`, `module_summary`) also gain the cold-start forwarding. Other tools stay silent during cold-start; future changes can opt them in.
- **Embedding model download progress is out of scope.** The original proposal listed download progress as a goal; investigation found that `JinaCodeEmbeddingGenerator` doesn't currently auto-download (it expects model files to already exist on disk; `ModelStore.EnsureAsync` exists but isn't wired). Adding the download capability + progress is a distinct feature and is not part of this change.
- **No protocol additions.** Everything described above uses MCP primitives already in the SDK (`notifications/progress`, `notifications/message`). No new tool, no new resource, no schema changes.

## Capabilities

### New Capabilities
<!-- None — this change extends three existing capabilities to cover the cold-start and download paths. -->

### Modified Capabilities

- `live-updates`: Adds a per-scope `IIndexingProgressSource` that the tool-call wrapper subscribes to during startup-blocking awaits, and a startup `notifications/message` requirement.
- `mcp-tools`: Extends the existing "Progress notifications on slow tools" requirement to cover the cold-start path for progress-aware tools (`find_definition`, `semantic_search`, `impact_of_change`, `module_summary`): when one of these tools' calls blocks on `ScopeHost.Ready` and the request carried a `progressToken`, the server forwards indexing-phase progress until the scope is ready and the underlying tool runs.

## Impact

- **Code (small-medium).** New `Indexing/IndexingProgressSource.cs` exposing the per-phase checkpoints. `LiveIndexService` instantiates one per scope and reports through it. The MCP tool-call wrapper (where `await ScopeHost.Ready` lives today) gains progress-forwarding logic that bridges the request's `IProgress<ProgressNotificationValue>` (when present) to the source's events. `JinaCodeEmbeddingGenerator` gains an `IProgress<ProgressNotificationValue>?` parameter on `EmbedAsync` (auto-injected today as null; passed through from `SemanticSearchAsync`); during model download, an HTTP-streaming progress callback emits download progress.
- **Spec.** Modifications to `live-updates` (1 new requirement: per-scope progress source + startup logging-message), `semantic-search` (1 new requirement: download progress), and `mcp-tools` (1 modified requirement: extend cold-start coverage).
- **Tests.** New `ColdStartProgressTests.cs` covering: progress emitted during a startup-blocked tool call when client supplies progressToken; no progress when no token; `logging/message` emitted at server start. New `EmbeddingDownloadProgressTests.cs` covering download checkpoints (with a fake HTTP handler whose stream writes 1MB chunks). Existing `ProgressReportingTests.cs` continue to pass — the three fast-path checkpoints (`encoding query`, `searching`, `formatting results`) are unchanged when no download is needed.
- **Public API / dependencies.** No new NuGet refs. No SDK bump. The `IProgress<ProgressNotificationValue>` parameter on `JinaCodeEmbeddingGenerator.EmbedAsync` is a new public surface; existing in-repo callers are updated, no third party consumes that type.
- **Backward compatibility.** Pure additive on the wire. Clients that don't supply a `progressToken` see no new notifications. Clients that supply one see incremental progress they previously didn't get. `logging/message` notifications are emitted regardless of `progressToken`; clients that don't surface them ignore them.
- **Performance.** The download progress callback fires every ~1 MB during the ONNX fetch — a single-digit number of `notifications/progress` per download. Cold-start progress fires once per indexing phase plus once per ~100 documents during pass 2 — bounded by the document count of the largest scope. Negligible compared to the work being narrated.
- **Documentation.** README "Observability" section gains a paragraph about cold-start and download visibility (alongside the existing four bullets). CLAUDE.md "Tool-usage guidance" gets a one-line note that first-call latency now narrates itself.
