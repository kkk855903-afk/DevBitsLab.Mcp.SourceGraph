## 1. Storage: extend the view layer

- [x] 1.1 In `src/DevBitsLab.Mcp.SourceGraph.Storage/Views.sql`, add three new `CREATE TEMP VIEW` blocks with `{{SCOPE_UNION_BLOCK_v_annotations}}`, `{{SCOPE_UNION_BLOCK_v_diagnostics}}`, and `{{SCOPE_UNION_BLOCK_v_history}}` placeholder tokens, alongside the existing five views. Keep the file's header comment-style consistent (no literal placeholder strings in the comment, per the lesson from the parent change).
- [x] 1.2 In `src/DevBitsLab.Mcp.SourceGraph.Storage/Views.cs`, add three new entries to `PerScopeBlockTemplates`:
  - `v_annotations`:
    ```sql
    SELECT '{SCOPE_ID}' AS scope, a.id AS id, a.symbol_id AS symbol_id,
           a.name AS name, a.full_name AS full_name, a.flavor AS flavor,
           a.args_json AS args_json, a.attribute_symbol_id AS attribute_symbol_id
    FROM "{SCOPE_ID}".annotations a
    ```
  - `v_diagnostics`:
    ```sql
    SELECT '{SCOPE_ID}' AS scope, d.id AS id, d.symbol_id AS symbol_id, d.file_id AS file_id,
           d.severity AS severity,
           (CASE d.severity WHEN 0 THEN 'hidden' WHEN 1 THEN 'info'
                            WHEN 2 THEN 'warning' WHEN 3 THEN 'error'
                            ELSE CAST(d.severity AS TEXT) END) AS severity_name,
           d.code AS code, d.message AS message, d.line AS line, d.col AS column_number
    FROM "{SCOPE_ID}".diagnostics d
    ```
  - `v_history`:
    ```sql
    SELECT '{SCOPE_ID}' AS scope, h.symbol_id AS symbol_id,
           h.last_commit_sha AS last_commit_sha, h.last_author AS last_author,
           h.last_authored_at AS last_authored_at, h.line_count AS line_count,
           h.blamed_content_sha AS blamed_content_sha
    FROM "{SCOPE_ID}".symbol_history h
    ```
- [x] 1.3 In `Views.cs`, add three new `ViewDescriptor` entries to the `BuildDescriptors()` list. Use the same level of column-by-column documentation as the existing five — every column gets a one-line description that an agent reading `describe_schema`'s output can act on. Notably:
  - `v_annotations.flavor`: enumerate the known values (`csharp-attribute`, `xaml-attached-property`) and note that future plugins can introduce new flavors.
  - `v_annotations.args_json`: warn that this is raw TEXT; for substring search prefer the `find_by_annotation` curated tool (FTS5-indexed via `annotations_fts`).
  - `v_diagnostics.symbol_id`: mark as nullable; document that diagnostics outside any indexed declaration (e.g. unused-using directives) carry NULL here.
  - `v_diagnostics.severity_name`: enumerate the four values (`hidden`, `info`, `warning`, `error`) and note the corresponding integer values via `severity`.
  - `v_diagnostics.column_number`: note the rename from underlying `col` (SQL-reserved bare `column`).
  - `v_history.last_authored_at`: document that this is Unix-millis (matching `v_files.last_indexed_at` and `v_scopes.last_indexed_at`); agents needing ISO-8601 use `datetime(last_authored_at / 1000, 'unixepoch')`.
- [x] 1.4 Bump `Views.SchemaVersion` from `1` to `2`. Update its XML doc comment to read: *"Bumps on any view-set change — addition, removal, column rename, or column-type change — so clients that cache `describe_schema` by version always re-introspect after a server upgrade."*
- [x] 1.5 Build clean (`dotnet build`). The connection helper `MultiScopeReadOnlyConnection.BuildViewDdl` iterates `Views.PerScopeBlockTemplates.Keys`, so the new views ride for free — no code changes there.

## 2. Tests: shape + composability

- [x] 2.1 Extend `tests/DevBitsLab.Mcp.SourceGraph.Tests/ViewsTests.cs` with three new test cases:
  - `Substituted_v_annotations_returnsExpectedColumns`: insert one annotation row in a single-scope test DB; assert `SELECT * FROM v_annotations` returns the descriptor's columns in the documented order with the expected values.
  - `Substituted_v_diagnostics_mapsSeverityToText`: insert two diagnostic rows (severity=2, severity=3); assert `severity_name` reads `warning` / `error` and `severity` reads `2` / `3`.
  - `Substituted_v_history_returnsExpectedColumns`: insert one symbol_history row; assert all six columns project correctly.
- [x] 2.2 Update `tests/.../GraphQueryToolTests.cs`:
  - The existing `DescribeSchema_returnsAllViewsAndLiveKinds` test: bump `ViewSchemaVersion.Should().Be(1)` → `Be(2)` and `dto.Views.Should().HaveCount(5)` → `HaveCount(8)`. Update the `BeEquivalentTo` view-name assertion to include the three new names.
  - Extend the `SeedScopeWithDataAsync` fixture helper to also insert: (a) one annotation row tagged on the seeded `Calculator` symbol (e.g. annotation name `"Obsolete"`, flavor `"csharp-attribute"`); (b) one diagnostic row at severity=2 against `Calculator` (e.g. code `"CS0612"`).
- [x] 2.3 Add `QueryGraph_annotationsJoinSymbols_findsDecoratedTypes` to `GraphQueryToolTests.cs`. Single-scope query (`scope: "frontend"`):
  ```sql
  SELECT s.fqn FROM v_annotations a
  JOIN v_symbols s ON s.id = a.symbol_id AND s.scope = a.scope
  WHERE a.name = @name AND s.is_type = 1
  ```
  Parameters: `{ "@name": "Obsolete" }`. Assert exactly one row, `fqn` = `Sample.frontend.Calculator`.
- [x] 2.4 Add `QueryGraph_diagnosticsJoinSymbols_findsSymbolsWithWarnings` to `GraphQueryToolTests.cs`. Single-scope query:
  ```sql
  SELECT DISTINCT s.fqn FROM v_diagnostics d
  JOIN v_symbols s ON s.id = d.symbol_id AND s.scope = d.scope
  WHERE d.severity_name = 'warning' AND s.is_public = 1 AND s.is_type = 1
  ```
  Assert exactly one row, `fqn` = `Sample.frontend.Calculator`.
- [x] 2.5 Add `QueryGraph_historyView_returnsExpectedColumns_evenWithEmptyData` to `GraphQueryToolTests.cs`. Asserts `SELECT * FROM v_history` runs without error against the fixture (which doesn't seed git-blame rows), `RowCount = 0`, columns include `last_commit_sha` and `last_authored_at`. (Composability against populated history rides on the existing curated-tool integration tests.)
- [x] 2.6 `dotnet build` clean; `dotnet test` — full suite green.

## 3. Documentation

- [x] 3.1 README updated: "Ad-hoc queries (escape hatch)" subsection now mentions all 8 views; the version bump from 1 → 2 is called out with the policy clarification; "Example tool calls" gained TWO new composability examples — one joining `v_annotations` + `v_diagnostics` + `v_symbols` (`[Obsolete]` types with outstanding CS-warnings), one joining `v_history` + `v_symbols` (long-method refactor candidates last touched > 6 months ago).
- [x] 3.2 CLAUDE.md: "ad-hoc questions" paragraph extended to list all 8 views and the `view_schema_version = 2` policy clarification.
- [x] 3.3 No proposal updates needed — already lists the new views.

## 4. Verification + spec sync

- [x] 4.1 `dotnet build` clean — 0 errors, 0 warnings.
- [x] 4.2 `dotnet test` — 483 main + 8 integration = 491/491 pass, deterministically across 3 consecutive runs. The parent-change URI-config race (noted as a known limitation in `add-graph-query`'s open questions) reproduced ~50% of runs once the new tests + extended fixture seeding shifted timing; opportunistically fixed in this change by retiring the `ATTACH 'file:…?mode=ro'` URI dance entirely and enforcing read-only via `PRAGMA query_only = 1` set on the connection after TEMP VIEW DDL is applied. Per-connection state, no global engine reconfigure, no race. SQLite still returns `SQLITE_READONLY (8)` on writes, so the existing structured-error contract is preserved.
- [x] 4.3 `openspec validate add-graph-query-extended-views --strict` — passes.
- [ ] 4.4 Manual smoke (only if the `.mcp.json` is restored and the MCP server is reloadable): **MANUAL STEP for the user**. Call `describe_schema`, confirm `view_schema_version == 2` and the response contains `v_annotations` / `v_diagnostics` / `v_history` with their column lists. Then run one of the composability queries from the README's "Example tool calls".

## 5. Spec sync (archive)

- [ ] 5.1 `openspec archive add-graph-query-extended-views --yes`. Confirm the new requirements (extended view coverage) and the modified one (view layer's version-bump policy + initial value) land in `openspec/specs/storage/spec.md`, and the modified `describe_schema` scenario lands in `openspec/specs/mcp-tools/spec.md`.
