# MCP Tools

## Purpose

Expose the code graph to MCP clients (Claude Code, Cursor, Continue, …) as a
set of stdio-callable tools so that an LLM coding agent can answer
symbol-level questions via one structured call instead of dozens of
`Grep` + `Read` operations.

## Requirements

### Requirement: Definition lookup
The server SHALL expose a `find_definition` tool that returns the location, kind, signature, accessibility, modifiers, and (when present) one-line XML summary for every symbol matching a name or fully-qualified name.

#### Scenario: Look up a class
- **WHEN** the agent invokes `find_definition(symbol = "Calculator")` against an indexed solution that contains `Sample.Domain.Calculator`
- **THEN** the response lists the class with file, line, column, signature, accessibility, modifiers, and (if any) the first sentence of its XML summary

### Requirement: Reference lookup
The server SHALL expose a `find_references` tool that returns every reference site for a symbol, surfacing the resolved `ReferenceKind` (`def`, `ref`, `call`, `read`, `write`, `impl`, `inherit`) per row, with an optional `include_generated` parameter (default `false`) that filters out references coming from source-generated files.

#### Scenario: Distinguish reads and writes
- **WHEN** the agent invokes `find_references(symbol = "_state")` against a graph where `_state` is read in one place and written in two
- **THEN** the response includes one row with `kind = read` and two rows with `kind = write`, each with file:line

#### Scenario: Default excludes generated
- **WHEN** the agent invokes `find_references(symbol = "MyVm.Title")` against a graph that includes generated `OnPropertyChanged` references
- **THEN** the response excludes those generated rows by default; passing `include_generated = true` includes them

### Requirement: File outline
The server SHALL expose a `list_symbols_in_file` tool that lists every symbol declared in a single file with kind, accessibility, modifiers, and one-line XML summary.

#### Scenario: Outline a source file
- **WHEN** the agent invokes `list_symbols_in_file(path = "Calculator.cs")`
- **THEN** the response includes every symbol whose `file_id` joins to a files row whose path matches the suffix, ordered by `start_line`, each annotated with kind, accessibility, modifiers, and (if any) XML summary first sentence

### Requirement: Caller and callee enumeration
The server SHALL expose `list_callers` and `list_callees` tools that walk `calls` edges by default, with an optional `kind` parameter that accepts a kebab-case edge kind name (`calls | uses-type | overrides-member | implements-member | instantiates | throws | tests | all`) or any future kind exposed by the active scope's plugins, to filter the edge kind walked.

#### Scenario: List callers (default = calls)
- **WHEN** the agent invokes `list_callers(symbol = "Calculator.Add")`
- **THEN** the response lists every symbol with an outgoing edge whose `kind_name = 'calls'` and whose `dst` is the resolved id

#### Scenario: List consumers via uses-type
- **WHEN** the agent invokes `list_callers(symbol = "CancellationToken", kind = "uses-type")`
- **THEN** the response lists every symbol whose edge with `kind_name = 'uses-type'` targets the resolved type id

#### Scenario: Plugin-defined kind
- **WHEN** the agent invokes `list_callers(symbol = "MyButton", kind = "renders-component")` against a scope whose loaded indexers emit `renders-component` edges
- **THEN** the response lists every symbol whose edge with `kind_name = 'renders-component'` targets `MyButton`

#### Scenario: Unknown kind reported back
- **WHEN** the agent invokes `list_callers(symbol = "X", kind = "not-a-real-kind")` against a scope where no indexer emits `not-a-real-kind`
- **THEN** the response is an empty result set with a brief note that the kind was not present in the active scope's published `edge_kinds` vocabulary (which unions the SDK constants with the scope's stored kinds, so a built-in kind like `"calls"` is never reported as unknown even on a never-indexed scope)

### Requirement: Free-text symbol search
The server SHALL expose a `search_symbols` tool that runs an FTS5 trigram match over `name`, `fqn`, `signature`, and `xml_summary`, optionally filtered by kind.

#### Scenario: Search by fragment
- **WHEN** the agent invokes `search_symbols(query = "Greet")`
- **THEN** the response lists every symbol whose name, FQN, or signature
  contains "Greet" (trigram-tokenized), ordered by FTS5 rank, capped by
  `topK` (default 25)

#### Scenario: Search by description fragment
- **WHEN** the agent invokes `search_symbols(query = "retry")` against a graph containing a method documented as `/// <summary>Retries the request on transient errors.</summary>`
- **THEN** that method is in the response, found via the `xml_summary` FTS column

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

### Requirement: Member enumeration tool
The server SHALL expose a `list_members` tool that returns the symbols whose `container_id` chain matches a named container, optionally including inherited members and filtered by accessibility.

#### Scenario: List class members
- **WHEN** the agent invokes `list_members(container = "Sample.Domain.Calculator")`
- **THEN** the response lists every symbol whose `container_id` resolves to `Calculator`'s row id, ordered by `start_line`

#### Scenario: Filter by accessibility
- **WHEN** the agent invokes `list_members(container = "X", accessibility = "public")`
- **THEN** only public members are returned

### Requirement: find_by_annotation tool
The server SHALL expose a `find_by_annotation` tool that returns symbols matching an annotation name and optional flavor, argument substring, and symbol kind filter. The legacy `find_by_attribute` tool SHALL NOT exist after this change; agents call `find_by_annotation(name = "...", flavor = "csharp-attribute", ...)` for the equivalent query.

#### Scenario: Find every POST endpoint
- **WHEN** the agent invokes `find_by_annotation(name = "HttpPost", flavor = "csharp-attribute")`
- **THEN** the response lists every symbol carrying a `csharp-attribute` annotation named `HttpPost`, with location and one-line summary

#### Scenario: Find a specific route
- **WHEN** the agent invokes `find_by_annotation(name = "HttpGet", flavor = "csharp-attribute", argValue = "/api/v2/users")`
- **THEN** the response is restricted to `csharp-attribute` annotations named `HttpGet` whose argument text matches `/api/v2/users` via trigram FTS

#### Scenario: Cross-flavor query
- **WHEN** the agent invokes `find_by_annotation(name = "Component")` (no flavor specified) against a polyglot scope
- **THEN** the response returns symbols whose annotations match `name = "Component"` across every flavor present in the scope, with each row tagged with the flavor that produced it

### Requirement: Annotations surfaced in existing tool output
`find_definition`, `list_symbols_in_file`, `neighborhood`, and `module_summary` SHALL include an `annotations:` line per result that lists each attached annotation's name (with truncated arg preview when present and a flavor tag when the scope has more than one flavor present), so an agent reads `[HttpGet("/api/users"), Authorize]` without a second call.

#### Scenario: Annotated method in find_definition output (single-flavor scope)
- **WHEN** `find_definition` returns a method that carries `[HttpGet("/api/users")]` and `[Authorize]` in a scope whose only flavor is `csharp-attribute`
- **THEN** the markdown for that result includes a line like `annotations: [HttpGet("/api/users"), Authorize]` (no flavor tags appended; the scope is single-flavor)

#### Scenario: Annotated symbol in a polyglot scope
- **WHEN** the same query runs in a scope where multiple flavors are present (e.g. `csharp-attribute` and `xaml-attached-property`)
- **THEN** each annotation in the markdown is suffixed with its flavor in parentheses, e.g. `annotations: [HttpGet("/api/users") (csharp-attribute), Grid.Row=2 (xaml-attached-property)]`

### Requirement: find_implementations tool
The server SHALL expose a `find_implementations` tool that returns every member linked to a named interface member via `ImplementsMember` edges.

#### Scenario: Concrete implementations of an interface method
- **WHEN** the agent invokes `find_implementations(symbol = "IGreeter.Greet")` against a graph that has two implementing classes `Greeter` and `LoudGreeter`
- **THEN** the response lists both `Greeter.Greet` and `LoudGreeter.Greet` with their definition locations

### Requirement: Semantic search tool
The server SHALL expose a `semantic_search` tool whose intent is fuzzy intent retrieval (not name-fragment matching, which `search_symbols` covers).

#### Scenario: Find code by intent
- **WHEN** the agent invokes `semantic_search(query = "logging that masks PII")`
- **THEN** the response is a top-k list of symbols ranked by cosine similarity to the query embedding, each annotated with location, score, and a one-line snippet

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

### Requirement: Optional scope parameter on every existing tool
Every existing query tool (`find_definition`, `find_references`, `list_symbols_in_file`, `list_callers`, `list_callees`, `search_symbols`, `neighborhood`, `module_summary`, `impact_of_change`, `graph_stats`, `usage_stats`) SHALL accept an optional `scope` parameter (a string id, a string array, or the literal `"*"` for all non-isolated scopes).

#### Scenario: Default behaviour
- **WHEN** any tool is invoked without a `scope` argument
- **THEN** the query runs against the configured `default_scope` (or the single registered scope when none is configured), and the response notes the implicit scope it queried

#### Scenario: Explicit single scope
- **WHEN** a tool is invoked with `scope = "backend"`
- **THEN** results come from the `backend` scope only

#### Scenario: Explicit multi-scope
- **WHEN** a tool is invoked with `scope = ["frontend", "backend"]`
- **THEN** results from both scopes are merged by `canonical_key`, each row tagged with the scopes it came from

#### Scenario: Wildcard scope
- **WHEN** a tool is invoked with `scope = "*"`
- **THEN** results come from every non-isolated scope; isolated scopes are excluded unless listed explicitly

### Requirement: Scope identity in result rows
Every result row from a scope-aware query SHALL include the originating scope (or sorted list of scopes) so the agent can filter on its side without a second call.

#### Scenario: Result row carries scope
- **WHEN** `find_references(symbol = "X", scope = "*")` returns rows from two scopes
- **THEN** each row's markdown includes `scope: <name>` (or `scope: [<a>, <b>]` for canonical_key dedup)

### Requirement: Server-published usage instructions
The server SHALL publish a non-empty `ServerInstructions` string in the MCP `initialize` response by default. The string SHALL convey two things to a connected model: (1) a directive to prefer source-graph tools over `Grep` + `Read` for symbol-level questions, and (2) a closing directive to call `usage_stats` at end-of-turn to verify the graph was actually queried.

#### Scenario: Client reads instructions on connect
- **WHEN** an MCP client (`McpClient`) completes the initialize handshake against a freshly started `sourcegraph-mcp serve` process
- **THEN** the client's `ServerInstructions` property contains both the preamble keyword (`prefer` or equivalent guidance against `Grep`+`Read`) and the epilogue keyword (`usage_stats`)

#### Scenario: Instructions suppressed via flag
- **WHEN** the server is started with `--no-instructions`
- **THEN** the `initialize` response carries no instructions string (null or empty)

#### Scenario: Instructions suppressed via env var
- **WHEN** the server is started without `--no-instructions` but with `SOURCEGRAPH_NO_INSTRUCTIONS=1` in env
- **THEN** the `initialize` response carries no instructions string

### Requirement: ToolTrigger attribute on built-in tools
The server SHALL define a `[ToolTrigger("…")]` attribute applicable to MCP tool methods, and at tool registration time SHALL append a `Use when: <trigger>` line as the final paragraph of each annotated tool's effective description before that description is surfaced via `tools/list`.

#### Scenario: Triggered tool surfaces its trigger in the catalog
- **WHEN** a tool method is annotated `[McpServerTool] [Description("Find the definition of a symbol.")] [ToolTrigger("\"where is X defined?\"")]` and the server starts
- **THEN** the catalog entry for that tool renders a description whose final line is `Use when: "where is X defined?"`

#### Scenario: Untriggered tool description is unchanged
- **WHEN** a tool method is annotated `[McpServerTool] [Description("...")]` with no `[ToolTrigger]`
- **THEN** the catalog entry's description matches the `[Description]` text verbatim, with no appended line

### Requirement: Trigger phrases co-located with their tool, not in instructions
The `ServerInstructions` string SHALL NOT enumerate a question-to-tool table. Trigger phrases live exclusively on each tool's effective description. This requirement constrains the format of `ServerInstructions` to cross-cutting guidance only.

#### Scenario: Instructions contain no per-tool trigger table
- **WHEN** a client reads `ServerInstructions` from the initialize response
- **THEN** the string MAY reference tool names by example but SHALL NOT carry a markdown table or structured list mapping question phrases to tool names

### Requirement: Vocabulary published in MCP initialize response
The MCP `initialize` response SHALL include three top-level string arrays alongside the existing `ServerInstructions` payload: `edge_kinds`, `symbol_kinds`, and `annotation_flavors`, plus a `scopes` map keyed by scope id whose values are per-scope `{ edge_kinds, symbol_kinds, annotation_flavors }` triples. The top-level arrays SHALL be the **server-wide union** across every configured scope; the per-scope entries SHALL be the union of the SDK's kebab-case constants (`EdgeKinds` / `SymbolKinds`), constants declared by loaded plugins, and the distinct values already present in that specific scope's storage. All arrays are sorted lowercase and deduplicated.

#### Scenario: Single-language scope vocabulary
- **WHEN** an MCP client completes the initialize handshake against a scope whose only loaded indexer is the built-in C# Roslyn indexer
- **THEN** `edge_kinds` is the sorted distinct union of the built-in C# constants (`["calls", "implements", "implements-member", "inherits", "instantiates", "overrides-member", "tests", "throws", "uses-type"]`); `symbol_kinds` is the corresponding C# symbol set; `annotation_flavors` is `["csharp-attribute"]`

#### Scenario: Vocabulary suppressed alongside instructions
- **WHEN** the server is started with `--no-instructions` (or `SOURCEGRAPH_NO_INSTRUCTIONS=1`)
- **THEN** the `initialize` response carries no `edge_kinds` / `symbol_kinds` / `annotation_flavors` arrays (the existing instructions suppression also suppresses the vocabulary)

#### Scenario: Polyglot scope vocabulary
- **WHEN** the active scope additionally loads a plugin that emits `renders-component` and `binds-path` edges and a `xaml-element` symbol kind
- **THEN** the `edge_kinds` array additionally contains `"binds-path"` and `"renders-component"`, and `symbol_kinds` additionally contains `"xaml-element"`, both still sorted lowercase
