## ADDED Requirements

### Requirement: Attributes table with FTS over arguments
The schema SHALL include an `attributes(id, symbol_id, name, full_name, args_json, attribute_symbol_id)` table indexed on `(symbol_id)`, `(name)`, and `(attribute_symbol_id)`, plus an `attributes_fts` virtual table tokenising the synthesised `args_text` column.

#### Scenario: Trigram match on argument text
- **WHEN** `attributes_fts MATCH 'users-list'` is queried against a row whose `args_json` contains `"/api/users-list"`
- **THEN** that row is returned

### Requirement: find_by_attribute query API
`IGraphStore` SHALL expose `FindByAttributeAsync(name, argSubstring?, kindFilter?, limit)` returning `SymbolHit` rows whose attached attributes match the criteria.

#### Scenario: Strict name match plus wildcard arg match
- **WHEN** `FindByAttributeAsync("HttpGet", "users", null, 50)` is called against a graph with `[HttpGet("/api/users")]` on method `M`
- **THEN** `M` is in the result set

#### Scenario: Name match without arg constraint
- **WHEN** `FindByAttributeAsync("Obsolete", null, null, 50)` is called
- **THEN** every symbol carrying any `[Obsolete]` (with or without args) is returned
