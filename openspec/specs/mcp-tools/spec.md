# MCP Tools

## Purpose

Expose the code graph to MCP clients (Claude Code, Codex, Cursor, Continue, …) as a
set of stdio-callable tools so that an LLM coding agent can answer
symbol-level questions via one structured call instead of dozens of
`Grep` + `Read` operations.
## Requirements
### Requirement: Definition lookup
The server SHALL expose a `find_definition` tool that returns the symbol id/FQN, exact
1-based half-open declaration range, `defines` relation, confidence, kind, signature,
accessibility, modifiers, and (when present) one-line XML summary for every symbol matching a
name or fully-qualified name.

#### Scenario: Look up a class
- **WHEN** the agent invokes `find_definition(symbol = "Calculator")` against an indexed solution that contains `Sample.Domain.Calculator`
- **THEN** the response lists the class with symbol id/FQN, exact file range, `defines` relation, `exact` confidence, signature, accessibility, modifiers, and (if any) the first sentence of its XML summary

### Requirement: Reference lookup
The server SHALL expose a `find_references` tool that returns every reference site for a symbol,
surfacing the resolved target symbol id/FQN, relation/`ReferenceKind` (`def`, `ref`, `call`,
`read`, `write`, `impl`, `inherit`), semantic confidence, and file/line/column per row, with an
optional `include_generated` parameter (default `false`) that filters out references coming from
source-generated files.

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
The server SHALL expose `list_callers` and `list_callees` tools that walk `calls` edges by default, with an optional `kind` parameter that accepts a kebab-case edge kind name (`calls | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-to | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all`) or any future kind exposed by the active scope's plugins, to filter the edge kind walked. The XAML edge kinds (`code-behind`, `binds-to`, `binds-path`, `binds-element`, `handles-event`, `uses-resource`, `instantiates-type`, `merges`, `applies-style`) are part of the enumerable vocabulary on every scope that loads the XAML indexer. When an edge row carries a non-null `payload` JSON value, the rendered markdown SHALL include an indented `payload:` sub-line under the edge row, displaying up to the first five key/value pairs from the payload object; if more than five pairs are present, an `(N more)` suffix SHALL indicate the elision count.

The Phase 1 MedInteropLens contract SHALL additionally register the exact compatibility
names `find_reference`, `find_callers`, `find_callees`, and `impact_analysis`. Each SHALL
delegate to the established plural/list/impact implementation with the same input defaults,
structured output schema, scope semantics, and errors. These aliases SHALL NOT fabricate
call-site locations before occurrence evidence is available.

`list_callers`/`find_callers` and `list_callees`/`find_callees` SHALL return one row per
evidence-backed logical edge. Every row SHALL contain the source and target symbol ids, FQNs,
stored `canonical_key` values when present, the edge's actual relation (including under
`kind = all`), relation confidence, and all included `edge_evidence` occurrences. Each
occurrence SHALL contain its real producing file path, 1-based half-open start/end range,
confidence, producer, and metadata. A logical edge without stored occurrence evidence SHALL be
skipped rather than rendered at either endpoint's declaration. `limit` SHALL be validated in
the inclusive range 1-1000 and the structured result SHALL report truncation.

`impact_of_change`/`impact_analysis` SHALL perform a breadth-first, cycle-safe upstream
traversal rather than returning an unauditable transitive set. `maxDepth` SHALL be validated in
the inclusive range 1-12 and `limit` in 1-1000. Every impacted row SHALL include its BFS
predecessor (the next symbol toward the changed target), an ordered source-to-target path, and
the weakest confidence across that path. Every path hop SHALL satisfy the same source/target,
relation, and occurrence-evidence contract as direct caller/callee rows. Reaching a depth,
result, or bounded branch-query cap while unseen evidence-backed relations may remain SHALL set
`truncated = true`.

#### Scenario: Phase 1 compatibility names are discoverable
- **WHEN** an MCP client lists tools
- **THEN** `find_reference`, `find_callers`, `find_callees`, and `impact_analysis` are registered alongside `find_references`, `list_callers`, `list_callees`, and `impact_of_change`

#### Scenario: Direct relations retain occurrence evidence
- **WHEN** `B` calls `C`, `A` has another relation to `C`, and the agent invokes `find_callers(symbol = "C", kind = "all")`
- **THEN** each row identifies its canonical source and target, preserves its actual relation and confidence, and includes the stored call-site file and half-open range

#### Scenario: Impact paths are independently auditable
- **WHEN** `A` calls `B`, `B` calls `C`, and the agent invokes `impact_analysis(symbol = "C")`
- **THEN** the row for `A` identifies `B` as its predecessor and contains the ordered evidence-backed path `A -> B -> C`, with path confidence equal to its weakest hop

#### Scenario: Malformed and cyclic impact edges stay safe
- **WHEN** an upstream graph contains a cycle and a logical edge with no `edge_evidence`
- **THEN** traversal terminates, never returns the changed root as its own impact, and omits the no-evidence edge without inventing a declaration-site occurrence

### Requirement: Evidence-first bounded call-path tracing
The server SHALL expose the exact Phase 1 tool name `trace_call_path`. It SHALL traverse
directed `calls` edges by default and MAY traverse another validated kebab-case relation through
its `kind` parameter. Traversal SHALL be breadth-first, path-cycle-safe, and bounded by validated
`maxDepth` (1-12), `maxPaths` (1-25), and `maxNodes` (1-5000) inputs. Reaching any configured
bound while unexplored work remains SHALL set `truncated = true`; the tool SHALL never silently
return an unbounded or incomplete result.

Every returned hop SHALL identify the source and target symbol, relation, and confidence, and
SHALL include the stored occurrence evidence: producing file path, 1-based half-open start/end
range, evidence confidence, producer, and metadata. The path confidence SHALL be the weakest
confidence among its hops. A malformed logical edge with no stored evidence SHALL be skipped;
the tool SHALL NOT invent a call site.

#### Scenario: Every call-path hop is independently auditable
- **WHEN** `A` calls `B` at one indexed source range and `B` calls `C` at another, and the agent invokes `trace_call_path(from = "A", to = "C")`
- **THEN** the returned path contains both hops, each hop contains its own file/range evidence and relation, and the path confidence is the weaker of the two hop confidences

#### Scenario: Cycles and resource limits remain bounded
- **WHEN** the graph contains a cycle reachable from the source or traversal reaches a configured depth, path, node, or per-hop evidence cap
- **THEN** traversal terminates without repeating a symbol within the same path and reports truncation whenever unexplored work or evidence may remain

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
transitive set of upstream callers via a breadth-first, cycle-safe traversal of
evidence-backed `Calls` edges.

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
Tools whose work has multi-second tails on representative inputs SHALL accept an `IProgress<ProgressNotificationValue>` parameter and emit progress at coarse, named checkpoints during the call. Each emitted `ProgressNotificationValue` SHALL set `Total = 1.0`, a `Progress` value in the inclusive range `[0.0, 1.0]` that is monotonically increasing across the call, and a `Message` drawn from one of the following structural shapes (no caller-supplied substrings interpolated):

- Short imperatives: `"encoding query"`, `"searching"`, `"formatting results"`, `"querying"`.
- Indexing phase markers: `"opening workspace"`, `"indexing"`, `"ready"`.

The set of tools opted in by this requirement at the per-tool level is `semantic_search`, `impact_of_change`, `module_summary`, and `find_definition`. **In addition**, every MCP `tools/call` to one of those tools whose dispatch awaits `ScopeHost.Ready` because the targeted scope has not yet completed initial indexing SHALL forward the per-scope indexing progress source's events as `notifications/progress` for the duration of that wait. Other tools MAY add per-tool `IProgress` parameters in future changes when their measured latency justifies it; until they do, cold-start time is silent for those tool calls (today's behaviour).

When an MCP client did not include a `progressToken` on the originating `tools/call` request, the SDK SHALL inject a no-op `IProgress<ProgressNotificationValue>` instance so tool bodies (and the cold-start wrapper) that call `Report(...)` unconditionally incur no wire-level overhead.

#### Scenario: semantic_search emits encoding, searching, and formatting checkpoints
- **WHEN** an MCP client invokes `semantic_search(query = "...")` with a `progressToken`
- **THEN** the server emits three `notifications/progress` messages over the call's lifetime, in order: `Progress = 0.0` with `Message = "encoding query"`, `Progress = 0.5` with `Message = "searching"`, and `Progress = 0.9` with `Message = "formatting results"`

#### Scenario: impact_of_change emits a starting checkpoint
- **WHEN** an MCP client invokes `impact_of_change(symbol = "...", maxDepth = 6)` with a `progressToken`
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins

#### Scenario: module_summary emits a starting checkpoint
- **WHEN** an MCP client invokes `module_summary(namespaceOrPath = "...")` with a `progressToken`
- **THEN** the server emits a single `notifications/progress` message with `Progress = 0.0` and `Message = "querying"` shortly after the request begins

#### Scenario: Cold-start tool call forwards indexing-phase progress
- **WHEN** an MCP client invokes a progress-aware tool (e.g. `find_definition`) with a `progressToken` against a scope whose initial indexing is still running
- **THEN** the server emits a sequence of `notifications/progress` messages drawn from the indexing phase markers (`"opening workspace"`, `"indexing"`, `"ready"`) for the duration of the cold-start wait; once `Ready`, the underlying tool's own checkpoints (if any) emit normally

#### Scenario: No progress emitted when client did not opt in
- **WHEN** any `tools/call` invocation arrives without a `progressToken`, whether against a slow tool or during cold-start
- **THEN** the server emits zero `notifications/progress` messages; the tool result returns identically to today's behaviour

#### Scenario: Progress values are monotonically increasing
- **WHEN** any opted-in tool or cold-start wait emits two or more progress notifications during a single call
- **THEN** each successive notification's `Progress` value is strictly greater than the previous notification's `Progress`, with all values in the closed interval `[0.0, 1.0]`

#### Scenario: Progress messages carry no user input
- **WHEN** any progress notification is emitted by an opted-in tool or by the cold-start forwarder
- **THEN** its `Message` matches one of the documented structural shapes — short imperative or indexing phase marker — and no caller-supplied substring (symbol name, query text, file path) appears in any `Message`

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

### Requirement: Soft size budget for list-shaped tool results

A defined set of high-fanout built-in MCP tools — currently `find_references`, `list_members`, `list_symbols_in_file`, and `semantic_search` — SHALL apply a soft serialized-size budget so a single `tools/call` result stays under MCP-client per-call truncation thresholds (Claude Code's threshold is approximately 16K tokens / 64K characters; the project budget targets 50K characters with headroom). Other list-shaped tools whose default `limit` and per-row size make overrun unlikely MAY opt in later via the same helper without requiring a spec change.

When applied, the budget SHALL be enforced at the call site by trimming the tool's row list **before** prose / `ResourceLinkBlock` / structured-content emission so all three representations remain internally consistent (i.e. the structured array length equals the prose row count equals the count of emitted `ResourceLinkBlock` items, every time).

The number of items budgeted SHALL be computed by the centralised `OutputBudget.ChooseKeep` helper from a per-tool per-row cost estimate (in characters) so opted-in tools share the same tuning surface rather than each picking its own caps.

The budget SHALL apply on top of the user-supplied `limit` parameter — a small `limit` returns at most `limit` rows; a large `limit` is further capped to fit the budget.

#### Scenario: find_references caps projected size

- **WHEN** `find_references` resolves a symbol with so many references that emitting all of them would push the serialized response past the budget
- **THEN** the tool body trims its `refs` list to the largest count that fits, builds prose / resource links / structured content from the trimmed list, and the resulting `CallToolResult` stays under the budget

#### Scenario: list_symbols_in_file caps projected size

- **WHEN** `list_symbols_in_file` is invoked on a file with so many symbols that emitting all of them with signature + XML summary lines would exceed the budget
- **THEN** the tool body trims its `hits` list before issuing per-symbol annotation/history queries (so dropped rows do not incur work), then builds prose / resource links / structured content from the trimmed list

#### Scenario: semantic_search trims before resolving symbols

- **WHEN** `semantic_search` returns more embedding hits than fit under the budget
- **THEN** the tool body computes the kept count from `hits.Count` and resolves only the kept hits via `GetSymbolByIdAsync` so dropped rows do not incur per-row DB work

#### Scenario: Trimmed response keeps representations in lockstep

- **WHEN** the size cap activates on any opted-in tool
- **THEN** the response's prose row count, count of `ResourceLinkBlock` items, and structured-content array length are all equal — the existing `StructuredContentInvariantTests` continue to pass

#### Scenario: Non-overflow query passes through unchanged

- **WHEN** the projected size of an opted-in tool's result is comfortably under the budget
- **THEN** `OutputBudget.ChooseKeep` returns `(items.Count, 0)` — no trimming occurs, no extra metadata key is emitted, and the response shape is identical to the pre-budget behaviour

### Requirement: Size-driven truncation signalled via omitted_size metadata

When an opted-in tool trims its row list to fit the soft size budget, the tool SHALL append an `omitted_size=<N>` extra to its existing audience-restricted `_meta:` block built via `AudienceMetadata.Build`, where `N` is the count of rows dropped from the tail. The key SHALL be omitted entirely when no size-driven trim occurred so non-overflow calls retain their pre-change metadata shape.

The `omitted_size` signal is distinct from `limit`-driven truncation: a tool may return `limit` rows and emit no `omitted_size` (the user-supplied cap was met without size pressure), or it may return fewer than `limit` rows AND emit `omitted_size=N` (the size budget bit before `limit` did).

#### Scenario: Trim signalled in audience metadata

- **WHEN** an opted-in tool trims `N` rows to fit the size budget
- **THEN** the trailing audience-restricted metadata block contains the substring `omitted_size=<N>` so an agent reading the block detects the truncation and can re-query with a smaller `limit` or refined filter

#### Scenario: Non-truncating call omits the key

- **WHEN** an opted-in tool returns its full result without size-driven trimming
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

### Requirement: Schema introspection tool
The server SHALL expose a `describe_schema` tool that returns the live view layer published by the storage layer (per the `Stable view layer over the underlying tables` requirement on the `storage` capability), suitable for an MCP agent to consume before composing a `query_graph` SQL statement.

The tool's `structuredContent` SHALL include:

- `view_schema_version`: integer matching the storage layer's `Views.SchemaVersion` constant; bumps on **any view-set change** (addition, removal, column rename, or column-type change) so cache-aware clients always re-introspect after a server upgrade.
- `views`: array of `{ name, description, columns: [{ name, type, nullable, description }] }`. The list is hand-curated in `Views.All` and SHALL include all nine views currently shipped: `v_symbols`, `v_files`, `v_edges`, `v_edge_evidence`, `v_references`, `v_scopes`, `v_annotations`, `v_diagnostics`, `v_history`.
- `symbol_kinds`: array of distinct `kind` values present in `v_symbols` across the resolved scope set, populated by `SELECT DISTINCT kind FROM v_symbols`.
- `edge_kinds`: array of distinct `kind` values present in `v_edges` across the resolved scope set, populated by `SELECT DISTINCT kind FROM v_edges`.

The tool's `outputSchema` SHALL declare the structured shape so MCP clients can validate it. The tool SHALL accept an optional `scope` parameter following the same convention as every other tool (`"*"` default, comma-list narrow, isolated scopes excluded from `*`).

#### Scenario: Agent enumerates the queryable views
- **WHEN** an MCP agent calls `describe_schema()` against an indexed multi-scope solution
- **THEN** the response lists `v_symbols`, `v_files`, `v_edges`, `v_edge_evidence`, `v_references`, `v_scopes`, `v_annotations`, `v_diagnostics`, `v_history` (nine views) with their columns; `view_schema_version` is `3`; `symbol_kinds` includes at minimum `class`, `interface`, `method`, `field`; `edge_kinds` includes at minimum `calls`, `uses-type`

#### Scenario: Symbol-kind vocabulary reflects live data
- **GIVEN** the indexer's vocabulary expands (e.g., the XAML indexer adds `xaml-view`, `xaml-element` to the `kind` column)
- **WHEN** `describe_schema` is invoked after re-indexing
- **THEN** `symbol_kinds` includes the new values without any code change to `describe_schema` or `Views.All`

#### Scenario: Schema version bumps on any view-set change
- **GIVEN** the prior revision shipped `view_schema_version = 2` with eight views
- **WHEN** the current revision adds `v_edge_evidence`
- **THEN** `view_schema_version` reads `3`; any future rename, addition, or removal SHALL increment it again (the policy does not distinguish breaking from additive)

#### Scenario: New view descriptors carry the same documentation depth
- **WHEN** an agent inspects the `columns` array of `v_edge_evidence`, `v_annotations`, `v_diagnostics`, or `v_history` in `describe_schema`'s response
- **THEN** every column has a `name`, `type`, `nullable`, and `description` populated; descriptions surface notable nuances (e.g. `v_edge_evidence.confidence` documents its ordered confidence mapping; `v_diagnostics.symbol_id` is documented as nullable; `v_history.last_authored_at` documents the Unix-millis unit)

### Requirement: Ad-hoc graph query tool
The server SHALL expose a `query_graph` tool that accepts a read-only SQL `SELECT` or `WITH` statement, optional named parameters, and an optional `scope` filter, executes the statement against the multi-scope view layer, and returns the resulting rows as `structuredContent` plus a markdown table for display.

The tool's input parameters:
- `sql` (string, required): a single `SELECT` or `WITH` statement against the views from `describe_schema`. Multi-statement input is rejected.
- `parameters` (object, optional): named binding values for `@name` placeholders. Each value is bound by `Microsoft.Data.Sqlite`'s standard parameter conversion.
- `scope` (string, optional, default `"*"`): scope-id, comma-separated list of scope ids, or `"*"` (all non-isolated). Same convention as every existing curated tool. Isolated scopes are included only when explicitly named.

The `structuredContent` SHALL include:
- `row_count`: number of rows returned (≤ `row_cap`).
- `truncated`: boolean; true when the underlying query produced more rows than `row_cap`.
- `row_cap`: the active row cap for this call (configured via `--query-row-limit` / env, default `5000`).
- `elapsed_ms`: query execution time in milliseconds.
- `columns`: array of `{ name, type }` describing each result column.
- `rows`: array of arrays; one inner array per row, in the column order from `columns`.

The `content[].text` SHALL render a GitHub-flavoured markdown table prefixed by `🌿 query_graph (N rows, M ms)` (subject to the existing brand-mark suppression flag), with numeric columns right-aligned and a trailing `_(truncated at {row_cap} rows; add a tighter LIMIT or WHERE)_` line when `truncated` is true.

#### Scenario: Count public types that use a given type
- **GIVEN** an indexed solution where `Sample.Domain.Calculator` is referenced (via `uses-type`) from members declared inside three public types and one internal type
- **WHEN** the agent invokes `query_graph` with
  ```sql
  SELECT COUNT(DISTINCT t.id) AS public_user_count
  FROM v_edges e
  JOIN v_symbols m ON m.id = e.src AND m.scope = e.scope
  JOIN v_symbols t ON t.id = m.container_id AND t.scope = m.scope
  WHERE e.dst = (SELECT id FROM v_symbols WHERE fqn = @fqn LIMIT 1)
    AND e.kind = 'uses-type'
    AND t.is_public = 1
    AND t.is_type = 1;
  ```
  with `parameters = { "@fqn": "Sample.Domain.Calculator" }`
- **THEN** the `structuredContent.rows[0][0]` is `3`; `row_count` is `1`; `truncated` is `false`

#### Scenario: Default scope excludes isolated scopes
- **GIVEN** a multi-scope solution with `frontend`, `backend`, and `vendor` scopes, where `vendor` is `isolated`
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT scope, COUNT(*) FROM v_symbols GROUP BY scope"` and no `scope` parameter
- **THEN** the result contains rows for `frontend` and `backend` only; the `vendor` scope is absent

#### Scenario: Explicit isolated-scope opt-in
- **WHEN** the agent invokes the same query with `scope = "vendor"`
- **THEN** the result contains a single row for `vendor`; `frontend` and `backend` are absent

#### Scenario: Comma-list scope filter
- **WHEN** the agent invokes the same query with `scope = "frontend,vendor"`
- **THEN** the result contains rows for `frontend` and `vendor` (isolated explicitly named); `backend` is absent

#### Scenario: Write attempt rejected
- **WHEN** the agent invokes `query_graph` with `sql = "INSERT INTO v_symbols(name) VALUES ('evil')"`
- **THEN** the tool returns a structured error `{ "error": "read_only", "hint": "query_graph is read-only; use a SELECT or WITH statement" }`; no row is inserted; the connection is closed

#### Scenario: Multi-statement input rejected
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT 1; ATTACH 'evil.db' AS evil;"`
- **THEN** the tool returns a structured error `{ "error": "multi_statement", "hint": "send one SELECT/WITH statement per call" }`; the second statement is never executed

#### Scenario: Statement timeout fires
- **GIVEN** the server is started with `--query-timeout-seconds 2`
- **WHEN** the agent invokes a query whose execution exceeds 2 seconds (e.g., a Cartesian join over a large symbol table without limits)
- **THEN** within ~2.5 seconds the tool returns a structured error `{ "error": "timeout", "elapsed_ms": <≈2000>, "hint": "narrow your WHERE clause or raise --query-timeout-seconds" }`; the underlying SQLite query is interrupted

#### Scenario: Row cap surfaces truncation
- **GIVEN** the server is started with `--query-row-limit 100`
- **WHEN** the agent invokes a query whose result set has 250 rows
- **THEN** `structuredContent.row_count` is `100`; `structuredContent.truncated` is `true`; `structuredContent.row_cap` is `100`; the markdown table includes the `_(truncated at 100 rows; …)_` footer line

#### Scenario: Parameter binding works for typed values
- **WHEN** the agent invokes `query_graph` with `sql = "SELECT * FROM v_symbols WHERE id = @id AND name LIKE @prefix LIMIT 5"` and `parameters = { "@id": 42, "@prefix": "Calc%" }`
- **THEN** the bound parameters are sent to SQLite via `SqliteParameter` (no string interpolation); the result reflects the bound values; an attempt to inject `@prefix = "%' OR 1=1 --"` returns rows whose `name` literally matches that string, not all rows

### Requirement: query_graph and describe_schema follow tool-output conventions
Both `query_graph` and `describe_schema` SHALL declare an `outputSchema` for their `structuredContent`, SHALL prefix their `Title` and `Description` in `tools/list` with the `🌿 ` brand mark (per the `Tool identity brand mark` requirement), SHALL prefix their text-content responses with `🌿 ` (per the `Tool response brand mark` requirement), SHALL include a `Use when:` line in their description, and SHALL be suppressible via `--no-leaf` / `SOURCEGRAPH_NO_LEAF=1` like every other built-in tool.

The `Use when:` lines:
- `query_graph`: *"the question you want to answer doesn't fit any other tool, or you need an aggregation/join/grouping over the graph that no curated tool exposes."*
- `describe_schema`: *"you're about to write `query_graph` SQL and don't yet know the view names or columns."*

#### Scenario: Tool list shows brand mark on both
- **WHEN** an MCP client calls `tools/list` against a server started without `--no-leaf`
- **THEN** the `query_graph` and `describe_schema` entries have `Title` starting with `🌿 ` and `Description` starting with `🌿 `

#### Scenario: --no-leaf suppresses brand on both
- **GIVEN** the server is started with `--no-leaf`
- **WHEN** the client calls `tools/list` and then `query_graph(...)` and `describe_schema()`
- **THEN** the `Title`, `Description`, and response text for both tools are unprefixed

### Requirement: ServerInstructions documents the layered tool model
The `ServerInstructions` block returned in the MCP `initialize` response SHALL include a sentence explaining when to prefer `query_graph` over the curated tools, suppressible by `--no-instructions` / `SOURCEGRAPH_NO_INSTRUCTIONS=1` like the existing guidance.

#### Scenario: Layered guidance is published by default
- **WHEN** an MCP client connects to a server without `--no-instructions`
- **THEN** the `ServerInstructions` payload includes a sentence of the form *"For ad-hoc questions that don't fit a curated tool, call `describe_schema` then `query_graph` — read-only SQL over a stable view layer."* in addition to the existing curated-tools recommendation

#### Scenario: --no-instructions suppresses the layered guidance
- **GIVEN** the server is started with `--no-instructions`
- **WHEN** an MCP client connects
- **THEN** the `ServerInstructions` payload is empty (or omitted entirely); the layered-guidance sentence is not present

### Requirement: Embedding cache management tools
The server SHALL expose four MCP tools that inspect and manage the embedding model cache: `embeddings_status`, `embeddings_pull`, `embeddings_remove`, and `embeddings_verify`. Each tool's response SHALL include typed `structuredContent` alongside the markdown prose and SHALL declare its `outputSchema` in `tools/list`. Built-in `🌿` brand-mark conventions apply to all four.

The mutating tools SHALL carry MCP-spec `annotations` so spec-aware clients can require explicit user confirmation before invocation:

| Tool                | `destructiveHint` | `idempotentHint` | `readOnlyHint` |
|---------------------|-------------------|------------------|-----------------|
| `embeddings_status` | false             | true             | true            |
| `embeddings_pull`   | false             | true             | false           |
| `embeddings_remove` | true              | true             | false           |
| `embeddings_verify` | false             | true             | true            |

Each tool's description SHALL include a `Use when:` line that describes the user-initiated nature of the operation (especially for `embeddings_pull` and `embeddings_remove`, which trigger network egress / disk deletion respectively).

#### Scenario: embeddings_status returns the cache report
- **WHEN** an MCP client invokes `embeddings_status` with no arguments
- **THEN** `result.structuredContent` includes `model_id`, `dimension`, `cache_dir`, an array of `files` each with `local_name` / `remote_path` / `present` / `size_bytes` / `computed_sha` / `pinned_sha` / `match`, and `free_disk_bytes` (snake_case per `ToolOutputJsonContext`'s `JsonKnownNamingPolicy.SnakeCaseLower`); the prose narrates the same data

#### Scenario: embeddings_pull on empty cache populates the cache
- **WHEN** an MCP client invokes `embeddings_pull` with no arguments and the active model's cache directory is empty
- **THEN** the server downloads the manifest files into the cache, the response's `structuredContent` matches the post-download `embeddings_status` snapshot, and every `files[*].present` is `true`

#### Scenario: embeddings_pull on warm cache is a no-op
- **WHEN** an MCP client invokes `embeddings_pull` against a populated cache
- **THEN** no HTTP request is issued, the response prose is prefixed with `Pull complete.` and renders the post-pull status table (which reflects the existing files unchanged), and the structured snapshot is identical to a fresh `embeddings_status` call

#### Scenario: embeddings_remove deletes the active model's cache
- **WHEN** an MCP client invokes `embeddings_remove` with no `modelId` argument
- **THEN** the active model's per-id directory under `models/` is deleted, `result.structuredContent.removed_dirs` lists the deleted path, `freed_bytes` reports the total bytes freed, and a subsequent `embeddings_status` call shows `files[*].present = false`

#### Scenario: embeddings_remove with all=true wipes every cached model
- **WHEN** an MCP client invokes `embeddings_remove(all = true)` against a cache containing two model directories
- **THEN** both directories are deleted, `removed_dirs` lists both paths, and `freed_bytes` reports the sum of both directories' sizes

#### Scenario: embeddings_remove rejects ambiguous combination
- **WHEN** an MCP client invokes `embeddings_remove(modelId = "jinaai/x", all = true)`
- **THEN** the tool returns an error response naming the conflict and disk is not touched

#### Scenario: embeddings_verify reports informational mode for unpinned manifests
- **WHEN** an MCP client invokes `embeddings_verify` against a populated cache for a model whose manifest has no pinned SHAs (e.g. an arbitrary `--model <id>` override that takes the best-effort branch)
- **THEN** every file row in `structuredContent.files` has `pinned_sha = null` and `match = null`, the prose includes a "no pinned SHA — informational only" note, and the tool's response is not an error

#### Scenario: embeddings_verify reports mismatch post-pin
- **WHEN** an MCP client invokes `embeddings_verify` against a populated cache where at least one cached file's computed SHA does not match its manifest pinned SHA
- **THEN** the affected file rows have `match = false`, the prose names the failing files, and the response is flagged with `isError = true`

### Requirement: verify_scope read-only health snapshot tool

The server SHALL expose a `verify_scope` MCP tool that returns a structured health snapshot for one scope or all scopes, without mutating any state.

The tool SHALL accept a single argument:
- `scope` (string, default `"*"`) — a scope id, comma-separated list, or `"*"` for all non-isolated scopes (the same resolution semantics as every other scope-aware tool).

The response SHALL ship `structuredContent` with one entry per resolved scope, each entry carrying:
- `scope` — the scope id
- `status` — one of `"ok"`, `"degraded"`, `"indexing"`
- `status_message` — the registry's `status_message` (may be `null` when `status = "ok"`)
- `schema_version` — the integer `Schema.Version` value the scope's DB was last opened with
- `views_schema_version` — the integer `Views.SchemaVersion` (matches `describe_schema`)
- `last_indexed_at` — ISO-8601 timestamp from the registry
- `row_counts` — object with `symbols`, `refs`, `edges`, `files`, `annotations`, `diagnostics` long fields
- `integrity_check` — string; `"ok"` when both `PRAGMA integrity_check` and the FTS5 integrity-check pass; otherwise the first failure line
- `drift_sample` — object with `sampled` (int, ≤ 20), `total_files` (int, the scope's `files` row count), `changed` (int, count of sampled files whose on-disk SHA-256 differs from the DB's `content_sha256`), and `changed_paths` (string list, capped at the first 5 changed paths so the response stays bounded)

The tool body SHALL emit `notifications/progress` (per the existing `Progress notifications on slow tools` requirement) at three checkpoints when a `progressToken` is on the originating request: `"reading row counts"` (0.0), `"running integrity_check"` (0.4), `"sampling drift"` (0.8). The `integrity_check` step is the slow one on large DBs; the progress checkpoints prevent the call from looking hung.

The tool SHALL NOT mutate any registry row, DB row, or filesystem state. It SHALL NOT emit a heal event (reads are not heals; the call is recorded in `usage.jsonl` like every other tool via `ToolMetrics.TrackAsync`).

When called against a scope whose `status = "degraded"` (e.g., from missing DB or stuck `indexing` detection in this change, or from any other degraded path), the tool SHALL return the registry's `status` and `status_message` and SHALL omit the `row_counts`, `integrity_check`, and `drift_sample` fields (set to `null`) since they require a healthy DB connection.

When called with `scope = "*"` against a registry containing no non-isolated scopes, the tool SHALL return a structured `no_scopes` diagnostic (matching the convention established by `query_graph` and `describe_schema`) rather than throw.

#### Scenario: Verify a healthy scope
- **GIVEN** a scope `backend` with `status = "ok"`, 1500 symbols, 800 refs, 600 edges, 80 files, 200 annotations, 30 diagnostics, and no drift
- **WHEN** the agent invokes `verify_scope(scope = "backend")`
- **THEN** the response's `structuredContent[0]` has `scope = "backend"`, `status = "ok"`, `row_counts = { symbols: 1500, refs: 800, edges: 600, files: 80, annotations: 200, diagnostics: 30 }`, `integrity_check = "ok"`, `drift_sample = { sampled: 20, total_files: 80, changed: 0, changed_paths: [] }`

#### Scenario: Verify a degraded scope
- **GIVEN** a scope `tools` whose registry row carries `status = "degraded"` and `status_message = "scope DB file missing — call repair_scope or restart"`
- **WHEN** the agent invokes `verify_scope(scope = "tools")`
- **THEN** the response's `structuredContent[0]` has `scope = "tools"`, `status = "degraded"`, `status_message = "scope DB file missing — call repair_scope or restart"`, and the `row_counts` / `integrity_check` / `drift_sample` fields are `null`

#### Scenario: Verify all scopes
- **GIVEN** a registry with non-isolated scopes `frontend` (ok) and `backend` (degraded), plus isolated scope `vendor`
- **WHEN** the agent invokes `verify_scope()` (default `scope = "*"`)
- **THEN** the response's `structuredContent` array contains two entries — one for `frontend` and one for `backend`; the `vendor` scope is excluded (matches the standard `*` fan-out semantics)

#### Scenario: Drift sample surfaces watcher-missed edits
- **GIVEN** a scope `backend` with 100 indexed files; while the server was offline, three of those files were edited so their on-disk SHA-256 no longer matches the DB's `content_sha256`
- **WHEN** the agent invokes `verify_scope(scope = "backend")` after the server restarts (without triggering any reindex)
- **THEN** the response's `drift_sample` has `changed >= 0`; if any of the three edited files were sampled (probabilistic with sample size 20 over 100 files), `changed > 0` and the affected paths appear in `changed_paths` (up to the cap of 5)

#### Scenario: Empty registry returns structured diagnostic
- **GIVEN** a `_meta.db` with no non-isolated scope rows
- **WHEN** the agent invokes `verify_scope()` (default `scope = "*"`)
- **THEN** the response is a `no_scopes` structured diagnostic (matching the established convention) rather than an exception

### Requirement: repair_scope tool

The server SHALL expose a `repair_scope` MCP tool that takes a destructive (or potentially destructive) action against a single named scope.

The tool SHALL accept:
- `scope` (string, required) — a single scope id; `"*"` and comma-separated lists SHALL be rejected with a structured `bad_scope` diagnostic. Repair acts at scope grain; the destructive intent must be explicit and singular.
- `mode` (string, default `"minimal"`) — one of `"minimal"` or `"rebuild"`. Other values SHALL be rejected with a structured `bad_argument` diagnostic.

The tool SHALL ship `structuredContent` of shape:
```
{ scope: string, mode: string, before_status: string, after_status: string, elapsed_ms: long, message: string }
```

`minimal` mode SHALL:
1. Capture `before_status` from the registry.
2. Run `IGraphStore.IntegrityCheckAsync`. If the result is not `"ok"`, return immediately with `after_status = before_status`, `message = "integrity_check failed: <result>; call repair_scope mode=rebuild"`. No mutation occurs.
3. Otherwise, call `IEmbeddingsStore.PruneOrphanedAsync()` (returning a count of pruned rows for the message).
4. Re-run the bounded-retry workspace-open path against the scope (per `Bounded retry on initial workspace open`). 
5. Return with `after_status` from the post-retry registry row, `message = "ok; pruned {N} orphan embeddings; reopened workspace"` (or "...workspace open failed after retries" on final failure).

`rebuild` mode SHALL:
1. Capture `before_status`.
2. Move the scope's DB file from `<repo>/.sourcegraph/scopes/<id>.db` to `<repo>/.sourcegraph/orphans/<id>-rebuild-<utc-iso>.db` (with `:` replaced by `-`). The orphans directory SHALL be created lazily. If the scope DB file does not exist (e.g., missing-DB degraded), skip the archive step.
3. Drop the scope's `IGraphStore` instance and any cached embeddings store; the next operation creates fresh ones.
4. Run a full cold-index for the scope from its `.sourcegraph.json` configuration (the same path as boot-time bring-up).
5. Return with `after_status` reflecting the post-cold-index registry row, `message = "rebuilt; archived previous DB to orphans/{filename}; new symbol_count={N}"`.

Both modes SHALL emit a heal event:
- `kind = "repair-scope-invoked"`
- `ok = (after_status == "ok")`
- `ms = elapsed_ms`
- `details = "mode={mode}; ..."` carrying the same `message` text as the structured response.

Both modes SHALL emit `notifications/progress` per the existing `Progress notifications on slow tools` requirement at the documented checkpoints (minimal: `"running integrity_check"` 0.0, `"pruning orphans"` 0.5, `"reopening workspace"` 0.8; rebuild: `"archiving old DB"` 0.0, `"cold-indexing"` 0.1, `"finalising"` 0.95).

Both modes SHALL be idempotent: calling `minimal` against an already-healthy scope is a no-op (re-runs integrity check + zero-row prune + a no-op workspace reopen); calling `rebuild` twice produces two archive files and two cold-indexes (both end in `ok`).

#### Scenario: minimal on healthy scope is a no-op
- **GIVEN** scope `backend` with `status = "ok"`, integrity_check returns `"ok"`, and zero orphan embeddings rows
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "minimal")`
- **THEN** `before_status = after_status = "ok"`; `message` mentions "ok; pruned 0 orphan embeddings; reopened workspace"; `heals.jsonl` contains one `repair-scope-invoked` line with `ok = true`; no DB row in `symbols` / `refs` / `edges` / `files` is touched

#### Scenario: minimal on corrupted scope refuses
- **GIVEN** scope `backend` whose `IntegrityCheckAsync` returns a non-`"ok"` string
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "minimal")`
- **THEN** `after_status = before_status` (no mutation); `message` includes the substring "call repair_scope mode=rebuild"; `heals.jsonl` contains one `repair-scope-invoked` line with `ok = false` and `details` carrying the integrity-check failure; the DB file is unchanged on disk

#### Scenario: rebuild archives and reindexes
- **GIVEN** scope `backend` exists with a populated DB at `<repo>/.sourcegraph/scopes/backend.db`
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "rebuild")`
- **THEN** an archive file `<repo>/.sourcegraph/orphans/backend-rebuild-<utc-iso>.db` exists with the original byte content; `<repo>/.sourcegraph/scopes/backend.db` exists as a fresh DB at `Schema.Version`; `after_status = "ok"` (assuming the cold-index succeeds); `heals.jsonl` contains one `repair-scope-invoked` line with `ok = true`, `details` matching the message

#### Scenario: rebuild on missing-DB scope skips archive but cold-indexes
- **GIVEN** scope `tools` whose registry row carries `status = "degraded"`, `status_message = "scope DB file missing — call repair_scope or restart"`, and no `<repo>/.sourcegraph/scopes/tools.db` exists
- **WHEN** the agent invokes `repair_scope(scope = "tools", mode = "rebuild")`
- **THEN** no archive file is created (nothing to archive); a fresh `tools.db` is created and cold-indexed; `after_status = "ok"`; `heals.jsonl` contains one `repair-scope-invoked` line

#### Scenario: scope = "*" rejected
- **WHEN** the agent invokes `repair_scope(scope = "*", mode = "minimal")`
- **THEN** the response is a structured `bad_scope` diagnostic (matching the established convention); no mutation occurs; no heal event is written

#### Scenario: invalid mode rejected
- **WHEN** the agent invokes `repair_scope(scope = "backend", mode = "nuke")`
- **THEN** the response is a structured `bad_argument` diagnostic; no mutation occurs; no heal event is written

### Requirement: reconcile_drift tool

The server SHALL expose a `reconcile_drift` MCP tool that walks a single scope's source tree, compares each file's on-disk SHA-256 to the DB's `content_sha256`, and applies the symmetric difference (reindex changed, index added, remove vanished).

The tool SHALL accept:
- `scope` (string, required) — single scope id; `"*"` and comma-separated lists SHALL be rejected with a structured `bad_scope` diagnostic.
- `max_files` (int, default `1000`, hard cap `50000`) — caps the walk; values above the cap SHALL be silently clamped.
- `dry_run` (bool, default `false`) — when `true`, computes the diff without applying it.

The walk SHALL use the same exclusion list as `SolutionWatcher.ShouldIgnore` (`obj/`, `bin/`, `.git/`, `.sourcegraph/`).

The tool SHALL ship `structuredContent` of shape:
```
{ scope: string, scanned_count: int, reindexed_count: int, added_count: int, removed_count: int, unchanged_count: int, partial: bool, dry_run: bool, elapsed_ms: long }
```

Where `partial = true` indicates the walk hit `max_files` and stopped before scanning every file under the root.

When `dry_run = false`, the tool SHALL dispatch the changed and added paths to `RoslynIndexer.IndexChangedFilesAsync` (the same path the watcher uses) and remove the vanished paths via the existing per-file delete path.

When `dry_run = true`, the tool SHALL compute the diff and return it without invoking the indexer or the delete path; no DB row is touched; no heal event is emitted.

When `dry_run = false`, the tool SHALL emit a heal event:
- `kind = "reconcile-drift-invoked"`
- `ok = true` (drift reconciliation does not have a "failed" semantic — partial is reported via the `partial` field, not via `ok`)
- `details = "scanned={N}, reindexed={M}, added={A}, removed={R}, unchanged={U}"`

The tool SHALL emit `notifications/progress` at three checkpoints: `"walking source tree"` (0.0), `"comparing hashes"` (0.3), `"applying changes"` (0.7) — the third checkpoint omitted when `dry_run = true`.

#### Scenario: Reconcile picks up watcher-missed edits, additions, and deletions
- **GIVEN** a scope `backend` with 10 indexed files; while the server was offline, file `A.cs` was edited (SHA changed), file `B.cs` was added, file `C.cs` was deleted
- **WHEN** the agent invokes `reconcile_drift(scope = "backend")` with default args
- **THEN** the response carries `scanned_count = 10` (the 9 remaining + the new B.cs), `reindexed_count = 1` (A.cs), `added_count = 1` (B.cs), `removed_count = 1` (C.cs), `unchanged_count = 8`, `partial = false`, `dry_run = false`; the DB now has rows for A.cs (with the new SHA), B.cs (newly inserted), and no row for C.cs; `heals.jsonl` contains one `reconcile-drift-invoked` line

#### Scenario: dry_run reports diff without applying
- **GIVEN** the same drift scenario as above
- **WHEN** the agent invokes `reconcile_drift(scope = "backend", dry_run = true)`
- **THEN** the response carries the same counts as above (1/1/1/8); the DB is NOT mutated (A.cs still has the old SHA, B.cs has no row, C.cs row still exists); no `heals.jsonl` line is written

#### Scenario: max_files cap returns partial = true
- **GIVEN** a scope whose root contains 5000 source files and `max_files = 100`
- **WHEN** the agent invokes `reconcile_drift(scope = "backend", max_files = 100)`
- **THEN** `scanned_count = 100`, `partial = true`; only the first-walked 100 files are compared; the response message hints "increase max_files to scan all"; no error

#### Scenario: scope = "*" rejected
- **WHEN** the agent invokes `reconcile_drift(scope = "*")`
- **THEN** the response is a structured `bad_scope` diagnostic; no mutation occurs

### Requirement: Autonomous corrupt-DB rebuild gated by env var

When the environment variable `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS` is set to a truthy value (`"1"`, `"true"`, `"yes"`, case-insensitive), the server SHALL autonomously rebuild a scope's DB whenever `ScopedExecution`'s reactive verification confirms corruption (per the `storage` capability's `Reactive integrity check on corruption suspicion` requirement). When the variable is unset or any other value, autonomous rebuild SHALL NOT fire — corruption detection stops at marking the scope `degraded`.

When the env var is enabled and the autonomous rebuild fires, the sequence SHALL be:

1. Emit a heal event with `kind = "corrupt-db-rebuild-started"`, `ok = true`, `details = "fire-and-forget rebuild kicked off"`. The agent's failed tool call returns immediately with the original `GraphStoreCorruptedException`; the rebuild does NOT block the response.
2. Schedule a background task that:
   - Archives the corrupt DB to `<repo>/.sourcegraph/orphans/<id>-corrupt-<utc-iso>.db` (using the same orphans-directory convention introduced in `add-scope-health-surface`, with the `-corrupt-` discriminator distinguishing it from `-rebuild-` and bare `<id>-<ts>.db`).
   - Drops the scope's DB file and runs a fresh cold-index from `.sourcegraph.json` (the same path as `repair_scope mode=rebuild`).
   - On completion, emits a heal event with `kind = "corrupt-db-rebuilt"`, `ok = (after_status == "ok")`, `ms = <wall-clock elapsed>`, `details = $"after_status={after_status}"`.
   - On exception during the rebuild, emits the same heal kind with `ok = false` and `details` carrying the failure message.

The background task SHALL be tied to the host's lifetime cancellation token; on shutdown, the rebuild gets cooperative cancellation. A partially-completed rebuild leaves a fresh-but-incomplete DB in `scopes/`; the next boot's stuck-`indexing` detection (per `add-scope-health-surface`) catches it.

The env var status SHALL be logged at info level on startup (`"Autonomous corrupt-DB rebuild is ENABLED via SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS"`) when enabled, and SHALL NOT be logged when disabled (the default has no signal).

#### Scenario: Env var enabled — autonomous rebuild fires
- **GIVEN** `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS=1` is set on server startup AND a scope `backend` whose DB is physically corrupted
- **WHEN** any tool call against `backend` triggers reactive verification (per the `storage` capability) and the integrity check confirms corruption
- **THEN** `heals.jsonl` contains, in order: one `corruption-detected` line and one `corrupt-db-rebuild-started` line; the agent's tool call returns immediately with the original exception; within bounded wall-clock time (test wait, e.g. 30s), `heals.jsonl` gains a `corrupt-db-rebuilt` line with `ok = true`; `<repo>/.sourcegraph/orphans/backend-corrupt-<utc-iso>.db` exists with the original (corrupted) byte content; `<repo>/.sourcegraph/scopes/backend.db` is fresh; the `backend` registry row is `Status = "ok"`; subsequent tool calls against `backend` succeed

#### Scenario: Env var unset — no autonomous rebuild
- **GIVEN** `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS` is unset (or set to `"0"` / `"false"` / `""`) AND a scope `backend` whose DB is physically corrupted
- **WHEN** the same corruption-triggering call as above is made
- **THEN** `heals.jsonl` contains one `corruption-detected` line; NO `corrupt-db-rebuild-started` line is written; the `backend` registry row is `Status = "degraded"`; subsequent calls return the degraded short-circuit; recovery requires an explicit `repair_scope mode=rebuild` call from the agent

#### Scenario: Background rebuild interrupted by shutdown
- **GIVEN** the env var is enabled AND an autonomous rebuild is in flight (cold-indexing) when the host receives shutdown
- **WHEN** the host's cancellation token fires
- **THEN** the rebuild's `Task.Run` body exits via `OperationCanceledException`; a `corrupt-db-rebuilt` heal line is written with `ok = false` and `details = "rebuild cancelled by host shutdown"`; the partially-built `scopes/<id>.db` is left in place; the next boot's stuck-`indexing` detection catches it (per `add-scope-health-surface`)

### Requirement: Autonomous embeddings prune on cold-index completion

`ScopeHost.ColdIndexAsync` SHALL call `IEmbeddingsStore.PruneOrphanedAsync()` after the cold-index reaches `Status = "ok"`, removing rows from `symbol_embeddings` whose `symbol_id` no longer exists in the `symbols` table. The same prune call SHALL fire from the `repair_scope mode=minimal` path (per `add-scope-repair-tools`), centralised in a small helper to avoid duplication.

When the prune count > 0, the caller SHALL emit a heal event:
- `kind = "embeddings-pruned"`
- `ok = true`
- `ms = <wall-clock elapsed>`
- `details = $"removed {count} orphan rows"`

When the count == 0, no heal event SHALL be emitted (zero-noise convention; cold-indexes that produce no orphans are the common case).

When `PruneOrphanedAsync` itself throws, the caller SHALL log at warning level and emit a heal event with `kind = "embeddings-pruned"`, `ok = false`, `details = ex.Message`. The cold-index outcome (`ok` status) SHALL NOT be reverted on prune failure; the prune is best-effort.

#### Scenario: Cold-index produces orphans, prune fires, heal recorded
- **GIVEN** a scope `backend` whose cold-index just completed and whose `symbol_embeddings` table has 5 rows referencing `symbol_id` values that no longer exist in `symbols` (e.g. after a refactor that deleted the corresponding source files)
- **WHEN** `ColdIndexAsync` completes the post-index prune step
- **THEN** the 5 orphan rows are deleted from `symbol_embeddings`; `heals.jsonl` contains one line with `kind = "embeddings-pruned"`, `scope = "backend"`, `ok = true`, `details = "removed 5 orphan rows"`; the `backend` registry row remains `Status = "ok"`

#### Scenario: Cold-index produces no orphans, no heal event
- **GIVEN** a scope whose cold-index just completed and whose `symbol_embeddings` table has zero orphan rows
- **WHEN** the prune step runs
- **THEN** `PruneOrphanedAsync` returns 0; no `embeddings-pruned` heal line is written; the registry row remains `Status = "ok"`

#### Scenario: Prune failure does not revert cold-index outcome
- **GIVEN** a scope whose cold-index just completed and `PruneOrphanedAsync` throws (e.g. embeddings store is in a broken state)
- **WHEN** the prune step catches the exception
- **THEN** `heals.jsonl` contains one line with `kind = "embeddings-pruned"`, `ok = false`, `details` carrying the exception message; the registry row remains `Status = "ok"`; subsequent tool calls against the scope succeed

#### Scenario: repair_scope minimal also emits the prune heal
- **WHEN** `repair_scope(scope = "backend", mode = "minimal")` runs against a scope with 3 orphan embeddings rows
- **THEN** the prune step within minimal mode emits one `embeddings-pruned` heal line with `details = "removed 3 orphan rows"`, in addition to the `repair-scope-invoked` heal line that the tool itself emits

### Requirement: Evidence-backed gRPC tools

The server SHALL expose read-only, idempotent `trace_rpc` and
`check_proto_contract` MCP tools with named object-root output schemas and
`structuredContent`. Both tools SHALL accept `scope = "<id>"`, `"*"`, or a
comma-separated list, reject selections above 16 scopes, sort per-scope output
by scope id, deterministically bound rows/evidence, and keep the complete
`CallToolResult` below 50,000 serialized characters.

`trace_rpc` SHALL accept only an exact `proto:R:` RPC canonical key or exact
`csharp:` managed canonical key. It SHALL traverse only stored auditable
`grpc-calls` and `implements-rpc` relations. Both relations are stored with the
managed symbol as source and the proto RPC as target; results SHALL state that
orientation and state that a proto-started query used reverse/inbound
traversal. Name-only candidates SHALL NOT be returned.

`check_proto_contract` SHALL report:

- `Grpc001` for a current RPC with no evidence-backed `implements-rpc` edge,
  only when the current source/link universe is complete;
- `Grpc002` for a field-number difference against the persisted prior
  first-successful baseline for the same exact field key;
- `Grpc003` for client/server streaming differences against that prior
  baseline for the same exact RPC key; and
- `Grpc004` for a uniquely associated generated client/server signature that
  is proven inconsistent with the current strict RPC contract.

Change findings SHALL carry current and baseline source evidence plus explicit
baseline policy. Current-state findings SHALL mark the baseline as not
applicable. Findings SHALL use semantic confidence unless their complete
derivation is exact; structural generated-code association SHALL NOT be
reported as exact. Partial/malformed input SHALL return a partial,
`retained_last_good` result with no speculative findings.

#### Scenario: trace_rpc reverses the stored direction
- **GIVEN** `Client.Send ->(grpc-calls) proto:R:medical.v1.Api.Run` and `Server.Run ->(implements-rpc) proto:R:medical.v1.Api.Run`, each with stored occurrence evidence
- **WHEN** `trace_rpc(rpc = "proto:R:medical.v1.Api.Run")` is invoked
- **THEN** it returns the client and server through reverse/inbound traversal, includes their stored evidence, and reports `stored_orientation = "managed-source-to-proto-rpc-target"`

#### Scenario: Exact managed start follows only its persisted RPC edge
- **WHEN** `trace_rpc(rpc = "csharp:M:Medical.Client.Send(...)")` is invoked
- **THEN** only proto RPC targets of that exact symbol's auditable `grpc-calls` / `implements-rpc` edges are selected; similarly named methods create no candidate

#### Scenario: First observation is not a change
- **GIVEN** a complete field/RPC contract has no prior baseline
- **WHEN** indexing and `check_proto_contract` run for the first time
- **THEN** the observation establishes its baseline and neither `Grpc002` nor `Grpc003` is returned

#### Scenario: Field and streaming changes carry two evidence generations
- **GIVEN** prior baselines for field number `1` and unary streaming flags
- **WHEN** current complete facts report field number `9` and server streaming
- **THEN** `check_proto_contract` returns `Grpc002` and `Grpc003`, each with semantic confidence, current evidence, baseline evidence, and `baseline_policy = "first-complete-successful-observation-per-exact-canonical-key"`

#### Scenario: Partial current input produces no speculative check
- **GIVEN** last-good edges and baselines exist
- **WHEN** the latest protobuf/index pass is partial or malformed
- **THEN** `check_proto_contract` returns `partial = true`, `retained_last_good = true`, zero new findings, and a bounded failure describing why the current universe was not analyzed

