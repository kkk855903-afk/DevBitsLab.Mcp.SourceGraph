## ADDED Requirements

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
