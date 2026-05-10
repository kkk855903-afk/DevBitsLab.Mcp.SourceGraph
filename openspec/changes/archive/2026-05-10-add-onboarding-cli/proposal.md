## Why

The journey from "I just heard about this" to "my agent is answering questions with 🌿 leaves" today is too long, too quiet, and too unevenly documented across MCP clients.

Concretely:

- A first-time .NET dev who clones the repo (or installs the global tool) has to read ~600 lines of README, hand-author a `.mcp.json`/`.vscode/mcp.json`/`~/.cursor/mcp.json` (each with a slightly different schema), wait through a silent 10–60s cold index, and only then hope their first agent question lands. There is no checkpoint anywhere where a human sees the leaf appear and knows it works.
- The `init-scopes` subcommand is the only existing piece of self-service onboarding, and it only delivers value to multi-solution monorepo users. Single-solution users — the larger cohort — get nothing scaffolded.
- Per-client documentation is uneven: Claude Code is first-class with detailed snippets; Cursor / Continue / Claude Desktop are summarized as "use the same shape"; **GitHub Copilot is not mentioned at all** despite shipping production MCP support, and uses a *different* config schema (`servers` + `type: "stdio"` in `.vscode/mcp.json`, not `mcpServers` in `.mcp.json`).
- Failures during environment setup (missing .NET 10 SDK, missing git, unreadable .slnx, malformed `.sourcegraph.json`) surface as raw exceptions in stderr, not actionable diagnostics.

The fix is three new CLI subcommands and a unified per-client config writer that treats every documented client as first-class with project-scoped defaults.

## What Changes

- **New `sourcegraph-mcp init` subcommand.** Interactive by default; fully flag-driven for CI. Detects environment (SDK, git, `.slnx`/`.sln` files at the root, existing client configs) and offers to wire any subset of clients the user picks. Writes project-scoped configs by default; user-scope writes (Claude Desktop, or `~/.cursor/mcp.json` instead of `.cursor/mcp.json`) require an explicit opt-in flag. Optionally pre-warms the index so the first `tools/call` returns warm.
- **New `sourcegraph-mcp doctor` subcommand.** Read-only environment diagnostic. Reports SDK version, git availability, repo-root detection, `.slnx`/`.sln` readability, `.sourcegraph.json` validity, embedding model cache size, per-scope DB writability, and which client config files are present (with whether each contains a `sourcegraph` entry). Exits `0` on healthy, `2` on actionable findings, `1` on hard errors.
- **New `sourcegraph-mcp demo` subcommand.** Runs a fixed bundle of canned tool calls (`ping`, `graph_stats`, `find_definition` against a representative top-by-incoming-edges symbol, `search_symbols` with a partial fragment) against the active scope and prints the same markdown an MCP client would render — leaf prefix included. Provides the "ah, it works" confidence moment without requiring an agent loop.
- **Per-client config writers, with merge semantics.** First-class support for: Claude Code (`.mcp.json` at repo root, or `~/.claude/.mcp.json` for user-scope), GitHub Copilot (`.vscode/mcp.json` using `servers` + `type: "stdio"` schema, or `chat.mcp.servers` in user `settings.json`), Cursor (`.cursor/mcp.json` or `~/.cursor/mcp.json`), Continue (`.continue/mcp/sourcegraph.yaml`), Claude Desktop (`claude_desktop_config.json` — user-scope only, no project equivalent exists). Every writer reads any existing file, replaces or inserts only its own server entry keyed by name, and preserves any other servers the user has registered. `--force` overwrites the existing `sourcegraph` entry without prompting; default behavior asks before replacing a non-trivial existing entry.
- **`init-scopes` is preserved as a focused entry point** and is invoked internally by `init` when multiple solutions are detected. Anyone scripting `init-scopes` today continues to work unchanged.
- **Flag surface designed for CI.** `--yes` for non-interactive defaults; `--print-only` emits each client's config snippet to stdout without writing files (the path it *would* write to is included as a comment); per-client `--<client>` / `--no-<client>` toggles bypass the picker; `--install-mode {global,local-tool,in-repo}` selects how the resulting `command`/`args` invokes the server (global tool is default, local tool manifest is recommended for committed configs, in-repo is for this repo's own bundled `.mcp.json`).
- **README quickstart restructure.** A "60-second first run" section above Features showing the `init` → `demo` → "open in Claude Code" path. Per-client sections gain equal real estate (Claude Code / Copilot / Cursor / Continue / Claude Desktop), each with the exact file path and JSON shape; Copilot's schema delta (`servers` + `type`) is called out inline.

## Capabilities

### New Capabilities
<!-- None — this change extends an existing CLI capability rather than adding a new domain. -->

### Modified Capabilities

- `cli`: Adds three new subcommands (`init`, `doctor`, `demo`) with their full flag surfaces, scenarios for interactive and non-interactive operation, and exit-code semantics. Documents that `init-scopes` continues to work and is invoked internally by `init`.
- `mcp-config`: Adds first-class support for Copilot (`.vscode/mcp.json` schema, including `servers`/`type` delta from Claude Code), Cursor, Continue, and Claude Desktop config writers. Documents merge semantics (preserve other servers; only the `sourcegraph` entry is touched). Documents the project-scoped-by-default rule and the explicit user-scope opt-in.

## Impact

- **Code (medium).** New `Cli/InitCli.cs`, `Cli/DoctorCli.cs`, `Cli/DemoCli.cs`. New `Cli/ClientConfigWriters/` directory with one writer per client (`ClaudeCodeWriter`, `CopilotWriter`, `CursorWriter`, `ContinueWriter`, `ClaudeDesktopWriter`) sharing a small `IClientConfigWriter` contract. `Program.cs` routes the three new subcommands. `init-scopes` is refactored to expose its core logic as a method that `init` calls.
- **Spec.** 1 new requirement block on `cli` (three subcommands + flag surface + exit codes). 1 new requirement block on `mcp-config` (Copilot/Cursor/Continue/Claude Desktop writers + merge semantics + scope rules).
- **Tests.** New `OnboardingCliTests.cs` exercising init in `--print-only` mode against a fake filesystem (no actual writes), per-client writer tests covering merge-into-existing-config and clean-write paths, doctor scenario tests covering each diagnostic, demo scenario tests against the existing `tests/fixtures/Sample.sln` fixture.
- **Public API / dependencies.** No new NuGet refs (we already pull in `System.Text.Json`; YAML for Continue uses `YamlDotNet` if it's not already in the tree — falls back to hand-emitted YAML if avoiding the dep is preferred). No SDK bump.
- **Backward compatibility.** Pure additive on the CLI side (new subcommands; existing subcommands untouched). On the file-write side, merge semantics ensure no user's existing client configs are clobbered. README restructure preserves existing anchors via redirect headings where the old section names linger.
- **Documentation.** README quickstart restructure (the largest doc delta in this change). CLAUDE.md gets a one-paragraph note about `init` / `doctor` / `demo` so future Claude sessions surface the commands when users ask "how do I set this up?". CHANGELOG.md note.
- **Token cost.** Zero on the wire — these are CLI-side commands that don't touch the MCP protocol surface. The README quickstart compresses ~30 lines of installation prose into a 3-command code block.
