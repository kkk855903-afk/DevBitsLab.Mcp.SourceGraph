## Why

`add-graph-query` shipped a stable view layer over the per-scope SQLite tables: `v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`. That covers the majority of agent questions — *"where is X defined?"*, *"who calls X?"*, *"how many public types use Y?"* — and proves the layered tooling thesis: curated tools for the highway, `query_graph` for the long tail.

But three meaningful tables stayed off-contract:

| Table | Rows in this repo's index | What's only-reachable-via-curated-tool today |
|---|---:|---|
| `annotations` | 656 | `find_by_annotation` (find every method with `[HttpPost]`, every type with `[Obsolete]`, etc.) |
| `diagnostics` | 496 | `find_diagnostics` (Roslyn warnings/errors per symbol) |
| `symbol_history` | 2053 | `who_authored`, `recent_changes` (per-symbol git blame) |

The asymmetry hurts the moment an agent wants to *compose* across these surfaces. A real question like *"public types decorated with `[Obsolete]` that have outstanding CS-warnings AND were last touched > 6 months ago"* needs all three datasets in a single SQL statement — but today it requires three curated tool calls plus an in-head intersection. That's exactly the shape of question `query_graph` was designed to absorb.

The ergonomics matter beyond this one example: **annotations are how agents identify every web-framework endpoint, every DI binding, every test method**, and being unable to filter / aggregate / join over them via SQL means an entire class of questions falls back to per-tool plumbing or `Grep`. Diagnostics enable "blast radius before refactor" queries (*"types I touch that already have warnings"*). History enables churn-aware queries (*"hot files that lack tests"*).

The fix is the same shape as the original change: add three more views over the existing tables, register them with the view layer, surface them in `describe_schema`, write tests. No new Roslyn work, no new edge kinds, no schema migration — the data is already there.

## What Changes

- **New `v_annotations` view** over the `annotations` table. Columns: `scope, id, symbol_id, name, full_name, flavor, args_json, attribute_symbol_id`. The `name` column carries the short identifier (e.g. `HttpPost`); `full_name` carries the qualified form (e.g. `Microsoft.AspNetCore.Mvc.HttpPostAttribute`); `flavor` discriminates `csharp-attribute` vs. `xaml-attached-property` vs. future `ts-decorator` / `vue-directive` / `svelte-action`. Agents join `v_annotations.symbol_id` → `v_symbols.id` (within the same scope) to reach the decorated symbol.
- **New `v_diagnostics` view** over the `diagnostics` table. Columns: `scope, id, symbol_id, file_id, severity, severity_name, code, message, line, column_number`. The `severity` column stores the raw integer (Roslyn `DiagnosticSeverity`: 0=Hidden, 1=Info, 2=Warning, 3=Error); `severity_name` is the convenience text mapping (`hidden`/`info`/`warning`/`error`) so agents don't have to memorise the enum. `column_number` follows the same rename convention as `v_references` (avoids the SQL-reserved bare `column`). `symbol_id` is nullable when a diagnostic's source span doesn't fall inside any indexed declaration (e.g. unused-using on a using directive).
- **New `v_history` view** over the `symbol_history` table. Columns: `scope, symbol_id, last_commit_sha, last_author, last_authored_at, line_count, blamed_content_sha`. Sourced from per-scope `symbol_history` rows populated by the existing git-blame pipeline (disabled when `--no-history`). Agents reach the symbol's `fqn` / `kind` / `file` by joining `v_history.symbol_id` → `v_symbols.id`.
- **`Views.SchemaVersion` bumps from `1` to `2`.** Additive change (no existing column changes), but the version still bumps because clients that cache schema by version need a signal to re-introspect after a server upgrade. The policy in `Views`'s XML doc gets a small clarification: bump on **any** view set change (addition or removal), not only on column-incompatible changes.
- **`describe_schema` returns the new views automatically** — `Views.All` is the source of truth, and the new descriptors join the existing five. No code change in the tool body; the test asserting `view_schema_version == 1` updates to `== 2` and the views-count assertion updates from 5 to 8.
- **Tests cover the composability promise**: at least one new test runs a `JOIN v_annotations + v_symbols` query against the synthetic fixture (e.g. *"every symbol decorated with a fixture annotation"*), and one runs `JOIN v_diagnostics + v_symbols` (e.g. *"public types with at least one CS-warning"*). History is tested through schema shape only — populating `symbol_history` requires a real git blame pipeline that's out of scope for unit tests; integration coverage stays via the existing `who_authored` / `recent_changes` curated tools.

## Capabilities

### New Capabilities
<!-- None — this change extends two existing capabilities. -->

### Modified Capabilities

- `storage`: gains an ADDED requirement covering the three new views (`v_annotations`, `v_diagnostics`, `v_history`) and a MODIFIED requirement on the version-bump policy (extends "backwards-incompatible only" to "any view-set change"). The `Stable view layer over the underlying tables` requirement gains scenarios for the new views; its initial-version-is-`1` clause becomes "version is currently `2`".
- `mcp-tools`: gains an ADDED scenario on the existing `Schema introspection tool` requirement covering the new views (`describe_schema` returns 8 views including the three new ones) and the `view_schema_version == 2` value.

## Impact

- **Code (small)**: ~80 lines split across:
  - Storage: 3 new `CREATE TEMP VIEW` placeholders in `Views.sql`, 3 new per-scope SELECT templates in `Views.cs`'s `PerScopeBlockTemplates`, 3 new `ViewDescriptor` entries in `Views.All`, the `SchemaVersion` constant flips from 1 to 2.
  - No changes to `MultiScopeReadOnlyConnection.cs` — its substitution loop is keyed on `PerScopeBlockTemplates.Keys`, so the new views ride for free.
  - No changes to `GraphTools.cs` — `DescribeSchemaAsync` reads `Views.All` and the live `kind` vocabularies; both update automatically.
- **Spec**: One MODIFIED requirement (storage view layer — version policy + initial value), one ADDED requirement (storage — extended view coverage), one MODIFIED scenario on `mcp-tools` describe_schema.
- **Tests**: 4–6 new tests in `tests/.../ViewsTests.cs` and `tests/.../GraphQueryToolTests.cs` — view shape assertions, JOIN composability assertions, the bumped `view_schema_version`. Existing tests adjust the views-count and version assertions.
- **Performance**: zero. The substitution and connection setup are unchanged in shape; we just emit three more `UNION ALL` blocks (one per view per attached scope). For a 5-scope monorepo this adds ~15 SELECT branches to the connection-setup DDL — milliseconds at worst, hidden behind the existing ATTACH cost.
- **Backwards compatibility**: pure addition for SQL queries — every existing `query_graph` call against `v_symbols`/`v_edges`/`v_files`/`v_references`/`v_scopes` keeps working unchanged. The version bump from 1 → 2 is the only visible change for clients that key behavior off the version.
- **Out of scope (parking lot)**:
  - `v_bindings` / `v_event_handlers` — JSON-extracted convenience views over `v_edges.payload` for the XAML binding / event-handler payloads (~11 rows in this repo). Cheap to add later; usage from `usage.jsonl` will tell us whether agents reach for them.
  - `v_symbols_fts` — a view layer over `symbols_fts` is awkward because FTS5 uses the `MATCH` operator with non-standard syntax; agents already have `search_symbols` for fuzzy lookup, and a SQL view doesn't compose cleanly without inventing helpers. Park unless real demand emerges.
  - `v_embeddings` — vec0's KNN syntax (`SELECT … MATCH … ORDER BY distance LIMIT k`) doesn't fit a SQL view layer cleanly. The curated `semantic_search` tool stays the path for embedding queries.
  - Cross-scope `v_history` aggregation tools (e.g. "most-changed file across all scopes"). Agents can compose this via `query_graph` once `v_history` ships.
