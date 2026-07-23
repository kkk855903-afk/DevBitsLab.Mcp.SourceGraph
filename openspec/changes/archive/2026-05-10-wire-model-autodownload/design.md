## Context

`ModelStore.EnsureAsync` was implemented as part of the original 2026-05-03 semantic-search change and ticked off in that change's `tasks.md` (item 2.2). The downloader is correct in isolation — it streams from `https://huggingface.co/{modelId}/resolve/main/{fileName}` to a `.tmp` sibling, verifies SHA-256, renames atomically. What never landed is the call site: `Program.cs` registers `ModelStore` in DI but only uses it to compute `FilePath(...)`, which is a pure path-building method that does no IO. The result: every fresh checkout has empty cache → `JinaCodeEmbeddingGenerator.EnsureInitialised()` flips `_available = false` → embeddings silently disabled.

A second smaller bug surfaces once we wire this up: the URL template `…/resolve/main/{FileName}` requires the local cache filename to match the HF source path, but for `jinaai/jina-embeddings-v2-base-code` the ONNX file lives at `onnx/model.onnx` while the generator looks for `model.onnx` flat in the cache. The current `ModelFile` record can't represent that mapping — it has a single `FileName`. We need to split it.

Constraints worth holding in mind:
- The MCP transport speaks JSON-RPC over **stdout**; logs go to stderr but are still observed by every connected client. Don't write download chatter to stdout.
- `serve` is a long-running process attached to a client (Claude Code, Cursor, …). The user's first turn after `serve` starts shouldn't hang silently for 30 s while ~280 MB downloads in the background. Progress notifications matter.
- The `index` one-shot CLI path is sometimes run in CI sandboxes with no network. Failure must be graceful (warn, continue, exit 0); not a hard error.
- The model is identified by `--model <id>` with a default. We can pin SHAs for the **default** model (we know exactly which build we shipped against). For an arbitrary `--model` override, we have to fall back to "best-effort, no SHA verification."

## Goals / Non-Goals

**Goals:**
- A fresh `dotnet tool install` (or `dotnet run`) of the server with embeddings enabled fetches the model on first start and works without manual user intervention.
- Failure modes (offline, SHA mismatch, partial download) leave the rest of the indexer functional and emit a single human-readable warning that points at the recovery path.
- Operators in air-gapped environments have a documented opt-out (`--no-model-download`) that doesn't disable embeddings entirely if the cache happens to already be populated.
- The download wire-up is identical between `serve` and `index` — same code, no copy-paste skew.

**Non-Goals:**
- A `pull-model` CLI subcommand. Composes well with auto-download but isn't required to fix the bug; tracked as a follow-up.
- Bundling the model in a NuGet package. Different distribution channel, separate decision.
- Downloading on-demand at first `semantic_search` call rather than at startup. Adds latency to a user-visible call; we already pay the cost at startup either way.
- Background re-download / model upgrades while the server is running. Out of scope; existing model-version invalidation handles model changes via restart.

## Decisions

### Decision 1: Auto-download runs at startup, before the embed worker

**What**: In `Program.cs`'s `serve` path, after constructing the `ICodeEmbeddingGenerator` singleton but **before** `EmbeddingsHostedService` starts processing the channel, run `ModelStore.EnsureAsync(modelInfo.ModelId, manifest, ct)`. Same in the `index` one-shot path: between creating the `JinaCodeEmbeddingGenerator` and calling `embedService.StartAsync(...)`.

**Why this over alternatives**:
- *Lazy download on first `EmbedAsync` call*: would mean the indexer's bulk pass starts producing embed requests, the channel fills, the worker tries to start, the model isn't there, the worker disables itself, and embeddings stay off for the whole session even though we *intended* to download. Rejected — the disabled-on-start trap is the bug we're fixing.
- *Download as a hosted service that runs before `EmbeddingsHostedService`*: hosted services don't have a deterministic ordering (other than start order from registration). Could work but adds indirection for no payoff. Rejected.
- *Block server startup until the download completes*: adds tens of seconds to MCP `initialize`; clients time out. Rejected.

The chosen shape: **fire the download, don't await it; the indexer runs concurrently; the embed worker awaits the download task before reading from the channel.** The channel buffers requests; once the model is ready, the worker drains. If the download fails, the worker observes the generator is unavailable and exits idle (current behaviour preserved).

```
   serve startup
        │
        ▼
   ModelStore.EnsureAsync(...) ───── fire-and-don't-await
        │                                     │
        ▼                                     ▼
   indexer starts, queues               (~30 s typical first run)
   embed requests on channel                  │
        │                                     ▼
        │                              generator becomes
        │                              IsAvailable=true
        │                                     │
        └────── channel drains ───────────────┘
```

### Decision 2: `ModelFile` carries `RemotePath` + `LocalName`

**What**: Replace the current `record ModelFile(string FileName, string? ExpectedSha256 = null)` with `record ModelFile(string RemotePath, string? LocalName = null, string? ExpectedSha256 = null)`. When `LocalName` is null, derive it from the trailing path segment of `RemotePath` (back-compat for entries where the remote and local names match).

**Why**:
- The HF URL template `…/resolve/main/{path}` accepts subdirectories — passing `RemotePath = "onnx/model.onnx"` resolves correctly.
- The local cache wants flat filenames so the generator can find `model.onnx` and `tokenizer.json` directly. Joining `Path.Join(modelDir, RemotePath)` would create an `onnx/` subdirectory, which the generator doesn't look for.
- Splitting the field is the smallest change that lets one entry name both ends of the pipe.

**Alternative considered**: keep `FileName`, change the *generator* to look in `onnx/` for the ONNX file. Rejected — couples the generator to the HF repo's directory layout; means swapping models becomes harder; the layout is HF's choice, not ours.

### Decision 3: Pinned SHA-256 for the default model only

**What**: `DefaultEmbeddingModel.Manifest` carries SHA-256 strings for the two files. For `--model <id>` overrides, the `Manifest` argument is null and `ModelStore.EnsureAsync` runs in best-effort mode (no hash check, atomic rename still in place).

**Why**: The default model is the one the project tested against; we know which build is correct. For arbitrary user-supplied models we can't anticipate the SHA, and silently downloading without verification is acceptable because (a) the URL is HF over TLS, (b) the user explicitly opted in via `--model`, (c) the downstream ONNX session load fails loudly if the file is corrupt.

**Alternative considered**: require SHA pinning for any model. Rejected — too restrictive; would prevent quick experimentation with other code-embedding models without a manifest config file.

### Decision 4: `--no-model-download` is separate from `--no-embeddings`

**What**: Two independent flags.
- `--no-embeddings` (existing): pipeline never starts; vec0 table still created; `semantic_search` returns disabled-payload.
- `--no-model-download` (new): pipeline starts iff the cache is already populated. Empty cache → behaves like `--no-embeddings` for this session.

**Why**: They cover different operator intents.
- `--no-embeddings` says "I don't want vector search at all, save the disk + RAM."
- `--no-model-download` says "I want vector search, but I don't trust this process to make outbound HTTP. Pre-populate the cache for me, or fall back."

Air-gapped CI / hardened developer machines (no egress to huggingface.co) need the second knob. Rolling them into one flag would force users in those environments to give up embeddings entirely.

### Decision 5: Progress notifications use the existing `progressToken` pattern

**What**: When `ModelStore.EnsureAsync` is invoked from a tool call (`semantic_search` triggering a re-fetch — a future possibility) or when startup is invoked from a context with a progress sink wired, emit `notifications/progress` on byte chunks. **For startup-triggered downloads, no progressToken exists** — the download fires before any tools/call request. So progress for startup goes only to stderr at a `LogLevel.Information` cadence (every ~5 MB or every second, whichever is later).

**Why**: The CLAUDE.md note about progressToken applies to `tools/call` interactions. The startup download isn't a tool call — there's no `progressToken` to attach to. The most we can do at startup is structured stderr logging, which connected clients (Claude Code) display in their server-output panel. Forcing the download into a tool call to surface progress would mean either delaying it until first `semantic_search` (rejected in Decision 1) or adding a synthetic startup tool call (an MCP-spec-bending hack).

**Trade-off**: First-time users may see no visible progress in their MCP client UI for ~30 s. Acceptable because:
- The stderr line "Downloading model.onnx … (122/278 MB)" is clearly visible in Claude Code's server log.
- The README documents the cold-start download.
- Subsequent starts hit the cache.

### Decision 6: Manifest lives next to `DefaultEmbeddingModel`

**What**: Add `public static IReadOnlyList<ModelFile> Manifest { get; }` to the `DefaultEmbeddingModel` static class. The `serve` and `index` paths read this when the user is on the default model and pass `null` (best-effort) when `--model` overrides it.

**Why**: Keeps the manifest co-located with the model identity it describes; one file change to swap models. Avoiding a new manifest config file (JSON, YAML) in the repo because there's exactly one default and the values are known at compile time.

## Risks / Trade-offs

[**HF availability**: huggingface.co goes down or rate-limits a CI fleet] → Server already degrades cleanly to embeddings-disabled when `EnsureAsync` throws. We surface the warning with cache-dir path so operators can pre-populate manually. Documented `--no-model-download` for hardened environments.

[**SHA drift**: HF maintainer re-uploads the file with a different hash, breaking pinned SHA verification on next download] → Pinned SHAs are in source. When verification fails, the warning text already says "SHA-256 mismatch" — operators can pin the new SHA and ship a patch release. Mitigation: review the model release feed before bumping the package.

[**Concurrent first starts** of `serve` and `index` against the same cache] → `EnsureAsync` is best-effort idempotent (atomic rename of `.tmp` to final), but two processes downloading concurrently both write to the same `.tmp` path. The second `File.Move` will throw because the source `.tmp` has been moved. Acceptable: the second process catches the IO exception, logs, retries cache-check; if the file landed it succeeds, otherwise it falls through to "embeddings disabled this session" exactly as today's offline failure does. Not adding a flock — over-engineering for an edge case.

[**280 MB download on a metered connection**] → README already documents the size. New flag `--no-model-download` lets the operator say "no" without losing `--no-embeddings`-then-pre-populate-the-cache options.

[**Breaking change to `ModelFile`**] → Internal API only. No public consumers. Searchable; one call site to update inside `ModelStore.EnsureAsync` itself.

[**Model file paths assume top-level cache layout forever**] → If a future model has *more* HF subdirectories, the same `(RemotePath, LocalName)` shape handles it. If a future model needs *multiple* files in subdirectories of the cache (e.g. a tokenizer with vocab bytes), `LocalName` can carry a relative path with a separator — the cache dir is just `Path.Join(modelDir, LocalName)` so it composes.

## Migration Plan

No migration required. Existing caches are still discovered (the cache layout is unchanged for the default-model files; only `ModelFile`'s shape changes, and that's compile-time-internal). Existing `--no-embeddings` users see no behaviour change. New users get a working first-run experience.

## Open Questions

- **Should the `serve` path ever block startup on a download** (e.g. if `--require-embeddings` is added)? Current answer: no — the indexer's non-embedding tools should always come up. If it's needed later, can be a follow-up flag.
- **Does the `index` one-shot path need a `--wait-for-embeddings` flag** so CI runs that produce embeddings don't exit before the channel drains? `index` already calls `embedService.StopAsync(...)` with a 60 s timeout, which drains the queue. Should be enough; revisit if CI runs hit the timeout.
