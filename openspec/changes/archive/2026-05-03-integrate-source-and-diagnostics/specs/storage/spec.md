## ADDED Requirements

### Requirement: is_generated column on files
The `files` table SHALL include `is_generated INTEGER NOT NULL DEFAULT 0`; the column is `1` for any document obtained from `Project.GetSourceGeneratedDocumentsAsync()` and `0` for regular documents.

#### Scenario: Generated file flag
- **WHEN** an indexed solution contains a source generator emitting `Foo.g.cs`
- **THEN** the `files` row for `Foo.g.cs` has `is_generated = 1`

### Requirement: Diagnostics table
The schema SHALL include `diagnostics(id, symbol_id, file_id, severity, code, message, line, col)` with indexes on `(file_id)`, `(severity)`, `(code)`, and `(symbol_id)` for fast filtering.

#### Scenario: Severity filter
- **WHEN** `FindDiagnosticsAsync(severity: 2 (Warning), null, null, 100)` is called
- **THEN** the SQL plan uses `idx_diagnostics_severity` and returns rows with severity `>= 2`
