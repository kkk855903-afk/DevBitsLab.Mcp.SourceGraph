## ADDED Requirements

### Requirement: Source-generated documents indexed
The indexer SHALL include source-generated documents (`Project.GetSourceGeneratedDocumentsAsync()`) alongside regular documents, marking the corresponding `files.is_generated` row to `1`.

#### Scenario: Generated document indexed
- **WHEN** a project uses a source generator producing a `*.g.cs` document with a real `class GeneratedFoo`
- **THEN** that class appears in `symbols` with `kind = Class`, its `files.is_generated = 1`, and tools render `(generated)` next to its name

#### Scenario: SHA gate on generated content
- **WHEN** the same source-gen run produces byte-identical output to last time
- **THEN** the file row's `content_sha256` is unchanged and no symbol/edge work happens for that file

### Requirement: Roslyn diagnostics captured per file
The indexer SHALL run `compilation.GetDiagnostics(ct)` after pass 2 and persist every diagnostic with a non-empty `Location.SourceSpan` into the `diagnostics` table; on reindex, prior diagnostics for the file SHALL be deleted before reinserting.

#### Scenario: Warning attached to a symbol
- **WHEN** a method calls an `[Obsolete("Use Foo")]`-tagged member and Roslyn emits `CS0618`
- **THEN** a diagnostics row exists with `code = "CS0618"`, `severity = 2 (Warning)`, the message text, line/col, and `symbol_id` resolving to the calling method

#### Scenario: Diagnostic without symbol attribution
- **WHEN** a diagnostic's location lies between symbol boundaries (e.g. an unused-using warning)
- **THEN** the row's `symbol_id` is `NULL` and the diagnostic is file-scoped

#### Scenario: Diagnostic reconciliation
- **WHEN** a file is edited to remove the cause of a warning
- **THEN** after live reindex, no `diagnostics` row remains with that file_id and the resolved code
