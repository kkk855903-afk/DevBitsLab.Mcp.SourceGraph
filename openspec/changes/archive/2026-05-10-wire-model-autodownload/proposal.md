## Why

Embeddings are silently disabled on every fresh checkout. The Hugging Face downloader (`ModelStore.EnsureAsync`) was implemented but never wired into server startup, so the model files are never fetched. The generator then sees the cache is empty, flips itself unavailable, and the embedding pipeline sits idle — `semantic_search` returns the disabled-message even though the user did nothing wrong. This contradicts the README ("Disable with `--no-embeddings` to skip the model download entirely") and the archived semantic-search spec, both of which promise auto-download on first run.

## What Changes

- Wire `ModelStore.EnsureAsync` into the `serve` and `index` startup paths so the model is fetched (or verified cached) before `EmbeddingsHostedService` starts processing the embed channel. The bulk indexer is **not** blocked on the download — it queues embed requests; the worker drains them once the model is ready.
- Add a `Manifest` to `DefaultEmbeddingModel` listing the files needed for `jinaai/jina-embeddings-v2-base-code` with optional pinned SHA-256 hashes.
- Split `ModelFile.FileName` into `RemotePath` (HF-relative source path) and `LocalName` (cache filename). For Jina v2 the ONNX file lives at `onnx/model.onnx` on HF but the generator expects `model.onnx` flat in the cache; this shape lets us map them. **BREAKING** for anyone calling `ModelStore.EnsureAsync` externally — but it's an internal API, no public consumers exist.
- Add a `--no-model-download` CLI flag (and matching `SOURCEGRAPH_NO_MODEL_DOWNLOAD` env var). With this flag, the server enables embeddings only if the cache is already populated; otherwise it behaves like `--no-embeddings`. Useful for air-gapped environments where the operator wants to deny network egress but still use embeddings if a sysadmin pre-populated the cache.
- On download failure (network, SHA mismatch, timeout): keep current degraded behaviour, but improve the warning to point at the cache directory and at the `--no-embeddings` / `--no-model-download` opt-outs.
- When a `progressToken` is supplied by an MCP `tools/call` that triggers semantic search, emit `notifications/progress` ticks during a download. (No-op for direct `dotnet run` use; the cold-start UX in connected clients gets a visible progress bar instead of a silent stall.)

Out of scope (follow-ups, not promised here):
- Explicit `sourcegraph-mcp pull-model` CLI subcommand for manual prefetch.
- Bundling the model bytes in a separate optional NuGet for offline distribution.

## Capabilities

### New Capabilities
*(none — auto-download is a delivery mechanism for an existing capability, not a new one)*

### Modified Capabilities
- `semantic-search`: adds an "Auto-download on first run" requirement; tightens the existing "Model not yet downloaded" scenario so it covers the `--no-model-download` path explicitly.
- `cli`: adds `--no-model-download` to the documented flag set.

## Impact

- **Affected code**: `Program.cs` (serve + index startup blocks), `ModelStore.cs`, `EmbeddingTypes.cs` (manifest), `CommandLine.cs` (flag).
- **Affected behaviour**: First start with embeddings enabled will reach huggingface.co to download ~280 MB. Subsequent starts use the cache (idempotent, no network). Users who want to deny network egress have `--no-embeddings` (existing) and `--no-model-download` (new).
- **Tests**: adds unit coverage for `RemotePath != LocalName` in `ModelStore`; adds an integration test that verifies the cache is populated on first start (using a stubbed `HttpMessageHandler` so CI doesn't actually hit HF). Existing `EmbeddingsDisabledPathTests` keep passing for the `--no-embeddings` shape.
- **Dependencies**: no new packages.
- **Docs**: README's "Optional code-aware semantic search" section gets one extra sentence noting the cold-start download size and the `--no-model-download` knob; CLI help text gains the new flag.
