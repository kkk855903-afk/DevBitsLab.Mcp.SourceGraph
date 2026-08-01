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
- **WHEN** a fresh `sourcegraph-mcp index <sln> --enable-embeddings` runs against a solution and the model is available
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
The semantic-search subsystem SHALL be disabled by default and SHALL degrade safely when the model isn't cached, a permitted fetch fails, the `sqlite-vec` extension isn't loadable, or `--no-embeddings` was passed.

#### Scenario: Disabled by default
- **WHEN** the server is started without `--enable-embeddings`, including with the compatibility `--no-embeddings` flag
- **THEN** the embedding pipeline never runs, no `symbol_embeddings` rows are written, and `semantic_search` responds with the "semantic search disabled" hint while every other tool works as before

#### Scenario: Model not yet downloaded in the default offline mode
- **WHEN** the server starts with `--enable-embeddings`, an empty model cache, and no explicit download opt-in
- **THEN** no HTTP request is issued, the server logs a one-time warning naming the cache path and the explicit `embeddings pull` / `--allow-model-download` choices, the pipeline is disabled, and the rest of the index proceeds normally

#### Scenario: Air-gapped via --no-model-download with empty cache
- **WHEN** the server is started with `--enable-embeddings --no-model-download` and the cache is empty
- **THEN** no HTTP request is issued, the embedding pipeline is disabled for this session, the warning text names the cache path so the operator can pre-populate it, and `semantic_search` returns the disabled-message

#### Scenario: Air-gapped via --no-model-download with populated cache
- **WHEN** the server is started with `--enable-embeddings --no-model-download` and the cache is already populated
- **THEN** no HTTP request is issued, the cached model is used, and the embedding pipeline runs normally

### Requirement: Model identity tracked per row
Each embedding row SHALL carry a `model_version` so that swapping the embedding model invalidates the existing embeddings rather than mixing dimensions.

#### Scenario: Model upgrade
- **WHEN** the server starts with a different `--model` than the rows on disk
- **THEN** rows whose `model_version` doesn't match the active model are treated as missing; affected symbols re-embed on the next index pass

### Requirement: Explicit opt-in model bootstrap
`serve` and `index` SHALL NOT fetch an embedding model by default. The operator MAY populate the cache with the explicit `embeddings pull` command or opt into automatic bootstrap with `--allow-model-download` / `SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1`. A permitted fetch SHALL be idempotent: subsequent runs detect the cached files and skip the download. When automatic bootstrap is enabled, the bulk indexer SHALL run concurrently with the download — embed requests queue on the channel and drain once the model is ready.

#### Scenario: Default cold-start stays offline
- **WHEN** `sourcegraph-mcp serve --enable-embeddings` is started with an empty cache and no download opt-in
- **THEN** no HTTP request is issued and semantic search is disabled for the session while all non-embedding indexing continues

#### Scenario: Opted-in cold-start populates cache
- **WHEN** `sourcegraph-mcp serve --enable-embeddings --allow-model-download` is started and the cache directory `~/.cache/devbitslab.sourcegraph/models/<id>/` is empty
- **THEN** the server fetches `model.onnx` and `tokenizer.json` from `https://huggingface.co/<id>/resolve/main/`, the indexer's bulk pass runs in parallel, and once the download completes the embedding worker drains queued requests and writes vectors

#### Scenario: Warm cache skips download
- **WHEN** `sourcegraph-mcp serve --enable-embeddings` is started and the cache already contains valid `model.onnx` + `tokenizer.json`
- **THEN** no HTTP request is issued, the worker initialises immediately, and the cache is reused as-is

#### Scenario: SHA mismatch refuses partial file
- **WHEN** the default model's manifest pins a SHA-256 and the downloaded file's hash does not match
- **THEN** the server logs a `ModelDownloadException` warning naming the file and the expected hash, deletes the `.tmp` partial download, leaves the cache empty, and embeddings are unavailable for the session (every other tool keeps working)

### Requirement: Manifest carries remote and local paths
The model manifest SHALL represent each file as `(RemotePath, LocalName, ExpectedSha256)` so the HF source path can differ from the cache filename. When `LocalName` is omitted, the trailing path segment of `RemotePath` is used.

#### Scenario: Subdirectory remote path flattens to cache filename
- **WHEN** the default model's manifest contains `RemotePath = "onnx/model.onnx"` and `LocalName = "model.onnx"`
- **THEN** the downloader fetches `https://huggingface.co/<id>/resolve/main/onnx/model.onnx` and writes the bytes to `<cache>/<id>/model.onnx`

### Requirement: Override model is downloaded best-effort only after explicit authorization
When the user supplies a non-default `--model <id>` (no pinned manifest available), the server SHALL only attempt a network download when that run also has explicit download authorization or when the user invokes `embeddings pull`. An authorized download SHALL use the same atomic-rename behaviour but without SHA-256 verification.

#### Scenario: Custom model fetched without hash check
- **WHEN** the user passes `--enable-embeddings --model someorg/some-other-code-embed --allow-model-download` and the cache is empty
- **THEN** the server downloads `model.onnx` and `tokenizer.json` from that HF repo, writes them atomically, skips hash verification, and the embedding pipeline starts normally

### Requirement: Tokenizer-format detection
The embedding generator SHALL detect the tokenizer model type by reading the `model.type` field of the cached `tokenizer.json` and dispatch to the matching concrete tokenizer implementation. Supported tokenizer model types: `BPE` (RoBERTa-style, used by `jinaai/jina-embeddings-v2-base-code` and most modern code-aware models) and `WordPiece` (BERT-style). Any other value SHALL log a warning naming the unsupported type and disable the embedding pipeline for the session — the rest of the indexer continues to function.

#### Scenario: RoBERTa BPE tokenizer loads successfully
- **WHEN** the cached `tokenizer.json` for `jinaai/jina-embeddings-v2-base-code` (whose `model.type = "BPE"` and `post_processor.type = "RobertaProcessing"`) is loaded
- **THEN** the generator constructs a BPE-style tokenizer, `IsAvailable` becomes true, and a subsequent `EmbedAsync` call returns a 768-dim L2-normalised vector

#### Scenario: BERT WordPiece tokenizer loads successfully
- **WHEN** the cached `tokenizer.json` for a BERT-tokenised model (e.g. `BAAI/bge-base-en-v1.5`, `model.type = "WordPiece"`) is loaded
- **THEN** the generator constructs a WordPiece-style tokenizer, `IsAvailable` becomes true, and `EmbedAsync` returns the model's documented dimension

#### Scenario: Unsupported tokenizer type degrades gracefully
- **WHEN** the cached `tokenizer.json` declares a `model.type` other than `BPE` or `WordPiece` (e.g. `Unigram`, `SentencePiece`)
- **THEN** the generator logs a warning naming the unsupported type, `IsAvailable` returns false, no exception escapes, and `semantic_search` returns the disabled-message while every other tool keeps working

### Requirement: Token-id reproducibility against fixture
The generator's tokenizer SHALL produce the same token-id sequence for a fixed input string as a regression-guard fixture. The fixture covers at minimum the cached `tokenizer.json` for the default model (`jinaai/jina-embeddings-v2-base-code`).

#### Scenario: Known input produces fixed ids
- **WHEN** the generator tokenises `"Hello world"` against the committed/fetched Jina v2 tokenizer fixture
- **THEN** the first 10 token ids match the values produced by the upstream HuggingFace `transformers.AutoTokenizer.from_pretrained("jinaai/jina-embeddings-v2-base-code")` for the same input

