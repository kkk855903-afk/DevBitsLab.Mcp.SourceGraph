## ADDED Requirements

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

## MODIFIED Requirements

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
