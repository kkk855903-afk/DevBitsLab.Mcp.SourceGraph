## ADDED Requirements

### Requirement: Extended view coverage for annotations, diagnostics, and per-symbol git history
The storage layer SHALL extend the view layer (per the existing `Stable view layer over the underlying tables` requirement) with three additional views — `v_annotations`, `v_diagnostics`, `v_history` — covering the corresponding underlying tables (`annotations`, `diagnostics`, `symbol_history`). The new views SHALL follow the same per-scope `UNION ALL` pattern, the same `(scope, id)` composite-uniqueness convention, and the same `Views.PerScopeBlockTemplates` registration that the existing five views use.

The view shapes:

- `v_annotations(scope, id, symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)` — one row per indexed annotation (C# attribute, XAML attached property, future plugin-defined flavor). `flavor` discriminates the source language / framework. `args_json` is raw TEXT; agents who need substring search over arguments SHOULD prefer the `find_by_annotation` curated tool (FTS5-indexed) and use this view for compositional queries (joins / aggregations) instead.
- `v_diagnostics(scope, id, symbol_id, file_id, severity, severity_name, code, message, line, column_number)` — one row per Roslyn diagnostic. `severity` is the raw integer (matching `Microsoft.CodeAnalysis.DiagnosticSeverity`: 0=Hidden, 1=Info, 2=Warning, 3=Error). `severity_name` is the convenience text mapping (`hidden` / `info` / `warning` / `error`) computed via CASE. `symbol_id` is nullable (some diagnostics — e.g. unused-using directives — don't fall inside any indexed declaration). `column_number` renames the underlying `col` to avoid the SQL-reserved bare `column`.
- `v_history(scope, symbol_id, last_commit_sha, last_author, last_authored_at, line_count, blamed_content_sha)` — one row per symbol with cached git-blame metadata. `last_authored_at` is Unix-millis (matches `v_files.last_indexed_at` and `v_scopes.last_indexed_at`); agents needing ISO-8601 use `datetime(last_authored_at / 1000, 'unixepoch')`. Empty when the server runs with `--no-history` or against an environment without git on PATH.

The new views SHALL appear in `Views.All` (the curated descriptor list returned by `describe_schema`) with the same column-by-column documentation depth as the existing five.

#### Scenario: v_annotations exposes the indexed annotation set
- **GIVEN** an indexed scope containing one C# class decorated with `[Obsolete("use Foo")]` and one XAML element with `Grid.Row="2"`
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT name, flavor, args_json FROM v_annotations ORDER BY name"`
- **THEN** the result contains two rows: one with `name = 'Obsolete'`, `flavor = 'csharp-attribute'`, `args_json` containing the literal `"use Foo"`; one with `name = 'Grid.Row'`, `flavor = 'xaml-attached-property'`, `args_json` containing `2`

#### Scenario: v_annotations joins to v_symbols
- **WHEN** the agent invokes `query_graph` with
  ```sql
  SELECT s.fqn FROM v_annotations a
  JOIN v_symbols s ON s.id = a.symbol_id AND s.scope = a.scope
  WHERE a.name = 'Obsolete' AND s.is_type = 1
  ```
- **THEN** the result lists the FQN of every type-kind symbol decorated with `[Obsolete]`, joined via the `(scope, id)` composite key

#### Scenario: v_diagnostics maps severity integer to text
- **GIVEN** a scope where a public class symbol has one diagnostic stored at `severity = 2`
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT severity, severity_name FROM v_diagnostics WHERE code = 'CS0612'"`
- **THEN** the row reads `severity = 2`, `severity_name = 'warning'`

#### Scenario: v_diagnostics with nullable symbol_id
- **GIVEN** a diagnostic whose source span doesn't fall inside any indexed declaration (e.g. an unused-using on a using directive at file scope)
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT code, symbol_id FROM v_diagnostics WHERE symbol_id IS NULL"`
- **THEN** the result includes that row with `symbol_id = NULL`; an INNER JOIN against `v_symbols` would silently drop it (LEFT JOIN preserves it)

#### Scenario: v_history shape against an empty history table
- **GIVEN** a scope whose `symbol_history` table is empty (e.g. test fixture, `--no-history` mode)
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT scope, symbol_id, last_commit_sha, last_authored_at FROM v_history LIMIT 5"`
- **THEN** the call succeeds with `row_count = 0`; the response's `columns` array carries the four named columns with the documented types

#### Scenario: v_history joins to v_symbols when populated
- **GIVEN** a scope with at least one populated `symbol_history` row referencing a symbol whose `fqn` is `Sample.Foo.Bar`
- **WHEN** the agent invokes `query_graph` with
  ```sql
  SELECT s.fqn, h.last_author, h.last_authored_at FROM v_history h
  JOIN v_symbols s ON s.id = h.symbol_id AND s.scope = h.scope
  WHERE s.fqn = @fqn
  ```
- **THEN** the result row joins the history record to the symbol via `(scope, symbol_id)`, returning the author + Unix-millis timestamp

## MODIFIED Requirements

### Requirement: Stable view layer over the underlying tables
The storage layer SHALL ship a versioned set of read-only SQL views (`v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`, `v_annotations`, `v_diagnostics`, `v_history`) that present a denormalised, agent-facing contract over the underlying `symbols` / `edges` / `symbol_references` / `files` / `annotations` / `diagnostics` / `symbol_history` tables and the `_meta.db` scope-registry tables. The views SHALL be the public contract for any consumer that runs ad-hoc SQL via the MCP `query_graph` tool; the underlying tables remain implementation details and may evolve without bumping `Views.SchemaVersion`.

`Views.SchemaVersion` SHALL be a compile-time integer constant exposed on the storage assembly (current value `2`); it SHALL bump on **any view-set change** — addition, removal, column rename, or column-type change — so clients that cache `describe_schema` by version always re-introspect after a server upgrade. The original "backwards-incompatible only" wording is superseded: cache-aware clients need a signal even for additive changes (a new view added without a version bump would otherwise be invisible to a client still serving cached schema).

The view definitions SHALL live in an embedded SQL resource (`Views.sql`) with `{{SCOPE_UNION_BLOCK_<view>}}` placeholder tokens for the per-scope `UNION ALL` branches that the connection helper inlines at attach time. Each view (except `v_scopes`) SHALL include a `scope TEXT NOT NULL` column carrying the scope id, populated by the per-scope branch's `SELECT '<scope_id>' AS scope, …` clause; cross-scope joins SHALL use the composite `(scope, id)` tuple.

The view definitions of the original five views (`v_symbols` / `v_files` / `v_edges` / `v_references` / `v_scopes`) are unchanged from the prior version of this requirement (their column shapes do not change in this revision); only the view-set extension and the version-bump-policy clarification differ.

#### Scenario: View definitions execute against a single-scope DB
- **GIVEN** a temp SQLite DB containing one row each in `symbols`, `edges`, `files`, `symbol_references`, `annotations`, `diagnostics`, `symbol_history` (mimicking a single attached scope)
- **WHEN** the test inlines `Views.Sql` with one branch per per-view template and applies it as TEMP views
- **THEN** every view from the set executes without syntax error and returns the expected column shapes; `v_symbols.is_public` is `1` for the symbol whose `accessibility = 6`; `v_symbols.is_type` is `1` for the symbol whose `kind_name = 'class'`; `v_diagnostics.severity_name` reflects the documented CASE mapping; `v_history` returns the seeded blame row with all six columns

#### Scenario: View columns match describe_schema's response
- **WHEN** `describe_schema` returns the `views` array
- **THEN** every column listed in the `Views.All` descriptor for each of the eight views is present (and only those columns) when the view is queried via `SELECT * FROM <view> LIMIT 0`; the SQLite `name` and `type` for each column matches the descriptor

#### Scenario: View schema version bumps with the view-set change
- **WHEN** a consumer reads `Views.SchemaVersion` (or the `view_schema_version` field returned by `describe_schema`)
- **THEN** the value is `2` for this revision; was `1` for the prior revision (which shipped only the five core views); a future change adding or removing a view SHALL increment the version again

#### Scenario: Underlying table schema evolution does not bump view version
- **GIVEN** a hypothetical future storage change that adds an internal column `symbols.cyclomatic_complexity INTEGER` without exposing it via `v_symbols`
- **WHEN** that change ships
- **THEN** `Views.SchemaVersion` remains unchanged; agent queries against `v_symbols` return the same columns as before; `Schema.Version` (the storage on-disk version) bumps independently

#### Scenario: Cache-aware client re-introspects after additive view change
- **GIVEN** a client that called `describe_schema` against a server reporting `view_schema_version = 1` and cached the resulting schema
- **WHEN** the client reconnects to a server reporting `view_schema_version = 2` after this change ships
- **THEN** the client SHOULD discard its cached schema and re-call `describe_schema`; `v_annotations`, `v_diagnostics`, and `v_history` appear in the refreshed view list with their columns
