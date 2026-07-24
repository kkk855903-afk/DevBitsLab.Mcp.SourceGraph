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
listing every supported subcommand and flag. Help SHALL default to English and
accept `--lang en` or `--lang zh` before or after `--help` / `-h` to select
English or Simplified Chinese. `--lang` without help, a missing value, and an
unsupported language SHALL fail with exit code `2`.

#### Scenario: Display help
- **WHEN** the user runs `sourcegraph-mcp --help`
- **THEN** the help text lists `serve`, `index`, `stats`, `clear` plus the
  `--solution`, `-s`, and `--db` flags and their defaults

#### Scenario: Display Simplified Chinese help
- **WHEN** the user runs `sourcegraph-mcp --help --lang zh` or
  `sourcegraph-mcp --lang zh --help`
- **THEN** the process exits `0`, writes a Simplified Chinese usage block to
  stdout, and preserves the same command and flag tokens as English help

#### Scenario: Reject invalid help language usage
- **WHEN** `--lang` is used without `--help` / `-h`, has no value, or names a
  language other than `en` or `zh`
- **THEN** parsing fails with an explanatory error and the process exits `2`

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
The CLI SHALL accept `--model <id>` to override the embedding model, `--no-embeddings` to disable the embedding pipeline entirely, `--allow-model-download` to authorize automatic download, and `--no-model-download` as an explicit/legacy fail-closed switch while still using a pre-populated cache. These flags apply to `serve` and `index`. Automatic model download SHALL be disabled by default. `SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1` SHALL be equivalent to the allow flag; `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` SHALL force offline mode and take precedence.

#### Scenario: Disable embeddings
- **WHEN** `sourcegraph-mcp serve --solution <sln> --no-embeddings` is invoked
- **THEN** no per-scope embeddings drain is started, the model is not downloaded, and `semantic_search` returns the disabled-message

#### Scenario: Override model
- **WHEN** the user passes `--model nomic-ai/CodeRankEmbed`
- **THEN** the server selects that model identity without issuing an HTTP request, ignores cached embeddings whose `model_version` is different, and either uses an already-populated cache or leaves semantic search disabled until the model is explicitly pulled or download is explicitly allowed

#### Scenario: Default empty cache stays offline
- **WHEN** `serve` or `index` starts with an empty model cache and no model-download flag or environment variable
- **THEN** no HTTP request is issued, non-embedding indexing continues, and semantic search is disabled for that session

#### Scenario: Explicitly allow automatic download
- **WHEN** the user passes `--allow-model-download` or sets `SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1` and the model cache is empty
- **THEN** the server downloads the selected model best-effort and starts the embedding pipeline when the files are ready

#### Scenario: Disable auto-download with empty cache
- **WHEN** the user passes `--no-model-download` and the cache directory has no `model.onnx` or `tokenizer.json`
- **THEN** no HTTP request is issued, the embedding pipeline is disabled for this session (same payload as `--no-embeddings`), and the warning text names the cache path so the operator can pre-populate it

#### Scenario: Disable auto-download with populated cache
- **WHEN** the user passes `--no-model-download` and the cache directory already contains valid `model.onnx` + `tokenizer.json`
- **THEN** the cached model is loaded and embeddings run normally; no HTTP request is issued

#### Scenario: Disable auto-download via environment variable
- **WHEN** the user starts the server with `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` and no `--no-model-download` flag
- **THEN** the server behaves identically to the `--no-model-download` flag form

#### Scenario: Fail-closed environment variable wins
- **WHEN** `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` is set together with `--allow-model-download` or `SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1`
- **THEN** no HTTP request is issued and the pre-populated-cache-only behavior is retained

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

### Requirement: Embeddings subcommand group
The CLI SHALL accept a `sourcegraph-mcp embeddings <verb>` top-level subcommand group that exposes inspection and management of the embedding model cache. At v1 the supported verbs are `status`, `pull`, `remove`, and `verify`. An unknown nested verb SHALL exit with code `2` and an error message naming the supported verbs.

#### Scenario: Default model status
- **WHEN** `sourcegraph-mcp embeddings status` is invoked with no `--model` override
- **THEN** the command prints the cache directory path, the active model id and dimension, one row per manifest file (`localName`, presence flag, size in bytes when present, computed SHA-256 when present, pinned SHA when the manifest specifies one and a `match` indicator), and the free-disk bytes on the cache volume; the exit code is `0`

#### Scenario: Explicit pull
- **WHEN** `sourcegraph-mcp embeddings pull` is invoked with no `--model` override and an empty cache
- **THEN** the command synchronously downloads the active model's manifest files into the cache directory, prints a final status snapshot identical to the `status` verb's output, and exits `0`

#### Scenario: Pull is idempotent
- **WHEN** `sourcegraph-mcp embeddings pull` is invoked against a populated cache
- **THEN** no HTTP request is issued, the existing files are left untouched, the status snapshot is printed, and the command exits `0`

#### Scenario: Remove the active model
- **WHEN** `sourcegraph-mcp embeddings remove` is invoked with no flags and the active model's cache directory is populated
- **THEN** the command deletes the active model's per-id directory under `models/`, prints `{ "modelId": "<active>", "removedDirs": [...], "freedBytes": N }` (or the equivalent prose), and exits `0`

#### Scenario: Remove all cached models
- **WHEN** `sourcegraph-mcp embeddings remove --all` is invoked
- **THEN** every per-id directory under `models/` is deleted (the `models/` parent itself is preserved), the printed report names every removed directory and the total bytes freed, and the command exits `0`

#### Scenario: Conflicting --model and --all rejected
- **WHEN** `sourcegraph-mcp embeddings remove --model jinaai/foo --all` is invoked
- **THEN** the command prints an `ArgumentException` message naming both flags, prints the `embeddings remove` usage line, and exits `2` without touching disk

#### Scenario: Verify, no pinned SHA in manifest
- **WHEN** `sourcegraph-mcp embeddings verify --model someorg/custom-model` is invoked against a populated cache for a non-default model whose manifest has no pinned SHA-256 strings (the override-model path uses a best-effort manifest)
- **THEN** the command prints the computed SHA of every cached file alongside a `(no pinned SHA — informational only)` note and exits `0`

#### Scenario: Verify, pinned SHA matches
- **WHEN** `sourcegraph-mcp embeddings verify` is invoked against a populated cache and every cached file's computed SHA matches its manifest pinned SHA
- **THEN** every row carries `match: true` and the command exits `0`

#### Scenario: Verify, pinned SHA mismatch
- **WHEN** `sourcegraph-mcp embeddings verify` is invoked against a populated cache where at least one cached file's computed SHA does not match its manifest pinned SHA
- **THEN** the affected rows carry `match: false`, the prose names the failing files, and the command exits `2`

#### Scenario: Inspect a non-active cached model
- **WHEN** the user passes `sourcegraph-mcp embeddings status --model someorg/other-model` against a cache containing both the active model and `someorg/other-model`
- **THEN** the printed status reflects the `someorg/other-model` directory only; the active model's data is not included in the report

### Requirement: init subcommand
The CLI SHALL accept `sourcegraph-mcp init` that runs an interactive (default) or flag-driven (`--yes`) onboarding flow producing per-client MCP configuration files, optionally pre-warming the index, and printing a closing report. Default writes SHALL be project-scoped (under `--root`, default CWD); user-scope writes SHALL require an explicit per-client opt-in flag.

The subcommand SHALL accept the following flags:

- `--yes` / `-y` — non-interactive; accept all defaults documented in this requirement.
- `--client <id>` (repeatable) — restrict to the listed clients (`claude-code`, `codex`, `copilot`, `cursor`, `continue`, `claude-desktop`).
- `--no-<client>` — exclude one client even if it would otherwise be auto-selected.
- `--user-<client>` — write a supported client's config to its user-scope path instead of the project-scope path. Codex is project-only and `--user-codex` is rejected.
- `--claude-desktop` — required to wire Claude Desktop (no project-scope option exists for that client).
- `--solution <path>` (repeatable) — override solution-discovery; passes through to `init-scopes` core logic when multiple solutions are configured.
- `--no-embeddings` / `--no-history` — propagate the corresponding `serve` flag into the written `args` array.
- `--prewarm` / `--no-prewarm` — opt in to / out of running `RoslynIndexer.IndexSolutionOnceAsync` after writing configs.
- `--install-mode {global,local-tool,in-repo}` — choose the resulting `command`/`args` shape: `global` invokes `sourcegraph-mcp` directly (default); `local-tool` emits `command: "dotnet"`, `args: ["sourcegraph-mcp", ...]` and assumes the repo already has a `.config/dotnet-tools.json` listing the tool (created via `dotnet new tool-manifest && dotnet tool install DevBitsLab.Mcp.SourceGraph.Tool`; `init` does not create or merge the manifest in v1); `in-repo` emits `command: "dotnet"`, `args: ["run", "--project", "<server csproj>", "--no-build", "--", "serve", ...]`.
- `--print-only` — print the per-client config snippets to stdout with `# would write to: <path>` comment lines; write no files.
- `--force` — overwrite an existing `sourcegraph` server entry without prompting (in interactive mode) or without skipping (in `--yes` mode); never modifies other servers' entries.
- `--root <path>` — repository root (default CWD).

#### Scenario: Interactive init wires Claude Code in a fresh repo
- **WHEN** a user runs `sourcegraph-mcp init` in a repo containing `MySln.slnx` and accepts the defaults at every prompt
- **THEN** `<root>/.mcp.json` is written with the `mcpServers.sourcegraph` entry; the closing report names the file written and suggests `sourcegraph-mcp demo` as the next step

#### Scenario: Non-interactive init for CI
- **WHEN** a user runs `sourcegraph-mcp init --yes --client copilot --client claude-code --print-only` from a CI script
- **THEN** the command exits `0` after writing nothing, having printed two config snippets to stdout — one prefixed with `# would write to: <root>/.vscode/mcp.json` (Copilot's `servers`/`type` shape) and one prefixed with `# would write to: <root>/.mcp.json` (Claude Code's `mcpServers` shape)

#### Scenario: Non-interactive Codex init
- **WHEN** a user runs `sourcegraph-mcp init --yes --client codex`
- **THEN** `<root>/.codex/config.toml` contains a valid `[mcp_servers.sourcegraph]` table with portable repository-relative args and `cwd = ".."`; no user-level Codex config is read or written

#### Scenario: Init merges into an existing config without clobbering other servers
- **WHEN** the user has a pre-existing `<root>/.mcp.json` containing an `mcpServers.other-server` entry, and `sourcegraph-mcp init --yes --client claude-code` is invoked
- **THEN** the resulting `.mcp.json` contains both `mcpServers.other-server` (unchanged) and a new `mcpServers.sourcegraph` entry; the closing report says `wired sourcegraph (existing other-server preserved)`

#### Scenario: Init refuses to overwrite a differing existing entry without --force
- **WHEN** the user has a pre-existing `<root>/.mcp.json` containing an `mcpServers.sourcegraph` entry whose `args` differ from what we would write, and `sourcegraph-mcp init --yes --client claude-code` (without `--force`) is invoked
- **THEN** the file is left unchanged; the closing report includes a warning naming the file and suggesting `--force` to overwrite, and the process exits `2`

#### Scenario: Claude Desktop requires --claude-desktop opt-in
- **WHEN** `sourcegraph-mcp init --yes` is invoked with no `--claude-desktop` flag
- **THEN** Claude Desktop's user-scope config file is not touched even if every other auto-detected client is wired

#### Scenario: Pre-warm runs after writing configs
- **WHEN** `sourcegraph-mcp init --yes --client claude-code --prewarm --solution ./MySln.slnx` is invoked
- **THEN** after the `.mcp.json` write completes, `RoslynIndexer.IndexSolutionOnceAsync` is invoked against `./MySln.slnx`; the closing report includes the line `pre-warmed index: N files in T s`

### Requirement: doctor subcommand
The CLI SHALL accept `sourcegraph-mcp doctor` that runs a read-only environment diagnostic and prints a per-check `pass | warn | fail` summary. The subcommand SHALL accept `--root <path>` and `--json` flags. The exit code SHALL follow the convention: `0` if every check passed, `2` if at least one warn was raised, `1` if any check produced a hard fail.

The diagnostic SHALL cover at minimum: `.NET SDK >= 10.0` on PATH; `git` on PATH; `--root` readable; presence and parseability of `.sourcegraph.json` (or graceful absence); embedding model cache presence and size; per-scope DB writability under `<root>/.sourcegraph/scopes/`; and whether each existing per-client config contains a `sourcegraph` server entry. Presence checks SHALL NOT be described as full command/args equivalence checks.

#### Scenario: Doctor recognizes Codex TOML
- **WHEN** `<root>/.codex/config.toml` contains a semantic `mcp_servers.sourcegraph` table
- **THEN** `doctor` reports `client-codex` as wired; a valid Codex config without that table produces a warning instead of a false pass

#### Scenario: Healthy environment
- **WHEN** `sourcegraph-mcp doctor` is invoked in a repo with a valid `.sourcegraph.json`, .NET 10 SDK on PATH, git on PATH, and `.mcp.json` already wired
- **THEN** every check prints `[OK]` (or `✓` on a tty), the command exits `0`, and the summary line reads `8/8 checks passed`

#### Scenario: Missing git surfaces as a warn
- **WHEN** `sourcegraph-mcp doctor` is invoked on a system where `git` is not on PATH
- **THEN** the corresponding check prints `[WARN]` with the message `git not on PATH — \`who_authored\` and \`recent_changes\` will return empty; pass --no-history to silence`, the command exits `2`, and other checks continue to run

#### Scenario: Missing .NET SDK surfaces as a fail
- **WHEN** `sourcegraph-mcp doctor` is invoked in an environment where `.NET 10` is not installed
- **THEN** the corresponding check prints `[FAIL]` with a message naming the expected version and a download URL; the command exits `1`

#### Scenario: --json output is machine-readable
- **WHEN** `sourcegraph-mcp doctor --json` is invoked
- **THEN** stdout is a single JSON document with shape `{"checks": [{"name": "...", "status": "pass|warn|fail", "message": "..."}, ...], "exit_code": <code>}` and no human-readable preamble

### Requirement: demo subcommand
The CLI SHALL accept `sourcegraph-mcp demo` that runs four canned MCP tool calls (`ping`, `graph_stats`, `search_symbols`, `find_definition`) against the active scope and prints each result's markdown — leaf prefix included — to stdout. The subcommand SHALL accept `--scope <id>`, `--root <path>`, and `--no-color` flags.

If `graph_stats` reports zero symbols (the scope was never indexed), the subcommand SHALL bail with a "no symbols indexed — run `sourcegraph-mcp index <solution>` first, or run `init --prewarm`" message and exit `2`.

#### Scenario: Demo against a freshly-indexed scope
- **WHEN** `sourcegraph-mcp demo` is invoked in a repo where the default scope has been indexed
- **THEN** four bordered sections appear on stdout — one per canned call, each labeled with its tool name — and each section's body begins with a `🌿` glyph (unless `--no-color` or `SOURCEGRAPH_NO_LEAF=1` is set, in which case the leaf is suppressed but the section borders remain)

#### Scenario: Demo bails on an empty graph
- **WHEN** `sourcegraph-mcp demo` is invoked against a scope whose DB has zero symbols
- **THEN** the `ping` and `graph_stats` sections still print; instead of attempting `search_symbols`/`find_definition`, the command prints the empty-graph guidance message and exits `2`

#### Scenario: Demo with --scope picks a specific scope
- **WHEN** `sourcegraph-mcp demo --scope frontend` is invoked in a multi-scope repo where only `frontend` is indexed
- **THEN** all four canned calls run against the `frontend` scope; the closing summary reads `demo against scope=frontend: ok`

### Requirement: init-scopes integration
The CLI SHALL preserve the existing `init-scopes` subcommand behaviour and the existing `init-scopes` requirement; `init` SHALL invoke the same scope-discovery logic internally when multiple `.slnx`/`.sln` files are detected, so an existing setup script that calls `init-scopes` continues to work unchanged.

#### Scenario: init-scopes still works standalone
- **WHEN** a user runs `sourcegraph-mcp init-scopes` in a multi-solution repo (without going through `init`)
- **THEN** `.sourcegraph.json` is written with one scope per discovered solution, exactly as before this change

#### Scenario: init delegates scope discovery to the same code path
- **WHEN** a user runs `sourcegraph-mcp init` in a repo containing `frontend.slnx` and `backend.slnx`
- **THEN** the resulting `.sourcegraph.json` is identical to the file `init-scopes` would have written, and the closing report names both `frontend` and `backend` as configured scopes

