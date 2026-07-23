## ADDED Requirements

### Requirement: Soft size budget for list-shaped tool results

Built-in MCP tools that emit per-row content (prose table row, `ResourceLinkBlock`, structured-content array entry) SHALL apply a soft serialized-size budget so a single `tools/call` result stays under MCP-client per-call truncation thresholds (Claude Code's threshold is approximately 16K tokens / 64K characters; the project budget targets 50K characters with headroom).

The budget SHALL be enforced at the call site by trimming the tool's row list **before** prose / `ResourceLinkBlock` / structured-content emission so all three representations remain internally consistent (i.e. the structured array length equals the prose row count equals the count of emitted `ResourceLinkBlock` items, every time).

The number of items budgeted SHALL be computed by the centralised `OutputBudget.ChooseKeep` helper from a per-tool per-row cost estimate so future tools share the same tuning surface rather than each picking its own caps.

The budget SHALL apply on top of the user-supplied `limit` parameter — a small `limit` returns at most `limit` rows; a large `limit` is further capped to fit the budget.

#### Scenario: find_references caps projected size

- **WHEN** `find_references` resolves a symbol with so many references that emitting all of them would push the serialized response past the budget
- **THEN** the tool body trims its `refs` list to the largest count that fits, builds prose / resource links / structured content from the trimmed list, and the resulting `CallToolResult` stays under the budget

#### Scenario: list_symbols_in_file caps projected size

- **WHEN** `list_symbols_in_file` is invoked on a file with so many symbols that emitting all of them with signature + XML summary lines would exceed the budget
- **THEN** the tool body trims its `hits` list before issuing per-symbol annotation/history queries (so dropped rows do not incur work), then builds prose / resource links / structured content from the trimmed list

#### Scenario: Trimmed response keeps representations in lockstep

- **WHEN** the size cap activates on any list-shaped tool
- **THEN** the response's prose row count, count of `ResourceLinkBlock` items, and structured-content array length are all equal — the existing `StructuredContentInvariantTests` continue to pass

#### Scenario: Non-overflow query passes through unchanged

- **WHEN** the projected size of a list-shaped tool's result is comfortably under the budget
- **THEN** `OutputBudget.ChooseKeep` returns `(items.Count, 0)` — no trimming occurs, no extra metadata key is emitted, and the response shape is identical to the pre-budget behaviour

### Requirement: Size-driven truncation signalled via omitted_size metadata

When a list-shaped tool trims its row list to fit the soft size budget, the tool SHALL append an `omitted_size=<N>` extra to its existing audience-restricted `_meta:` block built via `AudienceMetadata.Build`, where `N` is the count of rows dropped from the tail. The key SHALL be omitted entirely when no size-driven trim occurred so non-overflow calls retain their pre-change metadata shape.

The `omitted_size` signal is distinct from `limit`-driven truncation: a tool may return `limit` rows and emit no `omitted_size` (the user-supplied cap was met without size pressure), or it may return fewer than `limit` rows AND emit `omitted_size=N` (the size budget bit before `limit` did).

#### Scenario: Trim signalled in audience metadata

- **WHEN** a list-shaped tool trims `N` rows to fit the size budget
- **THEN** the trailing audience-restricted metadata block contains the substring `omitted_size=<N>` so an agent reading the block detects the truncation and can re-query with a smaller `limit` or refined filter

#### Scenario: Non-truncating call omits the key

- **WHEN** a list-shaped tool returns its full result without size-driven trimming
- **THEN** the trailing audience-restricted metadata block does NOT contain `omitted_size=` — it carries only the pre-existing keys (`scope`, `latency_ms`, plus the per-tool count key like `references` or `members`)

### Requirement: Lowered defaults for high-fanout list tools

The default `limit` parameter for tools whose rows triplicate across prose / `ResourceLinkBlock` / structured content SHALL stay aligned across the family rather than allowing one tool to ship a default that routinely overruns Claude Code's per-call ceiling. Specifically:

- `find_references` default `limit` SHALL be 50 (matching `list_callers`, `list_callees`, `find_implementations`).
- `list_members` default `limit` SHALL be 100.

Callers requiring more rows pass an explicit larger `limit`; the soft size budget continues to apply on top.

#### Scenario: find_references default returns 50 rows

- **WHEN** `find_references` is invoked without an explicit `limit` argument and the resolved symbol has more than 50 references in the graph
- **THEN** the response carries 50 reference rows (prose, link blocks, and structured array length all 50), matching the family-wide default

#### Scenario: list_members default returns 100 rows

- **WHEN** `list_members` is invoked without an explicit `limit` argument and the resolved container has more than 100 direct members
- **THEN** the response carries 100 member rows
