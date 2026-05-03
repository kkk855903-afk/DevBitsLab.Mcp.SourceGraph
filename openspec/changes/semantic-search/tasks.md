## 1. New project: Embeddings

- [ ] 1.1 Create `src/DevBitsLab.Mcp.SourceGraph.Embeddings/`. References `Microsoft.ML.OnnxRuntime`, `Microsoft.Extensions.AI.Abstractions`, and (via `Microsoft.Data.Sqlite`) the in-process SQLite extension API.
- [ ] 1.2 `IEmbeddingGenerator<string, Embedding<float>>` adapter `JinaCodeEmbeddingGenerator` with batch encoding (16-32 inputs per call).
- [ ] 1.3 Tokenizer (`FastBertTokenizer` or equivalent) calibrated to the model's vocab.

## 2. Model bootstrap

- [ ] 2.1 `ModelStore` resolves cache dir (`~/.cache/devbitslab.sourcegraph/models/<id>/` on Unix, `%LOCALAPPDATA%/devbitslab.sourcegraph/models/<id>/` on Windows).
- [ ] 2.2 First-run downloader: streams from Hugging Face, verifies manifest SHA-256, writes atomically.
- [ ] 2.3 CLI flags: `--model <id>` (override), `--no-embeddings` (disable the whole pipeline).

## 3. Storage

- [ ] 3.1 Load `sqlite-vec` (`vec0`) extension on connection open. Detect missing extension (graceful warning, disable semantic-search tool).
- [ ] 3.2 New table `symbol_embeddings` (vec0 virtual): `symbol_id INTEGER PRIMARY KEY, embedding FLOAT[768]` plus a sibling `embedding_meta(symbol_id, content_hash BLOB, model_version TEXT)` for hash gating.
- [ ] 3.3 `IEmbeddingsStore.UpsertAsync(symbolId, hash, embedding, modelVersion)`.
- [ ] 3.4 `IEmbeddingsStore.SearchAsync(queryEmbedding, k, kindFilter?)` — returns `IReadOnlyList<EmbeddingHit(symbolId, score)>`.

## 4. Indexer integration

- [ ] 4.1 `EmbeddingsHostedService`: reads a `Channel<EmbedRequest>`, batches, calls the generator, persists.
- [ ] 4.2 Pass 2 enqueues an `EmbedRequest` for every (re)indexed symbol with the synthesized text + hash.
- [ ] 4.3 Skip rules: trivial accessors, `*.g.cs`, `*.Designer.cs`, test trivia.
- [ ] 4.4 On `--no-embeddings`, the channel is replaced with a no-op writer.

## 5. MCP tool

- [ ] 5.1 New tool `semantic_search(query, k = 20, kind?)`.
- [ ] 5.2 Encodes the query via the same generator, runs `IEmbeddingsStore.SearchAsync`, joins to `symbols` for hit metadata, returns markdown with `score` (0-1) per row.
- [ ] 5.3 If embeddings unavailable (extension missing, model not downloaded, `--no-embeddings`), the tool returns a concise "semantic search disabled — install the model or remove --no-embeddings" hint.

## 6. Tests

- [ ] 6.1 Unit test the synthesized-text builder over fixture symbols.
- [ ] 6.2 Integration test: index `tests/fixtures/Sample.sln` with a mock embedder (deterministic vectors); call `semantic_search("calculator")`; expect `Sample.Domain.Calculator` in top results.
- [ ] 6.3 Hash-gating test: change a method's body comment only; expect no re-embed.
- [ ] 6.4 Model-missing test: serve without the cache; assert `semantic_search` returns the disabled-message and other tools work.

## 7. Update specs

- [ ] 7.1 Sync delta specs into `openspec/specs/{indexing, storage, mcp-tools, cli}/spec.md` and create `openspec/specs/semantic-search/spec.md` on archive.
