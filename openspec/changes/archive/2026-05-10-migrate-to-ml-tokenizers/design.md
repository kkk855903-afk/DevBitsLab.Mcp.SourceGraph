## Context

The 2026-05-03 semantic-search change picked `FastBertTokenizer` because it was small, fast, and shipped a clean batch-encode-into-preallocated-buffers API that paired well with the rented arrays in `JinaCodeEmbeddingGenerator.EncodeBatch`. The choice was made before the default model id was finalised. Once `jinaai/jina-embeddings-v2-base-code` was selected — based on a quick search for "code-aware embeddings under 1 GB" — nobody walked the round-trip end-to-end against the real `tokenizer.json`, so the BERT-vs-RoBERTa mismatch went unnoticed.

The runtime evidence is unambiguous:
```
Embedding model load failed; semantic search disabled
System.Text.Json.JsonException: JSON deserialization for type
'FastBertTokenizer.TokenizerJson+PostProcessorSection' was missing
required properties including: 'special_tokens'.
   at FastBertTokenizer.BertTokenizer.LoadTokenizerJsonAsync(...)
   at JinaCodeEmbeddingGenerator.EnsureInitialised() :line 210
```

`Microsoft.ML.Tokenizers` (latest stable: 2.0.0) covers BPE / RoBERTa / WordPiece / Tiktoken from one library and is the obvious replacement. It has a few API differences from FastBertTokenizer that require minor surgery to the encode path, but no architectural change to `JinaCodeEmbeddingGenerator`'s public surface (`ICodeEmbeddingGenerator.EmbedAsync`, `IsAvailable`, `Model`).

Constraints to hold while migrating:
- No DB migration. `model_version` stays the same string so the existing `symbol_embeddings` rows remain valid across the upgrade.
- ONNX input shapes and dtypes must match what the loaded model expects: `input_ids` and `attention_mask`, both `int64`, both `(batch, seq)`. The loaded `model.onnx` is a 641 MB float32 graph from the upstream Jina v2 base code repo — its inputs are fixed.
- Don't widen the public API surface. This is a library swap behind an existing seam.

## Goals / Non-Goals

**Goals:**
- The default model `jinaai/jina-embeddings-v2-base-code` loads, embeds a string, and produces a 768-dim normalised vector.
- BERT-tokenized models (e.g. `BAAI/bge-base-en-v1.5`) continue to load and embed via the WordPiece branch, so a future `--model <id>` override into a BERT model still works.
- The change is reviewable as a single library swap: same public methods, same return shapes, same behaviour for the consumer.
- An automated unit test exercises tokenizer load against the real Jina `tokenizer.json` fixture so the bug we're fixing has a regression guard.

**Non-Goals:**
- Mid-session tokenizer hot-swap (tokenizer is loaded once at first `EmbedAsync` call; restart to switch).
- Embedding output bit-for-bit reproducibility against HuggingFace `transformers` (not previously promised; we make best effort and document any small floating-point drift caused by `int32 → int64` conversion ordering, attention-mask float promotion, etc., but don't gate on it).
- Removing `Microsoft.ML.OnnxRuntime` (still in use).
- Adding tokenizer types beyond BERT-WordPiece and RoBERTa-BPE in this change. Tiktoken / SentencePiece dispatch could be a follow-up if a future model needs it.

## Decisions

### Decision 1: Tokenizer-type detection by parsing `model.type` from `tokenizer.json`

**What**: Before constructing a concrete tokenizer, read the cached `tokenizer.json` file once with `System.Text.Json`, extract `model.type`, and dispatch:
- `"BPE"` → `BpeTokenizer.Create(...)` (Jina v2 base code path, RoBERTa-style)
- `"WordPiece"` → `WordPieceTokenizer.Create(...)` (classic BERT path)
- anything else → log a clear warning ("unsupported tokenizer.model.type: X — embeddings disabled") and flip `_available = false`. Same graceful-disable shape we already follow for missing files.

**Why this over alternatives**:
- *Try-each-tokenizer-type in sequence*: gives confusing exception traces when the real cause is "this is a sentencepiece model we don't support." Rejected.
- *Hard-code the dispatch by model id*: tightly couples the generator to the manifest; breaks the moment a user passes `--model <id>`. Rejected.
- *Wait for `Microsoft.ML.Tokenizers` to add a `Tokenizer.FromHuggingFaceJson(path)` factory*: doesn't exist in 2.0 stable per quick exploration; we'd be blocked indefinitely. The custom dispatch is ~15 lines.

The dispatch cost is one cheap JSON parse at startup (≪ 1 ms vs. the ~30 s ONNX session load it precedes).

### Decision 2: Per-string encode + manual padding/mask construction

**What**: Replace the FastBertTokenizer batch-encode-into-preallocated-buffers call with a per-string loop:
```csharp
for each input:
    var ids32 = tokenizer.EncodeToIds(text, settings: new EncodeSettings { MaxTokenCount = maxTokens });
    // Pad ids32 to maxTokens with PadId; promote to int64; build mask (1 for real, 0 for pad) in parallel.
```
The output `int64[batch, maxTokens]` arrays go into the existing `DenseTensor<long>` wiring unchanged.

**Why**: `Microsoft.ML.Tokenizers` 2.0 doesn't expose a batch encode that writes into a caller-supplied buffer. Building one ourselves would mean wrapping the per-string call anyway. For our typical batch (32 strings, ≤512 tokens each), the per-string overhead is negligible against the ONNX session run that follows. If profiling later flags this as hot, parallelise with `Parallel.For` — the C# tokenizer API is documented as thread-safe for concurrent encode.

The manual attention-mask construction is a 2-line inner loop and matches the shape FastBertTokenizer was producing. Cheap.

### Decision 3: Keep the class name `JinaCodeEmbeddingGenerator`

**What**: Don't rename the class. Internally it now handles BERT and RoBERTa; the name is no longer technically accurate, but the diff stays focused on the library swap.

**Why**: A rename ripples through `Program.cs`, the integration tests, and any plugin code that references the type. None of that adds value to this change. A separate rename PR (or no rename at all — the class is `internal` to the `Embeddings` project's `[InternalsVisibleTo]` set) is the right shape if the name becomes a maintenance issue.

The XML doc comment gets updated to drop the Jina-specific framing.

### Decision 4: Spike before committing to the design

**What**: First implementation task is a 30-line throwaway program that:
1. Loads `~/.cache/devbitslab.sourcegraph/models/jinaai_jina-embeddings-v2-base-code/tokenizer.json` via the chosen `BpeTokenizer.Create(...)` API.
2. Encodes `"Hello world"`.
3. Prints the first 10 token ids.
4. Compares against the expected output from running the same input through HuggingFace transformers (we have a fixture script in scratch — Python `tokenizers.AutoTokenizer.from_pretrained("jinaai/jina-embeddings-v2-base-code").encode("Hello world").ids[:10]`).

If the spike doesn't produce matching ids, **stop**. Pause and re-evaluate before continuing. Two likely failure modes worth pre-naming:
- `BpeTokenizer.Create(...)` API requires a different argument shape than expected (e.g. wants `vocab.json` + `merges.txt` separately rather than the bundled `tokenizer.json`). Fallback: download those two files from the same HF repo (they're alongside `tokenizer.json` in upstream) and add them to the manifest.
- Special-token handling differs (the RoBERTa post-processor adds `<s>` ... `</s>` rather than BERT's `[CLS]` ... `[SEP]`). Fix: pass the right `addSpecialTokens: true` arg or use `RobertaTokenizer.Create` / `EnglishRobertaTokenizer.Create` if exposed.

**Why**: The exploration round flagged "`Microsoft.ML.Tokenizers` 2.0 lacks a single `Tokenizer.FromJson(path)` factory" as uncertain. Building the full rewrite on an unverified premise wastes time. A 30-minute spike de-risks the rewrite by hours.

### Decision 5: Test against a committed `tokenizer.json` fixture, not the live cache

**What**: Commit a copy of the actual Jina v2 `tokenizer.json` (2.5 MB, license-permissive — verify before committing) under `tests/fixtures/embeddings/jina-tokenizer.json`. The new unit test loads that file, encodes a fixed string, and asserts on the resulting token ids. The test does **not** touch the live cache or hit huggingface.co; CI environments without network still run it.

**Why**:
- A test that depends on the live cache is environment-dependent and flaky in CI.
- A 2.5 MB fixture is small enough to commit; it's the same file every user downloads anyway.
- The fixture also serves as the canonical "this is what we tested against" record — when the upstream model swaps to a v3 with a new tokenizer, the diff in the fixture file is the visible signal that we need to re-validate.

**Caveat**: licensing. Jina v2's tokenizer is part of a model release with their own license terms. If commit-license isn't permissive, alternative is a fixture-fetch step in the test setup that downloads the file once into `tests/fixtures/.cache/` (gitignored) on first run.

### Decision 6: Drop FastBertTokenizer entirely; no fallback

**What**: Remove the FastBertTokenizer NuGet reference. The new code handles BERT-WordPiece via `WordPieceTokenizer.Create(...)`, so we don't need the old library for anything.

**Why**: Carrying both libraries doubles the dep footprint and creates two maintenance surfaces. Microsoft.ML.Tokenizers covers the FastBertTokenizer use case at parity. If a perf regression appears, that's a separate decision; default to the cleaner state.

## Risks / Trade-offs

[**Loading API uncertainty** — `Microsoft.ML.Tokenizers` 2.0 exposes `BpeTokenizer.Create(vocabStream, mergesStream)` per docs; whether it can ingest a single `tokenizer.json` is unconfirmed.] → Spike (Decision 4) validates this before we commit. Fallback path: parse `tokenizer.json` ourselves, extract the `model.vocab` and `model.merges` arrays, write them to temp files, hand those to `BpeTokenizer.Create`. Adds ~20 lines of one-time setup; not architecturally hairy.

[**Performance regression** — per-string encode replaces FastBertTokenizer's batch-into-preallocated-buffers call.] → For our batch sizes (32 × ≤512 tokens), tokenisation is O(milliseconds); the ONNX session run that follows is O(seconds). The percentage hit is invisible. If profiling later disagrees, parallelise the per-string loop. Not protecting against a phantom regression up front.

[**Token-id drift** — different tokenizer libraries occasionally produce slightly different ids for edge cases (e.g. unicode handling, BOM, surrogate pairs).] → The committed-fixture unit test (Decision 5) catches drift on the round-trip we care about. For drift that only manifests on rare inputs (non-ASCII source code), the embedding still produces a vector — just not the same one HuggingFace would produce. Acceptable: semantic search is a recall tool, not a bit-exact reproducibility tool.

[**Existing `symbol_embeddings` rows might decode wrong** if the new tokenizer assigns different ids to the same source text.] → Wouldn't actually break anything — the stored vectors are derived from the embedding function as a whole, not from the token ids in isolation. Comparing a query's vector (computed via the new tokenizer) against existing stored vectors (computed via the old tokenizer) within the same model is the only thing that matters; it's all the model's output, not the tokenizer's directly. No DB migration required.

[**Special-token mismatch breaks pooling**] — if the new code accidentally inserts BERT's `[CLS]`/`[SEP]` instead of RoBERTa's `<s>`/`</s>`, the attention mask would still cover them but the model would receive the wrong tokens at positions 0 and -1, garbling the pooled vector. → The spike (Decision 4) ends with a numerical comparison against HF transformers, which catches this category of bug.

## Migration Plan

No data migration. Drop FastBertTokenizer, add Microsoft.ML.Tokenizers, rewrite the two methods, run the new test, smoke-test against the live Jina cache (`embeddings status` + `embeddings pull` + a `semantic_search` call from inside Claude Code). If anything fails, revert.

The `model_version` string (`jinaai/jina-embeddings-v2-base-code/768`) is unchanged across this swap, so existing `symbol_embeddings` rows are still considered valid by `IEmbeddingsStore.ShouldReembedAsync`. New embeddings going through the rewritten pipeline will hash to the same `(content_hash, model_version)` key as before; the upsert path is idempotent.

## Open Questions

- **Class rename** (`JinaCodeEmbeddingGenerator` → something more accurate)? Deferring per Decision 3. Worth revisiting after this lands and we have a feel for how the tokenizer-type detection grew.
- **`ICodeEmbeddingGenerator` interface changes** to surface tokenizer type for diagnostics (e.g. so `embeddings_status` could show "tokenizer: BPE")? Useful but not required to fix the bug. Folding into a follow-up if asked.
- **Spike artifact disposition** — keep the throwaway program in the repo as a developer doc, or genuinely throw it away? Suggest the latter; the unit test is the durable record.
- **Live-fixture vs committed-fixture** for `tokenizer.json` — committed is simpler if the license permits; otherwise lazily fetch in test setup. Verify license before deciding.
