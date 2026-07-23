## Context

The curated tool surface today is a fixed set of question shapes hand-coded as MCP tools (`find_references`, `list_callers`, `list_users_of_type`, `find_implementations`, `module_summary`, `impact_of_change`, etc., enumerated in `openspec/specs/mcp-tools/spec.md`). Each tool resolves a name → symbol id, runs a parameterised SQL query against `IGraphStore`, and renders the result.

This works well for the questions we predicted. It fails for questions we didn't:

- **"How many public types use this type?"** — requires aggregating reference edges back through the source member's `container_id`, then filtering by `accessibility = 1` and `kind_name IN ('class', 'interface', …)`. The data is all there; no tool composes it.
- **"Which classes implement IDisposable but have no Dispose method?"** — requires the cross-product of two queries (implements-edge + member-name absence). No tool does cross-products.
- **"Which types are used only by internal callers?"** — requires "all callers' enclosing types are non-public" as a per-symbol predicate. No tool does universal quantification.

The first instinct is to ship a new tool per question. That has been the pattern — the live spec lists 25+ requirements under `mcp-tools`, and the call log shows agents asking questions that none of them quite cover. Shipping `count_public_dependents`, `find_unimplemented_interfaces`, `find_internal_only_types`, etc. one by one doesn't scale: each one is ~200 lines of code, each one is over-fit to its predicted shape, and the long tail of questions just keeps growing.

The reframe (from the discovery thread): **the data model is the API.** Stop predicting questions and shipping tools; expose the graph and let agents compose queries.

The existing data is essentially complete for this purpose:

- `symbols(id, name, fqn, kind_name, accessibility, modifiers, container_id, file_id, start_line, …)` — every type and member, with visibility and containment.
- `edges(src, dst, kind_name, payload)` — every reference of every kind: `calls`, `uses-type`, `inherits`, `implements`, `instantiates`, `throws`, `tests`, `binds-path`, `handles-event`, …
- `refs(symbol_id, file_id, line, column, kind)` — every textual reference site.
- `files(id, path, sha, last_indexed_at, is_generated)` — file metadata.
- The scope registry in `_meta.db` — per-scope status, root, isolated flag, last-indexed time.

What's missing is (a) a **stable contract** over those tables so the schema can evolve without breaking agents, and (b) a **safe execution surface** that lets agents run arbitrary SQL without taking down the server, exfiltrating data through writes, or wedging it on a runaway query.

## Goals / Non-Goals

**Goals:**

- Agents can answer arbitrary structured questions about the indexed graph in one tool call by writing read-only SQL.
- Schema introspection via `describe_schema` is sufficient for an agent that has never seen this server before to construct correct queries — no reading the source tree, no out-of-band documentation. The tool's output is the contract.
- The view layer hides the underlying tables. Renames, column additions, column removals, and even table restructures land without breaking agent queries that targeted views.
- The default behaviour for `query_graph` is "ask across everything I care about" — all non-isolated scopes, no thinking required. Scope selection is opt-in narrowing.
- Safety is enforced at the connection level (`mode=ro`), at the prepare level (single-statement only), and at execution (timeout + row cap). A malicious or buggy SQL string can degrade one call but cannot wedge the server, mutate the DB, or read state outside the views.
- Every `query_graph` call is logged to `.sourcegraph/usage.jsonl` with the SQL text. The log becomes the evidence base for which queries deserve to be promoted into curated Layer-1 tools.

**Non-Goals:**

- Building a custom path-style or graph DSL. SQL is sufficient, LLMs are already fluent in it, and shipping a DSL means owning a parser, a planner, and a docs surface forever. Considered and rejected (alternatives section).
- Natural-language → SQL translation in the server. The agent already speaks SQL; running an LLM inside the MCP server to translate user prose adds latency, cost, and a second model dependency for no benefit.
- Saved queries / named queries / materialised views. A useful future extension if the call log shows the same query shape recurring; out of scope here.
- Mutation. `query_graph` is read-only at the SQLite mode level. Any future write surface (saved queries, agent-authored views) is a separate change with explicit auth.
- Promoting common queries into curated tools. That's an evidence-driven follow-up; this change builds the evidence-gathering machinery (the call log entries), not the promotion.
- A web UI, REST endpoint, or non-MCP surface for the query tool. MCP-only.
- Cross-server federation (querying multiple `sourcegraph-mcp` instances at once). Out of scope.

## Decisions

### Decision 1 — Read-only SQL over a custom DSL

The query language is **SQLite read-only SQL**. Agents write `SELECT … FROM v_symbols JOIN v_edges …` directly.

**Rationale**: LLMs are extremely good at SQLite SQL out of the box; SQL covers the full expressiveness spectrum (joins, aggregations, window functions, recursive CTEs); SQLite's planner is mature and fast on indexed data; implementation cost is essentially "expose a connection". The cost is that the schema becomes a public contract — addressed by the view layer (Decision 2).

**Alternatives considered**:

- **Custom DSL** (path-style, e.g. `Type(name="Foo") <-uses- Type(public=true)`). Cleaner abstraction, schema-stable by construction, but: huge to build (parser + planner + docs + agent training), would either be less expressive than SQL or would essentially BE SQL with extra syntax, and LLMs would need to learn it. Not worth the cost.
- **Composable primitives** (orthogonal `filter`, `traverse`, `aggregate`, `project` tool calls). More discoverable, but requires multiple round-trips per question, which negates the latency advantage of structural tools over `Grep`. Awkward for any question that doesn't decompose cleanly into the primitives.
- **Natural-language → SQL** in the server. Zero learning curve for the agent, but adds an LLM dependency to the MCP server, brittle on edge cases, and the agent is already an LLM that can write SQL.

### Decision 2 — Stable view layer over the underlying tables

`query_graph` exposes a small set of read-only SQL views as the public contract. The underlying tables (`symbols`, `edges`, `refs`, `files`, scope registry tables) remain private and free to evolve.

The initial view set:

```sql
CREATE TEMP VIEW v_symbols AS
  SELECT
    'frontend' AS scope,                    -- per-scope union (one branch per ATTACH)
    s.id              AS id,                -- per-scope id; cross-scope joins use (scope, id)
    s.name            AS name,
    s.fqn             AS fqn,
    s.kind_name       AS kind,              -- 'class' | 'interface' | 'method' | 'field' | …
    s.accessibility   AS accessibility,     -- Roslyn enum: 0=NotApplicable, 1=Private, 2=ProtectedAndInternal, 3=Protected, 4=Internal, 5=ProtectedOrInternal, 6=Public
    (s.accessibility = 6) AS is_public,     -- convenience boolean
    (s.kind_name IN ('class','interface','struct','record','enum','delegate')) AS is_type,
    s.modifiers       AS modifiers,
    s.xml_summary     AS xml_summary,
    s.container_id    AS container_id,
    s.file_id         AS file_id,
    s.start_line      AS start_line,
    s.start_column    AS start_column
  FROM frontend.symbols s
  UNION ALL
  -- one SELECT per attached scope DB, generated at connection-setup time
  ;

CREATE TEMP VIEW v_files AS
  SELECT 'frontend' AS scope, f.id, f.path, f.sha, f.last_indexed_at, f.is_generated
  FROM frontend.files f
  UNION ALL …;

CREATE TEMP VIEW v_edges AS
  SELECT 'frontend' AS scope, e.src, e.dst, e.kind_name AS kind, e.payload
  FROM frontend.edges e
  UNION ALL …;

CREATE TEMP VIEW v_references AS
  SELECT 'frontend' AS scope, r.symbol_id, r.file_id, r.line,
         r.col AS column_number,                  -- renamed: bare `column` is SQL-reserved
         (CASE r.kind WHEN 0 THEN 'def' WHEN 1 THEN 'ref' WHEN 2 THEN 'call'
                      WHEN 3 THEN 'impl' WHEN 4 THEN 'inherit'
                      WHEN 5 THEN 'read' WHEN 6 THEN 'write' END) AS kind  -- mapped from Core.ReferenceKind enum int
  FROM frontend.refs r
  UNION ALL …;

CREATE TEMP VIEW v_scopes AS
  SELECT id AS scope, name, root, isolated, status, last_indexed_at
  FROM meta.scopes;
```

**Rationale**:

- The `v_` prefix makes the contract boundary explicit: anything `v_*` is stable; raw table names are not.
- A row's `(scope, id)` uniquely identifies a symbol; single-scope queries see a constant `scope` column and can use bare `id` joins.
- `is_public` and `is_type` are pre-computed because they're the most common filters in the questions this change targets, and forcing every agent to remember `accessibility = 6` is a footgun (Roslyn's enum has six accessibility values, easy to miscompare).
- Snake-case column names match the existing `structuredContent` convention from the rest of the tool surface.
- Views are `TEMP` (per-connection) — they're created at connection setup, live for the duration of the call, vanish when the connection closes. No on-disk DDL, no schema migration, no concurrency story.

**Alternatives considered**:

- **Expose raw tables.** Faster to ship; but every internal refactor becomes a breaking change for agents, and the column names (`kind_name`, `start_line`) leak Roslyn-isms.
- **No `v_` prefix; rename underlying tables.** Cleaner agent-facing names but a larger migration in the storage layer. Defer.
- **Pre-join convenience views** (e.g. `v_uses_type(user_member_id, user_type_id, used_type_id)`). Useful for the "how many public types use T" case but speculative — we don't know which joins are common until we see usage. Park for a follow-up; ship the base views now.

### Decision 3 — Multi-scope by default via in-memory ATTACH

Every `query_graph` call opens a fresh in-memory SQLite connection, ATTACHes each relevant scope DB read-only, and creates the temp views as `UNION ALL` over the per-scope tables. Default scope set is "all non-isolated scopes from the registry"; the `scope` parameter narrows it.

```
                    in-memory connection (per call)
                    ┌──────────────────────────────────┐
                    │  TEMP VIEW v_symbols  ◀── UNION ─┼── ATTACH 'scopes/frontend.db' AS frontend
                    │  TEMP VIEW v_edges    ◀── UNION ─┼── ATTACH 'scopes/backend.db'  AS backend
                    │  TEMP VIEW v_files    ◀── UNION ─┼── ATTACH 'scopes/vendor.db'   AS vendor   (only if scope='*'+'vendor' or scope='vendor')
                    │  TEMP VIEW v_scopes   ◀─────────┐│
                    │                                 ││
                    └─────────────────────────────────┼┴─ ATTACH '_meta.db' AS meta
                                                      │
                                          (every connection always attaches meta for v_scopes)
```

**Rationale (informed by user clarification)**: scopes were introduced as an indexing/space optimization, not a logical isolation boundary. Agents shouldn't have to think about which scope a symbol lives in to ask a question. Default fan-out across all non-isolated scopes preserves the original `isolated`-as-opt-out semantic from `add-scoping` (vendored / generated code stays out of default queries) without exposing the sharding to agents who don't care about it.

`isolated` scopes ARE excluded by default — same rule the existing curated tools follow for `scope='*'`. Agents include them by naming the scope explicitly: `scope='vendor'` or `scope='backend,vendor'`.

**ATTACH ceiling**: SQLite's default `SQLITE_MAX_ATTACHED` is 10. For monorepos with > 10 scopes this is a hard limit. Mitigations:

1. Microsoft.Data.Sqlite exposes `SqliteConnection` with full SQLite library; we can raise the limit at compile-time in the bundled SQLite binary OR via `sqlite3_limit(SQLITE_LIMIT_ATTACHED, …)` at runtime up to the absolute ceiling of 125.
2. If the user's scope filter resolves to > N scopes (where N is the configured ceiling), `query_graph` returns an error directing them to narrow the filter, with a list of resolvable scope ids.
3. For now: raise the runtime limit to 64 (well below the 125 absolute cap, generous for any realistic monorepo); fail closed if exceeded.

**Per-call connection vs. shared connection**: per-call. Shared connections would couple call lifetimes (one slow query blocks others) and complicate the safety story (a temp view created for one query would leak into another). The per-call cost is small — ~5ms per ATTACH, dominated by OS file open. The base `_meta.db` ATTACH is constant; per-scope ATTACHes are linear in scope count.

**Alternatives considered**:

- **Per-scope query, server unions results.** More robust against the ATTACH ceiling, but: the agent's SQL no longer composes across scopes (no cross-scope joins), and aggregations have to be re-aggregated server-side. Loses most of the point.
- **Single combined DB.** Would mean abandoning the per-scope DB layout entirely. Out of scope for this change.

### Decision 4 — Safety rails: read-only mode + single-statement + timeout + row cap

Five layered rails, each independently sufficient for the threat it addresses:

1. **Connection-level read-only.** Every ATTACH uses `mode=ro` on the URI; the in-memory main connection is opened with `Mode=ReadOnly`. Any `INSERT` / `UPDATE` / `DELETE` / `CREATE` / `DROP` / `ALTER` / `REPLACE` returns `SQLITE_READONLY`. No keyword scan needed.
2. **Single-statement enforcement.** `Microsoft.Data.Sqlite` doesn't natively reject multi-statement input; we prepare the SQL via `SqliteCommand.Prepare()`, then check that `cmd.CommandText` after preparation is the same as the input (no leftover SQL). Multi-statement input (e.g. `SELECT 1; ATTACH 'evil.db' AS evil`) is rejected at prepare time.
3. **No `ATTACH` / `DETACH` / `PRAGMA writes`.** The `mode=ro` connection rejects new ATTACHes; PRAGMA writes (`PRAGMA writable_schema = 1`, etc.) are write operations and are blocked at the SQLite level. Read-only PRAGMAs (`PRAGMA table_info(…)`) are allowed; they're useful for agent introspection.
4. **Statement timeout.** `SqliteCommand.CommandTimeout = 5` (seconds, configurable via `--query-timeout-seconds` CLI flag and `SOURCEGRAPH_QUERY_TIMEOUT_SECONDS` env). If the query runs longer, `SQLITE_INTERRUPT` fires and the tool returns a structured error: `{ "error": "timeout", "elapsed_ms": 5042, "hint": "narrow your WHERE clause or raise --query-timeout-seconds" }`.
5. **Row cap.** Default `5000` rows (configurable via `--query-row-limit` and `SOURCEGRAPH_QUERY_ROW_LIMIT`). The tool reads up to `cap + 1` rows; if `cap + 1` come back, it returns the first `cap` rows plus `{ "truncated": true, "row_cap": 5000, "hint": "add LIMIT or tighter filters" }` in the structured content.

**Rationale**: defence in depth. The read-only mode is the real safety bound; the single-statement check prevents `SELECT 1; ATTACH …` smuggling; the timeout and row cap protect availability against accidental table scans and Cartesian joins. Each one is cheap to implement (a few lines each) and they compose without interference.

**Alternatives considered**:

- **Keyword regex blocklist.** Brittle (`SELECT … FROM (DELETE …)` wouldn't match a naive blocklist; `SELECT load_extension('evil')` is a function call, not a keyword); SQLite's `mode=ro` is the canonical answer.
- **Sandbox via subprocess.** Overkill for a tool that runs entirely inside one in-process SQLite connection; adds latency and complexity for no security gain over `mode=ro`.
- **Soft truncation** (silently cut at cap). Hides the cap from the agent; a structured `truncated: true` flag is more honest and lets the agent re-query.

### Decision 5 — `describe_schema` returns views, not tables

`describe_schema` returns the live view list, columns (name, type, nullability, description), the current `kind_name` vocabulary present in `v_symbols` and `v_edges` (since these are enum-like and an agent can't enumerate them without writing a query), and the `view_schema_version`.

Crucially: it does **not** return the underlying tables. Agents that get the schema from `describe_schema` will only learn about views; agents that try to query the underlying tables will hit them by name (the views don't shadow the tables in the SQLite namespace, and the tables ARE attached) but they're explicitly off-contract — the tool description says so, and queries against raw tables aren't covered by the stability promise.

```jsonc
// describe_schema response, abridged
{
  "view_schema_version": 1,
  "views": [
    {
      "name": "v_symbols",
      "description": "Every declared symbol across non-isolated scopes (or the scopes selected via the `scope` parameter on query_graph). One row per (scope, id).",
      "columns": [
        { "name": "scope", "type": "TEXT", "nullable": false, "description": "Scope id this row lives in." },
        { "name": "id", "type": "INTEGER", "nullable": false, "description": "Per-scope symbol id; combine with `scope` for cross-scope uniqueness." },
        { "name": "name", "type": "TEXT", "nullable": false },
        { "name": "kind", "type": "TEXT", "nullable": false, "description": "Symbol kind (see `symbol_kinds` for the full vocabulary)." },
        { "name": "accessibility", "type": "INTEGER", "nullable": false, "description": "Roslyn `Accessibility`: 0=NotApplicable, 1=Private, 2=ProtectedAndInternal, 3=Protected, 4=Internal, 5=ProtectedOrInternal, 6=Public" },
        { "name": "is_public", "type": "INTEGER", "nullable": false, "description": "1 when accessibility = 6 (Public); 0 otherwise. Convenience for the common filter." },
        { "name": "is_type", "type": "INTEGER", "nullable": false, "description": "1 when kind ∈ {class, interface, struct, record, enum, delegate}; 0 otherwise." }
        // …
      ]
    },
    // v_files, v_edges, v_references, v_scopes …
  ],
  "symbol_kinds": ["class", "interface", "struct", "record", "enum", "delegate", "method", "field", "property", "event", "namespace", "xaml-view", …],
  "edge_kinds":   ["calls", "uses-type", "inherits", "implements", "instantiates", "throws", "tests", "binds-path", "handles-event", "uses-resource", …]
}
```

**Rationale**: an agent that calls `describe_schema` once at the start of a session has everything it needs to compose any query in this proposal's "Why" section without re-querying. The `symbol_kinds` and `edge_kinds` arrays are populated from `SELECT DISTINCT kind FROM v_symbols` and `SELECT DISTINCT kind FROM v_edges` — fast (indexed columns) and accurate to the live data.

**Alternatives considered**:

- **MCP `resources/list`** for the schema. MCP supports resource discovery, and the schema is a natural fit. Considered, but: a tool result is more discoverable from the agent's POV (one less concept to learn), and the schema is small enough that there's no cost to packing it into a tool response. Could be added as a parallel surface later.
- **Embed the schema in the `query_graph` tool description.** Bloats the tool list; the schema is meaningfully large (~50 columns across 5 views).

### Decision 6 — Rendering: structured + markdown, like every other tool

Response structure follows the existing `find_*` tools' convention (snake-case, `structuredContent` declared in `outputSchema`):

```jsonc
{
  "structuredContent": {
    "row_count": 42,
    "truncated": false,
    "row_cap": 5000,
    "elapsed_ms": 23,
    "columns": [
      { "name": "scope", "type": "TEXT" },
      { "name": "user_type", "type": "TEXT" },
      { "name": "user_count", "type": "INTEGER" }
    ],
    "rows": [
      ["backend", "Sample.Domain.Calculator", 17],
      ["backend", "Sample.Domain.Logger", 12],
      // …
    ]
  },
  "content": [
    { "type": "text", "text": "🌿 query_graph (42 rows, 23 ms)\n\n| scope   | user_type                  | user_count |\n|---------|----------------------------|-----------:|\n| backend | Sample.Domain.Calculator   |         17 |\n…" }
  ]
}
```

Tabular `[[col1, col2], …]` rather than `[{col1: val, col2: val}, …]` because it's ~2× more compact in the wire format for typical query results, and the column metadata is already in `columns`. Agents that prefer object form can transpose trivially.

**Markdown rendering**: GitHub-flavoured table. Numeric columns right-aligned. Long strings (file paths > 80 chars) NOT truncated — the agent needs the data; truncation is a UI concern. The `🌿` brand mark prefixes the result line, matching existing tool output. `--no-leaf` suppresses it like everywhere else.

## Risks / Trade-offs

- **[Risk] Agents bypass curated tools for questions where curated tools are better.** A curated `find_references` already handles cross-language XAML edges, has progress notifications, integrates with the scope system, and emits well-shaped result rows; an agent that writes raw SQL might miss those affordances. → **Mitigation**: `ServerInstructions` explicitly orders the recommendation ("Prefer curated tools …; for ad-hoc questions use `query_graph`"); the `query_graph` tool description repeats the rule in its `Use when:` line; `usage.jsonl` analysis can surface query patterns that should have been curated tool calls and inform documentation updates.

- **[Risk] View-as-contract breaks anyway when we evolve the schema.** Even with the view layer, a column rename (`kind` → `kind_name`) or removal would break any query that referenced it. → **Mitigation**: views are versioned (`view_schema_version`); breaking changes bump the version; `describe_schema` exposes the version so agents can detect changes. We can also ship a `v_symbols_v1` legacy view in parallel for one cycle if a breaking change is unavoidable.

- **[Risk] An agent writes a Cartesian-join query that runs for hours.** → **Mitigation**: 5-second statement timeout (configurable, but bounded). After timeout, the connection is closed; SQLite's `sqlite3_interrupt` cancels in-flight queries within milliseconds.

- **[Risk] Result-set truncation surprises the agent.** A query that would have returned 50,000 rows returns 5,000 with `truncated: true`; the agent might not notice. → **Mitigation**: `truncated` is a top-level field in `structuredContent`; the markdown rendering surfaces it with a 🌿 footer line (`(truncated at 5000 rows; add a tighter LIMIT or WHERE)`); the in-band hint string spells out the next step.

- **[Risk] ATTACH ceiling in giant monorepos.** Default SQLite `SQLITE_MAX_ATTACHED = 10`; raised to 64 at runtime, hard cap 125. A monorepo with 70 scopes can't query across all of them at once. → **Mitigation**: clear error message on overflow ("scope filter resolves to N scopes; this server's limit is 64. Narrow with `scope='backend,api,frontend'`."), with the resolvable scope list. Anyone hitting 64+ scopes can also restructure their `.sourcegraph.json` to consolidate.

- **[Risk] Schema-as-public-API is a one-way door.** Once views ship, agents will write queries against them; future refactors are constrained. → **Mitigation**: that's exactly the point. The view layer is the contract. Internal table refactors stay free; view-layer changes are deliberate.

- **[Risk] SQL injection in agent-built queries.** If an agent builds a query by string-concatenating user input, it's vulnerable to SQL injection just like any application. → **Mitigation**: this is the agent's responsibility, not the server's. Document the parameter binding (`@name` placeholders + `parameters: {name: value}`) in the tool description so agents have a safe path. The read-only connection means injection can leak data already exposed via views, but cannot mutate or escape the views.

- **[Trade-off] We're committing to SQLite as the storage layer for the foreseeable future.** Today's storage IS SQLite, but the abstraction (`IGraphStore`) leaves room for swapping it out. Exposing SQL closes that door — any swap would have to provide a SQLite-compatible query surface. → **Accepted**: we have no realistic plan to swap SQLite, the embedded local-file model is core to the product's "just works" promise, and the win from exposing SQL outweighs the optionality cost.

- **[Trade-off] The view layer is duplicate work for `IGraphStore` consumers.** The curated tools all go through `IGraphStore` methods; `query_graph` goes through views. Same data, two access paths. → **Accepted**: the curated path stays optimised and structured-output-shaped; the view path stays SQL-flexible. Convergence (rewriting curated tools as `query_graph` calls under the hood) is not a goal — they serve different audiences.

## Migration Plan

This is purely additive — no data migration, no breaking changes to existing tools. Land in phases so each commit is independently shippable:

1. **Storage: view definitions.** Embed `Views.sql` as a resource in `DevBitsLab.Mcp.SourceGraph.Storage`; add a `ViewSchema` constant for `view_schema_version = 1`. No code wiring yet — just the SQL text and a unit test that parses + executes it against a temp SQLite DB to catch syntax errors.

2. **Storage: multi-scope read-only connection helper.** Add `MultiScopeReadOnlyConnection.OpenAsync(IScopeRegistry, string scopeFilter, CancellationToken)` that opens an in-memory SQLite connection, ATTACHes `_meta.db` + each per-scope DB read-only, and applies the view definitions with the scope-list inlined into the `UNION ALL` branches. Returns the open connection (caller disposes). Unit-tested against `tests/fixtures/MultiScope/`.

3. **Server: `describe_schema` tool.** Pure read; no SQL execution. Returns the current view list (hard-coded from the same `Views.sql` resource), plus live `symbol_kinds` and `edge_kinds` populated from a single `SELECT DISTINCT kind …` against the multi-scope connection. `outputSchema` declared. Smoke-tested against the fixture.

4. **Server: `query_graph` tool.** SQL prepare → single-statement check → execute with timeout → read up to `row_cap + 1` rows → render structured + markdown. Plug in the safety rails. End-to-end tests for: trivial SELECT, SELECT with parameters, multi-scope UNION query, write rejected, multi-statement rejected, timeout, row cap. Integration test that runs the "how many public types use T" query end-to-end against the fixture and asserts the count.

5. **CLI: configuration flags.** Add `--query-timeout-seconds` (default `5`, env `SOURCEGRAPH_QUERY_TIMEOUT_SECONDS`) and `--query-row-limit` (default `5000`, env `SOURCEGRAPH_QUERY_ROW_LIMIT`) to the `serve` command. Pass through to the tool's options.

6. **`ServerInstructions` update.** Add the layered-recommendation sentence to the `initialize` response. Suppress under `--no-instructions`.

7. **Tool catalog: brand mark + `Use when:` line.** Both new tools get the standard `🌿 ` prefix on `Title` / `Description` (per `leaf-to-tool-identity`) and a `Use when:` line in the description. `Use when:` for `query_graph`: *"the question you want to answer doesn't fit any other tool, or you need an aggregation/join/grouping over the graph that no curated tool exposes."*. `Use when:` for `describe_schema`: *"you're about to write `query_graph` SQL and don't yet know the view names or columns."*.

8. **README.md update.** New section "Ad-hoc queries" near the existing "Tool-usage guidance" block, with one worked example (the public-types-use-T query end-to-end).

9. **Spec sync.** `openspec validate add-graph-query --strict`; `openspec archive add-graph-query --yes` after merge.

**Rollback strategy**: revert in reverse order. Each phase's commit is independent. The view layer leaves no on-disk artefact (TEMP views), so rolling back the storage helper is risk-free. Rolling back the tools just removes them from `tools/list`; existing curated tools are untouched.

## Open Questions

- **View versioning policy.** When `view_schema_version` bumps, should the previous version's views ship in parallel for one cycle (`v_symbols_v1`)? Likely yes for soft deprecation, but defer the policy decision until the first breaking view change is on the table.

- **Should `describe_schema` ship view SQL definitions?** Returning the actual `CREATE VIEW` text is maximum transparency and lets agents reason about the view's semantics. But it leaks the underlying table names and the per-scope ATTACH structure. Probably keep view definitions internal for now; revisit if agents struggle to predict view behaviour.

- **EXPLAIN QUERY PLAN exposure.** Useful for debugging slow queries; does the agent need it as a separate tool, or can `query_graph` accept an `explain: true` flag? Lean toward the flag for parsimony, but defer until someone asks.

- **Result format alternatives for very large results.** 5000 rows × 10 columns of strings can be ~500KB of JSON. JSON Lines streaming or CSV download would be more efficient for big result sets — but MCP's current tool-result transport doesn't streamline streaming. Park unless real call patterns hit the ceiling.

- **Cross-server federation.** If two `sourcegraph-mcp` servers index different repos, can `query_graph` join across them? Out of scope here, but the view layer is a natural surface for it (each server publishes its views; a federation server unions them). Mention as a future direction in the README, no commitment.

- **Should the SDK ship a typed query-builder helper?** For consumers of `DevBitsLab.Mcp.SourceGraph.Sdk` building plugin tools — they could use a strongly-typed builder over the views instead of string concatenation. Probably yes eventually; not in this change.
