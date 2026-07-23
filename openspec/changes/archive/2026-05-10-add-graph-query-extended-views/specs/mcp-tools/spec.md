## MODIFIED Requirements

### Requirement: Schema introspection tool
The server SHALL expose a `describe_schema` tool that returns the live view layer published by the storage layer (per the `Stable view layer over the underlying tables` requirement on the `storage` capability), suitable for an MCP agent to consume before composing a `query_graph` SQL statement.

The tool's `structuredContent` SHALL include:

- `view_schema_version`: integer matching the storage layer's `Views.SchemaVersion` constant; bumps on **any view-set change** (addition, removal, column rename, or column-type change) so cache-aware clients always re-introspect after a server upgrade.
- `views`: array of `{ name, description, columns: [{ name, type, nullable, description }] }`. The list is hand-curated in `Views.All` and SHALL include all eight views currently shipped: `v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`, `v_annotations`, `v_diagnostics`, `v_history`.
- `symbol_kinds`: array of distinct `kind` values present in `v_symbols` across the resolved scope set, populated by `SELECT DISTINCT kind FROM v_symbols`.
- `edge_kinds`: array of distinct `kind` values present in `v_edges` across the resolved scope set, populated by `SELECT DISTINCT kind FROM v_edges`.

The tool's `outputSchema` SHALL declare the structured shape so MCP clients can validate it. The tool SHALL accept an optional `scope` parameter following the same convention as every other tool (`"*"` default, comma-list narrow, isolated scopes excluded from `*`).

#### Scenario: Agent enumerates the queryable views
- **WHEN** an MCP agent calls `describe_schema()` against an indexed multi-scope solution
- **THEN** the response lists `v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`, `v_annotations`, `v_diagnostics`, `v_history` (eight views) with their columns; `view_schema_version` is `2`; `symbol_kinds` includes at minimum `class`, `interface`, `method`, `field`; `edge_kinds` includes at minimum `calls`, `uses-type`

#### Scenario: Symbol-kind vocabulary reflects live data
- **GIVEN** the indexer's vocabulary expands (e.g., the XAML indexer adds `xaml-view`, `xaml-element` to the `kind` column)
- **WHEN** `describe_schema` is invoked after re-indexing
- **THEN** `symbol_kinds` includes the new values without any code change to `describe_schema` or `Views.All`

#### Scenario: Schema version bumps on any view-set change
- **GIVEN** the prior revision shipped `view_schema_version = 1` with five views
- **WHEN** the current revision ships with three additional views
- **THEN** `view_schema_version` reads `2`; a future revision that renames `v_symbols.kind` → `v_symbols.symbol_kind` SHALL bump it to `3`; a future revision that adds `v_bindings` SHALL also bump (the policy does not distinguish breaking from additive)

#### Scenario: New view descriptors carry the same documentation depth
- **WHEN** an agent inspects the `columns` array of `v_annotations`, `v_diagnostics`, or `v_history` in `describe_schema`'s response
- **THEN** every column has a `name`, `type`, `nullable`, and `description` populated; descriptions surface notable nuances (e.g. `v_diagnostics.symbol_id` documented as nullable; `v_diagnostics.severity_name` documents the integer-to-text mapping; `v_history.last_authored_at` documents the Unix-millis unit)
