# MCP Config

## Purpose

Make the server registerable in MCP clients (Claude Code, Cursor, Continue,
Claude Desktop) via a project-scoped `.mcp.json` file at the repo root, and
support relative-style paths so the same config works across machines without
per-user edits.

## Requirements

### Requirement: Project-scoped registration via .mcp.json
The repository SHALL ship a `.mcp.json` at its root that registers the
`sourcegraph` server with `command` and `args` arrays following the MCP
client convention.

#### Scenario: Open the repo in Claude Code
- **WHEN** a user opens the repository in Claude Code
- **THEN** Claude Code reads `.mcp.json`, lists `sourcegraph` as a
  project-scoped server, and prompts the user to approve it once

### Requirement: ${workspaceFolder} placeholder support
`--solution` (and `--db`) values in `.mcp.json` SHALL accept a
`${workspaceFolder}` placeholder; if the MCP client doesn't expand it, the
server resolves it from `WORKSPACE_FOLDER`, `CLAUDE_PROJECT_DIR`, or
`MCP_WORKSPACE_FOLDER` env vars.

#### Scenario: Client expands the token
- **WHEN** the MCP client (Claude Code, Cursor) substitutes
  `${workspaceFolder}` with the project root before launching the
  subprocess
- **THEN** the server receives an absolute path and starts indexing without
  any further expansion

#### Scenario: Server expands the token as a fallback
- **WHEN** the MCP client passes `${workspaceFolder}` through verbatim and
  `MCP_WORKSPACE_FOLDER` is set in the spawn environment
- **THEN** `CommandLine.ExpandTokens` substitutes the env value and the
  server proceeds

### Requirement: Run from in-repo source
The shipped `.mcp.json` SHALL invoke the in-repo Server project via
`dotnet run --no-build --no-launch-profile --verbosity quiet`, so a
freshly-cloned developer can run the MCP server without
`dotnet tool install -g` after a one-time `dotnet build`.

#### Scenario: Fresh clone, build once
- **WHEN** a developer clones the repository and runs `dotnet build`
- **THEN** opening the repo in Claude Code launches the local server via
  `dotnet run --project ${workspaceFolder}/src/DevBitsLab.Mcp.SourceGraph.Server`
  with no global tool install required

### Requirement: Document alternative registration patterns
Project documentation SHALL describe how to use the server in *other*
repositories: global `dotnet tool` install, git submodule + `dotnet run`,
and `.config/dotnet-tools.json` local manifest.

#### Scenario: Reference the alternatives
- **WHEN** a developer reads `CLAUDE.md` (or `README.md`)
- **THEN** they see concrete `.mcp.json` snippets for each alternative
  pattern with the prerequisite steps for each
