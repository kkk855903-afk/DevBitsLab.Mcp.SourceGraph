## 1. Schema bump

- [ ] 1.1 Bump `Schema.Version` to 5; the existing drop-and-rebuild path takes care of migration.
- [ ] 1.2 Add columns to the `symbols` CREATE TABLE: `modifiers TEXT`, `accessibility INTEGER NOT NULL DEFAULT 0`, `xml_summary TEXT`.
- [ ] 1.3 Add `xml_summary` to the FTS5 virtual table column list and to all three triggers (`symbols_ai`, `symbols_au`, `symbols_ad`).

## 2. Core model

- [ ] 2.1 Extend the `Symbol` record in `DevBitsLab.Mcp.SourceGraph.Core` with `Modifiers`, `Accessibility`, `XmlSummary` (all nullable string / int).
- [ ] 2.2 Update `SymbolHit` and `RawSymbolHit` to surface the new fields.

## 3. SymbolMapping

- [ ] 3.1 Add `Modifiers(ISymbol)` returning the canonical comma-joined token string.
- [ ] 3.2 Add `Accessibility(ISymbol)` returning the int enum value.
- [ ] 3.3 Add `XmlSummary(ISymbol)` that calls `GetDocumentationCommentXml()`, parses `<summary>` (preserving inline tags as text), resolves `<inheritdoc/>` up the override chain, returns null if absent.

## 4. Indexer

- [ ] 4.1 In pass 1, capture the new fields and pass them to `UpsertSymbolAsync`.
- [ ] 4.2 After pass-1a (declarations inserted), run pass-1b: for each `(symbol, ContainingSymbol)` pair, look up the parent's id in `_symbolIdByKey` and `UPDATE symbols SET container_id = ? WHERE id = ?`.
- [ ] 4.3 Wrap the inheritdoc resolution in a defensive try/catch — Roslyn occasionally throws on circular inheritdoc.

## 5. Storage

- [ ] 5.1 Update `UpsertSymbolAsync` SQL to include the new columns.
- [ ] 5.2 Add `BatchUpdateContainerIdsAsync(IReadOnlyList<(long childId, long parentId)>)` for the pass-1b update.
- [ ] 5.3 Update `RawSymbolHit.ToHit()` to map the new fields.

## 6. MCP tools

- [ ] 6.1 `find_definition`, `list_symbols_in_file`, `neighborhood`, `module_summary`: include accessibility, modifiers, and a one-line XML summary in markdown output.
- [ ] 6.2 `graph://symbol/{id}` resource: render the full XML summary as a quoted block.
- [ ] 6.3 New tool `list_members(container, includeInherited=false, accessibility?)`. Recursive CTE on `container_id`. Excludes inherited unless requested.

## 7. Tests

- [ ] 7.1 Unit test for each `SymbolMapping` extractor (modifiers, accessibility, xml summary, inheritdoc resolution).
- [ ] 7.2 Integration test against `tests/fixtures/Sample.sln`: assert that `IGreeter.Greet` has `accessibility=public`, `Greeter._prefix` has `accessibility=private modifiers=readonly`, `Calculator.Add` has no modifiers.
- [ ] 7.3 FTS5 query test: a fixture method with `<summary>retry on transient errors</summary>` is found by `search_symbols("retry")`.

## 8. Update specs

- [ ] 8.1 Sync delta specs into `openspec/specs/{indexing, storage, mcp-tools}/spec.md` on archive.
