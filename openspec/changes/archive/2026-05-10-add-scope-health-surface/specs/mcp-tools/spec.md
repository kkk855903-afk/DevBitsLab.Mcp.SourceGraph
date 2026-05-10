## ADDED Requirements

### Requirement: verify_scope read-only health snapshot tool

The server SHALL expose a `verify_scope` MCP tool that returns a structured health snapshot for one scope or all scopes, without mutating any state.

The tool SHALL accept a single argument:
- `scope` (string, default `"*"`) — a scope id, comma-separated list, or `"*"` for all non-isolated scopes (the same resolution semantics as every other scope-aware tool).

The response SHALL ship `structuredContent` with one entry per resolved scope, each entry carrying:
- `scope` — the scope id
- `status` — one of `"ok"`, `"degraded"`, `"indexing"`
- `status_message` — the registry's `status_message` (may be `null` when `status = "ok"`)
- `schema_version` — the integer `Schema.Version` value the scope's DB was last opened with
- `views_schema_version` — the integer `Views.SchemaVersion` (matches `describe_schema`)
- `last_indexed_at` — ISO-8601 timestamp from the registry
- `row_counts` — object with `symbols`, `refs`, `edges`, `files`, `annotations`, `diagnostics` long fields
- `integrity_check` — string; `"ok"` when both `PRAGMA integrity_check` and the FTS5 integrity-check pass; otherwise the first failure line
- `drift_sample` — object with `sampled` (int, ≤ 20), `total_files` (int, the scope's `files` row count), `changed` (int, count of sampled files whose on-disk SHA-256 differs from the DB's `content_sha256`), and `changed_paths` (string list, capped at the first 5 changed paths so the response stays bounded)

The tool body SHALL emit `notifications/progress` (per the existing `Progress notifications on slow tools` requirement) at three checkpoints when a `progressToken` is on the originating request: `"reading row counts"` (0.0), `"running integrity_check"` (0.4), `"sampling drift"` (0.8). The `integrity_check` step is the slow one on large DBs; the progress checkpoints prevent the call from looking hung.

The tool SHALL NOT mutate any registry row, DB row, or filesystem state. It SHALL NOT emit a heal event (reads are not heals; the call is recorded in `usage.jsonl` like every other tool via `ToolMetrics.TrackAsync`).

When called against a scope whose `status = "degraded"` (e.g., from missing DB or stuck `indexing` detection in this change, or from any other degraded path), the tool SHALL return the registry's `status` and `status_message` and SHALL omit the `row_counts`, `integrity_check`, and `drift_sample` fields (set to `null`) since they require a healthy DB connection.

When called with `scope = "*"` against a registry containing no non-isolated scopes, the tool SHALL return a structured `no_scopes` diagnostic (matching the convention established by `query_graph` and `describe_schema`) rather than throw.

#### Scenario: Verify a healthy scope
- **GIVEN** a scope `backend` with `status = "ok"`, 1500 symbols, 800 refs, 600 edges, 80 files, 200 annotations, 30 diagnostics, and no drift
- **WHEN** the agent invokes `verify_scope(scope = "backend")`
- **THEN** the response's `structuredContent[0]` has `scope = "backend"`, `status = "ok"`, `row_counts = { symbols: 1500, refs: 800, edges: 600, files: 80, annotations: 200, diagnostics: 30 }`, `integrity_check = "ok"`, `drift_sample = { sampled: 20, total_files: 80, changed: 0, changed_paths: [] }`

#### Scenario: Verify a degraded scope
- **GIVEN** a scope `tools` whose registry row carries `status = "degraded"` and `status_message = "scope DB file missing — call repair_scope or restart"`
- **WHEN** the agent invokes `verify_scope(scope = "tools")`
- **THEN** the response's `structuredContent[0]` has `scope = "tools"`, `status = "degraded"`, `status_message = "scope DB file missing — call repair_scope or restart"`, and the `row_counts` / `integrity_check` / `drift_sample` fields are `null`

#### Scenario: Verify all scopes
- **GIVEN** a registry with non-isolated scopes `frontend` (ok) and `backend` (degraded), plus isolated scope `vendor`
- **WHEN** the agent invokes `verify_scope()` (default `scope = "*"`)
- **THEN** the response's `structuredContent` array contains two entries — one for `frontend` and one for `backend`; the `vendor` scope is excluded (matches the standard `*` fan-out semantics)

#### Scenario: Drift sample surfaces watcher-missed edits
- **GIVEN** a scope `backend` with 100 indexed files; while the server was offline, three of those files were edited so their on-disk SHA-256 no longer matches the DB's `content_sha256`
- **WHEN** the agent invokes `verify_scope(scope = "backend")` after the server restarts (without triggering any reindex)
- **THEN** the response's `drift_sample` has `changed >= 0`; if any of the three edited files were sampled (probabilistic with sample size 20 over 100 files), `changed > 0` and the affected paths appear in `changed_paths` (up to the cap of 5)

#### Scenario: Empty registry returns structured diagnostic
- **GIVEN** a `_meta.db` with no non-isolated scope rows
- **WHEN** the agent invokes `verify_scope()` (default `scope = "*"`)
- **THEN** the response is a `no_scopes` structured diagnostic (matching the established convention) rather than an exception
