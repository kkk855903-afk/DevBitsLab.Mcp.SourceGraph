## Why

A live `serve` against the freshly-auto-downloaded default model (`jinaai/jina-embeddings-v2-base-code`) crashes during tokenizer load:

```
System.Text.Json.JsonException: JSON deserialization for type
'FastBertTokenizer.TokenizerJson+PostProcessorSection' was missing
required properties including: 'special_tokens'.
```

Root cause: Jina v2 base code ships a **RoBERTa-style BPE tokenizer** (`post_processor.type = "RobertaProcessing"`); `FastBertTokenizer` only supports BERT WordPiece (`BertProcessing` with a `special_tokens` field). The library doesn't fail open on unknown post-processor types — it throws a deserialization error that bubbles up as "Embedding model load failed; semantic search disabled" with no operator-actionable hint.

This isn't a localized bug in the default model. **Almost every modern code-aware embedding model is RoBERTa-based** — Microsoft's CodeBERT/UniXcoder, Jina v2/v3/v4, BGE-M3, GTE-code, …. By choosing `FastBertTokenizer`, the original 2026-05-03 semantic-search change quietly excluded the entire useful tier and the project's documented default. The bug went unnoticed because that change's task 7.1 ("run serve once locally against live HF") was deferred and never run.

Switching to `Microsoft.ML.Tokenizers` (Microsoft-maintained, supports BPE / RoBERTa / WordPiece / Tiktoken from a single API surface) unblocks the documented default and future-proofs us against tokenizer drift.

## What Changes

- Drop the `FastBertTokenizer` NuGet dependency. Add `Microsoft.ML.Tokenizers` v2.0.
- Rewrite `JinaCodeEmbeddingGenerator.EnsureInitialised()` and `EncodeBatch()` to use the new API:
  - **Tokenizer load**: parse the `model.type` field of `tokenizer.json` to detect tokenizer kind (`BPE` for RoBERTa/Jina, `WordPiece` for BERT), then construct the matching concrete tokenizer (`BpeTokenizer.Create(...)` or `WordPieceTokenizer.Create(...)`). A first implementation task is a 30-line spike that loads the actual cached Jina `tokenizer.json` and prints the first 10 token ids of `"Hello world"` — proceed only once the spike works against the real file.
  - **Encode**: per-string `EncodeToIds(text, settings: new EncodeSettings { MaxTokenCount })`. Manually pad each sequence to the per-batch max length, build the `attention_mask` (1 for real tokens, 0 for pad tokens), and convert `int32` → `int64` for the ONNX inputs.
  - **Special tokens**: ML.Tokenizers handles `[CLS]`/`<s>` and `[SEP]`/`</s>` insertion via its built-in normalisation; no manual prepending needed.
- **No user-visible behavior change**: same default model, same dimension (768), same MCP tool surface, same CLI verbs. The semantic_search payload, manifest shape, cache layout, and auto-download wiring are untouched.
- **BREAKING for plugin authors** that re-use `JinaCodeEmbeddingGenerator` directly with FastBertTokenizer-shaped expectations. No public consumers known; flagged for the changelog.
- Class rename **deferred to design.md** — leaning toward keeping `JinaCodeEmbeddingGenerator` to minimise diff and make this change reviewable as "library swap behind the same public surface."

Out of scope (worth flagging as follow-ups):
- Re-pinning manifest SHAs (still task 7.1 of `wire-model-autodownload`; this change doesn't depend on it, but smoke-testing this change on the live model will incidentally produce the SHAs we need to pin).
- Switching the default model away from Jina. The whole point of this change is to make Jina actually work.
- Streaming download progress through MCP `notifications/progress` (still queued from the prior change).
- Replacing `Microsoft.ML.OnnxRuntime` (still in use; only the tokenizer library is changing).

## Capabilities

### New Capabilities
*(none — this is a library swap behind an existing capability surface)*

### Modified Capabilities
- `semantic-search`: clarify that the embedding generator handles both BERT-WordPiece and RoBERTa-BPE tokenizer formats from a `tokenizer.json` file. The original spec was silent on tokenizer type, which is what allowed the FastBertTokenizer-only assumption to slip through.

## Impact

- **Affected code**: `src/DevBitsLab.Mcp.SourceGraph.Embeddings/JinaCodeEmbeddingGenerator.cs` (full rewrite of `EnsureInitialised` + `EncodeBatch`), `src/DevBitsLab.Mcp.SourceGraph.Embeddings/DevBitsLab.Mcp.SourceGraph.Embeddings.csproj` (drop one PackageReference, add another), `Directory.Packages.props` (centralised version pin).
- **Affected behaviour**: the documented default `jinaai/jina-embeddings-v2-base-code` finally works end-to-end. BERT-tokenized models continue to work via the WordPiece branch. Embedding output is byte-identical to what the model would produce in HF transformers (modulo any L2-norm rounding differences) — the change is in *how* we tokenize, not *what* the model receives.
- **Tests**: new unit test exercises tokenizer load against a real cached `tokenizer.json` fixture and asserts on a known token-id sequence; existing tests (`EmbeddingsDisabledPathTests`, `ModelDownloadGateFactoryTests`, `EmbeddingsManagerTests`, `EmbeddingsToolsTests`, `EmbeddingsToolsIntegrationTests`) keep passing without modification.
- **Dependencies**: drop `FastBertTokenizer`, add `Microsoft.ML.Tokenizers` 2.0. Net DLL footprint roughly comparable.
- **Database**: no migration needed — the `model_version` is unchanged so existing `symbol_embeddings` rows stay valid across the upgrade.
- **Docs**: README's "Optional code-aware semantic search" bullet stays accurate (no changes); CLAUDE.md unchanged.
