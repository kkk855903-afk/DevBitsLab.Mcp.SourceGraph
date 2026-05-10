## ADDED Requirements

### Requirement: Stable view layer over the underlying tables
The storage layer SHALL ship a versioned set of read-only SQL views (`v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`) that present a denormalised, agent-facing contract over the underlying `symbols` / `edges` / `symbol_references` / `files` tables and the `_meta.db` scope-registry tables. The views SHALL be the public contract for any consumer that runs ad-hoc SQL via the MCP `query_graph` tool; the underlying tables remain implementation details and may evolve without bumping `Views.SchemaVersion`.

`Views.SchemaVersion` SHALL be a compile-time integer constant exposed on the storage assembly (initial value `1`); it SHALL bump only when a view's column shape changes in a backwards-incompatible way (column removed, renamed, or whose type meaningfully changes).

The view definitions SHALL live in an embedded SQL resource (`Views.sql`) with a `{{SCOPE_UNION_BLOCK}}` placeholder for the per-scope `UNION ALL` branches that the connection helper inlines at attach time. Each view SHALL include a `scope TEXT NOT NULL` column carrying the scope id, populated by the per-scope branch's `SELECT '<scope_id>' AS scope, …` clause; cross-scope joins SHALL use the composite `(scope, id)` tuple.

The initial view set:
- `v_symbols(scope, id, name, fqn, kind, accessibility, is_public, is_type, modifiers, xml_summary, container_id, file_id, start_line, start_column)`
- `v_files(scope, id, path, sha, last_indexed_at, is_generated)`
- `v_edges(scope, src, dst, kind, payload)`
- `v_references(scope, symbol_id, file_id, line, column_number, kind)` — the column-position field is named `column_number` rather than `column` because the bare identifier `COLUMN` is reserved in SQL; renaming avoids forcing every agent query to double-quote it. The underlying table column is `refs.col`; the view exposes it as `column_number`.
- `v_scopes(scope, name, root, isolated, status, last_indexed_at)` — sourced from `_meta.db`'s `scopes` table; not unioned (single source).

`v_symbols` SHALL include the convenience boolean columns:
- `is_public INTEGER NOT NULL`: `1` when `accessibility = 6` (Roslyn `Accessibility.Public`); `0` otherwise.
- `is_type INTEGER NOT NULL`: `1` when `kind IN ('class', 'interface', 'struct', 'record', 'enum', 'delegate')`; `0` otherwise.

The views SHALL be created as `TEMP` views on the per-call connection (no on-disk DDL); they live for the duration of one `query_graph` call and are released when the connection closes. They SHALL NOT be created via `EnsureSchemaAsync`; the on-disk schema is unchanged by this requirement.

#### Scenario: View definitions execute against a single-scope DB
- **GIVEN** a temp SQLite DB containing one row each in `symbols`, `edges`, `files`, `symbol_references` (mimicking a single attached scope)
- **WHEN** the test inlines `Views.Sql` with one branch in `{{SCOPE_UNION_BLOCK}}` and applies it as TEMP views
- **THEN** every view from the set executes without syntax error and returns the expected column shapes; `v_symbols.is_public` is `1` for the symbol whose `accessibility = 6`; `v_symbols.is_type` is `1` for the symbol whose `kind_name = 'class'`

#### Scenario: View columns match describe_schema's response
- **WHEN** `describe_schema` returns the `views` array
- **THEN** every column listed in the `Views.All` descriptor for each view is present (and only those columns) when the view is queried via `SELECT * FROM <view> LIMIT 0`; the SQLite `name` and `type` for each column matches the descriptor

#### Scenario: View schema version is exposed for consumers
- **WHEN** a consumer reads `Views.SchemaVersion` (or the `view_schema_version` field returned by `describe_schema`)
- **THEN** the value is `1` for this initial change; a future change that renames any view's column SHALL increment it to `2` (or higher)

#### Scenario: Underlying schema evolution does not bump view version
- **GIVEN** a hypothetical future storage change that adds an internal column `symbols.cyclomatic_complexity INTEGER` without exposing it via `v_symbols`
- **WHEN** that change ships
- **THEN** `Views.SchemaVersion` remains unchanged; agent queries against `v_symbols` return the same columns as before; `Schema.Version` (the storage on-disk version) bumps independently

### Requirement: Read-only multi-scope attached connection helper
The storage layer SHALL expose `MultiScopeReadOnlyConnection.OpenAsync(IScopeRegistry registry, string scopeFilter, int maxAttached, CancellationToken ct)` returning an open `SqliteConnection` configured for read-only access to a resolved set of scope DBs plus the `_meta.db` registry, with the view layer (per the `Stable view layer over the underlying tables` requirement) created as TEMP views ready for query.

The helper SHALL:
- Open an in-memory SQLite connection (`Data Source=:memory:`).
- Raise the runtime ATTACH limit to `maxAttached` (default `64`, hard-bounded by SQLite's compile-time ceiling of `125`) via `sqlite3_limit(SQLITE_LIMIT_ATTACHED, …)`.
- Resolve `scopeFilter` against `IScopeRegistry`:
  - `"*"` → all scopes whose `isolated` flag is `false`.
  - Comma-separated list → those scopes by id (isolated permitted when explicitly named).
  - Single id → just that scope.
- ATTACH `_meta.db` AS `meta` (read-only via `?mode=ro` on the URI), once per connection regardless of scope filter.
- ATTACH each per-scope DB AS `<scope_id>` (read-only via `?mode=ro`).
- Expand `Views.Sql`'s `{{SCOPE_UNION_BLOCK}}` token into one `SELECT '<scope_id>' AS scope, … FROM <scope_id>.<table> UNION ALL` branch per attached scope, then execute the resulting DDL.
- If the resolved scope set's count exceeds `maxAttached`, throw `ScopeAttachLimitExceededException` carrying the resolved scope-id list and the configured ceiling, **before** opening any per-scope ATTACH.

#### Scenario: Default filter resolves to non-isolated scopes
- **GIVEN** a scope registry with `frontend` (not isolated), `backend` (not isolated), and `vendor` (isolated)
- **WHEN** `OpenAsync(registry, "*", maxAttached: 64, ct)` runs
- **THEN** the returned connection has `meta`, `frontend`, and `backend` attached; `vendor` is NOT attached; `v_symbols` enumerates rows from `frontend` and `backend` only

#### Scenario: Explicit naming includes isolated scopes
- **WHEN** `OpenAsync(registry, "vendor", maxAttached: 64, ct)` runs
- **THEN** the returned connection has `meta` and `vendor` attached; `v_symbols` enumerates rows from `vendor` only

#### Scenario: Comma-list filter is honoured exactly
- **WHEN** `OpenAsync(registry, "frontend,vendor", maxAttached: 64, ct)` runs
- **THEN** the returned connection has `meta`, `frontend`, and `vendor` attached; `backend` is NOT attached even though it's not isolated

#### Scenario: Read-only enforcement at the per-scope ATTACH
- **GIVEN** an open multi-scope connection
- **WHEN** the caller executes `INSERT INTO frontend.symbols(name) VALUES ('evil')`
- **THEN** SQLite returns error code `SQLITE_READONLY`; no row is inserted; the on-disk `frontend.db` is untouched

#### Scenario: ATTACH ceiling enforced
- **GIVEN** a registry containing 70 non-isolated scopes
- **WHEN** `OpenAsync(registry, "*", maxAttached: 64, ct)` runs
- **THEN** the helper throws `ScopeAttachLimitExceededException`; the exception's `ResolvedScopes` property lists all 70 scope ids; `Limit` is `64`; no SQLite connection is leaked

#### Scenario: Connection is per-call and disposable
- **WHEN** `query_graph` opens a multi-scope connection, executes one query, and disposes it
- **THEN** the in-memory main DB and every ATTACH are released; subsequent calls do not see leftover TEMP views from a prior call; opening two connections concurrently does not interfere with each other (no shared state)
