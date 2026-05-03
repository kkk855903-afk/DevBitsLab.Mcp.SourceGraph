# MCP Tools

## Purpose

Expose the code graph to MCP clients (Claude Code, Cursor, Continue, …) as a
set of stdio-callable tools so that an LLM coding agent can answer
symbol-level questions via one structured call instead of dozens of
`Grep` + `Read` operations.

## Requirements

### Requirement: Definition lookup
The server SHALL expose a `find_definition` tool that returns the location,
kind, and signature for every symbol matching a name or fully-qualified
name.

#### Scenario: Look up a class
- **WHEN** the agent invokes
  `find_definition(symbol = "Calculator")` against an indexed solution that
  contains `Sample.Domain.Calculator`
- **THEN** the response lists the class with file, line, column, and
  signature, plus any methods whose FQN contains "Calculator"

### Requirement: Reference lookup
The server SHALL expose a `find_references` tool that resolves a name to its
top match and returns every reference site for that symbol.

#### Scenario: Find callers and usages of a method
- **WHEN** the agent invokes `find_references(symbol = "Calculator.Add")`
- **THEN** the response includes the definition site plus every ref row in
  the graph that joins to that symbol's id, ordered by file path then line

### Requirement: File outline
The server SHALL expose a `list_symbols_in_file` tool that lists every
symbol declared in a single file.

#### Scenario: Outline a source file
- **WHEN** the agent invokes
  `list_symbols_in_file(path = "Calculator.cs")`
- **THEN** the response includes every symbol whose `file_id` joins to a
  files row whose path matches the suffix, ordered by `start_line`

### Requirement: Caller and callee enumeration
The server SHALL expose `list_callers` and `list_callees` tools that walk
the `Calls` edges in either direction from a named symbol.

#### Scenario: Trace upstream callers of a method
- **WHEN** the agent invokes `list_callers(symbol = "Calculator.Add")`
- **THEN** the response lists every symbol with an outgoing
  `EdgeKind.Calls` edge whose `dst` is the resolved id

#### Scenario: Trace downstream callees of a method
- **WHEN** the agent invokes `list_callees(symbol = "Calculator.Multiply")`
- **THEN** the response lists every symbol that `Multiply` calls based on
  its outgoing `EdgeKind.Calls` edges

### Requirement: Free-text symbol search
The server SHALL expose a `search_symbols` tool that runs an FTS5 trigram
match over `name`, `fqn`, and `signature`, optionally filtered by kind.

#### Scenario: Search by fragment
- **WHEN** the agent invokes `search_symbols(query = "Greet")`
- **THEN** the response lists every symbol whose name, FQN, or signature
  contains "Greet" (trigram-tokenized), ordered by FTS5 rank, capped by
  `topK` (default 25)

### Requirement: Local neighborhood
The server SHALL expose a `neighborhood` tool that returns the immediate
callers and callees of a symbol in one call, capped per category.

#### Scenario: Quick orientation around a symbol
- **WHEN** the agent invokes `neighborhood(symbol = "X", perCategory = 20)`
- **THEN** the response sections show up to 20 callers and 20 callees with
  file:line references, plus the symbol's own definition site

### Requirement: Module summary
The server SHALL expose a `module_summary` tool that ranks the symbols in a
namespace or path-substring by inbound call count.

#### Scenario: Top-K most-referenced symbols in a namespace
- **WHEN** the agent invokes `module_summary(namespaceOrPath = "X.Y")`
- **THEN** the response lists symbols whose `fqn = "X.Y"` or starts with
  `"X.Y."` (or whose file path contains the substring), each annotated with
  in-degree count, ordered by `in_degree DESC`, length(fqn), fqn

### Requirement: Impact of change
The server SHALL expose an `impact_of_change` tool that returns the
transitive set of upstream callers via a recursive CTE on the `Calls`
edges.

#### Scenario: Walk transitive callers
- **WHEN** the agent invokes
  `impact_of_change(symbol = "X", maxDepth = 4)`
- **THEN** the response lists every symbol within `maxDepth` hops upstream
  on the call graph, deduplicated, with the minimum depth at which each was
  reached

### Requirement: Self-reporting tool stats
The server SHALL expose `graph_stats` and `usage_stats` tools that report the
current graph counts and per-tool call activity respectively.

#### Scenario: Confirm the graph is populated
- **WHEN** the agent invokes `graph_stats()`
- **THEN** the response is a single line of the form
  `files=… symbols=… references=… edges=…` reflecting the live counts

#### Scenario: Confirm tools are being used
- **WHEN** the agent invokes `usage_stats()` after several other tool calls
- **THEN** the response is a markdown table with one row per tool that has
  fired in the current process: count, errors, avg/max latency, avg
  response size, and "X seconds ago"

### Requirement: Health probe
The server SHALL expose a `ping` tool that returns a fixed `pong @ <UTC>`
string for connectivity checks.

#### Scenario: Verify server reachability
- **WHEN** the agent invokes `ping()`
- **THEN** the response begins with `pong @` and contains an ISO-8601 UTC
  timestamp
