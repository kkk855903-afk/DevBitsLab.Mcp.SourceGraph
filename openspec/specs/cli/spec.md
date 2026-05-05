# CLI

## Purpose

Expose the indexer and MCP server as a single user-friendly executable
(`sourcegraph-mcp`) with focused subcommands for one-shot indexing,
long-running stdio service, stats, and DB cleanup, plus token expansion so a
project-scoped `.mcp.json` can use placeholders like `${workspaceFolder}`.

## Requirements

### Requirement: Subcommand routing
The CLI SHALL accept four top-level subcommands (`serve`, `index`, `stats`,
`clear`) and exit with code `2` on an unrecognised subcommand or argument.

#### Scenario: Run serve with a solution
- **WHEN** `sourcegraph-mcp serve --solution <sln>` is invoked
- **THEN** the host starts an MCP stdio server, registers the
  `LiveIndexService` background service, and runs the indexer against the
  given solution

#### Scenario: Run a one-shot index
- **WHEN** `sourcegraph-mcp index <sln>` is invoked
- **THEN** `RoslynIndexer.IndexSolutionOnceAsync` runs against the solution,
  prints `indexed N files, M symbols, R refs in T s`, and exits `0`

#### Scenario: Print database stats
- **WHEN** `sourcegraph-mcp stats` is invoked against an existing graph DB
- **THEN** the file/symbol/reference/edge counts and the resolved DB path
  are printed to stdout

#### Scenario: Clear the database
- **WHEN** `sourcegraph-mcp clear` is invoked
- **THEN** the graph DB file is deleted (if present), an empty schema is
  recreated, and `cleared <path>` is printed

### Requirement: Help and error UX
The CLI SHALL show a usage block on `--help` / `-h` and on argument errors,
listing every supported subcommand and flag.

#### Scenario: Display help
- **WHEN** the user runs `sourcegraph-mcp --help`
- **THEN** the help text lists `serve`, `index`, `stats`, `clear` plus the
  `--solution`, `-s`, and `--db` flags and their defaults

### Requirement: Token expansion in path arguments
The CLI SHALL expand `${VAR}` placeholders in `--solution` and `--db` values
against process environment variables, with a special-case for
`${workspaceFolder}` that falls back to `WORKSPACE_FOLDER`,
`CLAUDE_PROJECT_DIR`, and `MCP_WORKSPACE_FOLDER` in that order.

#### Scenario: Expand workspaceFolder via env var
- **WHEN** the CLI receives `--solution '${workspaceFolder}/My.slnx'` and
  the MCP client did not expand the token, but `MCP_WORKSPACE_FOLDER=/abs`
  is in the environment
- **THEN** the resolved `SolutionPath` is `/abs/My.slnx`

#### Scenario: Expand a generic env var
- **WHEN** the CLI receives `--solution '${HOME}/repo/My.slnx'`
- **THEN** the resolved `SolutionPath` substitutes `$HOME` from the
  environment

#### Scenario: Reject unresolved placeholders
- **WHEN** the CLI receives a value containing a placeholder that resolves
  to nothing (e.g. `${nope}/foo.sln`)
- **THEN** parsing fails with an `ArgumentException` whose message names
  the offending flag, prints the helpful "use ${workspaceFolder} or set
  MCP_WORKSPACE_FOLDER" guidance, and the process exits with code `2`

### Requirement: Sensible default DB path resolution
`ResolvedDbPath` SHALL pick the database location with a deterministic
priority: `--db` if given, then `<solution-dir>/.sourcegraph/graph.db` if
`--solution` is given, then a per-user cache directory
(`$XDG_CACHE_HOME` / `%LOCALAPPDATA%` / `~/.cache`), then `$TMPDIR`.

#### Scenario: Per-solution DB beside the .slnx
- **WHEN** the CLI is given `--solution /work/My.slnx` and no `--db`
- **THEN** the resolved DB path is
  `/work/.sourcegraph/graph.db` and the directory is created if missing

#### Scenario: Per-user fallback when no solution
- **WHEN** the CLI is invoked from CWD `/` (e.g. by an MCP host) without
  `--solution` or `--db`
- **THEN** the DB lands at the per-user cache path; CWD is never used

### Requirement: Embedding-related CLI flags
The CLI SHALL accept `--model <id>` to override the embedding model and `--no-embeddings` to disable the embedding pipeline entirely; both apply to `serve` and `index`.

#### Scenario: Disable embeddings
- **WHEN** `sourcegraph-mcp serve --solution <sln> --no-embeddings` is invoked
- **THEN** no per-scope embeddings drain is started, the model is not downloaded, and `semantic_search` returns the disabled-message

#### Scenario: Override model
- **WHEN** the user passes `--model nomic-ai/CodeRankEmbed`
- **THEN** the server resolves and (if needed) downloads that model, ignores any cached embeddings whose `model_version` is different, and re-embeds on next index

### Requirement: Scope-management subcommands
The CLI SHALL accept `sourcegraph-mcp scopes list`, `sourcegraph-mcp scopes add <name> ...`, and `sourcegraph-mcp scopes remove <name>` to inspect and edit `.sourcegraph.json`.

#### Scenario: List scopes
- **WHEN** the user runs `sourcegraph-mcp scopes list` in a repo with three configured scopes
- **THEN** the command prints each scope's id, name, kind (solutions/projects/paths), isolation flag, and last-indexed timestamp

### Requirement: init-scopes scaffolder
The CLI SHALL accept `sourcegraph-mcp init-scopes` that discovers .slnx files at the repo root and writes a `.sourcegraph.json` listing one scope per discovered solution.

#### Scenario: Bootstrap from siblings
- **WHEN** the user runs `init-scopes` in a repo containing `frontend.slnx` and `backend.slnx` at the root
- **THEN** `.sourcegraph.json` is written with two scopes (`frontend`, `backend`), each pointing at its solution, and no `default_scope` is set
