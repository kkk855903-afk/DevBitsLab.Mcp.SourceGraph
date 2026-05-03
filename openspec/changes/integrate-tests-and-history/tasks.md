## 1. Test detection

- [ ] 1.1 Add `test_framework TEXT` column on `symbols` (`xunit | nunit | mstest | null`).
- [ ] 1.2 In pass 2 (after `index-attributes`), inspect each method's attached attributes; if matched, set `test_framework`.
- [ ] 1.3 Walk the method body's calls; find the first call into a non-test-project symbol; emit `Tests` edge.

## 2. Schema

- [ ] 2.1 Bump `Schema.Version`.
- [ ] 2.2 New table `symbol_history(symbol_id PK, last_commit_sha TEXT, last_author TEXT, last_authored_at INTEGER, line_count INTEGER, blamed_content_sha BLOB)`.
- [ ] 2.3 Optional view `vw_recent_changes` (last 7 days) for `who_authored` queries that don't specify a symbol.

## 3. Git pipeline

- [ ] 3.1 `GitBlameRunner` invokes `git blame --line-porcelain --no-progress <path>` and parses output.
- [ ] 3.2 `HistoryHostedService` reads `Channel<HistoryRequest>`, batches per file, computes per-symbol last-touch.
- [ ] 3.3 SHA-gated cache: skip if `blamed_content_sha == files.content_sha256`.
- [ ] 3.4 Disable cleanly when git isn't on PATH, repo isn't a git working tree, or `--no-history` is set.

## 4. MCP tools

- [ ] 4.1 New tool `list_tests_for(symbol, includeIndirect = false, limit = 50)` walking incoming `Tests` edges.
- [ ] 4.2 New tool `who_authored(symbol)` returning last commit sha, author, authored time, lines blamed.
- [ ] 4.3 `find_definition`, `list_symbols_in_file`: add a one-line history note when available (`last touched 2026-04-12 by jacques`).
- [ ] 4.4 New tool `recent_changes(days = 7, author?)` listing symbols whose `last_authored_at` falls in window.

## 5. CLI

- [ ] 5.1 `--no-history` global flag; persisted into the in-process config.

## 6. Tests

- [ ] 6.1 Fixture: a test project with one xUnit `[Fact]` calling `Calculator.Add`. Confirm a `Tests` edge `(test → Calculator.Add)`.
- [ ] 6.2 Multi-framework: an NUnit `[Test]` and an MSTest `[TestMethod]` in the same fixture.
- [ ] 6.3 History fixture: a temp repo with a known commit; run the indexer; confirm `symbol_history` rows match `git blame` output.
- [ ] 6.4 Disable test: run with `--no-history`; confirm no git invocations and `who_authored` returns the disabled-message.

## 7. Update specs

- [ ] 7.1 Sync delta specs into `openspec/specs/{indexing, storage, mcp-tools, cli}/spec.md` on archive.
