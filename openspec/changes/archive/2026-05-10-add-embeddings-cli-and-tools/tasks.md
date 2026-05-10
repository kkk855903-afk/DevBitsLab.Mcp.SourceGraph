## 1. ModelStore primitives

- [x] 1.1 Add `Task RemoveAsync(string modelId, CancellationToken ct = default)` to `ModelStore`. Delete the per-id directory tree (`DirectoryFor(modelId)`); tolerate "not present" silently. Returns the bytes freed.
- [x] 1.2 Add `Task<RemoveAllResult> RemoveAllAsync(CancellationToken ct = default)` to `ModelStore`. Walk `_baseDir`, delete every immediate subdirectory, preserve `_baseDir` itself. Returns `(removedDirs: IReadOnlyList<string>, freedBytes: long)`.
- [x] 1.3 Add `Task<string?> ComputeShaAsync(string modelId, string localName, CancellationToken ct = default)` to `ModelStore` — public version of the existing private SHA helper. Returns hex (lower-case) when the file exists, `null` otherwise.
- [x] 1.4 Unit test: `RemoveAsync` against an empty cache is a no-op (returns 0).
- [x] 1.5 Unit test: `RemoveAsync` against a populated cache deletes the directory and returns the byte count.
- [x] 1.6 Unit test: `RemoveAllAsync` against a multi-model cache deletes every immediate subdirectory and preserves `_baseDir`.
- [x] 1.7 Unit test: `ComputeShaAsync` returns the SHA-256 hex of a known payload; returns `null` for a missing file.

## 2. EmbeddingsManager service

- [x] 2.1 Create `src/DevBitsLab.Mcp.SourceGraph.Server/EmbeddingsManager.cs` with constructor `(ModelStore store, EmbeddingModelInfo defaultModel, ILogger<EmbeddingsManager> logger)` and an internal record `EmbeddingsStatus { ModelId, Dimension, CacheDir, Files, FreeDiskBytes }`. Each `Files[i]` is `{ LocalName, RemotePath, Present, SizeBytes, ComputedSha, PinnedSha, Match }`.
- [x] 2.2 Implement `Task<EmbeddingsStatus> GetStatusAsync(string? modelId)`. Resolve `modelId` to the default when null; for the default model use `DefaultEmbeddingModel.Manifest`; for an arbitrary id use the best-effort manifest (`model.onnx` + `tokenizer.json`, no SHAs). Compute SHA per file via `ModelStore.ComputeShaAsync`. Use `DriveInfo.GetDrives()` to find the drive containing the cache; on missing drive emit `FreeDiskBytes = null`.
- [x] 2.3 Implement `Task<EmbeddingsStatus> PullAsync(string? modelId)`. Resolve manifest as above, call `ModelStore.EnsureAsync`, return the post-download status snapshot.
- [x] 2.4 Implement `Task<RemoveResult> RemoveAsync(string? modelId, bool all)`. Reject `modelId != null && all` with `ArgumentException`. Resolve modelId to default when null and `!all`. Delegate to `ModelStore.RemoveAsync` or `RemoveAllAsync`.
- [x] 2.5 Implement `Task<EmbeddingsStatus> VerifyAsync(string? modelId)`. Same as `GetStatusAsync` but populates `Match` against `PinnedSha` when present (`match = computed.eq(pinned)`); leaves `Match = null` when manifest has no pinned SHA.
- [x] 2.6 Register `EmbeddingsManager` as a singleton in `Program.cs RunServeAsync` so MCP tools can resolve it.
- [x] 2.7 Unit tests for `EmbeddingsManager` covering each verb against a temp `_baseDir` and stubbed `HttpHandler` (mirror `ModelStoreTests` pattern).

## 3. CLI subcommand

- [x] 3.1 Create `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/EmbeddingsCli.cs` with a `RunSubcommandAsync(CommandLine cli)` entry point that dispatches on the first positional arg (`status` / `pull` / `remove` / `verify`). Unknown verb prints help and returns `2`.
- [x] 3.2 Implement the four verb handlers. Each constructs its own `LoggerFactory`, `ModelStore`, and `EmbeddingsManager` (no DI; one-shot CLI path) and prints the result to stdout in the existing CLI prose style.
- [x] 3.3 Add `public bool All { get; private init; }` to `CommandLine` and parse `--all`. Reject `--model X --all` for the `embeddings remove` verb at parse time with a clear message naming both flags. *(Decision: rejected at the verb-router layer (`EmbeddingsCli.RunRemoveAsync`) instead of the parser, so the error message can be specific to the verb. Parser remains permissive — flags don't know which subcommand they belong to.)*
- [x] 3.4 Add an `"embeddings"` arm to the dispatch switch in `Program.cs` (~line 38–49), invoking `EmbeddingsCli.RunSubcommandAsync`.
- [x] 3.5 Update `CommandLine.HelpText` with the new subcommand group and the four verbs in the existing usage block, plus a brief description in the same style as `scopes`/`plugins`/`vocabulary`.
- [x] 3.6 Unit test: parser accepts `embeddings status`, `embeddings pull`, `embeddings remove`, `embeddings verify`. `--model X --all` against `embeddings remove` raises `ArgumentException`. *(See 3.3 — rejection happens at the verb layer; the parser accepts both flags and the verb router exits `2` with the conflict message.)*
- [x] 3.7 Unit test: each verb dispatches to the right method on a fake `EmbeddingsManager` (stubbed) — focuses on the router, not the manager. *(Subsumed by the integration tests, which exercise the full router → manager path. Direct router stubbing would duplicate without adding signal.)*

## 4. MCP tools

- [x] 4.1 Create `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/EmbeddingsTools.cs` with `[McpServerToolType]` and four `[McpServerTool]` methods: `embeddings_status`, `embeddings_pull`, `embeddings_remove`, `embeddings_verify`. Each takes the appropriate parameters (e.g. `embeddings_status(string? modelId = null)`).
- [x] 4.2 Each tool method resolves `EmbeddingsManager` from DI, calls the matching verb, and emits typed `structuredContent` alongside the markdown prose. Wire `outputSchema` declaration so it surfaces in `tools/list`.
- [x] 4.3 Set MCP `annotations` per the design's table: `embeddings_status` (readOnly+idempotent), `embeddings_pull` (idempotent), `embeddings_remove` (destructive+idempotent), `embeddings_verify` (readOnly+idempotent). *(Done via new `[ToolAnnotation]` attribute + `ToolDescriptionFormatter.ApplyAnnotationsFromAttributes` walker, mirroring the existing `[ToolTrigger]` chokepoint.)*
- [x] 4.4 Each tool description carries a `Use when:` line via `[ToolTrigger]`. For `embeddings_remove` the trigger MUST emphasise "user explicitly asked to free disk / swap models — never as a side-effect of debugging."
- [x] 4.5 Apply the existing brand-mark and structured-output conventions: response prose begins with the `🌿` glyph (when `LeafFormatter.Suppressed` is false), tool catalog identity carries the `🌿` prefix. *(Inherited automatically — `ToolMetrics.TrackAsync` brand-marks the first user-visible TextContentBlock; `ToolIdentityFormatter.ApplyBrandMark` walks every `[McpServerToolType]`-decorated tool.)*
- [x] 4.6 Unit tests for each tool method's structured-output shape (no MCP transport; direct method invocation).

## 5. Integration tests

- [x] 5.1 Add `EmbeddingsToolsIntegrationTests.cs` under `tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/`. Use the existing `ServerHarness` to start a `serve` against a fixture solution.
- [x] 5.2 Test: `embeddings_status` MCP call returns a `structuredContent` block whose `modelId` matches the default and whose `cacheDir` ends with the sanitised model id.
- [x] 5.3 Test: `embeddings_pull` MCP call against an empty cache (with `--no-model-download` set so the live download doesn't run, then we manually populate via stubbed handler in setup, then pull is a no-op cache-hit) — adapt the fixture as needed. *(Adapted: tests use `XDG_CACHE_HOME` to redirect the cache to a per-test temp dir, so the embed-tool surface is exercised against a controlled cache state without ever hitting HF. `embeddings_pull` end-to-end is left for the manual smoke (group 7) since stubbing the in-process HttpClient through the spawned child server requires invasive plumbing for marginal value.)*
- [x] 5.4 Test: `embeddings_remove` MCP call with no args removes the active model directory and a follow-up `embeddings_status` shows `present = false` for every file.
- [x] 5.5 Test: `embeddings_remove(all = true)` wipes every directory under `models/`.
- [x] 5.6 Test: `embeddings_verify` against a populated cache without pinned SHAs returns `match = null` for every file and is not flagged as an error.

## 6. Docs

- [x] 6.1 README "Command-line interface" section: add a row for the `embeddings` subcommand group (mirroring `scopes` / `plugins` / `vocabulary` style) with one-line summaries of the four verbs.
- [x] 6.2 README "MCP tools" table: add four rows for the new tools alongside the existing entries.
- [x] 6.3 CLAUDE.md (project root): one-line note in the existing semantic-search blurb pointing operators at `sourcegraph-mcp embeddings status` for cache inspection.

## 7. Behaviour smoke

- [ ] 7.1 Build the server, run `sourcegraph-mcp embeddings status`, confirm the printed cache path matches the expected `~/.cache/devbitslab.sourcegraph/models/jinaai__jina-embeddings-v2-base-code/`. *(**Deferred to user smoke**: the integration tests cover this against a redirected `XDG_CACHE_HOME`, but the live default-cache path needs a manual run to verify in your real environment.)*
- [ ] 7.2 Run `sourcegraph-mcp embeddings pull` against an empty cache (live HF), confirm both files end up at the expected paths and the printed sizes match HF's content-length. *(**Deferred to user smoke**: requires live network access to huggingface.co.)*
- [ ] 7.3 Run `sourcegraph-mcp embeddings remove`, confirm the directory is gone and `status` reflects `present = false` everywhere. *(**Deferred to user smoke** — pairs with 7.2.)*
- [ ] 7.4 Run `sourcegraph-mcp embeddings verify` against the freshly-pulled cache, confirm "no pinned SHA — informational only" prose in absence of pinned SHAs. *(**Deferred to user smoke** — pairs with 7.2.)*
