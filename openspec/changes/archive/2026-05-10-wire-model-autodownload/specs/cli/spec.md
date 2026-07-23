## MODIFIED Requirements

### Requirement: Embedding-related CLI flags
The CLI SHALL accept `--model <id>` to override the embedding model, `--no-embeddings` to disable the embedding pipeline entirely, and `--no-model-download` to disable the auto-download step while still using a pre-populated cache. All three flags apply to `serve` and `index`. The `--no-model-download` flag SHALL also be settable via the `SOURCEGRAPH_NO_MODEL_DOWNLOAD` environment variable.

#### Scenario: Disable embeddings
- **WHEN** `sourcegraph-mcp serve --solution <sln> --no-embeddings` is invoked
- **THEN** no per-scope embeddings drain is started, the model is not downloaded, and `semantic_search` returns the disabled-message

#### Scenario: Override model
- **WHEN** the user passes `--model nomic-ai/CodeRankEmbed`
- **THEN** the server resolves and (if needed) downloads that model best-effort (no SHA-256 verification, atomic rename still in place), ignores any cached embeddings whose `model_version` is different, and re-embeds on next index

#### Scenario: Disable auto-download with empty cache
- **WHEN** the user passes `--no-model-download` and the cache directory has no `model.onnx` or `tokenizer.json`
- **THEN** no HTTP request is issued, the embedding pipeline is disabled for this session (same payload as `--no-embeddings`), and the warning text names the cache path so the operator can pre-populate it

#### Scenario: Disable auto-download with populated cache
- **WHEN** the user passes `--no-model-download` and the cache directory already contains valid `model.onnx` + `tokenizer.json`
- **THEN** the cached model is loaded and embeddings run normally; no HTTP request is issued

#### Scenario: Disable auto-download via environment variable
- **WHEN** the user starts the server with `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` and no `--no-model-download` flag
- **THEN** the server behaves identically to the `--no-model-download` flag form
