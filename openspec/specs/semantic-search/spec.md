# Semantic Search

## Purpose

Provide intent-based code retrieval as a complement to FTS5 trigram name
matching: a vector index of code-aware embeddings answers questions phrased
by intent ("retry on transient errors", "rate-limiting code", "auth flow")
that the existing `search_symbols` tool cannot. The pipeline runs entirely
in-process via ONNX Runtime + `sqlite-vec`, with model bootstrap and
graceful degradation when the model or extension is unavailable.

## Requirements

### Requirement: Per-symbol code embeddings
The system SHALL compute and persist a code-aware embedding for every indexed symbol whose synthesized text passes the skip rules, gated on a SHA-256 hash of that text so reindexing only re-embeds changed symbols.

#### Scenario: Cold index produces embeddings
- **WHEN** a fresh `sourcegraph-mcp index <sln>` runs against a solution and the model is available
- **THEN** every non-skipped symbol has a row in `symbol_embeddings` with a 768-dim vector and a `content_hash` that matches the SHA-256 of its synthesized text

#### Scenario: Comment-only edit doesn't re-embed
- **WHEN** a method's body changes only in trivial whitespace or comments such that the synthesized text hashes to the same value
- **THEN** no embedding write occurs for that symbol on the live reindex

### Requirement: semantic_search MCP tool
The server SHALL expose a `semantic_search(query, k = 20, kind?)` tool that returns symbols whose embeddings are nearest to the query embedding by cosine distance.

#### Scenario: Intent query
- **WHEN** the agent invokes `semantic_search(query = "retry on transient errors", k = 10)` against a graph where one method's synthesized text is `"… <summary>Retries the request when …</summary> …"`
- **THEN** that method appears in the top-k with a `score` between 0.0 (no relation) and 1.0 (identical)

### Requirement: Graceful disable when embeddings unavailable
The semantic-search subsystem SHALL degrade safely when the model isn't cached, the `sqlite-vec` extension isn't loadable, or `--no-embeddings` was passed.

#### Scenario: Disable via flag
- **WHEN** the server is started with `--no-embeddings`
- **THEN** the embedding pipeline never runs, no `symbol_embeddings` rows are written, and `semantic_search` responds with the "semantic search disabled" hint while every other tool works as before

#### Scenario: Model not yet downloaded
- **WHEN** the server starts without an internet connection and the cached model is missing
- **THEN** the server logs a one-time warning, the pipeline is disabled, and the rest of the index proceeds normally

### Requirement: Model identity tracked per row
Each embedding row SHALL carry a `model_version` so that swapping the embedding model invalidates the existing embeddings rather than mixing dimensions.

#### Scenario: Model upgrade
- **WHEN** the server starts with a different `--model` than the rows on disk
- **THEN** rows whose `model_version` doesn't match the active model are treated as missing; affected symbols re-embed on the next index pass
