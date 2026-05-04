## ADDED Requirements

### Requirement: Member enumeration tool
The server SHALL expose a `list_members` tool that returns the symbols whose `container_id` chain matches a named container, optionally including inherited members and filtered by accessibility.

#### Scenario: List class members
- **WHEN** the agent invokes `list_members(container = "Sample.Domain.Calculator")`
- **THEN** the response lists every symbol whose `container_id` resolves to `Calculator`'s row id, ordered by `start_line`

#### Scenario: Filter by accessibility
- **WHEN** the agent invokes `list_members(container = "X", accessibility = "public")`
- **THEN** only public members are returned

## MODIFIED Requirements

### Requirement: Definition lookup
The server SHALL expose a `find_definition` tool that returns the location, kind, signature, accessibility, modifiers, and (when present) one-line XML summary for every symbol matching a name or fully-qualified name.

#### Scenario: Look up a class
- **WHEN** the agent invokes `find_definition(symbol = "Calculator")` against an indexed solution that contains `Sample.Domain.Calculator`
- **THEN** the response lists the class with file, line, column, signature, accessibility, modifiers, and (if any) the first sentence of its XML summary

### Requirement: File outline
The server SHALL expose a `list_symbols_in_file` tool that lists every symbol declared in a single file with kind, accessibility, modifiers, and one-line XML summary.

#### Scenario: Outline a source file
- **WHEN** the agent invokes `list_symbols_in_file(path = "Calculator.cs")`
- **THEN** the response includes every symbol whose `file_id` joins to a files row whose path matches the suffix, ordered by `start_line`, each annotated with kind, accessibility, modifiers, and (if any) XML summary first sentence

### Requirement: Free-text symbol search
The server SHALL expose a `search_symbols` tool that runs an FTS5 trigram match over `name`, `fqn`, `signature`, and `xml_summary`, optionally filtered by kind.

#### Scenario: Search by description fragment
- **WHEN** the agent invokes `search_symbols(query = "retry")` against a graph containing a method documented as `/// <summary>Retries the request on transient errors.</summary>`
- **THEN** that method is in the response, found via the `xml_summary` FTS column
