## ADDED Requirements

### Requirement: list_tests_for tool
The server SHALL expose a `list_tests_for(symbol, includeIndirect = false, limit = 50)` tool that returns the test methods linked to a target via `Tests` edges.

#### Scenario: Direct test list
- **WHEN** the agent invokes `list_tests_for(symbol = "Calculator.Add")` against a graph where one `[Fact]` test directly exercises that method
- **THEN** the response lists that test with its file:line, framework, and the test class name

### Requirement: who_authored tool
The server SHALL expose a `who_authored(symbol)` tool that returns the last commit sha, author, and authored time for a symbol from the `symbol_history` cache.

#### Scenario: Authored info available
- **WHEN** the agent invokes `who_authored(symbol = "Calculator.Add")` and `symbol_history` has the row
- **THEN** the response is a single-line string with `<sha>` (truncated to 7 chars), author, ISO-8601 authored time, lines blamed

#### Scenario: History disabled
- **WHEN** the server was started with `--no-history`
- **THEN** the response is "git history unavailable on this server (--no-history)"

### Requirement: recent_changes tool
The server SHALL expose a `recent_changes(days = 7, author? = null, limit = 50)` tool that returns symbols whose `last_authored_at` falls within the window.

#### Scenario: Last week's changes
- **WHEN** the agent invokes `recent_changes(days = 7)`
- **THEN** the response lists every indexed symbol whose `last_authored_at` is within the past 7 days, ordered by recency
