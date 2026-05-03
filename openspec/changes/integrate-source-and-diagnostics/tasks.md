## 1. Schema

- [x] 1.1 Bump `Schema.Version`.
- [x] 1.2 Add `is_generated INTEGER NOT NULL DEFAULT 0` to `files`.
- [x] 1.3 Create `diagnostics(id, symbol_id, file_id, severity, code, message, line, col)` with indexes on `(severity)`, `(code)`, `(file_id)`, `(symbol_id)`.

## 2. Indexer

- [x] 2.1 In addition to `Project.Documents`, also enumerate `Project.GetSourceGeneratedDocumentsAsync()`.
- [x] 2.2 Mark `files.is_generated = 1` when upserting a generated document's path.
- [x] 2.3 After pass 2, call `compilation.GetDiagnostics(ct)` and walk every `Diagnostic` with a non-empty `Location.SourceSpan`.
- [x] 2.4 For each diagnostic, look up the smallest enclosing indexed symbol via the syntax tree; set `symbol_id` or `NULL`.
- [x] 2.5 Reconcile diagnostics on file change: `DELETE FROM diagnostics WHERE file_id = ?` before re-emitting.

## 3. Storage

- [x] 3.1 `IGraphStore.UpsertDiagnosticsForFileAsync(fileId, IEnumerable<Diagnostic>)`.
- [x] 3.2 `IGraphStore.FindDiagnosticsAsync(severity?, code?, symbolId?, limit)`.
- [x] 3.3 `IGraphStore.ListGeneratedFilesAsync(limit)`.

## 4. MCP tools

- [x] 4.1 New tool `find_diagnostics(severity? = "warning", code?, symbol?, limit = 100)` returning markdown rows.
- [x] 4.2 New tool `list_generated_files()` for a quick overview.
- [x] 4.3 `find_definition`, `list_symbols_in_file`, `module_summary` etc. annotate `(generated)` for symbols whose file is `is_generated = 1`.
- [x] 4.4 `find_references` adds an `include_generated = false` parameter; defaults to filtering generated.

## 5. Tests

- [x] 5.1 Fixture using a source generator (e.g. CommunityToolkit.Mvvm `[ObservableProperty]` or a simple `IIncrementalGenerator`); confirm generated symbols are indexed and marked.
- [x] 5.2 Diagnostic test: a fixture with a method tagged `[Obsolete]` whose caller emits CS0618; assert `find_diagnostics(code = "CS0618")` returns the caller location.
- [x] 5.3 Reconcile test: edit the fixture to remove the warning; reindex; confirm the diagnostic row disappears.

## 6. Update specs

- [ ] 6.1 Sync delta specs into `openspec/specs/{indexing, storage, mcp-tools}/spec.md` on archive.
