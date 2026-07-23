## Context

The previous change (`wire-model-autodownload`) closed the embeddings-don't-work-out-of-the-box bug, but it left two operator-facing rough edges. First, the new `--no-model-download` warning instructs an air-gapped user to "pre-populate the cache" but doesn't tell them the path — they have to read source. Second, switching `--model <id>` orphans the previous model's files on disk forever (cache key includes model id, so the old directory just sits there).

The codebase already has the right pattern for adding subcommand groups: `scopes`, `plugins`, and `vocabulary` each have their own `<Group>Cli.cs` static helper invoked by a switch arm in `Program.RunSubcommandAsync`. MCP tools follow a parallel pattern — one file under `Server/Tools/` per related set, with `[McpServerTool]` attributes that the SDK auto-discovers. Both surfaces typically wrap a service so the logic lives in one place.

Constraints to hold:
- The MCP transport speaks JSON-RPC over stdout — keep CLI prose to stdout (people read it) and any logging that the *server* would emit on stderr (the transport's contract). One-shot CLI verbs run before the MCP server starts, so they can use stdout freely.
- Cache resolution lives in `ModelStore` (per-model directory under `_baseDir`). Don't duplicate the path-resolution logic; route every disk operation through `ModelStore`.
- The default model id is the configured active model — `EmbeddingModelInfo` from DI in the long-running paths, or `DefaultEmbeddingModel.Info` when no `--model` override was given. Only the embeddings CLI verbs should resolve this lazily (since the CLI subcommand router doesn't go through Hosting DI).

## Goals / Non-Goals

**Goals:**
- An operator who hits the `--no-model-download` warning can run `sourcegraph-mcp embeddings status` and learn within 2 seconds where to drop pre-fetched files.
- Switching `--model` and freeing the old model's disk usage is a one-liner: `sourcegraph-mcp embeddings remove --model jinaai/old-model`.
- The same operations are reachable from inside a coding-agent session via MCP tools, with destructive operations carrying explicit MCP `annotations` so a permission-aware host can prompt before invoking them.
- All four CLI verbs and all four MCP tools share one `EmbeddingsManager` so adding a new operation later (e.g. `embeddings list-cached` to enumerate every directory under `_baseDir`) is a single-place change.

**Non-Goals:**
- Mid-session model swap without server restart. Decoupling the live ONNX session from the disk cache is a separate concern; today, switching `--model` requires a restart.
- Bundling the model bytes in a NuGet for offline distribution. Different channel.
- Versioned cache directories (e.g. `model.onnx.v2`). Single-id-flat-dir is enough; manifest changes are tracked by `model_version` in the database.
- Streaming download progress through MCP `notifications/progress`. The auto-download path emits stderr lines today; until we wire `progressToken` end-to-end on the startup path, `embeddings_pull` follows the same source of truth (stderr).

## Decisions

### Decision 1: Shared `EmbeddingsManager` service, not a duplicated implementation per surface

**What**: Add `EmbeddingsManager` in the Server project. It depends on `ModelStore` and `EmbeddingModelInfo` and exposes: `GetStatusAsync(modelId)`, `PullAsync(modelId)`, `RemoveAsync(modelId)`, `RemoveAllAsync()`, `VerifyAsync(modelId)`. Both `EmbeddingsTools` (MCP) and `EmbeddingsCli` (CLI) call it.

**Why this over alternatives**:
- *Two parallel implementations (one per surface)*: invariably drift; a bug fixed in CLI doesn't reach MCP and vice versa. Rejected.
- *Inline the logic into the existing `ModelStore`*: would couple an HTTP-and-disk library to "build a status report" formatting concerns. `ModelStore` should stay the disk + HTTP primitive layer.

`EmbeddingsManager` is registered as a singleton in DI for the long-running `serve` path (so the MCP tools resolve it) and constructed inline in the one-shot CLI verb router (no DI scope there).

### Decision 2: `embeddings remove` defaults to the active model — `--all` is opt-in

**What**: Running `embeddings remove` with no flags clears the cache for the currently-configured model only. `--all` is required to wipe every directory under `_baseDir/models/`. The combination `--model X --all` is rejected at parse time.

**Why**: `--all` is a footgun if it's the default — wiping a 280 MB cache that takes minutes to refetch is the kind of thing that should be an explicit opt-in. Defaulting to the active model gives users the natural shape: "remove the model I'm using right now, e.g. before re-downloading after switching `--model`."

**Alternative considered**: `embeddings remove --model X` required, no default. Rejected — needlessly verbose for the common case.

### Decision 3: MCP tool annotations carry the safety hints; the host handles permissioning

**What**: The four MCP tools carry MCP-spec `annotations`:

| Tool | `destructiveHint` | `idempotentHint` | `readOnlyHint` |
|---|---|---|---|
| `embeddings_status` | false | true | true |
| `embeddings_pull` | false | true | false |
| `embeddings_remove` | true | true | false |
| `embeddings_verify` | false | true | true |

The hosting client (Claude Code) prompts the user before invoking any tool by default; spec-aware clients additionally use `destructiveHint: true` to require explicit confirmation. The `Use when:` triggers in tool descriptions reinforce this in prose.

**Why**: We don't roll our own confirmation flow inside the server — the MCP host is the right place. We just have to label tools accurately so the host knows what shape of confirmation to require.

**Alternative considered**: a server-side "are you sure?" dialog (e.g. `embeddings_remove` returns a token, then a second `embeddings_confirm_remove(token)` call actually does it). Rejected — duplicates the host's job, makes the tool surface clunky, breaks the structured-output convention.

### Decision 4: Scaffold `verify` now, even though manifest SHAs are still null

**What**: Implement the full `verify` semantics today. Pre-pin behaviour (current state — manifest SHAs are null): for every cached file, print `localName`, `computedSha`, and `(no pinned SHA — informational only)`. CLI exits 0; MCP tool `match` field is `null` rather than `true`/`false`. Post-pin behaviour (after `wire-model-autodownload` task 7.1 lands): the same code paths populate `pinnedSha`, compute `match = computed == pinned`, the CLI exits 2 on any false, the MCP tool's structured output reflects the boolean.

**Why**: The same code surface answers both states. Building it now means task 7.1 is the only future change needed to enable strict verification — no second wave of `verify` plumbing.

### Decision 5: `EmbeddingsStatus` shape is the canonical structured payload for status / pull / verify

**What**: All three of these verbs return the same shape:
```jsonc
{
  "modelId": "jinaai/jina-embeddings-v2-base-code",
  "dimension": 768,
  "cacheDir": "/Users/x/.cache/devbitslab.sourcegraph/models/jinaai__jina-embeddings-v2-base-code",
  "files": [
    {
      "localName": "model.onnx",
      "remotePath": "onnx/model.onnx",
      "present": true,
      "sizeBytes": 480000000,
      "computedSha": "abc123…",
      "pinnedSha": null,
      "match": null
    },
    { /* tokenizer.json */ }
  ],
  "freeDiskBytes": 90000000000
}
```

`pull` returns this snapshot *after* the download has settled (success or graceful failure). `remove` returns a different shape: `{ modelId | "*", removedDirs: [...], freedBytes: N }`.

**Why**: Reusing one shape across status/pull/verify means callers can chain them — "pull, then verify the result by reading `result.structuredContent.files[*].match`" — without re-parsing. CLI prose is rendered from this same shape, so prose and structured output stay in lock-step.

### Decision 6: CLI verbs are wired through the existing `Subcommand` switch in `Program.cs`

**What**: Add an `"embeddings"` arm to the existing dispatch switch in `Program.cs:38-49`. Inside, dispatch to `EmbeddingsCli.RunSubcommandAsync(cli)` which switches on the first positional arg (`status` / `pull` / `remove` / `verify`).

**Why**: Mirrors the established pattern (`ScopesCli.RunSubcommandAsync`, `PluginsCli.RunSubcommandAsync`, `VocabularyCli.RunSubcommandAsync`). One-line addition to `Program.cs`; new file `EmbeddingsCli.cs` in the same `Server/Cli/` directory.

## Risks / Trade-offs

[**MCP tool misuse — agent calls `embeddings_remove` mid-debug**] → MCP `destructiveHint: true` + the `Use when:` prose ("user explicitly asked to free disk / swap models — never as a side-effect of debugging") combined with Claude Code's per-tool confirmation prompt is the layered defense. We don't add a server-side confirmation token because that doubles the host's job and breaks the structured-output convention.

[**`embeddings remove` while the live server holds an open ONNX session**] → Deleting the file backing an open `InferenceSession` is platform-dependent: on Linux/macOS the unlinked file lives until the file descriptor closes (no immediate breakage); on Windows it might fail. The running server keeps inferring fine until restart. Documented in the tool description; not a blocker.

[**`--model X --all` ambiguity**] → Reject at parse time with a clear message. No silent winner.

[**Concurrent `embeddings pull` from CLI while the server has an in-flight auto-download for the same model**] → `EnsureAsync`'s atomic-rename keeps both writers safe-ish — they both write to `.tmp`, and one rename will succeed while the other catches an IO exception. Best-effort behaviour today; documented in design but not protected by a flock.

[**`free disk` calculation crosses platforms**] → `DriveInfo.GetDrives()` works on every supported runtime. Use the drive containing `_baseDir`. Edge: `_baseDir` doesn't exist yet → fall back to its parent. Edge: parent doesn't exist either → emit `freeDiskBytes: null` rather than throwing.

[**Manifest SHA pinning still hasn't landed**] → `verify` falls back to the informational-only mode automatically. No second wave of code changes needed when the SHAs are pinned later.

## Migration Plan

No migration required. The new verbs are purely additive — no existing CLI verb, MCP tool, or behaviour changes shape. Existing operators keep their workflows; the new surface is opt-in.

If future work adds an `init-scopes`-equivalent for embeddings (e.g. `embeddings init` to scaffold a custom model config), it should follow the same pattern.

## Open Questions

- **Should `embeddings remove --all` skip the active model?** Currently it wipes everything including the active model. Argument for skipping: prevents an "I just wanted to remove the *other* models" footgun. Argument against: explicit `--all` already means "yes, all of them." Defaulting to "all means all" feels right; we could add `--except-active` later if it's actually wanted.
- **Should `embeddings status` show ALL cached models, not just the active one?** Today's design is single-model-at-a-time (`--model <id>` to override). A more powerful "list every cached model" view (`embeddings list` perhaps) is a future addition; out of scope here.
- **Should the `--model` flag reuse the existing top-level CLI flag, or be subcommand-local?** Reusing the top-level flag keeps consistency with `serve`/`index`. Local would be clearer ("this `--model` doesn't change which one auto-downloads on serve"). Going with reuse for now — same `--model` across the whole CLI.
