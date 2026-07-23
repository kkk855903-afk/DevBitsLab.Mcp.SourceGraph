## ADDED Requirements

### Requirement: Schema introspection tool
The server SHALL expose a `describe_schema` tool that returns the live view layer published by the storage layer (per the `Stable view layer over the underlying tables` requirement on the `storage` capability), suitable for an MCP agent to consume before composing a `query_graph` SQL statement.

The tool's `structuredContent` SHALL include:

- `view_schema_version`: integer matching the storage layer's `Views.SchemaVersion` constant; bumps when any view's column shape changes in a backwards-incompatible way.
- `views`: array of `{ name, description, columns: [{ name, type, nullable, description }] }`. The list is hand-curated in `Views.All`; agents can rely on it being a complete enumeration of the queryable surface.
- `symbol_kinds`: array of distinct `kind` values present in `v_symbols` across the resolved scope set, populated by `SELECT DISTINCT kind FROM v_symbols`.
- `edge_kinds`: array of distinct `kind` values present in `v_edges` across the resolved scope set, populated by `SELECT DISTINCT kind FROM v_edges`.

The tool's `outputSchema` SHALL declare the structured shape so MCP clients can validate it. The tool SHALL accept an optional `scope` parameter following the same convention as every other tool (`"*"` default, comma-list narrow, isolated scopes excluded from `*`).

#### Scenario: Agent enumerates the queryable views
- **WHEN** an MCP agent calls `describe_schema()` against an indexed multi-scope solution
- **THEN** the response lists `v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes` with their columns; `view_schema_version` is `1`; `symbol_kinds` includes at minimum `class`, `interface`, `method`, `field`; `edge_kinds` includes at minimum `calls`, `uses-type`

#### Scenario: Symbol-kind vocabulary reflects live data
- **GIVEN** the indexer's vocabulary expands (e.g., the XAML indexer adds `xaml-view`, `xaml-element` to the `kind` column)
- **WHEN** `describe_schema` is invoked after re-indexing
- **THEN** `symbol_kinds` includes the new values without any code change to `describe_schema` or `Views.All`

#### Scenario: Schema version bumps on breaking change
- **GIVEN** a hypothetical future change that renames `v_symbols.kind` → `v_symbols.symbol_kind`
- **WHEN** that change ships and `describe_schema` is invoked
- **THEN** `view_schema_version` is `2` (one greater than `1`); the response makes the renamed column visible in the `columns` array

### Requirement: Ad-hoc graph query tool
The server SHALL expose a `query_graph` tool that accepts a read-only SQL `SELECT` or `WITH` statement, optional named parameters, and an optional `scope` filter, executes the statement against the multi-scope view layer, and returns the resulting rows as `structuredContent` plus a markdown table for display.

The tool's input parameters:
- `sql` (string, required): a single `SELECT` or `WITH` statement against the views from `describe_schema`. Multi-statement input is rejected.
- `parameters` (object, optional): named binding values for `@name` placeholders. Each value is bound by `Microsoft.Data.Sqlite`'s standard parameter conversion.
- `scope` (string, optional, default `"*"`): scope-id, comma-separated list of scope ids, or `"*"` (all non-isolated). Same convention as every existing curated tool. Isolated scopes are included only when explicitly named.

The `structuredContent` SHALL include:
- `row_count`: number of rows returned (≤ `row_cap`).
- `truncated`: boolean; true when the underlying query produced more rows than `row_cap`.
- `row_cap`: the active row cap for this call (configured via `--query-row-limit` / env, default `5000`).
- `elapsed_ms`: query execution time in milliseconds.
- `columns`: array of `{ name, type }` describing each result column.
- `rows`: array of arrays; one inner array per row, in the column order from `columns`.

The `content[].text` SHALL render a GitHub-flavoured markdown table prefixed by `🌿 query_graph (N rows, M ms)` (subject to the existing brand-mark suppression flag), with numeric columns right-aligned and a trailing `_(truncated at {row_cap} rows; add a tighter LIMIT or WHERE)_` line when `truncated` is true.

#### Scenario: Count public types that use a given type
- **GIVEN** an indexed solution where `Sample.Domain.Calculator` is referenced (via `uses-type`) from members declared inside three public types and one internal type
- **WHEN** the agent invokes `query_graph` with
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
  with `parameters = { "@fqn": "Sample.Domain.Calculator" }`
- **THEN** the `structuredContent.rows[0][0]` is `3`; `row_count` is `1`; `truncated` is `false`

#### Scenario: Default scope excludes isolated scopes
- **GIVEN** a multi-scope solution with `frontend`, `backend`, and `vendor` scopes, where `vendor` is `isolated`
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT scope, COUNT(*) FROM v_symbols GROUP BY scope"` and no `scope` parameter
- **THEN** the result contains rows for `frontend` and `backend` only; the `vendor` scope is absent

#### Scenario: Explicit isolated-scope opt-in
- **WHEN** the agent invokes the same query with `scope = "vendor"`
- **THEN** the result contains a single row for `vendor`; `frontend` and `backend` are absent

#### Scenario: Comma-list scope filter
- **WHEN** the agent invokes the same query with `scope = "frontend,vendor"`
- **THEN** the result contains rows for `frontend` and `vendor` (isolated explicitly named); `backend` is absent

#### Scenario: Write attempt rejected
- **WHEN** the agent invokes `query_graph` with `sql = "INSERT INTO v_symbols(name) VALUES ('evil')"`
- **THEN** the tool returns a structured error `{ "error": "read_only", "hint": "query_graph is read-only; use a SELECT or WITH statement" }`; no row is inserted; the connection is closed

#### Scenario: Multi-statement input rejected
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT 1; ATTACH 'evil.db' AS evil;"`
- **THEN** the tool returns a structured error `{ "error": "multi_statement", "hint": "send one SELECT/WITH statement per call" }`; the second statement is never executed

#### Scenario: Statement timeout fires
- **GIVEN** the server is started with `--query-timeout-seconds 2`
- **WHEN** the agent invokes a query whose execution exceeds 2 seconds (e.g., a Cartesian join over a large symbol table without limits)
- **THEN** within ~2.5 seconds the tool returns a structured error `{ "error": "timeout", "elapsed_ms": <≈2000>, "hint": "narrow your WHERE clause or raise --query-timeout-seconds" }`; the underlying SQLite query is interrupted

#### Scenario: Row cap surfaces truncation
- **GIVEN** the server is started with `--query-row-limit 100`
- **WHEN** the agent invokes a query whose result set has 250 rows
- **THEN** `structuredContent.row_count` is `100`; `structuredContent.truncated` is `true`; `structuredContent.row_cap` is `100`; the markdown table includes the `_(truncated at 100 rows; …)_` footer line

#### Scenario: Parameter binding works for typed values
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT * FROM v_symbols WHERE id = @id AND name LIKE @prefix LIMIT 5"` and `parameters = { "@id": 42, "@prefix": "Calc%" }`
- **THEN** the bound parameters are sent to SQLite via `SqliteParameter` (no string interpolation); the result reflects the bound values; an attempt to inject `@prefix = "%' OR 1=1 --"` returns rows whose `name` literally matches that string, not all rows

### Requirement: query_graph and describe_schema follow tool-output conventions
Both `query_graph` and `describe_schema` SHALL declare an `outputSchema` for their `structuredContent`, SHALL prefix their `Title` and `Description` in `tools/list` with the `🌿 ` brand mark (per the `Tool identity brand mark` requirement), SHALL prefix their text-content responses with `🌿 ` (per the `Tool response brand mark` requirement), SHALL include a `Use when:` line in their description, and SHALL be suppressible via `--no-leaf` / `SOURCEGRAPH_NO_LEAF=1` like every other built-in tool.

The `Use when:` lines:
- `query_graph`: *"the question you want to answer doesn't fit any other tool, or you need an aggregation/join/grouping over the graph that no curated tool exposes."*
- `describe_schema`: *"you're about to write `query_graph` SQL and don't yet know the view names or columns."*

#### Scenario: Tool list shows brand mark on both
- **WHEN** an MCP client calls `tools/list` against a server started without `--no-leaf`
- **THEN** the `query_graph` and `describe_schema` entries have `Title` starting with `🌿 ` and `Description` starting with `🌿 `

#### Scenario: --no-leaf suppresses brand on both
- **GIVEN** the server is started with `--no-leaf`
- **WHEN** the client calls `tools/list` and then `query_graph(...)` and `describe_schema()`
- **THEN** the `Title`, `Description`, and response text for both tools are unprefixed

### Requirement: ServerInstructions documents the layered tool model
The `ServerInstructions` block returned in the MCP `initialize` response SHALL include a sentence explaining when to prefer `query_graph` over the curated tools, suppressible by `--no-instructions` / `SOURCEGRAPH_NO_INSTRUCTIONS=1` like the existing guidance.

#### Scenario: Layered guidance is published by default
- **WHEN** an MCP client connects to a server without `--no-instructions`
- **THEN** the `ServerInstructions` payload includes a sentence of the form *"For ad-hoc questions that don't fit a curated tool, call `describe_schema` then `query_graph` — read-only SQL over a stable view layer."* in addition to the existing curated-tools recommendation

#### Scenario: --no-instructions suppresses the layered guidance
- **GIVEN** the server is started with `--no-instructions`
- **WHEN** an MCP client connects
- **THEN** the `ServerInstructions` payload is empty (or omitted entirely); the layered-guidance sentence is not present
