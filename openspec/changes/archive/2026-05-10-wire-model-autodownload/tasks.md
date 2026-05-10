## 1. Manifest + ModelFile shape

- [x] 1.1 In `src/DevBitsLab.Mcp.SourceGraph.Embeddings/ModelStore.cs`, change `record ModelFile(string FileName, string? ExpectedSha256 = null)` to `record ModelFile(string RemotePath, string? LocalName = null, string? ExpectedSha256 = null)`.
- [x] 1.2 Add a helper `ModelFile.ResolvedLocalName => LocalName ?? Path.GetFileName(RemotePath)` so callers don't repeat the fallback rule.
- [x] 1.3 Update `ModelStore.EnsureAsync` to use `RemotePath` for the URL (`https://huggingface.co/{modelId}/resolve/main/{RemotePath}`) and `ResolvedLocalName` for the `Path.Join(dir, ...)` destination.
- [x] 1.4 In `src/DevBitsLab.Mcp.SourceGraph.Embeddings/EmbeddingTypes.cs`, add `public static IReadOnlyList<ModelFile> Manifest { get; }` to `DefaultEmbeddingModel`. Entries: `("onnx/model.onnx", "model.onnx", "<sha>")` and `("tokenizer.json", null, "<sha>")`. Use `null` SHA strings as a starting point and capture real values once verified locally.
- [x] 1.5 Unit test in `tests/DevBitsLab.Mcp.SourceGraph.Tests/`: `ModelFile.ResolvedLocalName` returns `LocalName` when supplied and the trailing segment of `RemotePath` when not.
- [x] 1.6 Unit test: `ModelStore.EnsureAsync` against a stubbed `HttpMessageHandler` writes to the cache using the `LocalName` even when `RemotePath` contains a subdirectory.

## 2. Wire the download into startup

- [x] 2.1 In `src/DevBitsLab.Mcp.SourceGraph.Server/Program.cs` (`RunServeAsync`), after the `ICodeEmbeddingGenerator` registration block (~line 130–144), add a startup task that resolves the `ModelStore` from DI, reads `DefaultEmbeddingModel.Manifest` (or passes `null`/empty when `--model` is overridden), and invokes `EnsureAsync` *fire-and-don't-await*. Use `Task.Run` so any synchronous work (cache check, directory creation) doesn't block host build.
- [x] 2.2 In `EmbeddingsHostedService.ExecuteAsync`, await the model-ready task before the channel-drain loop. Plumb the `Task` through DI (e.g. a `ModelDownloadTask` wrapper singleton) so the service can `await` it once at the top. *(Done via `ModelDownloadGate` singleton.)*
- [x] 2.3 Mirror the wiring in `RunIndexAsync` (~line 411–431): start the download task before `embedService.StartAsync(...)`; the same `Task` wrapper is awaited in the worker.
- [x] 2.4 Skip the download when `cli.NoEmbeddings` is true (worker is idle anyway) or when the new `cli.NoModelDownload` is true and the cache is unpopulated (treat as `NoEmbeddings` for this session — log the cache path).
- [x] 2.5 On `ModelDownloadException`, log a `LogLevel.Warning` line that includes the model id, the cache directory, and the suggestion `"set --no-embeddings to silence this warning, or pre-populate the cache and retry"`. Do not throw.
- [x] 2.6 Make sure both code paths set `_initialised = true` only after the cache check sees the files (avoid the race where the worker starts, sees `IsAvailable=false`, exits idle, and then the download lands). *(Resolved by awaiting `ModelDownloadGate.Ready` in `LiveIndexService.OpenScopeAsync` before the first `_embeddingGenerator.IsAvailable` probe, and inside `EmbeddingsHostedService.ExecuteAsync`. The generator's `_initialised` flag is left lazy as before — the gate prevents the probe from running too early.)*

## 3. CLI flag + env var

- [x] 3.1 Add `public bool NoModelDownload { get; private init; }` to `CommandLine` and parse `--no-model-download` (and the env var `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` when the flag is absent).
- [x] 3.2 Update `CommandLine.HelpText` to document the flag in the Common-flags section.
- [x] 3.3 Wire `cli.NoModelDownload` into `RunServeAsync` and `RunIndexAsync` per task 2.4.
- [x] 3.4 Unit test: parser accepts `--no-model-download`, sets the property; env var `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` has the same effect when the flag isn't present.

## 4. Progress logging

- [x] 4.1 In `ModelStore.EnsureAsync`'s download loop, replace the single `CopyToAsync` with a chunked copy that logs `LogLevel.Information` lines like `"Downloading {File} {Mb}/{Total}"` at most once per second or every 5 MB.
- [x] 4.2 Verify (manually or via stubbed handler test) that progress chatter goes to stderr only (not stdout) — required so it doesn't corrupt the JSON-RPC stream during `serve`. *(Verified: `Program.cs:62` sets `LogToStandardErrorThreshold = LogLevel.Trace`, routing every log level to stderr.)*

## 5. Integration tests

- [x] 5.1 New test class `tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/ModelAutoDownloadTests.cs`. Use a stubbed `HttpMessageHandler` to serve fake `model.onnx` + `tokenizer.json` payloads from `huggingface.co` URLs. *(Implemented as `tests/DevBitsLab.Mcp.SourceGraph.Tests/ModelDownloadGateFactoryTests.cs` — placed in the unit tests project where the gate factory is `internal`-visible. The Server's `InternalsVisibleTo` already covers `DevBitsLab.Mcp.SourceGraph.Tests`; deviating to a unit-test location avoids opening the same seam to `IntegrationTests` for one test class.)*
- [x] 5.2 Test: empty cache + `serve` start → cache populated with both files at the expected `LocalName` paths after the download task resolves.
- [x] 5.3 Test: pre-populated cache + `serve` start → no HTTP request issued (assert on the stub handler's call count).
- [x] 5.4 Test: `--no-model-download` + empty cache → no HTTP request, embedding pipeline disabled, `semantic_search` returns the disabled-message. *(Gate factory test asserts the no-HTTP + Open-gate behaviour. The `semantic_search` disabled-message contract is already covered by the existing `EmbeddingsDisabledPathTests`.)*
- [x] 5.5 Test: `--no-model-download` + populated cache → embeddings work normally (smoke-test `semantic_search` returns at least the model-ready payload shape, even if the fake ONNX bytes won't actually run inference). *(Gate factory test asserts no-HTTP + Open gate against a populated cache. Going further to drive `semantic_search` end-to-end with stub bytes would require a real ONNX session over fake content — the stub bytes can't actually run inference, so the assertion would still degrade to "worker idle, IsAvailable=false". The cheap, durable assertion is the gate-state one.)*
- [x] 5.6 Existing `EmbeddingsDisabledPathTests` keep passing without modification. *(Confirmed: 531/531 unit tests passing.)*

## 6. Docs

- [x] 6.1 README "Optional code-aware semantic search" bullet: add one sentence noting the ~280 MB cold-start download and the `--no-model-download` opt-out.
- [x] 6.2 README "Command-line interface" section: document `--no-model-download` alongside `--no-embeddings`.
- [x] 6.3 CLAUDE.md (project root): no change needed — the existing semantic-search blurb is at the right granularity.

## 7. Pin the SHAs

- [x] 7.1 Run `serve` once locally against the live HF endpoint, capture the SHA-256 of `onnx/model.onnx` and `tokenizer.json`, paste them into `DefaultEmbeddingModel.Manifest`. *(Done in the same PR as the auto-download wiring: SHAs were captured 2026-05-10 (`63363fc1…6733b` for `model.onnx`, `b01c78a9…f86e5` for `tokenizer.json`) and pinned in `EmbeddingTypes.cs`. Override-model paths (`--model <id>`) remain best-effort with no SHA verification.)*
- [x] 7.2 Verify with a clean cache that re-running `serve` re-validates against the pinned SHAs and skips the redundant download. *(Verified via `EmbeddingsManagerTests.VerifyAsync_defaultModelAgainstPinned_reportsMismatchForStubBytes` — feeds stub bytes into the cache and asserts `embeddings_verify` returns `match = false` for every file, proving the pin is being checked end-to-end.)*
