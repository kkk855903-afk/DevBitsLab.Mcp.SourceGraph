## Why

The auto-download wire-up landed (`wire-model-autodownload`) but operators still have no programmatic way to inspect or manage the embedding model cache. Two specific gaps:
- The new `--no-model-download` warning tells an air-gapped operator to "pre-populate the cache" but doesn't surface the cache path through any tool — they have to read source. A `status` verb fixes that in 2 seconds.
- After switching `--model`, the old model's files sit on disk indefinitely. No `remove` verb means manual `rm -rf` against a path the user has to derive themselves.

Adding configuration verbs lets operators inspect, pull, remove, and verify the cache from both the CLI (operator-facing) and via MCP tools (agent-assisted). Both surfaces share one service so they can never drift.

## What Changes

- **New CLI subcommand group** `sourcegraph-mcp embeddings <verb>` mirroring the existing `scopes` / `plugins` / `vocabulary` shape:
  - `embeddings status [--model <id>]` — print cache path, active model id + dimension, per-file (presence, size, computed SHA, pinned-SHA match if manifest provides one), free disk on the cache volume.
  - `embeddings pull [--model <id>]` — explicit synchronous download. Idempotent with the auto-download cache-hit path.
  - `embeddings remove [--model <id>] [--all]` — clear cache for one model. Defaults to the active model. `--all` wipes every cached model. `--model X --all` is rejected (combination is ambiguous).
  - `embeddings verify [--model <id>]` — recompute SHAs, compare against manifest. Pre-pin (today): exits 0, prints "no pinned SHA — informational only" beside each computed hash. Post-pin: exits 2 on any mismatch.
- **Four new MCP tools** that emit typed `structuredContent` and call the same service:
  - `embeddings_status` — read-only.
  - `embeddings_pull` — mutating, MCP `idempotentHint: true`, `destructiveHint: false`. `Use when:` line marks it user-initiated.
  - `embeddings_remove` — mutating, `destructiveHint: true`, `idempotentHint: true`. `Use when:` marks it user-explicit (never as a debug side-effect).
  - `embeddings_verify` — read-only (recomputes SHAs but doesn't mutate).
- **New `EmbeddingsManager`** service in the Server project: shared call site for the CLI router and MCP tools so the two surfaces stay in lock-step.
- **Three small additions to `ModelStore`**: `RemoveAsync(modelId)`, `RemoveAllAsync()`, `ComputeShaAsync(modelId, fileName)`.
- **Brand mark + "Use when:" triggers** apply to all four new tools per the existing convention.

Out of scope (worth flagging as follow-ups):
- Mid-session model switch without server restart (orthogonal — requires invalidating the live ONNX session).
- `embeddings_pull` progress streamed via MCP `notifications/progress` — the auto-download path already logs to stderr; if we later wire `progressToken` end-to-end, this tool follows the same pattern.
- Bundling model bytes as a separate NuGet (different distribution channel; out of scope here).

## Capabilities

### New Capabilities
*(none — these verbs operate on the existing semantic-search / cache surface; they don't introduce a separate capability.)*

### Modified Capabilities
- `cli`: adds the `embeddings` subcommand group requirement.
- `mcp-tools`: adds a requirement covering the four new tools, including the MCP `annotations` (destructiveHint / idempotentHint) on the mutating ones.

## Impact

- **Affected code**: `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/EmbeddingsTools.cs` (new), `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/EmbeddingsCli.cs` (new), `src/DevBitsLab.Mcp.SourceGraph.Server/EmbeddingsManager.cs` (new), `src/DevBitsLab.Mcp.SourceGraph.Embeddings/ModelStore.cs` (three new methods), `src/DevBitsLab.Mcp.SourceGraph.Server/Program.cs` (subcommand dispatch entry), `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/CommandLine.cs` (`--all` flag).
- **Affected behaviour**: New surface only — no existing CLI verb or MCP tool changes shape. Operators get four new verbs on each surface; the auto-download flow is unchanged.
- **Tests**: unit coverage for `EmbeddingsManager` and the CLI parser; integration coverage for each new MCP tool through the existing harness shape (`PayloadToolingTests` style). Existing test suites keep passing without modification.
- **Dependencies**: no new packages.
- **Docs**: README CLI section gets four new rows in the verbs table; "Wiring it into an MCP client" section gains a brief note that semantic-search-related cache management is now a tool surface; spec files updated as listed above.
