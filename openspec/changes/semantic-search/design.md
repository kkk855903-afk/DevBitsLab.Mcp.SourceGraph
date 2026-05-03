## Context

Adding semantic retrieval has three independent design problems: (1) pick a model + library that runs in-process on a laptop, (2) keep the index fresh without storming the CPU during live editing, (3) integrate without breaking users who can't or don't want to pay the bootstrap cost. Recent (2026) library maturity makes all three solvable.

## Goals / Non-Goals

**Goals:**
- 100 % in-process; no sidecar daemon, no cloud, no API key.
- Sub-second top-k for solutions up to 500 k symbols on a developer laptop.
- Live-update friendly: editing one file re-embeds at most that file's symbols, gated on synthesized-text hash.
- Optional: a user can `--no-embeddings` and the rest of the system works unchanged.

**Non-Goals:**
- Replacing `search_symbols`. Vector search is *additional*; FTS stays for name fragments. Agents pick.
- Indexing whole-file or whole-class chunks. Per-symbol with synthesized text is the v1 unit.
- HNSW or IVF indexing in v1. Brute-force in `sqlite-vec` is fast enough at our scale; HNSW comes if profiling demands it.
- Cross-encoder re-ranking. Stretch goal, separate change.

## Decisions

**1. Library: ONNX Runtime + Microsoft.Extensions.AI abstraction.**
`Microsoft.ML.OnnxRuntime` (NuGet) is mature, ~25 MB native, runs on every host. We wrap it behind `Microsoft.Extensions.AI`'s `IEmbeddingGenerator<string, Embedding<float>>` so future model swaps don't touch the call sites.

**2. Model: `jinaai/jina-embeddings-v2-base-code` (INT8 ONNX).**
161 M params, 768-dim, ~280 MB on disk, 8192-token context (fits whole methods/classes). Code-trained, MIT-comparable license. Best size/quality balance per the 2026 benchmarks (CodeRankEmbed and Nomic-Embed-Code rank slightly higher but ~2× the bytes).

**3. Storage: `sqlite-vec` (`vec0` virtual table) loaded into the existing graph DB.**
`SqliteConnection.LoadExtension("vec0")` — same connection, same WAL, same backups. Brute-force KNN with SIMD; sub-30 ms at our cardinalities. New `symbol_embeddings(symbol_id INTEGER PRIMARY KEY, content_hash BLOB, embedding FLOAT[768], model_version TEXT)` virtual table.

**4. What to embed.**
Per symbol, we synthesize:
```
{kind} {fqn}
{xml_summary}
{signature}
{first 40 lines of body, if a method/property}
```
Hashed via SHA-256. Re-embed only when hash differs from `content_hash`. Skipped for trivial accessors, generated code (`*.g.cs`, `*.Designer.cs`), tests of trivial accessors.

**5. Model lifecycle.**
First run downloads from Hugging Face to `~/.cache/devbitslab.sourcegraph/models/<model-id>/` with manifest SHA verification. Subsequent runs use the cache. CLI `--model <id>` overrides; `--no-embeddings` skips the pipeline entirely. The repo never stores model bytes — checking 280 MB into git would be wrong.

**6. Indexing pipeline.**
A single `EmbeddingsHostedService` consumes a `Channel<EmbedRequest>` populated from pass 2. Batches 16-32 inputs per ONNX `Run` call. One worker, never parallel — keeps CPU bounded so live editing isn't laggy. Failures (model load error, runtime error) drop the request and log; never block the rest of the indexer.

## Risks / Trade-offs

- **Cold startup hit (~280 MB download).** Documented as expected on first run; in CI / stateless sandboxes the user can `--no-embeddings`.
- **CPU usage during big initial embed.** ~15-60 s on a typical solution, bounded to one worker, low-priority by intent. Documented.
- **Model drift / version pinning.** `model_version` column lets us invalidate embeddings on model upgrade. Embedding rebuild is idempotent.
- **`sqlite-vec` is single-author-maintained.** If it goes stale we can swap to LanceDB or a HNSW SQLite extension; the abstraction lives in `IEmbeddingsStore`.
- **Index size on huge repos.** ~3 KB per symbol = ~1.5 GB at 500 k symbols. Disk-acceptable; may want to skip lower-value symbols (private, generated) to halve.
