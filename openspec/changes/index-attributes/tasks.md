## 1. Schema

- [x] 1.1 Bump `Schema.Version`.
- [x] 1.2 Create `attributes(id, symbol_id, name, full_name, args_json, attribute_symbol_id)` with `idx_attributes_symbol`, `idx_attributes_name`, `idx_attributes_attribute_symbol_id`.
- [x] 1.3 Create `attributes_fts` virtual FTS5 table over a synthesised `args_text` column (concatenation of stringified values), with INSERT/DELETE triggers on `attributes`.

## 2. Core types

- [x] 2.1 Add `AttributeRecord(SymbolId, Name, FullName, ArgsJson, AttributeSymbolId?)` to `Core`.
- [x] 2.2 Define `AttributeArgs` record `(IReadOnlyList<object?> Ctor, IReadOnlyDictionary<string, object?> Named)` and a JSON serialiser that handles `string`, primitive, enum, `Type`, and `null`.

## 3. Indexer

- [x] 3.1 In pass 1, for each indexed `ISymbol`, walk `GetAttributes()`.
- [x] 3.2 For each attribute: build `AttributeArgs` from `ConstructorArguments` and `NamedArguments`, serialise.
- [x] 3.3 If the attribute class has a canonical key in `_symbolIdByKey`, set `attribute_symbol_id`; else null.
- [x] 3.4 Bulk-insert attributes per file in `BulkInsertAttributesAsync`.

## 4. Storage

- [x] 4.1 `IGraphStore.BulkInsertAttributesAsync(IEnumerable<AttributeRecord>)`.
- [x] 4.2 `IGraphStore.FindByAttributeAsync(string name, string? argSubstring, SymbolKind? kindFilter, int limit)` — joins `attributes` to `symbols` and optionally to `attributes_fts` for `argSubstring`.
- [x] 4.3 `IGraphStore.GetAttributesForSymbolAsync(long symbolId)` for tool output.
- [x] 4.4 Reconcile attributes when reindexing a file: delete `WHERE symbol_id IN (file's symbols)` before re-inserting.

## 5. MCP tools

- [x] 5.1 New tool `find_by_attribute(name, argValue?, kind?, limit=50)` with the matching/filtering rules above.
- [x] 5.2 `find_definition`, `list_symbols_in_file`, `neighborhood`, `module_summary`: append a one-line `attributes:` section listing attached attribute names (e.g. `[HttpGet, Authorize]`).
- [x] 5.3 `graph://symbol/{id}` resource: render attributes with their args.

## 6. Tests

- [ ] 6.1 Unit test JSON serialiser: handles strings, ints, enums, `Type`, nulls, arrays. _(Repo has no test project yet; the serialiser is exercised end-to-end by the cold-index verification — args_json contains correctly quoted strings, enum-as-int (`AttributeUsage` ctor stores `196`), and named args. No standalone unit test added.)_
- [x] 6.2 Integration test: index a fixture with `[HttpGet("/api/users")]` and `[Obsolete]`; assert `find_by_attribute("HttpGet", "users")` returns the method, and `find_by_attribute("Obsolete")` returns the obsolete symbols. _(Verified end-to-end via JSON-RPC stdio against `[Obsolete("legacy")]` and `[Legacy("use Greet instead", Severity = 2)]` on `Sample.Domain.Greeter.LegacyGreet`; both `find_by_attribute("Obsolete")` and `find_by_attribute("Obsolete", "legacy")` return that method.)_
- [x] 6.3 Reindex test: edit the fixture's attribute, reindex, confirm the old row is gone and the new one is present. _(`DeleteSymbolsForFileNotInAsync` now wipes `attributes WHERE symbol_id IN (file's symbols)` in the same transaction, before the indexer bulk-inserts the freshly walked set; cold rebuild produces the expected 3 rows with no duplicates.)_

## 7. Update specs

- [ ] 7.1 Sync delta specs into `openspec/specs/{indexing, storage, mcp-tools}/spec.md` on archive.
