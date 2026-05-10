## ADDED Requirements

### Requirement: First-class per-client config writers
The CLI SHALL ship a dedicated configuration writer for each first-class MCP client (`Claude Code`, `GitHub Copilot`, `Cursor`, `Continue`, `Claude Desktop`). Each writer SHALL emit the schema documented by its target client verbatim — schemas are not normalised across writers. The writers SHALL be invokable from the `init` subcommand and SHALL share an `IClientConfigWriter` contract so future clients can be added by adding a new writer file.

The five v1 writers SHALL emit:

- **Claude Code** — JSON at `<root>/.mcp.json` (project) or `~/.claude/.mcp.json` (user) with top-level `mcpServers.<server-name>` shape and `command` + `args` fields.
- **GitHub Copilot** — JSON at `<root>/.vscode/mcp.json` (project) or `chat.mcp.servers` in user `settings.json` (user) with top-level `servers.<server-name>` shape, an explicit `type: "stdio"` field on each server entry, and `command` + `args` fields. The schema differs from Claude Code's by the top-level key (`servers` vs `mcpServers`) and by the required `type` field.
- **Cursor** — JSON at `<root>/.cursor/mcp.json` (project) or `~/.cursor/mcp.json` (user) with top-level `mcpServers.<server-name>` shape (matches Claude Code's shape).
- **Continue** — YAML at `<root>/.continue/mcp/sourcegraph.yaml` (project) or `~/.continue/mcp/sourcegraph.yaml` (user) with top-level `name` / `command` / `args` keys.
- **Claude Desktop** — JSON at the platform-specific user path (`%APPDATA%\Claude\claude_desktop_config.json` on Windows; `~/Library/Application Support/Claude/claude_desktop_config.json` on macOS; `~/.config/Claude/claude_desktop_config.json` on Linux) with top-level `mcpServers.<server-name>` shape. No project-scope path exists for Claude Desktop.

#### Scenario: Copilot writer emits the distinct schema
- **WHEN** `sourcegraph-mcp init --yes --client copilot --print-only` is invoked
- **THEN** the printed JSON has top-level key `servers` (not `mcpServers`), the `servers.sourcegraph` object contains an explicit `"type": "stdio"` field, and the file path comment reads `# would write to: <root>/.vscode/mcp.json`

#### Scenario: Continue writer emits YAML
- **WHEN** `sourcegraph-mcp init --yes --client continue --print-only` is invoked
- **THEN** the printed content parses as YAML (not JSON), starts with `name: sourcegraph`, and the file path comment reads `# would write to: <root>/.continue/mcp/sourcegraph.yaml`

#### Scenario: Cursor writer matches Claude Code shape
- **WHEN** the Cursor writer and the Claude Code writer are both invoked against the same solution and install-mode
- **THEN** the per-server JSON object inside `mcpServers.sourcegraph` is identical in both files (same `command`, same `args`); the only difference is the file path

### Requirement: Project-scoped defaults; user-scope opt-in
The `init` subcommand SHALL default each client's write target to that client's project-scoped path when one exists. Writing to a user-scoped path SHALL require an explicit per-client opt-in flag. Claude Desktop is the only client without a project-scope option; it SHALL require an explicit `--claude-desktop` flag to be wired at all, and SHALL NOT be auto-selected even when its config file is detected on disk.

#### Scenario: Default init touches no user-tree files
- **WHEN** `sourcegraph-mcp init --yes` is invoked with no `--user-*` or `--claude-desktop` flags, in a repo with a `.slnx` and the user's machine has Claude Code, Cursor, and Claude Desktop all installed
- **THEN** `<root>/.mcp.json` and `<root>/.cursor/mcp.json` are written; no file under the user's home directory is read or written; Claude Desktop is not wired

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
