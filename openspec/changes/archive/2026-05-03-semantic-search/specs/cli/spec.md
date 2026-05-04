## ADDED Requirements

### Requirement: Embedding-related CLI flags
The CLI SHALL accept `--model <id>` to override the embedding model and `--no-embeddings` to disable the embedding pipeline entirely; both apply to `serve` and `index`.

#### Scenario: Disable embeddings
- **WHEN** `sourcegraph-mcp serve --solution <sln> --no-embeddings` is invoked
- **THEN** `EmbeddingsHostedService` is not registered, the model is not downloaded, and `semantic_search` returns the disabled-message

#### Scenario: Override model
- **WHEN** the user passes `--model nomic-ai/CodeRankEmbed`
- **THEN** the server resolves and (if needed) downloads that model, ignores any cached embeddings whose `model_version` is different, and re-embeds on next index
