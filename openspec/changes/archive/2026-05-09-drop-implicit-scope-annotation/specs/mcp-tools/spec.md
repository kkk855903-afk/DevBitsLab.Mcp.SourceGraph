## MODIFIED Requirements

### Requirement: Optional scope parameter on every existing tool
Every existing query tool (`find_definition`, `find_references`, `list_symbols_in_file`, `list_callers`, `list_callees`, `search_symbols`, `neighborhood`, `module_summary`, `impact_of_change`, `graph_stats`, `usage_stats`) SHALL accept an optional `scope` parameter (a string id, a string array, or the literal `"*"` for all non-isolated scopes).

#### Scenario: Default behaviour
- **WHEN** any tool is invoked without a `scope` argument
- **THEN** the query runs against the configured `default_scope` (or the single registered scope when none is configured), and the response carries no in-band scope annotation — the agent can call `list_scopes` if it needs to know which scope answered

#### Scenario: Explicit single scope
- **WHEN** a tool is invoked with `scope = "backend"`
- **THEN** results come from the `backend` scope only

#### Scenario: Explicit multi-scope
- **WHEN** a tool is invoked with `scope = ["frontend", "backend"]`
- **THEN** results from both scopes are merged by `canonical_key`, each row tagged with the scopes it came from

#### Scenario: Wildcard scope
- **WHEN** a tool is invoked with `scope = "*"`
- **THEN** results come from every non-isolated scope; isolated scopes are excluded unless listed explicitly
