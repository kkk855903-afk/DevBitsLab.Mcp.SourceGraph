## ADDED Requirements

### Requirement: Symbol metadata columns
The `symbols` table SHALL include `modifiers TEXT`, `accessibility INTEGER NOT NULL DEFAULT 0`, and `xml_summary TEXT` columns alongside the existing fields.

#### Scenario: Column presence after migration
- **WHEN** `EnsureSchemaAsync` runs against a v4 (or older) DB
- **THEN** the resulting `symbols` table has `modifiers`, `accessibility`, and `xml_summary` columns and `Schema.Version = 5`

### Requirement: FTS5 indexes XML summary
The `symbols_fts` virtual table SHALL include `xml_summary` as a tokenised column so `search_symbols` matches against it.

#### Scenario: Search by description
- **WHEN** `SearchSymbolsAsync("retry", null, 25)` is called against a graph that contains a method with `xml_summary = "Retries the operation on transient errors"`
- **THEN** that method appears in the result set

### Requirement: Container-id batch update
`SqliteGraphStore` SHALL expose `BatchUpdateContainerIdsAsync(IReadOnlyList<(long childId, long parentId)>)` that updates many `container_id` values in a single transaction.

#### Scenario: Bulk container update
- **WHEN** `BatchUpdateContainerIdsAsync` is called with N pairs
- **THEN** all updates run inside a single `BEGIN/COMMIT`, the affected row count equals N, and rows whose `parentId` doesn't exist are skipped (no FK error since FK was previously dropped)
