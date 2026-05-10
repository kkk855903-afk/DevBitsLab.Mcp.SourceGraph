## ADDED Requirements

### Requirement: Embedding cache management tools
The server SHALL expose four MCP tools that inspect and manage the embedding model cache: `embeddings_status`, `embeddings_pull`, `embeddings_remove`, and `embeddings_verify`. Each tool's response SHALL include typed `structuredContent` alongside the markdown prose and SHALL declare its `outputSchema` in `tools/list`. Built-in `🌿` brand-mark conventions apply to all four.

The mutating tools SHALL carry MCP-spec `annotations` so spec-aware clients can require explicit user confirmation before invocation:

| Tool                | `destructiveHint` | `idempotentHint` | `readOnlyHint` |
|---------------------|-------------------|------------------|-----------------|
| `embeddings_status` | false             | true             | true            |
| `embeddings_pull`   | false             | true             | false           |
| `embeddings_remove` | true              | true             | false           |
| `embeddings_verify` | false             | true             | true            |

Each tool's description SHALL include a `Use when:` line that describes the user-initiated nature of the operation (especially for `embeddings_pull` and `embeddings_remove`, which trigger network egress / disk deletion respectively).

#### Scenario: embeddings_status returns the cache report
- **WHEN** an MCP client invokes `embeddings_status` with no arguments
- **THEN** `result.structuredContent` includes `modelId`, `dimension`, `cacheDir`, an array of `files` each with `localName` / `present` / `sizeBytes` / `computedSha` / `pinnedSha` / `match`, and `freeDiskBytes`; the prose narrates the same data

#### Scenario: embeddings_pull on empty cache populates the cache
- **WHEN** an MCP client invokes `embeddings_pull` with no arguments and the active model's cache directory is empty
- **THEN** the server downloads the manifest files into the cache, the response's `structuredContent` matches the post-download `embeddings_status` snapshot, and every `files[*].present` is `true`

#### Scenario: embeddings_pull on warm cache is a no-op
- **WHEN** an MCP client invokes `embeddings_pull` against a populated cache
- **THEN** no HTTP request is issued, the response narrates "cache already populated", and the structured snapshot reflects the existing files unchanged

#### Scenario: embeddings_remove deletes the active model's cache
- **WHEN** an MCP client invokes `embeddings_remove` with no `modelId` argument
- **THEN** the active model's per-id directory under `models/` is deleted, `result.structuredContent.removedDirs` lists the deleted path, `freedBytes` reports the total bytes freed, and a subsequent `embeddings_status` call shows `files[*].present = false`

#### Scenario: embeddings_remove with all=true wipes every cached model
- **WHEN** an MCP client invokes `embeddings_remove(all = true)` against a cache containing two model directories
- **THEN** both directories are deleted, `removedDirs` lists both paths, and `freedBytes` reports the sum of both directories' sizes

#### Scenario: embeddings_remove rejects ambiguous combination
- **WHEN** an MCP client invokes `embeddings_remove(modelId = "jinaai/x", all = true)`
- **THEN** the tool returns an error response naming the conflict and disk is not touched

#### Scenario: embeddings_verify reports informational mode pre-pin
- **WHEN** an MCP client invokes `embeddings_verify` against a populated cache while the active model's manifest has no pinned SHAs
- **THEN** every file row in `structuredContent.files` has `pinnedSha = null` and `match = null`, the prose includes a "no pinned SHA — informational only" note, and the tool's response is not an error

#### Scenario: embeddings_verify reports mismatch post-pin
- **WHEN** an MCP client invokes `embeddings_verify` against a populated cache where at least one cached file's computed SHA does not match its manifest pinned SHA
- **THEN** the affected file rows have `match = false`, the prose names the failing files, and the response is flagged with `isError = true`
