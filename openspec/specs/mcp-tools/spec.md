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
The server SHALL expose `list_callers` and `list_callees` tools that walk `calls` edges by default, with an optional `kind` parameter that accepts a kebab-case edge kind name (`calls | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all`) or any future kind exposed by the active scope's plugins, to filter the edge kind walked. The XAML edge kinds (`code-behind`, `binds-path`, `binds-element`, `handles-event`, `uses-resource`, `instantiates-type`, `merges`, `applies-style`) are part of the enumerable vocabulary on every scope that loads the XAML indexer. When an edge row carries a non-null `payload` JSON value, the rendered markdown SHALL include an indented `payload:` sub-line under the edge row, displaying up to the first five key/value pairs from the payload object; if more than five pairs are present, an `(N more)` suffix SHALL indicate the elision count.

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

#### Scenario: Find the codebehind of a XAML view
- **WHEN** the agent invokes `list_callees(symbol = "xaml:view:Views/MainWindow.xaml", kind = "code-behind")` against a scope that loaded the XAML indexer and indexed a WPF solution
- **THEN** the response lists the C# partial class symbol (`csharp:T:SampleWpf.Views.MainWindow`) as the resolved target

#### Scenario: List every binding to a viewmodel property (cross-language)
- **WHEN** the agent invokes `list_callers(symbol = "csharp:P:SampleWpf.ViewModels.MainViewModel.UserName", kind = "binds-path")`
- **THEN** the response lists every XAML element with a `binds-path` edge whose payload `path` resolves to `UserName` on the same target type, with each row's payload sub-line (per `harden-sdk-pre-xaml`) showing the `path`, `mode`, and `converter` values

#### Scenario: Edge with no payload renders unchanged
- **WHEN** `list_callers` returns an edge whose `payload` column is `NULL` (e.g. a built-in C# `calls` edge today)
- **THEN** the markdown for that row is exactly the pre-change output — no `payload:` sub-line, no behavioural difference

#### Scenario: Edge with payload renders sub-line
- **WHEN** `list_callers` returns an edge whose `payload` is `{"path":"User.Name","mode":"two-way","converter":"BoolToVisibility"}`
- **THEN** the markdown row is followed by an indented line of the form `    payload: { path: "User.Name", mode: "two-way", converter: "BoolToVisibility" }`

#### Scenario: Edge payload truncated when many keys
- **WHEN** `list_callers` returns an edge whose `payload` carries seven key/value pairs
- **THEN** the rendered `payload:` sub-line shows the first five keys and appends ` (2 more)` so the agent sees the truncation without inspecting the row separately

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

### Requirement: Neighborhood tool surfaces payload
The server SHALL expose a `neighborhood` tool that returns the immediate
callers and callees of a symbol in one call, capped per category. The `neighborhood` tool SHALL render the same `payload:` sub-line under every edge row in its output, applying the same five-key cap and `(N more)` suffix rule as `list_callers` and `list_callees`.

#### Scenario: Quick orientation around a symbol
- **WHEN** the agent invokes `neighborhood(symbol = "X", perCategory = 20)`
- **THEN** the response sections show up to 20 callers and 20 callees with
  file:line references, plus the symbol's own definition site

#### Scenario: Neighborhood result with mixed payload presence
- **WHEN** `neighborhood` returns three edges, one with payload and two without
- **THEN** only the row with payload carries the indented `payload:` sub-line; the other two render exactly as before

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
The server SHALL expose a `find_by_annotation` tool that returns symbols matching an annotation name and optional flavor, argument substring, and symbol kind filter. The legacy `find_by_attribute` tool SHALL NOT exist after this change; agents call `find_by_annotation(name = "...", flavor = "csharp-attribute", ...)` for the equivalent query. The flavor enumeration accepted by the `flavor` parameter SHALL include `xaml-attached-property` (in addition to `csharp-attribute`) on every scope that loads the XAML indexer.

#### Scenario: Find every POST endpoint
- **WHEN** the agent invokes `find_by_annotation(name = "HttpPost", flavor = "csharp-attribute")`
- **THEN** the response lists every symbol carrying a `csharp-attribute` annotation named `HttpPost`, with location and one-line summary

#### Scenario: Find a specific route
- **WHEN** the agent invokes `find_by_annotation(name = "HttpGet", flavor = "csharp-attribute", argValue = "/api/v2/users")`
- **THEN** the response is restricted to `csharp-attribute` annotations named `HttpGet` whose argument text matches `/api/v2/users` via trigram FTS

#### Scenario: Cross-flavor query
- **WHEN** the agent invokes `find_by_annotation(name = "Component")` (no flavor specified) against a polyglot scope
- **THEN** the response returns symbols whose annotations match `name = "Component"` across every flavor present in the scope, with each row tagged with the flavor that produced it

#### Scenario: Find every element with Grid.Row set
- **WHEN** the agent invokes `find_by_annotation(name = "Grid.Row", flavor = "xaml-attached-property")` against a scope that loaded the XAML indexer
- **THEN** the response lists every XAML element symbol carrying a `Grid.Row` attached property, with the value visible in the args column

#### Scenario: Cross-flavor query returns mixed results
- **WHEN** the agent invokes `find_by_annotation(name = "Background")` with no flavor specified, against a scope where the C# indexer emits `csharp-attribute` annotations and the XAML indexer emits `xaml-attached-property` annotations
- **THEN** any annotation with `name == "Background"` from either flavor appears in the response, each row tagged with its flavor

### Requirement: Symbol kind enumeration in tool parameters
The kind parameter on `list_symbols_in_file`, `find_definition`, and `module_summary` SHALL accept (in addition to the C# kinds documented by `open-language-contract`) the new XAML symbol kinds: `xaml-view`, `xaml-element`, `xaml-resource`, `xaml-style`, `xaml-template`. The expanded enumeration appears in the parameter doc on every scope that loads the XAML indexer.

#### Scenario: List every XAML view in a project
- **WHEN** the agent invokes `list_symbols_in_file(file = "Views/MainWindow.xaml")` against a scope that loaded the XAML indexer
- **THEN** the response includes the `xaml-view` symbol for the file root plus any `xaml-element`, `xaml-resource`, `xaml-style`, or `xaml-template` symbols declared inside

#### Scenario: Find definition of a XAML resource
- **WHEN** the agent invokes `find_definition(symbol = "xaml:resource:App.xaml#AccentBrush")`
- **THEN** the response includes the resource's declaration site (file path, line, column) plus every `uses-resource` edge that targets it (via the existing reference-listing behaviour)

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

### Requirement: Scope identity in result rows
Every result row from a scope-aware query SHALL include the originating scope (or sorted list of scopes) so the agent can filter on its side without a second call.

#### Scenario: Result row carries scope
- **WHEN** `find_references(symbol = "X", scope = "*")` returns rows from two scopes
- **THEN** each row's markdown includes `scope: <name>` (or `scope: [<a>, <b>]` for canonical_key dedup)

### Requirement: Server-published usage instructions
The server SHALL publish a non-empty `ServerInstructions` string in the MCP `initialize` response by default. The string SHALL convey two things to a connected model: (1) a directive to prefer source-graph tools over `Grep` + `Read` for symbol-level questions, and (2) a closing directive to call `usage_stats` at end-of-turn to verify the graph was actually queried. When brand-mark suppression is not active (see *Brand-mark suppression*), the published string SHALL be prefixed with `🌿 ` (U+1F33F U+0020) so that a connecting client learns the leaf-glyph-to-`sourcegraph` association from the initialize handshake.

#### Scenario: Client reads instructions on connect
- **WHEN** an MCP client (`McpClient`) completes the initialize handshake against a freshly started `sourcegraph-mcp serve` process
- **THEN** the client's `ServerInstructions` property contains both the preamble keyword (`prefer` or equivalent guidance against `Grep`+`Read`) and the epilogue keyword (`usage_stats`)

#### Scenario: Instructions string starts with the leaf brand mark
- **WHEN** an MCP client reads `ServerInstructions` from the initialize response with neither `--no-leaf` nor `SOURCEGRAPH_NO_LEAF` set
- **THEN** the string starts with the byte sequence `🌿 ` (U+1F33F U+0020), and the cross-cutting guidance follows

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

### Requirement: Tool response brand mark
The server SHALL prefix the first line of every built-in MCP tool's response text with the green-leaf glyph `🌿` (U+1F33F) followed by a single space character (U+0020), before that response is shipped to the MCP client. The prefix SHALL apply uniformly to success responses, empty-result responses, and any error-string responses (i.e. any response a tool body returns as a `string` or `Task<string>`). Plugin-registered tools (registered via `IToolRegistry.AddTool` in `Plugins/ToolRegistry.cs`) SHALL NOT receive the brand-mark prefix, so plugin-authored output preserves its own voice.

#### Scenario: Built-in tool response leads with the leaf
- **WHEN** an MCP client invokes `find_definition(symbol = "Calculator")` against an indexed solution that contains `Sample.Domain.Calculator`
- **THEN** the response text starts with the byte sequence `🌿 ` (U+1F33F followed by U+0020), and the existing markdown content follows on the same line

#### Scenario: Empty-result response also leads with the leaf
- **WHEN** an MCP client invokes `find_definition(symbol = "Nonexistent")` against a graph with no matches
- **THEN** the response text starts with `🌿 ` followed by the no-match message (e.g. `🌿 No matches for 'Nonexistent'.`)

#### Scenario: Plugin tool response is not brand-marked
- **WHEN** an MCP client invokes a tool registered through `IToolRegistry.AddTool` (e.g. a plugin-supplied `xaml.find_view`) and the plugin's handler returns a string
- **THEN** the response text is the plugin's string verbatim, with no leading `🌿 ` prefix

#### Scenario: Brand-mark prefix is idempotent
- **WHEN** a built-in tool's body returns a string whose first characters are already `🌿 ` (e.g. due to internal pre-stamping)
- **THEN** the shipped response contains exactly one `🌿 ` prefix, not two stacked leaves

### Requirement: Brand-mark suppression
The server SHALL accept `--no-leaf` as a CLI flag on `sourcegraph-mcp serve` and SHALL honour `SOURCEGRAPH_NO_LEAF` as an environment variable (truthy values: exact `1`, or `true` case-insensitive — same convention as `SOURCEGRAPH_NO_INSTRUCTIONS`). When either is set, the server SHALL omit the brand-mark prefix from every built-in tool response AND SHALL omit the brand-mark prefix from the published `ServerInstructions` string. The two suppression mechanisms (`--no-leaf` and `--no-instructions`) compose independently: turning off one SHALL NOT turn off the other.

#### Scenario: Suppression via flag
- **WHEN** the server is started with `--no-leaf`
- **THEN** built-in tool responses contain no leading `🌿 ` and the published `ServerInstructions` string (if any) contains no leading `🌿 `

#### Scenario: Suppression via env var
- **WHEN** the server is started without `--no-leaf` but with `SOURCEGRAPH_NO_LEAF=1` in env
- **THEN** built-in tool responses contain no leading `🌿 ` and the published `ServerInstructions` string (if any) contains no leading `🌿 `

#### Scenario: Leaf suppression independent of instructions suppression
- **WHEN** the server is started with `--no-leaf` but WITHOUT `--no-instructions`
- **THEN** the `ServerInstructions` string is published, the rest of its cross-cutting guidance is intact, but it carries no leading `🌿 ` prefix

#### Scenario: Suppression knobs compose
- **WHEN** the server is started with both `--no-leaf` and `--no-instructions`
- **THEN** the `initialize` response carries no `ServerInstructions` string at all, and built-in tool responses carry no `🌿 ` prefix

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

### Requirement: Progress notifications on slow tools
Tools whose work has multi-second tails on representative inputs SHALL accept an `IProgress<ProgressNotificationValue>` parameter and emit progress at coarse, named checkpoints during the call. Each emitted `ProgressNotificationValue` SHALL set `Total = 1.0`, a `Progress` value in the inclusive range `[0.0, 1.0]` that is monotonically increasing across the call, and a short imperative `Message` (e.g. `"encoding query"`, `"searching"`, `"querying"`) that contains no user-controlled substrings.

The set of tools opted in by this requirement is `semantic_search`, `impact_of_change`, and `module_summary`. Other tools MAY add the parameter in future changes when their measured latency justifies it.

When an MCP client did not include a `progressToken` on the originating `tools/call` request, the SDK SHALL inject a no-op `IProgress<ProgressNotificationValue>` instance so tool bodies that call `Report(...)` unconditionally incur no wire-level overhead.

#### Scenario: semantic_search emits encoding, searching, and formatting checkpoints
- **WHEN** an MCP client invokes `semantic_search(query = "...")` and includes a `progressToken` on the request
- **THEN** the server emits three `notifications/progress` messages over the call's lifetime, in order: `Progress = 0.0` with `Message = "encoding query"`, `Progress = 0.5` with `Message = "searching"`, and `Progress = 0.9` with `Message = "formatting results"`; the request's final `tools/call` response carries the search results as today

#### Scenario: impact_of_change emits a starting checkpoint
- **WHEN** an MCP client invokes `impact_of_change(symbol = "...", maxDepth = 6)` with a `progressToken` on the request
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins; the final response carries the impact set as today

#### Scenario: module_summary emits a starting checkpoint
- **WHEN** an MCP client invokes `module_summary(namespaceOrPath = "...")` with a `progressToken` on the request
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins

#### Scenario: No progress emitted when client did not opt in
- **WHEN** an MCP client invokes any of `semantic_search`, `impact_of_change`, `module_summary` WITHOUT a `progressToken` on the request
- **THEN** the server emits zero `notifications/progress` messages for the call; the tool result returns identically to today's behaviour

#### Scenario: Progress values are monotonically increasing
- **WHEN** any opted-in tool emits two or more progress notifications during a single call
- **THEN** each successive notification's `Progress` value is strictly greater than the previous notification's `Progress`, with all values in the closed interval `[0.0, 1.0]`

#### Scenario: Progress messages carry no user input
- **WHEN** any progress notification is emitted by an opted-in tool
- **THEN** its `Message` string is one of the documented short imperatives (`"encoding query"`, `"searching"`, `"formatting results"`, `"querying"`) and does not interpolate any caller-supplied argument value (symbol name, query text, file path, etc.) into the message

### Requirement: Always-render-payload pattern is consistent across tools
Any MCP tool that renders per-edge result rows SHALL use the same indented `payload:` sub-line pattern, the same five-key cap, and the same `(N more)` suffix when payload truncation occurs. New tools MUST NOT invent alternative payload rendering shapes; the consistency lets agents and humans skim multi-tool output without re-learning the format.

#### Scenario: Future tool emits per-edge rows
- **WHEN** a new MCP tool that walks edges (e.g. an `inspect_edge` follow-up) renders results
- **THEN** its row format includes the same `payload:` sub-line shape with the same truncation rule, by reusing the shared rendering helper rather than implementing a parallel format

