## Why

`search_symbols` today is FTS5 trigram over names, FQNs, and signatures (plus XML doc once `enrich-symbol-model` lands). It's great when the agent already knows a fragment of the name and wants to fuzz over typos, but it can't handle questions like *"find code that does retry logic"*, *"how does this codebase handle authentication"*, or *"show me the rate-limiting code"* — questions phrased by intent rather than identifier. Coding agents ask these constantly. The fix is well-understood: a vector index of code-aware embeddings, queried alongside the FTS index.

The 2026 ecosystem makes this practical to ship in-process: ONNX Runtime + a code-trained embedding model under 300 MB run on every developer's laptop in single-digit ms per chunk; `sqlite-vec` plugs straight into our existing `Microsoft.Data.Sqlite` connection.

## What Changes

- New capability `semantic-search`.
- New columns / table: `symbol_embeddings(symbol_id, content_hash, embedding BLOB, model_version)` backed by the `sqlite-vec` extension via `LoadExtension`.
- `Embeddings` project / namespace with `IEmbeddingGenerator` (Microsoft.Extensions.AI's interface) and a `JinaCodeEmbeddingGenerator` ONNX adapter using `jina-embeddings-v2-base-code` (768-dim, ~280 MB INT8-quantized).
- Indexer queues per-symbol embedding work after pass 2 completes; runs in a single background `Task` to avoid CPU storms.
- New MCP tool `semantic_search(query, k = 20, kind?)` that runs a top-k vector query and returns symbol hits with a similarity score.
- Bootstrap: on first run, if the model isn't cached at `~/.cache/devbitslab.sourcegraph/models/`, download it (with manifest SHA verification) before serving queries. Documented and overridable via `--model` CLI flag.

## Capabilities

### New Capabilities

- `semantic-search`: vector-search tool surface, embedding pipeline, and on-disk format.

### Modified Capabilities

- `indexing`: pass 3 (new) computes embeddings for changed symbols and writes them to the vector store.
- `storage`: gains a `sqlite-vec` virtual table loaded as an extension, plus the embedding cache table.
- `mcp-tools`: gains `semantic_search` tool.
- `cli`: gains `--model <id>` and `--no-embeddings` flags.

## Impact

- New project `DevBitsLab.Mcp.SourceGraph.Embeddings` with ONNX Runtime + tokenizer dependency.
- Local cache directory ~280 MB the first time the user runs `serve`.
- First cold-index gains a sequential embedding pass that takes ~15-60 s on a typical solution; subsequent reindexes only re-embed changed symbols (hash-gated).
- Storage: one extra blob per symbol, ~3 KB on disk for 768-dim INT8. ~1.5 GB for a 500k-symbol monorepo. Acceptable.
- Failure modes documented: model missing → semantic_search returns "embeddings unavailable" but other tools work; sqlite-vec extension missing → graceful disable with a one-time warning.
