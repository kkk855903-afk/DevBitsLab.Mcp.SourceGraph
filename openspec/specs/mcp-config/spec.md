# MCP Config

## Purpose

Make the server registerable in MCP clients including Claude Code, Codex,
GitHub Copilot, Cursor, Continue, and Claude Desktop via each client's native
configuration shape, and support portable project-scoped paths where the
client provides that scope.
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

### Requirement: .sourcegraph.json config file
The system SHALL recognise `.sourcegraph.json` at the repo root as the source of truth for scope configuration; absent file = single synthesised `default` scope.

#### Scenario: Documented schema
- **WHEN** a developer reads `CLAUDE.md` or `README.md`
- **THEN** the `.sourcegraph.json` schema is documented with the three scope-definition kinds (`solutions[]`, `projects[]`, `paths[]`), the `exclude[]` glob list, the `isolated` flag, and the `default_scope` field

#### Scenario: Schema validation
- **WHEN** the loader reads a malformed `.sourcegraph.json` (missing required fields, unknown keys, conflicting scope ids)
- **THEN** it fails fast on startup with a precise message naming the offending key, and the server exits with code `2`

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

### Requirement: `.sourcegraph.json` scope shape
The documented `.sourcegraph.json` schema for a scope entry SHALL be extended with two optional fields: `language` (kebab-case string) and `enrichment` (object with one `lsp` sub-key carrying `command` + optional `args`). Both fields are absent by default; existing single-solution and multi-scope configs continue to load unchanged. The shipped schema documentation in the README SHALL include the new fields with one-line semantics.

#### Scenario: Pre-existing config loads unchanged
- **WHEN** a `.sourcegraph.json` file authored against the previous schema (no `language`, no `enrichment` on any scope) is loaded by the new host version
- **THEN** the loader produces the same `ScopeConfig` it produced before, with no additional warnings or errors

#### Scenario: New config with both fields populated
- **WHEN** a `.sourcegraph.json` declares
  ```jsonc
  { "scopes": [{ "name": "frontend", "paths": ["src/web/**/*.ts"],
                  "language": "typescript",
                  "enrichment": { "lsp": { "command": "typescript-language-server", "args": ["--stdio"] } } }] }
  ```
- **THEN** the loader succeeds, the `Scope` (or sister runtime record) exposes both values, and the README's documented shape matches what was loaded

### Requirement: `init-scopes` does not emit the new fields
The `sourcegraph-mcp init-scopes` CLI subcommand SHALL not emit `language` or `enrichment` keys for the synthesised default config — those are operator-authored, not auto-discoverable. Editing them into an existing `.sourcegraph.json` is the operator's responsibility at this SDK version; CLI helpers for adding them via flags on `scopes add` are deferred to a follow-up change.

#### Scenario: `init-scopes` produces a minimal config
- **WHEN** the user runs `sourcegraph-mcp init-scopes` in a repo with a single .slnx and no `.sourcegraph.json`
- **THEN** the scaffolder writes a config containing only `name` + `solutions` for the default scope; no `language` or `enrichment` keys appear

### Requirement: First-class per-client config writers
The CLI SHALL ship a dedicated configuration writer for each first-class MCP client (`Claude Code`, `Codex`, `GitHub Copilot`, `Cursor`, `Continue`, `Claude Desktop`). Each writer SHALL emit the schema documented by its target client verbatim — schemas are not normalised across writers. The writers SHALL be invokable from the `init` subcommand and SHALL share an `IClientConfigWriter` contract so future clients can be added by adding a new writer file.

The writers SHALL emit:

- **Claude Code** — JSON at `<root>/.mcp.json` (project) or `~/.claude/.mcp.json` (user) with top-level `mcpServers.<server-name>` shape and `command` + `args` fields.
- **Codex** — TOML at `<root>/.codex/config.toml` (project only) with a `[mcp_servers.sourcegraph]` table containing `command`, `args`, and `cwd`. It SHALL use `cwd = ".."` and repository-relative arguments instead of `${workspaceFolder}`. Codex loads this layer only for a trusted project; `init` SHALL NOT edit `~/.codex/config.toml`.
- **GitHub Copilot** — JSON at `<root>/.vscode/mcp.json` (project-scope only in v1) with top-level `servers.<server-name>` shape, an explicit `type: "stdio"` field on each server entry, and `command` + `args` fields. The schema differs from Claude Code's by the top-level key (`servers` vs `mcpServers`) and by the required `type` field. User-scope wiring for Copilot would require editing VS Code's `settings.json` under `chat.mcp.servers`; that path is intentionally not implemented in v1 and `--user-copilot` results in a documented skip with paste-it-yourself guidance.
- **Cursor** — JSON at `<root>/.cursor/mcp.json` (project) or `~/.cursor/mcp.json` (user) with top-level `mcpServers.<server-name>` shape (matches Claude Code's shape).
- **Continue** — YAML at `<root>/.continue/mcp/sourcegraph.yaml` (project) or `~/.continue/mcp/sourcegraph.yaml` (user) with top-level `name` / `command` / `args` keys.
- **Claude Desktop** — JSON at the platform-specific user path (`%APPDATA%\Claude\claude_desktop_config.json` on Windows; `~/Library/Application Support/Claude/claude_desktop_config.json` on macOS; `~/.config/Claude/claude_desktop_config.json` on Linux) with top-level `mcpServers.<server-name>` shape. No project-scope path exists for Claude Desktop.

#### Scenario: Copilot writer emits the distinct schema
- **WHEN** `sourcegraph-mcp init --yes --client copilot --print-only` is invoked
- **THEN** the printed JSON has top-level key `servers` (not `mcpServers`), the `servers.sourcegraph` object contains an explicit `"type": "stdio"` field, and the file path comment reads `# would write to: <root>/.vscode/mcp.json`

#### Scenario: Continue writer emits YAML
- **WHEN** `sourcegraph-mcp init --yes --client continue --print-only` is invoked
- **THEN** the printed content parses as YAML (not JSON), starts with `name: sourcegraph`, and the file path comment reads `# would write to: <root>/.continue/mcp/sourcegraph.yaml`

#### Scenario: Codex writer emits portable TOML
- **WHEN** `sourcegraph-mcp init --yes --client codex --print-only` is invoked
- **THEN** the printed content is valid TOML at `<root>/.codex/config.toml`, contains `[mcp_servers.sourcegraph]`, `command`, `args`, and `cwd = ".."`, contains no `${workspaceFolder}`, and writes no user-level config

#### Scenario: Cursor writer matches Claude Code shape
- **WHEN** the Cursor writer and the Claude Code writer are both invoked against the same solution and install-mode
- **THEN** the per-server JSON object inside `mcpServers.sourcegraph` is identical in both files (same `command`, same `args`); the only difference is the file path

### Requirement: Project-scoped defaults; user-scope opt-in
The `init` subcommand SHALL default each client's write target to that client's project-scoped path when one exists. Writing to a user-scoped path SHALL require an explicit per-client opt-in flag. Claude Desktop is the only client without a project-scope option; it SHALL require an explicit `--claude-desktop` flag to be wired at all, and SHALL NOT be auto-selected even when its config file is detected on disk.

#### Scenario: Default init touches no user-tree files
- **WHEN** `sourcegraph-mcp init --yes` is invoked with no `--user-*` or `--claude-desktop` flags, in a repo with a `.slnx` and the user's machine has Claude Code, Cursor, and Claude Desktop all installed
- **THEN** `<root>/.mcp.json`, `<root>/.codex/config.toml`, and `<root>/.cursor/mcp.json` are written; no file under the user's home directory is written; read-only detection may inspect existing user-scope client configs for the onboarding summary, and Claude Desktop is not wired

#### Scenario: --user-cursor writes to home
- **WHEN** `sourcegraph-mcp init --yes --client cursor --user-cursor` is invoked
- **THEN** `~/.cursor/mcp.json` is written or merged into; `<root>/.cursor/mcp.json` is not touched; the closing report names the home-tree path explicitly

### Requirement: Merge-by-server-name semantics
Each writer SHALL read any pre-existing target file before writing, parse it, and produce one of six plans:

- `Insert` — target file is absent OR exists without a `sourcegraph` server entry; the plan emits a fresh document or a merged document that adds ours.
- `NoOpAlreadyMatches` — a `sourcegraph` entry already exists and is logically equivalent to ours; no write is performed.
- `ReplaceOurs` — a `sourcegraph` entry exists and **differs** from ours, AND `--force` is set; the plan emits the new merged document.
- `SkipExistingDiffers` — a `sourcegraph` entry exists and differs, AND `--force` is NOT set; the file is left alone, the run reports a conflict, and `init` exits `2` (CI-failure signal).
- `SkipHasComments` — the existing target file contains JS-style line/block comments outside string literals; round-tripping through the JSON parser would silently strip them. The file is left alone and the writer prints the would-be snippet to stdout for the user to paste manually. Informational, exit `0`.
- `SkipUnsupported` — the selected client / scope combination has no writer support in v1 (e.g. `--user-copilot`, since Copilot's user-scope config requires editing VS Code's `settings.json` under `chat.mcp.servers`, which has no dedicated writer). Informational, exit `0`.

Other servers' entries SHALL never be removed, modified, or reordered by any writer.

For Codex TOML specifically, the writer SHALL strictly parse both the existing
file and every write candidate. It SHALL preserve comments, unrelated
settings, other MCP servers, extra `sourcegraph` options, and nested tables.
`--force` SHALL patch only the source spans for the owned `command`, `args`,
and `cwd` values or insert missing owned keys. Invalid UTF-8, invalid TOML,
inline/dotted/implicit target tables, table arrays, or incompatible
`mcp_servers` structures SHALL be left untouched even under `--force`. An
owned value containing nested comments SHALL likewise be left untouched when
updating it would discard those comments.

#### Scenario: Codex force update preserves the shared TOML document
- **WHEN** `.codex/config.toml` contains comments, another MCP server, extra `enabled` / timeout fields, a nested `mcp_servers.sourcegraph.env` table, and differing owned values, and `init --yes --client codex --force` runs
- **THEN** only `command`, `args`, and `cwd` are updated; every other byte-level setting and comment remains present and the resulting document passes strict TOML parsing

#### Scenario: Unsafe Codex TOML is never overwritten
- **WHEN** `.codex/config.toml` is malformed or expresses `sourcegraph` through an inline, dotted, implicit, or array-table shape
- **THEN** `init --yes --client codex --force` leaves the file untouched, reports manual intervention, and exits `2`

#### Scenario: Existing other-server is preserved
- **WHEN** `<root>/.mcp.json` already contains `mcpServers.other-server` and `init --yes --client claude-code` runs
- **THEN** the resulting file contains both `mcpServers.other-server` (with all its fields unchanged) and a new `mcpServers.sourcegraph`; key ordering is preserved where the underlying parser supports it

#### Scenario: Existing sourcegraph entry that differs without --force is skipped
- **WHEN** `<root>/.mcp.json` already contains an `mcpServers.sourcegraph` whose `args` array differs from what we would write, and `init --yes --client claude-code` (without `--force`) runs
- **THEN** the file is left untouched, a warning is printed naming the file and the conflicting entry, and the process exits `2`

#### Scenario: Existing matching sourcegraph entry is a no-op
- **WHEN** `<root>/.mcp.json` already contains an `mcpServers.sourcegraph` byte-for-byte equivalent to what we would write
- **THEN** the file is not rewritten (its mtime is unchanged), and the closing report logs `claude-code: already wired (no change)`

#### Scenario: --force replaces our entry only
- **WHEN** `<root>/.mcp.json` contains both `mcpServers.other-server` and a stale `mcpServers.sourcegraph`, and `init --yes --client claude-code --force` runs
- **THEN** the resulting file contains `mcpServers.other-server` unchanged and a freshly-written `mcpServers.sourcegraph`

### Requirement: Comment-aware degraded mode
When a writer detects that its target file contains JavaScript-style line comments (`//`) outside string literals — a hand-edited config — it SHALL NOT round-trip the file through the JSON parser (which would silently strip the comments). It SHALL instead degrade to print-only mode for that client, emit the snippet to stdout with a `# config has comments at <path> — please paste manually` warning, and continue with the next client.

#### Scenario: Hand-commented .mcp.json triggers degraded mode
- **WHEN** `<root>/.mcp.json` contains lines like `// only enable in dev` above an `mcpServers` object, and `init --yes --client claude-code` runs
- **THEN** the file is not modified, stdout includes the snippet that would have been written to it preceded by the warning line, and the process exits `0` (degrade is informational, not an error)
