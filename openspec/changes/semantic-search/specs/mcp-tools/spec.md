## ADDED Requirements

### Requirement: Semantic search tool
The server SHALL expose a `semantic_search` tool whose intent is fuzzy intent retrieval (not name-fragment matching, which `search_symbols` covers).

#### Scenario: Find code by intent
- **WHEN** the agent invokes `semantic_search(query = "logging that masks PII")`
- **THEN** the response is a top-k list of symbols ranked by cosine similarity to the query embedding, each annotated with location, score, and a one-line snippet
