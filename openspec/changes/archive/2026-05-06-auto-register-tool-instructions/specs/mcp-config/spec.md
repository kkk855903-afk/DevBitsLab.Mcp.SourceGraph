## ADDED Requirements

### Requirement: --no-instructions CLI flag
The `sourcegraph-mcp serve` CLI SHALL accept a `--no-instructions` flag that, when present, suppresses publication of `ServerInstructions` in the MCP `initialize` response.

#### Scenario: Flag suppresses instructions
- **WHEN** `sourcegraph-mcp serve --solution X.slnx --no-instructions` is invoked
- **THEN** the resulting MCP server's initialize response carries a null or empty instructions string

#### Scenario: Help text documents the flag
- **WHEN** `sourcegraph-mcp --help` (or equivalent) is invoked
- **THEN** the rendered help text includes a one-line description of `--no-instructions`

### Requirement: SOURCEGRAPH_NO_INSTRUCTIONS env var
The server SHALL also honour `SOURCEGRAPH_NO_INSTRUCTIONS` as a process environment variable; values `1` or `true` (case-insensitive) suppress `ServerInstructions` exactly as the CLI flag would.

#### Scenario: Env var with no flag suppresses
- **WHEN** the server is started without `--no-instructions` but with `SOURCEGRAPH_NO_INSTRUCTIONS=1` in env
- **THEN** the initialize response carries no instructions string

#### Scenario: Flag is sufficient on its own
- **WHEN** the server is started with `--no-instructions` and no env var is set
- **THEN** the initialize response carries no instructions string
