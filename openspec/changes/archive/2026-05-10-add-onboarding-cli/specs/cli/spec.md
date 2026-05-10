## ADDED Requirements

### Requirement: init subcommand
The CLI SHALL accept `sourcegraph-mcp init` that runs an interactive (default) or flag-driven (`--yes`) onboarding flow producing per-client MCP configuration files, optionally pre-warming the index, and printing a closing report. Default writes SHALL be project-scoped (under `--root`, default CWD); user-scope writes SHALL require an explicit per-client opt-in flag.

The subcommand SHALL accept the following flags:

- `--yes` / `-y` — non-interactive; accept all defaults documented in this requirement.
- `--client <id>` (repeatable) — restrict to the listed clients (`claude-code`, `copilot`, `cursor`, `continue`, `claude-desktop`).
- `--no-<client>` — exclude one client even if it would otherwise be auto-selected.
- `--user-<client>` — write that client's config to its user-scope path instead of the project-scope path.
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

The diagnostic SHALL cover at minimum: `.NET SDK >= 10.0` on PATH; `git` on PATH; `--root` readable; presence and parseability of `.sourcegraph.json` (or graceful absence); embedding model cache presence and size; per-scope DB writability under `<root>/.sourcegraph/scopes/`; per-client config-file presence and whether each contains a `sourcegraph` server entry that matches what `init` would write today.

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
