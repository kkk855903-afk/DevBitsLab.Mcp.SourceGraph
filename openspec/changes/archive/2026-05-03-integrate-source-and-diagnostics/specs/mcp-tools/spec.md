## ADDED Requirements

### Requirement: find_diagnostics tool
The server SHALL expose a `find_diagnostics(severity? = "warning", code?, symbol?, limit = 100)` tool that returns Roslyn diagnostic rows filtered by severity, code, and/or attached symbol.

#### Scenario: Listing all errors
- **WHEN** the agent invokes `find_diagnostics(severity = "error")`
- **THEN** the response lists every diagnostic with severity `>= Error`, ordered by file then line, with code, message, file:line

#### Scenario: Filter by diagnostic code
- **WHEN** the agent invokes `find_diagnostics(code = "CS0618")`
- **THEN** the response is restricted to obsolete-usage warnings

### Requirement: list_generated_files tool
The server SHALL expose a `list_generated_files(limit = 100)` tool returning every file row whose `is_generated = 1`, with path and (when available) the symbol count emitted from that file.

#### Scenario: Quick scan of generated code
- **WHEN** the agent invokes `list_generated_files()`
- **THEN** the response is a table with each generated file's path and symbol count, ordered by symbol count descending

## MODIFIED Requirements

### Requirement: Reference lookup
The server SHALL expose a `find_references` tool that returns every reference site for a symbol, with an optional `include_generated` parameter (default `false`) that filters out references coming from source-generated files.

#### Scenario: Default excludes generated
- **WHEN** the agent invokes `find_references(symbol = "MyVm.Title")` against a graph that includes generated `OnPropertyChanged` references
- **THEN** the response excludes those generated rows by default; passing `include_generated = true` includes them
