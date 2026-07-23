## 1. Spike — verify Microsoft.ML.Tokenizers can ingest the real Jina tokenizer.json

- [x] 1.1 In a scratch console project (or `tests/scratch/`, gitignored), reference `Microsoft.ML.Tokenizers` 2.0 and try to load `~/.cache/devbitslab.sourcegraph/models/jinaai_jina-embeddings-v2-base-code/tokenizer.json`. Identify which factory works: a single-call `Tokenizer.FromHuggingFaceJson(...)` if it exists, or `BpeTokenizer.Create(vocabStream, mergesStream)` after parsing `tokenizer.json` ourselves to extract vocab + merges. *(Outcome: parse `tokenizer.json` via `System.Text.Json` and feed `BpeOptions { Vocabulary, Merges, SpecialTokens, ByteLevel = true, BeginningOfSentenceToken, EndOfSentenceToken }` into `BpeTokenizer.Create(options)`. The 2.0 stable does not expose `Tokenizer.FromJson(path)` — confirmed by inspection of the SDK XML doc.)*
- [x] 1.2 Encode the string `"Hello world"`, print the first 10 token ids, and compare against the expected output from running `python3 -c "from tokenizers import AutoTokenizer; print(AutoTokenizer.from_pretrained('jinaai/jina-embeddings-v2-base-code').encode('Hello world').ids[:10])"` (or equivalent). *(Spike result: `[0, 10564, 7509, 2]` = `<s>` + "Hello" + " world" + `</s>` — the expected RoBERTa-wrapped shape. Captured as the test assertion.)*
- [x] 1.3 If the spike fails, **stop and re-evaluate**. Likely failure modes: (a) factory expects vocab.json + merges.txt as separate files (fix: download those alongside `tokenizer.json` in the manifest), (b) special-token insertion shape differs (fix: pass `addSpecialTokens: true` or use a Roberta-specific factory). Document the actual loading shape that worked in a comment for task 2. *(N/A — spike succeeded. Loading shape recorded in `JinaCodeEmbeddingGenerator.LoadBpe`.)*

## 2. Capture the tokenizer.json fixture for tests

- [x] 2.1 Verify the upstream Jina v2 base code license permits redistributing `tokenizer.json` (the model weights have their own license; the tokenizer config is usually under a separate, more permissive license — check the HF repo's LICENSE file). *(Outcome: HF repo has no top-level `LICENSE` file accessible at `/raw/main/LICENSE`. Defaulting to the safe path: don't redistribute.)*
- [ ] 2.2 If permitted, copy the cached `tokenizer.json` (~2.5 MB) to `tests/fixtures/embeddings/jina-v2-base-code-tokenizer.json` and commit it. *(Skipped — license unverified; chose 2.3 instead.)*
- [x] 2.3 If not permitted, add a one-time fetch step in the unit test class's `IClassFixture` setup that downloads the file into `tests/fixtures/.cache/` (gitignored) on first run; subsequent runs use the cached copy. Document the choice in the test file's class summary. *(Implemented as a no-network probe in `JinaTokenizerLoadTests.TokenizerFixture`: checks the live `~/.cache/devbitslab.sourcegraph/...` cache (resolved via `ModelStore.DefaultCacheDir()` for portability across `XDG_CACHE_HOME` / `LOCALAPPDATA` / `~/.cache`) then a per-project gitignored cache. **The HTTP-download fallback was deliberately removed** — synchronous `HttpClient.GetAsync(...).GetAwaiter().GetResult()` from an `IClassFixture` constructor hangs the test runner on slow / unreachable hosts. Tests use `[SkippableFact]` + `Skip.If(_fixture.SkipReason is not null, ...)` so a missing fixture cache registers as a visible skip in the test runner output rather than a hang or silent pass. Operators populate the fixture cache once via `curl` (recipe in the test class summary).)*

## 3. NuGet swap

- [x] 3.1 In `Directory.Packages.props`, add `<PackageVersion Include="Microsoft.ML.Tokenizers" Version="2.0.0" />` and remove the `FastBertTokenizer` line.
- [x] 3.2 In `src/DevBitsLab.Mcp.SourceGraph.Embeddings/DevBitsLab.Mcp.SourceGraph.Embeddings.csproj`, replace the `<PackageReference Include="FastBertTokenizer" />` line with `<PackageReference Include="Microsoft.ML.Tokenizers" />`.
- [x] 3.3 Run `dotnet restore` to verify both edges took effect. *(Verified by full server build: succeeds with 0 warnings.)*

## 4. Rewrite the generator

- [x] 4.1 In `src/DevBitsLab.Mcp.SourceGraph.Embeddings/JinaCodeEmbeddingGenerator.cs`, replace the `using FastBertTokenizer;` import with the appropriate `using Microsoft.ML.Tokenizers;`.
- [x] 4.2 Replace the `BertTokenizer? _tokenizer;` field declaration with the abstract `Tokenizer? _tokenizer;` (or whichever common base ML.Tokenizers exposes; if there's no useful base, declare two fields and switch on which one's set — keep it small). *(Settled on the abstract `Tokenizer?` from `Microsoft.ML.Tokenizers` — covers BpeTokenizer and WordPieceTokenizer through the same `EncodeToIds(string, int, out, out, bool, bool)` virtual.)*
- [x] 4.3 Rewrite `EnsureInitialised()` so the tokenizer load:
  - Reads `tokenizer.json` once with `System.Text.Json` to extract `model.type`.
  - If `BPE`: dispatches to whichever factory the spike validated. Pads vocabulary, merges, special-tokens metadata.
  - If `WordPiece`: dispatches to `WordPieceTokenizer.Create(...)` with the equivalent inputs.
  - Otherwise: log a warning naming `model.type`, set `_available = false`, return.
  - Catch any unexpected exception, log warning with model id + path, set `_available = false`. Same shape as today. *(Done as `TryLoadTokenizer` (internal static for direct testing) + `LoadBpe` / `LoadWordPiece` helpers. `EnsureInitialised` calls `TryLoadTokenizer` and reports the descriptive error on failure.)*
- [x] 4.4 Rewrite `EncodeBatch()` to use the per-string `EncodeToIds(text, settings: new EncodeSettings { MaxTokenCount = maxTokens })` shape:
  - Inner loop: for each input string, call `EncodeToIds`; pad the resulting `IReadOnlyList<int>` to `maxTokens` with the tokenizer's pad id; promote each `int` to `long` and copy into the rented `inputIds` buffer at offset `b * maxTokens`; build the matching attention-mask row (1 for real tokens, 0 for pad) into `attentionMask`.
  - Keep the existing `ArrayPool<long>` rental, the `DenseTensor<long>` construction, the ONNX `Run` call, the masked mean-pool, and the L2 normalise unchanged. *(Used the `(text, maxTokenCount, out, out, considerPreTokenization, considerNormalization)` overload — `EncodeSettings` is internal to one `BpeTokenizer` overload that takes both `string` AND `ReadOnlySpan<char>` for fast-path use; the public maxTokenCount overload is cleaner for our case.)*
- [x] 4.5 Update the class XML doc to drop the Jina-specific framing — it now generates embeddings via any BERT-WordPiece or RoBERTa-BPE tokenizer paired with a compatible ONNX graph.

## 5. New unit test

- [x] 5.1 Add `tests/DevBitsLab.Mcp.SourceGraph.Tests/JinaTokenizerLoadTests.cs`:
  - Test `Loading_jinaV2BaseCode_tokenizerJson_succeeds_andEncodesKnownString`: load the fixture from task 2, construct the generator pointed at the fixture path + a stub `model.onnx` path that doesn't exist (so the generator stops at the tokenizer-load step in `EnsureInitialised`), assert `IsAvailable` is false (because ONNX file doesn't exist) but **no exception escapes**.
  - Test `Tokenizer_encodes_helloWorld_toExpectedIds`: directly construct the tokenizer the same way the generator does, encode `"Hello world"`, assert the first 10 token ids match the upstream HuggingFace transformers output (precomputed and committed as a `[Theory]` data row or hard-coded). *(Implemented via `TryLoadTokenizer` directly — cleaner than constructing the full generator since we want to assert on tokenizer behaviour, not ONNX session lifecycle.)*
- [x] 5.2 Run the test in isolation; it must pass before continuing. *(4 new tests passing; full suite remains green.)*

## 6. Verify existing tests still pass

- [x] 6.1 `dotnet test tests/DevBitsLab.Mcp.SourceGraph.Tests/DevBitsLab.Mcp.SourceGraph.Tests.csproj` — every existing test in the unit suite passes (in particular, `EmbeddingsDisabledPathTests.JinaCodeEmbeddingGenerator_pointedAtMissingFile_isNotAvailable` should still flip to false rather than throw). *(568/568 passing.)*
- [x] 6.2 `dotnet test tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/DevBitsLab.Mcp.SourceGraph.IntegrationTests.csproj` — every integration test passes (the embeddings-management suite from `add-embeddings-cli-and-tools` shouldn't change shape). *(15/15 passing.)*

## 7. Live smoke (deferred to user, like prior changes)

- [ ] 7.1 Restart the MCP server against a real workspace, confirm stderr no longer logs `Embedding model load failed`. Sample stderr should show `Loaded embedding model jinaai/jina-embeddings-v2-base-code (dim=768)`. *(**Deferred to user** — requires a real `serve` run against the live cache.)*
- [ ] 7.2 Issue a `semantic_search(query = "rate limiting code")` MCP call against an indexed solution; confirm a non-empty result set with sensible scores. *(**Deferred to user** — pairs with 7.1.)*
- [ ] 7.3 Capture the SHA-256 of `model.onnx` and `tokenizer.json` from the now-working cache and paste them into `DefaultEmbeddingModel.Manifest` (this knocks off task 7.1 of `wire-model-autodownload` as a side-effect). *(**Deferred to user** — paired with 7.1; once the live load succeeds, `shasum -a 256` on the cached files.)*

## 8. Docs

- [x] 8.1 README's "Optional code-aware semantic search" bullet doesn't need wording changes — it talks about the model, not the tokenizer library. Skim to confirm. *(Confirmed: README and CLAUDE.md mention neither `FastBertTokenizer` nor `WordPiece` directly. No edit needed.)*
- [x] 8.2 If the `tokenizer.json` had to be lazily fetched (task 2.3 path), document the fetch step in the test file's class summary so contributors know why a one-off network hop happens on first run. *(Done in `JinaTokenizerLoadTests` class summary — explains the live-cache → fixture-cache → HF-download chain and why the file isn't committed.)*
