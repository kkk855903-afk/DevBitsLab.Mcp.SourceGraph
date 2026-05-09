## ADDED Requirements

### Requirement: Tabular rendering for list-shaped tool results
Every built-in tool whose result is a list of homogeneous rows SHALL render those rows as a GitHub-Flavored-Markdown (GFM) table when the row count is two or greater. The table SHALL begin with a header row enumerating the columns, followed by a separator row carrying alignment cues for any numeric column, followed by one data row per result. Single-result responses (one row) MAY remain bulleted prose so the table-chrome overhead is not paid for one-row data.

The first line of every tool response SHALL remain a substantive prose summary so the leaf brand-mark prefix from `add-leaf-brand-mark` lands on prose rather than on table chrome.

Cells containing the pipe character (`|`) — file paths, symbol identifiers — SHALL escape it to `\|` so a literal pipe in the data does not break table parsing in the consuming client.

Tools whose result is hierarchical (each row carries nested signature, summary, annotations, or history) — `find_definition`, `list_symbols_in_file` — SHALL retain their existing bulleted prose rendering. Tools that already render tables — `usage_stats`, `list_scopes`, `list_generated_files`, `graph_stats` — are unchanged.

#### Scenario: find_references with multiple references
- **WHEN** the agent invokes `find_references(symbol = "X")` against a graph that has 4 references to `X`
- **THEN** the response begins with a leaf-prefixed summary line (e.g. `🌿 4 references to **X** (class):`), followed by the definition line, followed by a GFM table with header `| Kind | Location |` and four data rows, one per reference

#### Scenario: find_references with a single reference falls back to prose
- **WHEN** `find_references(symbol = "Y")` returns one reference
- **THEN** the response renders the single reference as a bulleted line (no table)

#### Scenario: search_symbols with multiple hits
- **WHEN** `search_symbols(query = "Calc")` returns 6 hits
- **THEN** the response renders a `| Symbol | Kind | Location |` table with six data rows

#### Scenario: list_callers / list_callees / find_implementations table shape
- **WHEN** any of `list_callers`, `list_callees`, `find_implementations` returns two or more rows
- **THEN** the response renders a `| Symbol | Kind | Location |` table

#### Scenario: list_members table shape
- **WHEN** `list_members(container = "X")` returns two or more members
- **THEN** the response renders a `| Member | Kind | Signature |` table

#### Scenario: semantic_search table shape with right-aligned score column
- **WHEN** `semantic_search(query = "...")` returns two or more semantic hits
- **THEN** the response renders a `| Score | Symbol | Kind | Location |` table whose `Score` column header separator carries right-alignment (`---:`)

#### Scenario: find_diagnostics table shape
- **WHEN** `find_diagnostics(...)` returns two or more diagnostics
- **THEN** the response renders a `| Severity | Code | Location | Message |` table

#### Scenario: recent_changes table shape
- **WHEN** `recent_changes(...)` returns two or more rows
- **THEN** the response renders a `| When | Author | Symbol | Location |` table

#### Scenario: list_tests_for table shape
- **WHEN** `list_tests_for(symbol = "...")` returns two or more tests
- **THEN** the response renders a `| Framework | Test | Location |` table

#### Scenario: impact_of_change table shape with right-aligned depth column
- **WHEN** `impact_of_change(symbol = "...")` returns two or more upstream callers
- **THEN** the response renders a `| Depth | Symbol | Kind | Location |` table whose `Depth` column header separator carries right-alignment (`---:`)

#### Scenario: module_summary table shape with right-aligned in-degree column
- **WHEN** `module_summary(namespaceOrPath = "...")` returns two or more rows
- **THEN** the response renders a `| In-deg | Symbol | Kind | Location |` table whose `In-deg` column header separator carries right-alignment (`---:`)

#### Scenario: find_by_annotation table shape
- **WHEN** `find_by_annotation(name = "...")` returns two or more symbols
- **THEN** the response renders a `| Symbol | Kind | Location |` table

#### Scenario: neighborhood Inbound and Outbound sections render as tables
- **WHEN** `neighborhood(symbol = "X")` returns at least two inbound or outbound rows in a category
- **THEN** that category's `### Inbound (N)` / `### Outbound (N)` header is followed by a `| Symbol | Kind | Location |` table; categories with one or zero rows render as today's bulleted shape

#### Scenario: Cell pipe escaping
- **WHEN** a result row's symbol or file path contains a literal `|` character (rare but legal in arbitrary FQNs / paths)
- **THEN** the rendered table cell escapes that character as `\|` so the pipe is rendered literally and does not split the cell

#### Scenario: Fan-out scope tag in tabular rendering
- **WHEN** `find_references(symbol = "X", scope = "*")` produces a multi-scope merged table
- **THEN** each row's `Symbol` cell carries the inline scope annotation (`\`Symbol.Name\` — scope: \`<id>\``) so the existing per-row scope contract from "Scope identity in result rows" is preserved
