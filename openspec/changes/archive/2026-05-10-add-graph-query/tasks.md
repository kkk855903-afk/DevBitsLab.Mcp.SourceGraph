## 1. Storage: view definitions

- [x] 1.1 Add `src/DevBitsLab.Mcp.SourceGraph.Storage/Views.sql` (embedded resource) containing the parametrised `CREATE TEMP VIEW v_symbols / v_files / v_edges / v_references / v_scopes` text. Use placeholder tokens (e.g. `{{SCOPE_UNION_BLOCK}}`) for the per-scope `UNION ALL` branches that get inlined by the connection helper. XML documentation on each view header (`-- v_symbols: …`) explains the contract for any developer reading the file.
- [x] 1.2 Add `src/DevBitsLab.Mcp.SourceGraph.Storage/Views.cs` with `public static class Views` (made public so the Server assembly's `describe_schema` can surface `Views.All`/`SchemaVersion` without `InternalsVisibleTo`) carrying `SchemaVersion = 1`, the `Sql` scaffolding loaded from the embedded resource, a `PerScopeBlockTemplates` dictionary for the per-view per-scope SELECT templates, the `ViewColumn { Name, SqliteType, Nullable, Description }` record, and a hand-curated `IReadOnlyList<ViewDescriptor> All` for `describe_schema`'s response.
- [x] 1.3 Unit test `tests/.../ViewsTests.cs`: load `Views.sql`, substitute a single-scope branch, execute against a fresh in-memory SQLite DB seeded with one symbol / one edge / one file row, assert each view returns the expected columns and rows.

## 2. Storage: multi-scope read-only attached connection

- [x] 2.1 Add `src/DevBitsLab.Mcp.SourceGraph.Storage/MultiScopeReadOnlyConnection.cs` with `public static Task<SqliteConnection> OpenAsync(IScopeRegistry registry, string scopeFilter, int maxAttached = 64, CancellationToken ct = default)`. Behaviour:
  - Open `:memory:` connection in read-only mode (or read-write on the in-memory main; the safety bound is the per-ATTACH `mode=ro` on each scope DB plus the prepare-time check, since SQLite doesn't open `:memory:` in pure read-only mode).
  - Resolve `scopeFilter` against the registry: `"*"` → all non-isolated; comma-list → those scopes (isolated allowed if explicit).
  - Raise `sqlite3_limit(SQLITE_LIMIT_ATTACHED, maxAttached)` to `64` (or pass-through value).
  - ATTACH `_meta.db` AS `meta` (always).
  - ATTACH each per-scope DB AS `<scope_id>` with `?mode=ro` on the URI.
  - Apply `Views.Sql` with the `{{SCOPE_UNION_BLOCK}}` token expanded to one `SELECT 'scope_id' AS scope, … FROM <scope_id>.<table> UNION ALL` per attached scope.
  - On overflow (resolved scopes > `maxAttached`): throw `ScopeAttachLimitExceededException` with the resolved scope-id list and the configured ceiling.
- [x] 2.2 Unit tests in `tests/.../MultiScopeReadOnlyConnectionTests.cs`:
  - Single-scope filter resolves to one ATTACH and views show one branch.
  - `"*"` filter excludes `isolated` scopes by default; explicit naming includes them.
  - Comma-list filter with mixed isolation respects each scope's explicit naming.
  - Overflow case throws the expected exception with the right payload.
  - Connection rejects `INSERT INTO frontend.symbols …` with `SQLITE_READONLY`.

## 3. Server: `describe_schema` tool

- [x] 3.1 Added `DescribeSchemaAsync` to `GraphTools.cs` (line 2636). Returns `Views.All` with live `symbol_kinds`/`edge_kinds` populated from `SELECT DISTINCT kind` against the multi-scope connection. Top-level `view_schema_version` from `Views.SchemaVersion`.
- [x] 3.2 `OutputSchemaType = typeof(DescribeSchemaResult)` declared. New DTO at `src/.../Tools/Output/DescribeSchemaResult.cs` registered in `ToolOutputJsonContext`.
- [x] 3.3 `Use when:` line on the description; brand mark applied at runtime by `ToolIdentityFormatter.ApplyBrandMark` (the existing project convention — `[Description]` does NOT include literal `🌿 `; the post-build pass adds it dynamically and respects `LeafFormatter.Suppressed`).
- [x] 3.4 Smoke test in `tests/.../GraphQueryToolTests.cs` covers all 5 views, `view_schema_version == 1`, and the `symbol_kinds`/`edge_kinds` shape.

## 4. Server: `query_graph` tool

- [x] 4.1 Added `QueryGraphAsync` to `GraphTools.cs` (line 2759). Parameters take `IReadOnlyDictionary<string, JsonElement>` (matches MCP wire format); a `JsonElementToSqliteValue` helper unwraps each element to the correct CLR primitive. Multi-statement detection uses a hand-rolled lexer (`SingleStatementCheck`) that handles single-quoted strings, double-quoted identifiers, `--` line comments, and `/* */` block comments. Trailing semicolons + comments are tolerated; semicolons mid-string are not treated as statement boundaries.
- [x] 4.2 `OutputSchemaType = typeof(QueryGraphResult)` declared. New DTO at `src/.../Tools/Output/QueryGraphResult.cs` registered in `ToolOutputJsonContext`. `Rows` is `IReadOnlyList<JsonElement>` so heterogeneous row values (string/long/double/null/byte[]) ride the source-gen envelope while per-row values fall through to the reflection serializer (cold path acceptable).
- [x] 4.3 `Use when:` line on the description; brand mark applied at runtime by `ToolIdentityFormatter.ApplyBrandMark`.
- [x] 4.4 Structured errors via `BuildQueryGraphErrorResult` helper. Notable nuance: writes against TEMP views return `SQLITE_ERROR (1)` ("cannot modify ... because it is a view") instead of `SQLITE_READONLY (8)`; the `IsReadOnlyError` helper catches both and classifies as `read_only`, satisfying the spec's "every write attempt → read_only" intent.

## 5. CLI configuration

- [x] 5.1 Add `--query-timeout-seconds <int>` (default `5`, env `SOURCEGRAPH_QUERY_TIMEOUT_SECONDS`) and `--query-row-limit <int>` (default `5000`, env `SOURCEGRAPH_QUERY_ROW_LIMIT`) to the `serve` subcommand. Parsed in `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/CommandLine.cs` via the existing switch with a new `RequirePositiveInt` helper that rejects non-positive / unparseable values.
- [x] 5.2 New `GraphQueryOptions(int TimeoutSeconds, int RowLimit)` record at `src/DevBitsLab.Mcp.SourceGraph.Server/GraphQueryOptions.cs`, with a static `Resolve(timeoutFlag, rowLimitFlag)` for CLI → env → default precedence; registered as a singleton in `Program.cs` so `QueryGraphAsync` can take it via DI. New tests in `tests/.../CommandLineQueryFlagsTests.cs` cover the parser + the resolution precedence.
- [x] 5.3 `--help` text updated under "Common flags" for both flags; documents the env-var equivalents.

## 6. ServerInstructions guidance

- [x] 6.1 Updated `ServerInstructions.Template` with a new paragraph between the curated-tools recommendation and the `usage_stats` directive. Mentions the ad-hoc shapes (aggregations / joins / "how many public types use X") plus the explicit `describe_schema` → `query_graph` workflow. Suppressed via the existing `--no-instructions` / `SOURCEGRAPH_NO_INSTRUCTIONS` knobs (no code change needed — `Program.cs` already short-circuits publishing when suppression fires).
- [x] 6.2 New test class `tests/.../ServerInstructionsQueryGraphTests.cs` (6 tests) verifies: (a) the template contains the new sentence + `v_symbols`/`stable contract`; (b) `ResolvePublished` keeps the new sentence in both leaf-suppressed and unsuppressed modes; (c) `ShouldSuppress` honours both flag and env-var inputs.

## 7. End-to-end tests

- [x] 7.1 In `tests/.../GraphQueryToolTests.cs`, add a "real-question" test that runs the worked example from the proposal:
  ```sql
  SELECT COUNT(DISTINCT t.id) AS public_user_count
  FROM v_edges e
  JOIN v_symbols m ON m.id = e.src AND m.scope = e.scope
  JOIN v_symbols t ON t.id = m.container_id AND t.scope = m.scope
  WHERE e.dst = (SELECT id FROM v_symbols WHERE fqn = @fqn LIMIT 1)
    AND e.kind = 'uses-type'
    AND t.is_public = 1
    AND t.is_type = 1;
  ```
  with `parameters = { "@fqn": "Sample.Domain.Calculator" }`. Assert the count matches the expected value from the fixture.
- [x] 7.2 Safety-rail tests:
  - `INSERT INTO v_symbols VALUES (...)` returns the `read_only` structured error.
  - `SELECT 1; ATTACH 'evil.db' AS evil;` returns the `multi_statement` structured error.
  - A query crafted to be slow (`WITH RECURSIVE …` with a high bound, OR a join without indexes) hits the timeout and returns the `timeout` structured error in < 6 seconds.
  - A query that returns `row_cap + 1000` rows surfaces `truncated: true` and exactly `row_cap` rows in the result.
- [x] 7.3 Multi-scope tests covered by `QueryGraph_excludesIsolatedScopeFromDefaultStar`, `QueryGraph_explicitScope_includesIsolatedScope`, and `QueryGraph_commaListScope_includesEachNamedScope` in `GraphQueryToolTests.cs`. Used the synthetic in-test fixture (frontend / backend / vendor-isolated) rather than `tests/fixtures/MultiScope/` because the existing fixtures don't have a `uses-type` graph shape suitable for the worked example, and the synthetic fixture is hermetic + faster.
  - Default `scope='*'` excludes the `vendor` (isolated) scope's rows from `v_symbols`.
  - `scope='vendor'` returns vendor rows.
  - `scope='*,vendor'` returns the union of default + explicit isolated.

## 8. Documentation

- [x] 8.1 README updated: new "Ad-hoc queries (escape hatch)" subsection inside MCP tools (right after Operations), worked example added to "Example tool calls" block, and two new rows in "Resource limits and tunables" for the timeout / row cap flags + env vars. Mentions `view_schema_version` in the new subsection.
- [x] 8.2 CLAUDE.md: added a paragraph after the existing "usage.jsonl" block describing the view layer + the two new tools + their tunables.

## 9. Verification

- [x] 9.1 `dotnet build` clean — 0 warnings, 0 errors.
- [x] 9.2 `dotnet test` — 477/477 main + 8/8 integration = 485/485 pass.
- [ ] 9.3 End-to-end against `sourcegraph-mcp serve` from a real Claude Code session — **MANUAL STEP for the user**. After restoring `.mcp.json` (see this conversation) and reloading the MCP server, call `describe_schema` then run the worked-example SQL against a real symbol; confirm the count matches manual inspection. The unit-test fixture covers the same shape with synthetic data (`QueryGraph_workedExample_publicTypesUsingType`).
- [x] 9.4 `openspec validate add-graph-query --strict` — passes.

## 10. Spec sync (archive)

- [ ] 10.1 `openspec archive add-graph-query --yes`. Confirm the ADDED requirements (query_graph tool, describe_schema tool, view layer, multi-scope attached connection) land in `openspec/specs/mcp-tools/spec.md` and `openspec/specs/storage/spec.md` cleanly.
