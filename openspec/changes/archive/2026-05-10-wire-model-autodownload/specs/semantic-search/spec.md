## ADDED Requirements

### Requirement: Auto-download on first run
On the first `serve` or `index` invocation against a fresh cache, the server SHALL fetch the active embedding model's files (ONNX graph + tokenizer) from Hugging Face into the local cache before the embedding worker begins draining the channel. The fetch SHALL be idempotent: subsequent runs detect the cached files and skip the download. The bulk indexer SHALL run concurrently with the download — embed requests queue on the channel and drain once the model is ready.

#### Scenario: Cold-start populates cache
- **WHEN** `sourcegraph-mcp serve` is started with embeddings enabled and the cache directory `~/.cache/devbitslab.sourcegraph/models/<id>/` is empty
- **THEN** the server fetches `model.onnx` and `tokenizer.json` from `https://huggingface.co/<id>/resolve/main/`, the indexer's bulk pass runs in parallel, and once the download completes the embedding worker drains queued requests and writes vectors

#### Scenario: Warm cache skips download
- **WHEN** `sourcegraph-mcp serve` is started with embeddings enabled and the cache already contains valid `model.onnx` + `tokenizer.json`
- **THEN** no HTTP request is issued, the worker initialises immediately, and the cache is reused as-is

#### Scenario: SHA mismatch refuses partial file
- **WHEN** the default model's manifest pins a SHA-256 and the downloaded file's hash does not match
- **THEN** the server logs a `ModelDownloadException` warning naming the file and the expected hash, deletes the `.tmp` partial download, leaves the cache empty, and embeddings are unavailable for the session (every other tool keeps working)

### Requirement: Manifest carries remote and local paths
The model manifest SHALL represent each file as `(RemotePath, LocalName, ExpectedSha256)` so the HF source path can differ from the cache filename. When `LocalName` is omitted, the trailing path segment of `RemotePath` is used.

#### Scenario: Subdirectory remote path flattens to cache filename
- **WHEN** the default model's manifest contains `RemotePath = "onnx/model.onnx"` and `LocalName = "model.onnx"`
- **THEN** the downloader fetches `https://huggingface.co/<id>/resolve/main/onnx/model.onnx` and writes the bytes to `<cache>/<id>/model.onnx`

### Requirement: Override model is downloaded best-effort
When the user supplies a non-default `--model <id>` (no pinned manifest available), the server SHALL still attempt the download with the same atomic-rename behaviour but without SHA-256 verification.

#### Scenario: Custom model fetched without hash check
- **WHEN** the user passes `--model someorg/some-other-code-embed` and the cache is empty
- **THEN** the server downloads `model.onnx` and `tokenizer.json` from that HF repo, writes them atomically, skips hash verification, and the embedding pipeline starts normally

## MODIFIED Requirements

### Requirement: Graceful disable when embeddings unavailable
The semantic-search subsystem SHALL degrade safely when the model isn't cached and can't be fetched, the `sqlite-vec` extension isn't loadable, `--no-embeddings` was passed, or `--no-model-download` was passed against an empty cache.

#### Scenario: Disable via flag
- **WHEN** the server is started with `--no-embeddings`
- **THEN** the embedding pipeline never runs, no `symbol_embeddings` rows are written, and `semantic_search` responds with the "semantic search disabled" hint while every other tool works as before

#### Scenario: Model not yet downloaded and offline
- **WHEN** the server starts without an internet connection and the cached model is missing
- **THEN** the server logs a one-time warning naming the cache path and pointing at the `--no-embeddings` opt-out, the pipeline is disabled, and the rest of the index proceeds normally

#### Scenario: Air-gapped via --no-model-download with empty cache
- **WHEN** the server is started with `--no-model-download` and the cache is empty
- **THEN** no HTTP request is issued, the embedding pipeline is disabled for this session, the warning text names the cache path so the operator can pre-populate it, and `semantic_search` returns the disabled-message

#### Scenario: Air-gapped via --no-model-download with populated cache
- **WHEN** the server is started with `--no-model-download` and the cache is already populated
- **THEN** no HTTP request is issued, the cached model is used, and the embedding pipeline runs normally
