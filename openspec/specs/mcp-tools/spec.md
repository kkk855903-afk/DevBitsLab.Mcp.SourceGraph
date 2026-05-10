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
The server SHALL prefix the first user-visible `TextContentBlock` of every built-in MCP tool's response with the green-leaf glyph `🌿` (U+1F33F) followed by a single space character (U+0020), before the response is shipped to the MCP client. The chokepoint SHALL search the content list for the first text block whose `annotations.audience` is null, empty, or contains `Role.User` (skipping audience-restricted blocks); the brand mark attaches to the first match regardless of position relative to non-text blocks. The prefix SHALL apply uniformly to success responses, empty-result responses, and any error-string responses. When a tool returns a single-string body (legacy path, plus `PingTool` and any plugin-style return), the prefix applies to that string verbatim. When a content list contains zero user-visible text blocks (only resource links, only audience-restricted text, etc.), no prefix is applied. When the leaf chokepoint is suppressed (`--no-leaf` / `SOURCEGRAPH_NO_LEAF=1`), no prefix is applied regardless of return type. Plugin-registered tools (registered via `IToolRegistry.AddTool`) SHALL NOT receive the brand-mark prefix.

#### Scenario: Built-in tool response leads with the leaf
- **WHEN** an MCP client invokes `find_definition(symbol = "Calculator")` against an indexed solution that contains `Sample.Domain.Calculator`
- **THEN** the response's first `TextContentBlock.text` starts with the byte sequence `🌿 ` (U+1F33F followed by U+0020), and the existing markdown content follows on the same line

#### Scenario: Empty-result response also leads with the leaf
- **WHEN** an MCP client invokes `find_definition(symbol = "Nonexistent")` against a graph with no matches
- **THEN** the response's first `TextContentBlock.text` starts with `🌿 ` followed by the no-match message (e.g. `🌿 No matches for 'Nonexistent'.`)

#### Scenario: Plugin tool response is not brand-marked
- **WHEN** an MCP client invokes a tool registered through `IToolRegistry.AddTool` (e.g. a plugin-supplied `xaml.find_view`) and the plugin's handler returns a string or content list
- **THEN** the response is shipped verbatim, with no leading `🌿 ` on any block

#### Scenario: Brand-mark prefix is idempotent on text bodies
- **WHEN** a built-in tool's body returns a string (or first text block) whose first characters are already `🌿 ` (e.g. due to internal pre-stamping)
- **THEN** the shipped response contains exactly one `🌿 ` prefix, not two stacked leaves

#### Scenario: Audience-restricted block is not brand-marked
- **WHEN** a tool's content list contains a `TextContentBlock` with `annotations.audience = ["assistant"]`
- **THEN** the chokepoint never stamps the brand mark on that block; the prefix applies only to the first user-visible (non-audience-restricted) `TextContentBlock`

#### Scenario: First block is not text
- **WHEN** a tool returns a content list whose first item is a `ResourceLinkBlock` (or other non-text block) followed later by a user-visible `TextContentBlock`
- **THEN** the chokepoint walks the list and prefixes the user-visible text block with `🌿 ` regardless of its position; the non-text item earlier in the list is unchanged. Chokepoint behaviour is documented in `LeafFormatter.BrandFirstText`.

### Requirement: Tool identity brand mark
The server SHALL stamp every built-in MCP tool's catalog identity with the green-leaf brand mark before that tool is advertised to clients via `tools/list`. Two `Tool` fields carry the mark:

- `Tool.Title` SHALL be set to `"🌿 " + Tool.Name` (the U+1F33F glyph followed by U+0020 followed by the tool's snake_case name as already populated on `Tool.Name`).
- `Tool.Description` SHALL be prepended with `"🌿 "` (U+1F33F U+0020) so the existing description text follows the brand mark on the same line. Idempotency: if `Tool.Description` already begins with `"🌿 "`, no second prefix SHALL be added. When `Tool.Description` is null or empty, it SHALL be left unchanged — the brand mark is a prefix to existing prose, not a replacement for missing prose, so a tool registered without a description is surfaced verbatim rather than papered over with a bare glyph.

The stamping SHALL apply only to built-in tools — those whose backing method's declaring type carries `[McpServerToolType]`. Plugin-registered tools (registered via `IToolRegistry.AddTool` in `Plugins/ToolRegistry.cs`) SHALL NOT receive the brand mark on either `Title` or `Description`. The brand mark on `Tool.Title` and `Tool.Description` is independent of the existing brand mark on per-call response prose (governed by *Tool response brand mark*) and the brand mark on the `ServerInstructions` head string (governed by *Server-published usage instructions*); each surface is stamped independently and may be observed independently in `tools/list` (Title, Description) versus `tools/call` results (content prose) versus the `initialize` handshake (instructions).

The stamping SHALL be applied via a single chokepoint in the server's startup sequence (mirroring the existing `ToolDescriptionFormatter.ApplyTriggersFromAttributes` pass that mutates `ProtocolTool.Description` in place after `host.Build()`). The chokepoint SHALL respect `LeafFormatter.Suppressed` — when suppression is active, `Title` SHALL NOT be set (it remains null/unset, preserving the SDK's default behaviour) and `Description` SHALL NOT be prefixed.

#### Scenario: Built-in tool catalog entry carries Title and Description brand marks
- **WHEN** an MCP client calls `tools/list` against a freshly started `sourcegraph-mcp serve` process with no suppression flags
- **THEN** the response's `tools[]` entries for every built-in tool (e.g. `find_definition`, `search_symbols`, `find_references`) each have a populated `title` field equal to `"🌿 <name>"` (e.g. `"🌿 find_definition"`) and a `description` field whose first characters are `"🌿 "` followed by the tool's documented prose

#### Scenario: Plugin-registered tool catalog entry is not brand-marked
- **WHEN** a plugin-registered tool (registered through `IToolRegistry.AddTool`) appears in the `tools/list` response
- **THEN** that tool's `title` field is null/unset and its `description` does NOT start with `"🌿 "` — the plugin's authored identity ships verbatim

#### Scenario: Description prefix is idempotent
- **WHEN** a built-in tool's authored `Description` already begins with `"🌿 "` (e.g. due to manual pre-stamping in source, or a re-run of the post-build pass)
- **THEN** the post-build pass leaves it unchanged — no second `"🌿 "` is stacked

#### Scenario: Title is independent of Name
- **WHEN** an MCP client invokes a tool by `Name` (e.g. `find_definition`)
- **THEN** invocation succeeds because `Name` is the wire-level identifier and is unaffected by this requirement; `Title` is a separate display-only field

#### Scenario: Pass is repeatable
- **WHEN** the post-build mutation pass runs more than once on the same registered tool collection (e.g. due to test harness rebuilding the host)
- **THEN** the resulting `Title` and `Description` are identical to a single-pass run — `Title` is `"🌿 " + Name` (not `"🌿 🌿 " + Name`), `Description` starts with exactly one `"🌿 "`

#### Scenario: New built-in tools added in future changes are automatically branded
- **WHEN** a future change registers a new method-based tool on a type carrying `[McpServerToolType]` (e.g. `find_data_bindings` from the in-flight `payload-tooling` change)
- **THEN** that tool's catalog entry receives the same `🌿 ` Title and Description treatment without any per-tool wiring — the chokepoint walks the registered set on every startup

#### Scenario: Existing trigger-append survives the leaf prefix
- **WHEN** a built-in tool's method carries a `[ToolTrigger("...")]` attribute (whose value is appended to `Description` by `ToolDescriptionFormatter.ApplyTriggersFromAttributes`)
- **THEN** the final `Description` shipped in `tools/list` is `"🌿 " + <original description> + "\n\nUse when: <trigger>"` — the leaf rides at the start, the trigger at the end, both passes coexist

### Requirement: Brand-mark suppression
The server SHALL accept `--no-leaf` as a CLI flag on `sourcegraph-mcp serve` and SHALL honour `SOURCEGRAPH_NO_LEAF` as an environment variable (truthy values: exact `1`, or `true` case-insensitive — same convention as `SOURCEGRAPH_NO_INSTRUCTIONS`). When either is set, the server SHALL omit the brand-mark prefix from every built-in tool's per-call response, SHALL omit the brand-mark prefix from the published `ServerInstructions` string, AND SHALL NOT stamp `Tool.Title` with `"🌿 " + Name` or prepend `"🌿 "` to `Tool.Description` for any built-in tool. The three suppression effects (per-call response prose, instructions head, and tool-identity Title/Description) compose under the single `--no-leaf` / `SOURCEGRAPH_NO_LEAF` knob; there is no per-surface suppression. The `--no-leaf` and `--no-instructions` flags continue to compose independently: turning off one SHALL NOT turn off the other.

#### Scenario: Suppression via flag
- **WHEN** the server is started with `--no-leaf`
- **THEN** built-in tool responses contain no leading `🌿 ` on `content[0].text`, the published `ServerInstructions` string (if any) contains no leading `🌿 `, every built-in tool's `Tool.Title` is null/unset, and every built-in tool's `Tool.Description` does NOT start with `"🌿 "`

#### Scenario: Suppression via env var
- **WHEN** the server is started without `--no-leaf` but with `SOURCEGRAPH_NO_LEAF=1` in env
- **THEN** the same suppression pattern as the flag-based case applies: per-call prose unbranded, ServerInstructions head unbranded, Title null/unset, Description not branded

#### Scenario: Leaf suppression independent of instructions suppression
- **WHEN** the server is started with `--no-leaf` but WITHOUT `--no-instructions`
- **THEN** the `ServerInstructions` string is published with its cross-cutting guidance intact but no leading `🌿 ` prefix; every built-in tool's response prose has no leading `🌿 `; every built-in tool's `Title` is null/unset and `Description` is not `🌿 `-prefixed; no leaf appears anywhere in the catalog or per-call channels

#### Scenario: Suppression knobs compose
- **WHEN** the server is started with both `--no-leaf` and `--no-instructions`
- **THEN** the `initialize` response carries no `ServerInstructions` string at all, every built-in tool's response prose has no `🌿 ` prefix, every built-in tool's `Title` is null/unset, and every built-in tool's `Description` is not `🌿 `-prefixed

#### Scenario: Plugin tools unaffected by suppression
- **WHEN** any combination of suppression flags is set and a plugin-registered tool is invoked or appears in `tools/list`
- **THEN** the plugin's authored identity (Title, Description, response content) ships unchanged regardless of suppression — the suppression only governs surfaces the server itself stamps; plugin-authored text is never stamped to begin with

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

### Requirement: Multi-content tool responses
Every built-in MCP tool's response SHALL be representable as an ordered list of `ContentBlock` items rather than a single concatenated text blob. The list MAY include `TextContentBlock`, `ResourceLinkBlock`, and other protocol-defined content types in any order. The wire-level encoding (`CallToolResult.content`) follows the MCP spec verbatim — clients that recognise the richer block types render them; clients that don't fall back to rendering only `TextContentBlock` items.

The brand-mark chokepoint SHALL find the **first user-visible** `TextContentBlock` (i.e. the first text block whose `annotations.audience` is null, empty, or contains `Role.User`) anywhere in the list — regardless of position relative to non-text blocks — and prefix its `Text` with `🌿 ` (when leaf suppression is not active). Audience-restricted blocks (`audience = ["assistant"]` only) SHALL be skipped over while searching for the user-visible target. Lists containing zero user-visible text blocks SHALL ship unchanged.

#### Scenario: Tool returns a list of content blocks
- **WHEN** an MCP client invokes `find_references(symbol = "X")` and the server has matching results
- **THEN** the response's `content` array contains one leading `TextContentBlock` with the prose summary + body, zero or more `ResourceLinkBlock` items (one per result row), and at most one trailing `TextContentBlock` with `annotations.audience = ["assistant"]` carrying agent-only metadata

#### Scenario: Brand mark applies to first user-visible text block
- **WHEN** a built-in tool returns a content list whose first item is a user-visible `TextContentBlock`
- **THEN** the shipped response's `content[0].text` starts with `🌿 ` (or the unprefixed body text when leaf suppression is active), with subsequent content items unchanged

#### Scenario: Leaf attaches to a text block that isn't first in the list
- **WHEN** a built-in tool returns a content list whose first item is a `ResourceLinkBlock` (or any other non-text block) followed later by a user-visible `TextContentBlock`
- **THEN** the chokepoint walks the list, locates the first user-visible text block, and prefixes its `Text` with `🌿 `; the non-text items earlier in the list are passed through unchanged

#### Scenario: List with no user-visible text blocks ships unchanged
- **WHEN** a built-in tool returns a content list containing only non-text items (resource links, audio, etc.) or only audience-restricted text blocks
- **THEN** no `🌿 ` prefix is added anywhere; the response ships exactly as the tool body produced it

#### Scenario: Older clients ignore unfamiliar block types
- **WHEN** an MCP client that doesn't recognise `resource_link` content blocks reads a response from a built-in tool that emitted them
- **THEN** the client renders only the `TextContentBlock` items and skips the unrecognised ones; the user sees a complete prose answer because the prose is self-sufficient

### Requirement: Structured content output
Every built-in tool whose result is naturally typed (a list of hits, a typed singleton record, a counts summary) SHALL ship its successful result as both renderable `content` and a typed `structuredContent` object. The tool's MCP catalog entry SHALL declare an `outputSchema` matching the structured-content shape, with the top-level schema being `{"type":"object", ...}` (the MCP SDK rejects non-object root schemas at registration time).

`structuredContent` payloads SHALL use named DTO types — never anonymous types. The compile-time typing of `CallToolResult.StructuredContent` (`JsonElement?`) and `CallToolResult.Meta` (`JsonObject?`) enforces this at assignment: anonymous types simply do not satisfy either type, so the C# compiler rejects them before the code can even be built. No runtime guard is needed; the SDK's typed properties are the contract.

Property names on the wire SHALL use `snake_case` to match the tool catalog's published `outputSchema`. C# DTOs use PascalCase records with `[property: JsonPropertyName("snake_name")]` overrides on multi-word fields, so both the source-gen-derived `structuredContent` payload and the SDK's `JsonSchemaExporter`-derived `outputSchema` publish the same wire names.

The pair (`content`, `structuredContent`) SHALL describe the same result. The number of items in any structured array SHALL equal the number of corresponding rows in the rendered prose.

Diagnostic short-circuits — input validation failures (unknown severity / accessibility / edge kind), disabled subsystems (`--no-history`, `--no-embeddings`), scope-routing failures (no scopes registered, scope degraded, exception thrown inside the scope query) — MAY omit `structuredContent` and SHALL set `isError = true` on the wire. Successful zero-row responses (the query ran cleanly but produced no rows) SHALL still ship the typed structured shape with the empty collection populated.

#### Scenario: find_definition publishes structured hits
- **WHEN** the agent invokes `find_definition(symbol = "Calculator")` and the graph returns 3 hits
- **THEN** the response's `structuredContent` is a `{"hits": [...]}` object whose `hits` array has 3 typed entries with at least the fields `fqn`, `kind`, `file_path`, `line`, `column`, `signature`, `xml_summary`; and the rendered prose lists the same 3 hits in the same order

#### Scenario: Output schema declared at tools/list time
- **WHEN** an MCP client calls `tools/list`
- **THEN** every tool that ships `structuredContent` carries an `outputSchema` field with `{"type":"object", "properties": ...}` matching the tool's structured-content payload, with `snake_case` property names matching the wire shape of `structuredContent`

#### Scenario: Empty result populates structured content
- **WHEN** a tool that ships structured output returns no rows for a successful query (e.g. `find_definition(symbol = "Nonexistent")`)
- **THEN** the response's `structuredContent` is the typed object with an empty array (e.g. `{"hits": []}`), not omitted; `isError` is unset; the prose carries the existing "No matches for 'X'." line

#### Scenario: Diagnostic responses may omit structuredContent
- **WHEN** a tool short-circuits before producing a structured result — input validation failure, disabled subsystem, scope-routing failure, or caught exception
- **THEN** the response MAY omit `structuredContent` entirely; the prose carries the diagnostic message branded by the leaf chokepoint; `isError` is set to `true` so telemetry and strict-validating clients can distinguish the diagnostic from a successful zero-row response

#### Scenario: Successful no-resolve targets short-circuit without structuredContent
- **WHEN** a target-shaped tool (`find_references`, `list_callers`, `list_callees`, `find_implementations`, `list_members`, `neighborhood`, `impact_of_change`, `list_tests_for`, `who_authored`) is invoked with a symbol or container the graph doesn't resolve
- **THEN** the response ships the prose `"No matches for '<symbol>'."` line as a single text block, omits `structuredContent` (no resolved target descriptor exists to populate it), and leaves `isError` unset — telemetry counts the call as ok=true, symmetric with the historical `Task<string>` "no matches" behaviour

### Requirement: Resource-link content items
Tools whose result rows correspond to individual symbols or files SHALL emit a `ResourceLinkBlock` per row alongside the rendered prose. Each `ResourceLinkBlock` SHALL carry a URI in the project's defined `graph://` scheme — `graph://symbol/<id>` for symbols, `graph://file/<path>` for files — pointing at a resource the project's `Resources/GraphResources.cs` subsystem can serve.

URIs SHALL be constructed via the centralised `GraphResourceUris` helper so the URI shape stays consistent between tools and resource handlers. Tools SHALL emit links only for entities they have just queried out of the graph; speculative or synthesised URIs are not allowed.

#### Scenario: find_references emits a link per reference row
- **WHEN** `find_references(symbol = "X")` returns 5 reference rows
- **THEN** the response's `content` includes 5 `ResourceLinkBlock` items, each with `uri = "graph://symbol/<id>"` matching the reference's symbol id, plus `name`, `description`, and `mimeType` populated for renderer cards

#### Scenario: Tool-emitted resource links resolve via the resource handler
- **WHEN** an MCP client follows a `ResourceLinkBlock.uri` from a tool response by calling `resources/read` against that URI
- **THEN** the resource handler in `Resources/GraphResources.cs` returns the typed resource card without "URI not found" — every emitted URI is dereferenceable

#### Scenario: Centralised URI helper
- **WHEN** any built-in tool needs to emit a graph resource URI
- **THEN** the URI is constructed via `GraphResourceUris.Symbol(id)` or `GraphResourceUris.File(path)` (not by hand-formatted string interpolation), so the URI shape stays consistent across all tools and any future change to the URI scheme lands in one place

### Requirement: Audience-restricted metadata content blocks
Tools MAY emit a trailing `TextContentBlock` carrying agent-only metadata — resolved scope, latency, edge-kind defaults, "X of N rows omitted due to limit" notices, cache hit info — with `annotations.audience = ["assistant"]` and `annotations.priority` set to a low value (typically 0.2). Such blocks reach the connected model but SHALL NOT be rendered to the human user by clients that respect the `audience` annotation.

The brand mark SHALL NOT be stamped on audience-restricted blocks. Multiple audience-restricted blocks per response are allowed but discouraged for compactness.

#### Scenario: Tool ships scope and latency metadata to the model
- **WHEN** a built-in tool runs to completion and produces metadata about scope resolution, query timing, or row truncation
- **THEN** that metadata may be emitted as a `TextContentBlock` whose `annotations.audience` array equals `["assistant"]`; the model receives the block in its tool-result payload, but a client honoring the `audience` annotation does not render the block to the human user

#### Scenario: Audience-restricted content is not brand-marked
- **WHEN** a tool's content list contains an audience-restricted `TextContentBlock`
- **THEN** the chokepoint does NOT prepend `🌿 ` to that block; the brand mark applies only to the first user-visible `TextContentBlock`

### Requirement: find_data_bindings tool
The server SHALL expose a `find_data_bindings` MCP tool that returns rows from the `binds-path` edge kind, with optional filters on payload fields. Parameters: `target` (canonical key of bound symbol; matched against the edge's `dst`), `source` (canonical key of source UI element; matched against the edge's `src`), `path` (substring filter on `payload.path`), `mode` (exact match on `payload.mode`), `converter` (exact match on `payload.converter`), `scope` (scope id, comma-separated list, or `"*"`), `limit` (default 50). At least one filter parameter MUST be non-null; if all are null and no scope is restricted, the tool returns the first `limit` rows globally with a `note:` line asking the agent to add a filter.

The tool SHALL use the always-render-payload markdown shape (introduced by `harden-sdk-pre-xaml`): each row is `<source-element>  →  <target-or-unresolved>` followed by an indented `payload:` sub-line showing the relevant `path`, `mode`, `converter`, and `converter-parameter` keys.

#### Scenario: Find every TwoWay binding to a property
- **WHEN** the agent invokes `find_data_bindings(target = "csharp:P:SampleWpf.ViewModels.MainViewModel.UserName", mode = "two-way")`
- **THEN** the response lists every `binds-path` edge whose `dst` resolves to that C# property symbol AND whose `payload.mode` equals `"two-way"`; each row's payload sub-line shows the matched fields

#### Scenario: Find every binding using a specific converter
- **WHEN** the agent invokes `find_data_bindings(converter = "BoolToVisibility")`
- **THEN** the response lists every `binds-path` edge whose `payload.converter` equals `"BoolToVisibility"` (across every scope when no scope filter is specified, or restricted to the named scope)

#### Scenario: Path substring filter
- **WHEN** the agent invokes `find_data_bindings(path = "User.")`
- **THEN** the response lists every `binds-path` edge whose `payload.path` contains the substring `"User."` (e.g. `"User.Name"`, `"User.Email"`, `"User.Profile.Title"`)

#### Scenario: Soft empty when scope has no binds-path emitter
- **WHEN** the agent invokes `find_data_bindings(target = "csharp:P:Foo.Bar.Baz")` against a scope that has not loaded any indexer emitting the `binds-path` edge kind
- **THEN** the response is an empty result list with a single `note:` line indicating the scope's `edge_kinds` vocabulary does not include `binds-path` (the agent reads this and stops issuing the same query)

#### Scenario: All filters null returns hint
- **WHEN** the agent invokes `find_data_bindings()` with every filter null and no `scope` restriction
- **THEN** the response prepends a `note: provide at least one filter (target, source, path, mode, converter)` line and still returns up to `limit` rows so the agent can see what's available

### Requirement: find_event_handlers tool
The server SHALL expose a `find_event_handlers` MCP tool that returns rows from the `handles-event` edge kind, with optional filters on payload fields. Parameters: `handler` (canonical key of handler method; matched against the edge's `dst`), `event` (exact match on `payload.event`), `element` (canonical key of source UI element), `command` (exact match on `payload.command` for command-bound flavors where the indexer recorded a command name), `scope`, `limit` (default 50).

The tool SHALL render result rows as `<element>.<event-name>  →  <handler-or-command>` followed by the always-render-payload sub-line.

#### Scenario: Find every Click handler in a scope
- **WHEN** the agent invokes `find_event_handlers(event = "Click")` against a scope that loaded the XAML indexer
- **THEN** the response lists every `handles-event` edge whose `payload.event` equals `"Click"`, with each row showing the source XAML element and the resolved C# handler method

#### Scenario: Find every wiring to a specific handler
- **WHEN** the agent invokes `find_event_handlers(handler = "csharp:M:SampleWpf.Views.MainWindow.OnSave")`
- **THEN** the response lists every `handles-event` edge whose `dst` equals that C# method symbol

#### Scenario: Element + event combination
- **WHEN** the agent invokes `find_event_handlers(element = "xaml:element:Views/MainWindow.xaml#SaveBtn", event = "Click")`
- **THEN** the response lists the single edge wiring the named button's Click event to its handler

#### Scenario: Soft empty for command-only scope
- **WHEN** the agent invokes `find_event_handlers(command = "SaveCommand")` against a scope whose XAML indexer never emitted any `handles-event` edge with `payload.command` populated
- **THEN** the response is empty plus a `note:` line indicating no `handles-event` edges in the scope carried a `command` payload key

### Requirement: Tools self-advertise via tools/list
Both `find_data_bindings` and `find_event_handlers` SHALL appear in the MCP `tools/list` advertisement on every scope where the server runs, regardless of whether the scope's loaded indexers emit `binds-path` or `handles-event` edges. The tool description text MUST include a `Use when:` guidance line so an agent surfaces the tool on the right question shape.

#### Scenario: Tools advertised in a C#-only scope
- **WHEN** an MCP client invokes `tools/list` against a scope that loads only the built-in C# Roslyn indexer (no XAML indexer)
- **THEN** the returned tool list includes `find_data_bindings` and `find_event_handlers` with their `Use when:` descriptions; calling them returns the soft-empty behaviour documented above

#### Scenario: Tool descriptions document soft-empty behaviour
- **WHEN** an MCP client reads the `find_data_bindings` description from `tools/list`
- **THEN** the description names the edge kind it walks (`binds-path`), names the `PayloadKeys` constants its filters map to (`path`, `mode`, `converter`, `converter-parameter`), and documents the soft-empty response shape so an agent can predict the tool's behaviour without invoking it

