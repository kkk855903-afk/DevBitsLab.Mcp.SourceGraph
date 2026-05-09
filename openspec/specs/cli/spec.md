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

### Requirement: vocabulary subcommand
The CLI SHALL accept a `vocabulary` top-level subcommand that exposes diagnostic information about the soft-registry kind vocabulary. At v1 the only nested verb is `list`, which is also the default if the user invokes `vocabulary` with no nested verb. Future revisions may add `add` / `validate` / `import` if a strict registry is introduced.

#### Scenario: Run vocabulary list with default options
- **WHEN** `sourcegraph-mcp vocabulary list` is invoked from a repo root with at least one configured scope
- **THEN** the command exits `0`, prints one section per scope to stdout, and each section enumerates the scope's `edge_kinds`, `symbol_kinds`, and `annotation_flavors` arrays as observed in storage

#### Scenario: Vocabulary subcommand defaults to list
- **WHEN** `sourcegraph-mcp vocabulary` is invoked with no nested verb
- **THEN** the command behaves identically to `sourcegraph-mcp vocabulary list` (the only verb at v1 is the default)

#### Scenario: Unknown nested verb errors out
- **WHEN** `sourcegraph-mcp vocabulary register` (or any string other than `list`) is invoked
- **THEN** the command prints an unknown-subcommand error to stderr and exits `2`

### Requirement: vocabulary list output format
The `vocabulary list` subcommand SHALL print, for each scope known to the active `IScopeRegistry`, a header naming the scope id, then three labelled lists (`edge_kinds`, `symbol_kinds`, `annotation_flavors`). Each entry in each list SHALL be tagged with its source (`sdk` if the value matches a constant exposed by `EdgeKinds` / `SymbolKinds`; `plugin: <id>@<version>` if it matches a registered plugin's declared kinds; otherwise `unknown`) and a live emission count obtained by counting matching rows in the scope's storage (`COUNT(*) FROM edges WHERE kind_name = ?`, etc.).

#### Scenario: Single-language scope output
- **WHEN** `vocabulary list` runs against a scope whose only loaded indexer is the built-in C# Roslyn indexer with a freshly-indexed `Sample.sln`
- **THEN** every `edge_kinds` entry is tagged `[sdk, emitted: <N>]` and every `symbol_kinds` entry is tagged the same way; `annotation_flavors` is `csharp-attribute  [sdk, emitted: <N>]`

#### Scenario: Polyglot scope shows mixed sources
- **WHEN** `vocabulary list` runs against a scope that loads the built-in C# indexer and a hypothetical XAML indexer plugin with id `xaml-indexer@1.0.0`
- **THEN** SDK constants are tagged `[sdk]`, XAML-emitted kinds are tagged `[plugin: xaml-indexer@1.0.0]`, and live emission counts reflect the actual storage state for each

#### Scenario: Empty scope output
- **WHEN** `vocabulary list` runs against a scope whose storage is missing (cold scope, never indexed — the per-scope DB file does not exist on disk)
- **THEN** the section header for that scope is followed by a single `(no database at <path> — never indexed)` note, the `edge_kinds` / `symbol_kinds` / `annotation_flavors` lists are empty (no SDK fabricated `emitted: 0` rows), no error is produced, and the command continues to the next scope

### Requirement: vocabulary list drift detection
After printing the per-scope kind lists, the `vocabulary list` subcommand SHALL print a "Drift candidates" section that compares pairs of kinds within each scope using Levenshtein distance with threshold ≤2. Pairs that meet the threshold SHALL be listed in the form `<kind-a> ~ <kind-b>` so a maintainer can spot likely typos (`bind-path` vs `binds-path`).

#### Scenario: No drift detected
- **WHEN** every kind in every scope is at Levenshtein distance >2 from every other kind
- **THEN** the "Drift candidates" section header is followed by `(none)` and the command exits `0`

#### Scenario: Drift detected
- **WHEN** a scope's `edge_kinds` includes both `binds-path` and `bind-path` (Levenshtein distance 1)
- **THEN** the "Drift candidates" section lists `bind-path ~ binds-path` and the command still exits `0` by default

### Requirement: vocabulary list strict mode
The `vocabulary list` subcommand SHALL accept an optional `--strict` flag. When set, the command SHALL exit with code `2` if any drift candidate was reported (the full output is still printed first).

#### Scenario: Strict mode with no drift
- **WHEN** `vocabulary list --strict` is invoked against a scope with no drift candidates
- **THEN** the command exits `0`

#### Scenario: Strict mode with drift
- **WHEN** `vocabulary list --strict` is invoked against a scope where `bind-path` and `binds-path` both occur
- **THEN** the command prints the full output (including the drift candidate) and exits `2`

### Requirement: vocabulary list scope filter
The `vocabulary list` subcommand SHALL accept an optional `--scope <id>` flag that restricts the output to a single scope id; the default is to print every scope known to the registry.

#### Scenario: Filter to one scope
- **WHEN** `vocabulary list --scope backend` is invoked in a repo whose `.sourcegraph.json` declares scopes `backend`, `frontend`, and `vendor`
- **THEN** only the `backend` section is printed; `frontend` and `vendor` are not visited
