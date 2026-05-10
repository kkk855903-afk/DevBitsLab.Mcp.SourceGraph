## Context

`add-graph-query` (now archived after manual smoke + tasks 9.3/10.1) shipped the view layer architecture: `Views.sql` scaffolding, `Views.PerScopeBlockTemplates` per-view per-scope SELECT formats, `Views.All` curated descriptors for `describe_schema`, `MultiScopeReadOnlyConnection.OpenAsync` doing the in-memory ATTACH + substitution dance. Everything is keyed on view name; adding a view is a four-step pattern (scaffold token, template, descriptor, schema-version bump) with no architectural changes.

The original change deliberately scoped to the five "core" views — symbols, files, edges, references, scopes — to land the architecture without bloating the contract. Three other tables in the per-scope DB carry data agents would benefit from joining against:

- **`annotations`** (656 rows in this repo's index): every C# attribute and every XAML attached property is here, with `args_json` carrying the constructor / property-init arguments. The curated `find_by_annotation` tool exposes "find every X with [Y]" but doesn't compose with anything.
- **`diagnostics`** (496 rows): every Roslyn warning / error / hidden / info, mapped to its source span. The curated `find_diagnostics` tool exposes per-symbol filtering but doesn't compose with attribute / inheritance / git-history dimensions.
- **`symbol_history`** (2053 rows): per-symbol `git blame` cache (last commit, last author, last-authored-at, line count, blamed-content SHA). The curated `who_authored` / `recent_changes` tools surface this per-symbol but don't compose with diagnostic / accessibility / member-set dimensions.

There's also a fourth surface — `symbols_fts` (FTS5 trigram index, 2725 entries) — but its query semantics use `MATCH` with non-standard syntax that doesn't compose cleanly with normal SQL. Park.

The data is fully indexed by the existing pipelines (no new Roslyn work, no new git-blame work). The job is purely contract: expose three more views, register them, test the JOIN-with-existing-views story.

## Goals / Non-Goals

**Goals:**

- `query_graph` against `v_annotations`, `v_diagnostics`, `v_history` returns the same data the curated tools (`find_by_annotation`, `find_diagnostics`, `who_authored` / `recent_changes`) return today, in the same shape that joins cleanly against `v_symbols` / `v_files` / `v_edges` / `v_references`.
- The convenience boolean / text-mapping pattern from `v_symbols.is_public` / `v_references.kind` carries to the new views: `v_diagnostics.severity_name` provides the text mapping (`hidden` / `info` / `warning` / `error`) so agents don't have to memorise the integer enum.
- `describe_schema` updates automatically — `Views.All` is the source of truth, so adding three descriptors there propagates to the tool's response with no code change in `DescribeSchemaAsync`.
- `Views.SchemaVersion` bumps from `1` to `2`; the version-bump policy is clarified to "any view-set change, additive or breaking" so clients caching schema by version always re-introspect after a server upgrade.
- The architecture from `add-graph-query` is unchanged — every assumption about per-scope ATTACH, double-quoted scope aliases, `{SCOPE_ID}` template substitution, and the `{{SCOPE_UNION_BLOCK_<view>}}` scaffolding tokens carries forward.
- Tests demonstrate composability: at least one test joins `v_annotations` with `v_symbols` and at least one joins `v_diagnostics` with `v_symbols`, both exercising the (scope, id) tuple pattern.

**Non-Goals:**

- `v_bindings` / `v_event_handlers` — convenience views with `json_extract` over `v_edges.payload`. Cheap to add later; deferred until usage proves them out (only 11 payload-bearing edges in this repo, mostly XAML binding).
- `v_symbols_fts` — FTS5 syntax (`SELECT … MATCH …`) doesn't compose cleanly with normal `SELECT`s; the curated `search_symbols` tool stays the path for fuzzy name search.
- `v_embeddings` — vec0's KNN syntax doesn't fit. The curated `semantic_search` stays.
- Annotations FTS exposure — `annotations_fts` over `args_json` is reachable today via `find_by_annotation`'s `argValue` parameter; exposing it as a view requires the same FTS-syntax compromise as `v_symbols_fts`. Skip.
- Promotion of common cross-view queries to curated tools — that's the evidence-driven follow-up after `usage.jsonl` accumulates real query patterns. This change just makes the queries possible.
- Schema migration of any kind — no on-disk changes; `Schema.Version` does not bump.

## Decisions

### Decision 1 — Three new views, not five (defer `v_bindings` / `v_event_handlers`)

Ship `v_annotations`, `v_diagnostics`, `v_history`. Defer the JSON-extracted convenience views.

**Rationale**: the three deferred targets cover ~656 + 496 + 2053 = ~3200 rows of off-contract data agents demonstrably want. The bindings / handlers views cover ~11 rows (a single XAML fixture) and are a `json_extract` away from `v_edges` for any agent that needs them. The smaller change is easier to review, easier to roll back, and lands the high-value coverage without speculation. If `usage.jsonl` shows agents writing repeated `json_extract(payload, '$.path')` queries against `v_edges`, that's the evidence to add `v_bindings` next.

**Alternatives considered**:

- **Ship all five at once.** More complete but speculative on the bindings/handlers value; harder to roll back if a convenience view's column shape proves wrong.
- **Ship only `v_annotations`** (most-requested by agents that build for web / DI frameworks). Cleaner scope but underservices the diagnostic + history composability story, which has the same shape and same near-zero implementation cost.

### Decision 2 — `severity_name` text mapping on `v_diagnostics`

`v_diagnostics` exposes BOTH `severity` (raw integer matching `Microsoft.CodeAnalysis.DiagnosticSeverity`: 0/1/2/3) AND `severity_name` (text: `hidden`/`info`/`warning`/`error`). Same convenience pattern as `v_symbols.is_public` (raw `accessibility` + computed `is_public`) and `v_references.kind` (raw integer mapped to text via CASE).

**Rationale**: agents writing `WHERE severity_name = 'warning'` is more readable than `WHERE severity = 2`, and self-documents what the integer means. The raw `severity` stays for ordering / range queries (`WHERE severity >= 2` for "warnings and errors"). The CASE expression is cheap (compiled once per query) and matches the existing pattern from `v_references`.

**Alternatives considered**:

- **TEXT-only `severity`** (rename int to text). Loses the ordering use case (`severity >= 2` in SQL doesn't work over text).
- **No mapping; let agents discover the enum.** Forces every agent to read `describe_schema` carefully, then construct CASE expressions in their own SQL. Friction with no upside.

### Decision 3 — `column_number` rename in `v_diagnostics`

`v_diagnostics` exposes the `col` column from the underlying table as `column_number`. Same reason as `v_references` ([add-graph-query](../../add-graph-query/design.md) Decision 2): bare `column` is SQL-reserved and forces agents to double-quote it.

### Decision 4 — `Views.SchemaVersion` bumps to `2`; policy clarification

Bump even though the change is additive. Update `Views.SchemaVersion`'s XML doc to read: *"Bumps on any view-set change — addition, removal, column rename, or column-type change — so clients that cache `describe_schema` by version always re-introspect after a server upgrade."*

**Rationale**: a client that cached "version 1 → 5 views" after the parent change ships and re-connects after this change ships should see "version is 2 → 8 views." Without bumping the version, the client could legitimately stick with its cached schema and miss `v_annotations` entirely until cache invalidation. The original "breaking-change-only" policy was too narrow for the actual contract.

**Alternatives considered**:

- **Don't bump (per the original policy).** Honest to the spec wording but misleads cache-aware clients. Rejected.
- **Two-version system** (`SchemaVersion` for breaking, `AdditiveVersion` for additions). Cleaner abstractly but doubles the surface for an inflection clients may never need.
- **Time-based ETag** (`Views.LastChangedAt`). Useful in HTTP-style caches; overkill here where the version is already an integer agents can compare.

### Decision 5 — Test pattern: composability, not coverage of every view

Each of the three new views gets at least one shape-assertion test (column names match descriptor, simple SELECT returns expected rows). On top of that, two composability tests exercise the cross-view JOIN story:

- `QueryGraph_annotationsJoinSymbols_findsDecoratedTypes` — seed a fixture symbol with one annotation, query "all symbols with annotation X", JOIN through `v_annotations.symbol_id` → `v_symbols.id` (within scope), assert the right symbol surfaces.
- `QueryGraph_diagnosticsJoinSymbols_findsSymbolsWithWarnings` — seed a fixture diagnostic at severity=2 against a known symbol, query "every public type that has at least one warning", JOIN `v_diagnostics` + `v_symbols`, assert the right type surfaces.

History composability (`v_history` joined with `v_symbols`) is asserted on the **shape** only — populating `symbol_history` requires a real git-blame run, which is out of scope for unit tests. Integration coverage stays via the existing `who_authored` / `recent_changes` tools.

**Rationale**: the value of these views is composability, not raw access. A test suite that only checks `SELECT * FROM v_annotations LIMIT 5` proves the view exists but doesn't prove the agent-visible promise. The JOIN tests prove the (scope, id) tuple convention works across the new views the same way it works for the existing five.

## Risks / Trade-offs

- **[Risk] `symbol_history` is empty in many test environments** (CI without git, fresh clones without blame populated, `--no-history` mode). → **Mitigation**: don't write tests that require `v_history` to have rows. Tests assert the view's shape (columns from `describe_schema`) and that empty queries against it return `RowCount = 0` cleanly. Real coverage of populated history rides on the existing `who_authored` tool's integration tests.

- **[Risk] `v_diagnostics.symbol_id` is nullable** (some diagnostics — like unused-using on a using directive — don't fall inside any indexed declaration). Agents that JOIN `v_diagnostics.symbol_id` to `v_symbols.id` with INNER JOIN will silently drop those rows. → **Mitigation**: document the nullability in the `ViewColumn.Description` so `describe_schema`'s output flags it. LEFT JOIN is the agent's responsibility for queries that need the un-attributed diagnostics; INNER JOIN gives a "scoped to declared symbols" filter for free.

- **[Risk] `v_annotations.args_json` is raw TEXT** (no JSON-extracted convenience columns). Agents who want to filter on `argValue = '/api/v2/'` need `WHERE args_json LIKE '%/api/v2/%'` (slow over many rows) or `WHERE json_extract(args_json, '$[0]') = '/api/v2/'` (depends on the args' structure). → **Mitigation**: documented in the `ViewColumn.Description`. The curated `find_by_annotation` tool stays the optimised path for `argValue` substring search (it uses the existing `annotations_fts` trigram index). `query_graph` against `v_annotations` is the right tool for *compositional* queries, not optimised free-text search.

- **[Risk] Bumping `SchemaVersion` to 2 breaks any test that hardcoded `view_schema_version == 1`.** → **Mitigation**: update the affected assertions as part of this change. Identified test: `DescribeSchema_returnsAllViewsAndLiveKinds` in `GraphQueryToolTests.cs` — flips `1` → `2` and `5` → `8` for the views count.

- **[Trade-off] Agents now have to know to use `v_diagnostics.severity_name` vs. `v_diagnostics.severity`** (and which is more idiomatic). → **Accepted**: the descriptor for both columns is explicit; agents reading `describe_schema` see the mapping immediately. Same trade-off we accepted for `v_symbols.is_public` + `accessibility` and `v_references.kind` + the integer enum it maps from.

- **[Trade-off] `v_history.symbol_id` JOIN to `v_symbols.id` can drop rows** when a `symbol_history` entry's symbol was renamed / deleted between blame capture and view query. → **Accepted**: the existing curated tools have the same property; this isn't a new failure mode introduced by the view layer.

## Migration Plan

This is purely additive — no data migration, no breaking changes to existing tools. Land in three small phases:

1. **Storage: descriptors + templates + scaffolding.** Add the three `{{SCOPE_UNION_BLOCK_<view>}}` placeholder tokens to `Views.sql`. Add the three SELECT templates to `Views.PerScopeBlockTemplates`. Add the three `ViewDescriptor` entries to `Views.All`. Bump `Views.SchemaVersion` from `1` to `2`. Update the XML doc comment for `SchemaVersion` to reflect the policy clarification. CI green; tests adjust the version + view-count assertions in `GraphQueryToolTests`.

2. **Tests: shape + composability.** Add `ViewsTests` cases asserting the three new views project the expected columns from seeded rows in a single-scope DB. Add the two composability tests in `GraphQueryToolTests` (`QueryGraph_annotationsJoinSymbols_findsDecoratedTypes` and `QueryGraph_diagnosticsJoinSymbols_findsSymbolsWithWarnings`). The fixture's `SeedScopeWithDataAsync` helper extends to also seed one annotation + one warning per scope.

3. **Documentation.** Update README's "Ad-hoc queries (escape hatch)" subsection: add the three new view names to the table; update the worked-example block with a composability example (e.g. *"public types with at least one CS-warning AND no test edge"*). Update CLAUDE.md's view-list to mention the three new views.

**Rollback strategy**: revert in reverse order. Each phase's commit is independent. Removing `v_annotations` / `v_diagnostics` / `v_history` from `Views.cs` removes them from `describe_schema` and from any queries that use them, but doesn't break queries against the original five views. The version flip from 2 → 1 is cosmetic for clients that didn't cache.

## Open Questions

- **Should `v_history.last_authored_at` ship as Unix-millis (raw INT) or ISO-8601 TEXT?** Existing curated tools render it as ISO; the underlying column is INT. Lean toward raw INT (matches `v_files.last_indexed_at` and `v_scopes.last_indexed_at`), document the unit clearly, let agents `datetime(last_authored_at / 1000, 'unixepoch')` if they want.

- **`v_diagnostics` aggregation convenience**: should we ship a `v_diagnostic_counts` view that pre-aggregates `(scope, file_id, severity_name) → count`? Speculative — agents can compose it in two lines of SQL. Defer until evidence.

- **Should `find_by_annotation`, `find_diagnostics`, `who_authored`, `recent_changes` get rewritten to call `query_graph` internally?** Tempting (single source of truth), but the curated tools have richer output shapes (markdown rendering, scope fan-out, audience metadata) that don't degrade gracefully into a generic SQL response. Keep them parallel for now; convergence is an explicit non-goal of `add-graph-query` and remains so here.
