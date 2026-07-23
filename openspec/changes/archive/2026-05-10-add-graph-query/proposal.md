## Why

The curated tool surface (`find_references`, `list_callers`, `list_users_of_type`, …) answers the questions we predicted. It can't answer the questions we didn't predict — and the question space is open-ended:

- *How many **public** types use this type?* — needs a JOIN through `container_id` and an `accessibility = 1` filter; no tool aggregates references by enclosing-type.
- *Which types implement `IDisposable` but have no `Dispose` method?* — exists-vs-not-exists, no tool composes the two queries.
- *Which classes have grown beyond 50 members?* — a one-line aggregation, no tool returns counts grouped by container.
- *Which types are used **only** by internal callers?* — could be made `internal` safely; a per-symbol "all callers are internal" predicate.
- *Which scopes contain the most public surface area?* — group by scope, count public types.

Each of these is one or two SQL lines against data we already have ([Schema.cs:44-87](src/DevBitsLab.Mcp.SourceGraph.Storage/Schema.cs:44)). Today the only path is to either ship a new tool per question (doesn't scale, encourages over-fitting to predicted use cases) or fall back to `Grep` + `Read` (loses the structural advantage that's the whole pitch of this server).

The reframe: **stop carving the question space into tools.** Expose the graph and let agents compose queries. Keep curated tools as the highway for the common 80% — add a `query_graph` escape hatch for the long tail. This doesn't dilute the curated surface; it backfills it. Queries that show up repeatedly in the call log are the evidence base for which curated tools to add next, instead of guessing.

## What Changes

- **Stable view layer over the existing SQLite tables.** Adds a small set of read-only SQL views (`v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`) that present a denormalised, agent-friendly contract over `symbols` / `edges` / `refs` / `files` / scope-registry tables. The views become the public schema; the underlying tables remain free to evolve. The view layer ships a `view_schema_version` integer so consumers can detect breaking changes the same way `schema_version` works for the storage layer today.
- **A new `query_graph` MCP tool** that takes a read-only SQL `SELECT` (or `WITH`) statement plus optional `parameters` (named binding via `@name` placeholders) and an optional `scope` filter (string id, comma-separated list, or `"*"`; default `"*"` excluding `isolated` scopes). Returns tabular `{columns, rows}` `structuredContent` plus a markdown table for display. Logged into `.sourcegraph/usage.jsonl` like every other tool call, with the SQL text recorded so the call log is the evidence base for future curated tools.
- **A new `describe_schema` MCP tool** that returns the current view layer: each view name, its column list (with type, nullability, and a one-line description), the current set of `kind_name` values present in `v_symbols` and `v_edges` (so agents can enumerate the type/edge vocabulary without writing exploratory queries), and the `view_schema_version`. Replaces "agent guesses the schema" with "agent queries the schema first".
- **A read-only multi-scope attached connection.** `query_graph` opens an in-memory SQLite connection that ATTACHes every relevant scope DB in `mode=ro` and creates the views as `UNION ALL` over the per-scope tables (each row carries a `scope` column). `isolated` scopes are excluded from the default fan-out; queries can opt them in by naming the scope explicitly. Connection is per-call; no shared state.
- **Safety rails on `query_graph`.** Read-only mode at the connection level (`mode=ro`); reject any prepared statement that's not a single `SELECT`/`WITH` (no DDL/DML keyword scan needed — SQLite's `mode=ro` rejects writes, and the prepare-time leftover-SQL check enforces single-statement); a 5-second statement timeout (configurable via `--query-timeout-seconds` / env var); a 5000-row cap (configurable via `--query-row-limit`); rejects rows beyond the cap rather than truncating silently, so the agent can re-query with a tighter `WHERE`. No `ATTACH`, no `PRAGMA writes`, no temp tables that survive past the call.

## Capabilities

### New Capabilities
<!-- None — this change extends two existing capabilities. -->

### Modified Capabilities

- `mcp-tools`: gains two ADDED requirements — `query_graph` and `describe_schema` — plus an ADDED requirement covering the brand mark and `Use when:` line conventions for these new tools so they're indistinguishable from the rest of the curated surface in `tools/list`.
- `storage`: gains ADDED requirements covering the stable view layer (`v_*` views over the existing tables, with `view_schema_version`) and the read-only multi-scope attached connection helper that powers `query_graph`. The underlying tables and schema migration story are unchanged.

## Impact

- **Code (medium)**: ~300–500 lines of production code split across:
  - Storage: new `Views.cs` SQL embedded resource (~150 lines of `CREATE VIEW`), `MultiScopeReadOnlyConnection` helper (~80 lines), and a small extension on `IGraphStore` / `IScopeRegistry` for resolving the active scope set.
  - Server: two new MCP tool methods in `GraphTools.cs` (`QueryGraphAsync`, `DescribeSchemaAsync`) plus the structured-content shape and markdown rendering — patterned on existing tools like `SearchSymbols` and `Neighborhood`.
  - Safety: SQL prepare-and-validate helper (single-statement enforcement, row cap), reused for any future query-style tool.
- **Spec**: Two MODIFIED capabilities (`mcp-tools`, `storage`); 4–5 ADDED requirements total, no MODIFIED scenarios on existing requirements.
- **Tests**: New test class `GraphQueryToolTests.cs` exercising the curated examples from this proposal (`how many public types use T`, `IDisposable without Dispose`, etc.) plus safety-rail tests (write rejected, multi-statement rejected, timeout fires, row cap enforced). New `ViewLayerTests.cs` covering each view's shape and the multi-scope union behaviour against `tests/fixtures/MultiScope/`.
- **Performance**: Per-call connection setup (open in-memory DB + ATTACH N scope DBs + create views) is dominated by the ATTACH calls — measured at ~5ms per scope on a warm OS cache. For a 5-scope monorepo: ~25ms call overhead. The user's SQL execution is the dominant cost. Statement timeout (default 5s) bounds tail latency. View-layer overhead per query is "one extra layer of name resolution" — SQLite's query planner inlines views, so steady-state cost is the same as querying the underlying tables directly.
- **Backwards compatibility**: Pure addition. No existing tool changes shape. The view layer is additive to the SQLite schema (`Schema.Version` does NOT bump; views are created on connect by the new helper, not by `EnsureSchemaAsync`, so the on-disk DB format is unchanged). Single-solution users who never call `query_graph` see zero behavioural change; multi-scope users likewise.
- **Branding / `ServerInstructions`**: The MCP `initialize` response gains a sentence explaining the layered model: *"Prefer curated tools (find_references, list_callers, …) for common questions. For ad-hoc questions, call `describe_schema` then `query_graph` — read-only SQL over a stable view layer."* Suppressible by `--no-instructions` like the existing guidance.
- **Out of scope**:
  - A custom DSL or path-style query language (SQL is sufficient and LLMs are already fluent in it; alternative considered in `design.md`).
  - Saved / named queries, EXPLAIN QUERY PLAN exposure, query-result caching — all parking-lot for a follow-up if usage justifies them.
  - A web/UI surface for `query_graph`; this is an MCP-only feature.
  - Promoting common queries to curated tools — that's a separate, evidence-driven exercise after `usage.jsonl` accumulates real query patterns.
  - Mutation of any kind. `query_graph` is read-only forever; if write workflows emerge (saved queries, materialised projections), they'll be a separate change with explicit auth + audit semantics.
