-- Views.sql — stable agent-facing view layer over the per-scope SQLite tables.
-- Substituted at connection-setup time by MultiScopeReadOnlyConnection.
-- The placeholder tokens below (each beginning with double-brace) are replaced with one
-- per-scope SELECT branch joined by UNION ALL. See
-- openspec/changes/archive/2026-05-10-add-graph-query/design.md Decision 2 for the full
-- contract (extended in archive/2026-05-10-add-graph-query-extended-views/ to add
-- v_annotations / v_diagnostics / v_history).
-- (Tokens are not shown here verbatim to avoid being mistaken for substitution targets;
-- the {{SCOPE_UNION_BLOCK_<view>}}-style placeholders appear only in the view bodies below.)
--
-- Naming notes (renames vs. underlying tables):
--   * v_symbols.start_column renames the underlying symbols.start_col for ergonomics.
--     end_line / end_column are NOT projected — agents who need the end position can
--     query the underlying table directly via "<scope>".symbols.end_line / .end_col.
--   * v_references.column_number renames the underlying refs.col — SQL reserves the
--     bare identifier COLUMN, so we avoid the double-quoting tax for agent queries.
--   * v_diagnostics.column_number renames the underlying diagnostics.col for the
--     same reason.
--   * v_files.sha renames files.content_sha256 — "sha" is the agent-facing name.
--   * v_edges.kind / v_symbols.kind are projected from the underlying *_kind_name
--     columns; "kind" is the contract.
--   * v_edge_evidence.confidence maps the ordered integer confidence to
--     'inferred' / 'semantic' / 'exact'; confidence_level retains the integer.
--   * v_references.kind maps the integer Core.ReferenceKind enum to short text:
--     0='def', 1='ref', 2='call', 3='impl', 4='inherit', 5='read', 6='write'.
--   * v_diagnostics.severity_name maps the integer Roslyn DiagnosticSeverity enum
--     to short text: 0='hidden', 1='info', 2='warning', 3='error'. The raw integer
--     stays available via the severity column for ordering / range queries.

CREATE TEMP VIEW v_symbols AS
{{SCOPE_UNION_BLOCK_v_symbols}}
;

CREATE TEMP VIEW v_files AS
{{SCOPE_UNION_BLOCK_v_files}}
;

CREATE TEMP VIEW v_edges AS
{{SCOPE_UNION_BLOCK_v_edges}}
;

CREATE TEMP VIEW v_edge_evidence AS
{{SCOPE_UNION_BLOCK_v_edge_evidence}}
;

CREATE TEMP VIEW v_references AS
{{SCOPE_UNION_BLOCK_v_references}}
;

CREATE TEMP VIEW v_scopes AS
SELECT
  s.id              AS scope,
  s.name            AS name,
  s.root            AS root,
  s.isolated        AS isolated,
  s.status          AS status,
  s.last_indexed_at AS last_indexed_at
FROM meta.scopes s;

CREATE TEMP VIEW v_annotations AS
{{SCOPE_UNION_BLOCK_v_annotations}}
;

CREATE TEMP VIEW v_diagnostics AS
{{SCOPE_UNION_BLOCK_v_diagnostics}}
;

CREATE TEMP VIEW v_history AS
{{SCOPE_UNION_BLOCK_v_history}}
;
