## ADDED Requirements

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
