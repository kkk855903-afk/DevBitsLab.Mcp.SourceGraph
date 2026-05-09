## MODIFIED Requirements

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
- **THEN** the response is an empty result set with a brief note that the kind was not present in the active scope's published `edge_kinds` vocabulary

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

## ADDED Requirements

### Requirement: Vocabulary published in MCP initialize response
The MCP `initialize` response SHALL include three string arrays alongside the existing `ServerInstructions` payload: `edge_kinds`, `symbol_kinds`, and `annotation_flavors`. Each array SHALL list the distinct kebab-case identifiers that the active scope's loaded indexers are configured to emit, sorted lowercase and deduplicated. Sources are: the kebab-case constants exposed by `EdgeKinds` / `SymbolKinds`; constants declared by loaded plugins; and any kinds already present in the scope's storage from a prior index pass.

#### Scenario: Single-language scope vocabulary
- **WHEN** an MCP client completes the initialize handshake against a scope whose only loaded indexer is the built-in C# Roslyn indexer
- **THEN** `edge_kinds` is the sorted distinct union of the built-in C# constants (`["calls", "implements", "implements-member", "inherits", "instantiates", "overrides-member", "tests", "throws", "uses-type"]`); `symbol_kinds` is the corresponding C# symbol set; `annotation_flavors` is `["csharp-attribute"]`

#### Scenario: Vocabulary suppressed alongside instructions
- **WHEN** the server is started with `--no-instructions` (or `SOURCEGRAPH_NO_INSTRUCTIONS=1`)
- **THEN** the `initialize` response carries no `edge_kinds` / `symbol_kinds` / `annotation_flavors` arrays (the existing instructions suppression also suppresses the vocabulary)

#### Scenario: Polyglot scope vocabulary
- **WHEN** the active scope additionally loads a plugin that emits `renders-component` and `binds-path` edges and a `xaml-element` symbol kind
- **THEN** the `edge_kinds` array additionally contains `"binds-path"` and `"renders-component"`, and `symbol_kinds` additionally contains `"xaml-element"`, both still sorted lowercase
