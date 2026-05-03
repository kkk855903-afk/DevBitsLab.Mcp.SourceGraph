## ADDED Requirements

### Requirement: test_framework column on symbols
The `symbols` table SHALL include `test_framework TEXT NULL` to record the detected test framework (`xunit | nunit | mstest`).

#### Scenario: xUnit method recorded
- **WHEN** a method tagged `[Fact]` is indexed
- **THEN** its `symbols.test_framework = 'xunit'`

### Requirement: symbol_history table
The schema SHALL include `symbol_history(symbol_id PRIMARY KEY, last_commit_sha TEXT, last_author TEXT, last_authored_at INTEGER, line_count INTEGER, blamed_content_sha BLOB)` with the cache key `blamed_content_sha` matching the source file's current `content_sha256` to skip redundant blame.

#### Scenario: Blame cache key
- **WHEN** a file's `content_sha256` matches the symbol's `symbol_history.blamed_content_sha`
- **THEN** the indexer skips `git blame` for that file
