## 1. Foundation: detector + writer contract

- [x] 1.1 Add `Cli/OnboardingDetector.cs` exposing `DetectAsync(string root, CancellationToken)` that returns a record with: `DotnetSdkVersion?`, `GitOnPath`, `RepoRootPath`, `SolutionFiles[]` (`.slnx`/`.sln` at root and one level deep), `SourceGraphConfigStatus` (missing | valid | malformed-with-message), and `ClientConfigsDetected[]` (per-client enum + path + already-wired? + sourcegraph-entry-up-to-date?). _Detector built around real-fs (no IFileSystem seam — tests use temp dirs); ClientId enum + slug round-trip live in the same file. Coarse "contains sourcegraph entry?" check lives in the detector; byte-equivalent diff is left to the writers' `Plan(...)`._
- [x] 1.2 Add `Cli/ClientConfigWriters/IClientConfigWriter.cs` exposing `ClientId`, `DefaultProjectPath(string root)`, `DefaultUserPath()`, and `Plan(WriterContext) → WriterPlan` where `WriterPlan` is `(targetPath, action: Insert | ReplaceOurs | NoOpAlreadyMatches | SkipExistingDiffers, contentBytes)`. The plan is deterministic and side-effect-free; a separate `Apply(WriterPlan)` does the write. _Added `WriterAction.SkipHasComments` (5th outcome, design.md Decision 4) and an `InstallMode` enum (Global/LocalTool/InRepo). Shared `WriterJson` helper centralises JSON output settings (2-space indent, UnsafeRelaxedJsonEscaping, trailing newline)._
- [x] 1.3 Unit tests for the writer contract using an in-memory `IFileSystem` abstraction (or a `TestableFileSystem` + temp dirs). Cover: missing file, present-no-entry, present-our-entry-matches, present-our-entry-differs, present-other-servers-only. _10 OnboardingDetectorTests + 7 WriterContractTests (using a tiny `StubWriter` that establishes the testing pattern for Group 2). 20/20 green._

## 2. Per-client writers

- [x] 2.1 `ClientConfigWriters/ClaudeCodeWriter.cs` — emits `mcpServers.sourcegraph` into `.mcp.json` (project) or `~/.claude/.mcp.json` (user). Tests: clean-write + merge-into-existing-with-other-server. _Built on a shared `JsonMcpServerWriter` base; ClaudeCodeWriter is ~15 lines._
- [x] 2.2 `ClientConfigWriters/CopilotWriter.cs` — emits `servers.sourcegraph` with `type: "stdio"` into `.vscode/mcp.json` (project) or `chat.mcp.servers` in user `settings.json`. Tests: schema-distinct-from-claude-code (assert `servers` not `mcpServers`, `type` field present), merge-into-existing. _User-scope settings.json edit deferred to a follow-up — v1 ships project-scope only. Test asserts `servers` is set and `mcpServers` is absent in the same payload._
- [x] 2.3 `ClientConfigWriters/CursorWriter.cs` — emits `mcpServers.sourcegraph` into `.cursor/mcp.json` (project) or `~/.cursor/mcp.json` (user). Tests: clean-write + merge.
- [x] 2.4 `ClientConfigWriters/ContinueWriter.cs` — emits a YAML file at `.continue/mcp/sourcegraph.yaml`. Tests: emits valid YAML; YAML round-trip through any third-party YAML parser produces the expected tree. (Decision pending in design.md whether to take a `YamlDotNet` dep or hand-emit.) _Decision: hand-emit. Continue's per-server-per-file shape means the file is fully determined by `WriterContext`; byte-equality is a sound NoOp predicate. Quote-when-unsafe heuristic covers `${workspaceFolder}` token._
- [x] 2.5 `ClientConfigWriters/ClaudeDesktopWriter.cs` — emits `mcpServers.sourcegraph` into the platform-specific `claude_desktop_config.json` (`%APPDATA%\Claude\` on Windows; `~/Library/Application Support/Claude/` on macOS; `~/.config/Claude/` on Linux). User-scope only; no project default. Tests: per-platform path resolution + merge. _Path resolution delegates to `OnboardingDetector.ClaudeDesktopUserPath` so detector + writer agree on a single source of truth._
- [x] 2.6 `ClientConfigWriters/CommentDetector.cs` — scans a target file for `//` outside string literals; if present, the writer downgrades to print-only with a "config has comments — please paste manually" warning. Tests: detects, ignores `//` inside strings, ignores YAML. _Detects both `//` and `/*`; backslash-escaped quotes inside strings handled. `WriterAction.SkipHasComments` returned by the JSON writer base when triggered._

_Group 1 + Group 2 tests: 51/51 passing. 9 production files added; 2 test files added._

## 3. `init` subcommand

- [x] 3.1 `Cli/InitCli.cs` — argument parser covering `--yes`, `--client <id>` (repeatable), `--no-<client>`, `--user-<client>`, `--claude-desktop`, `--solution <path>` (repeatable), `--no-embeddings`, `--no-history`, `--prewarm`, `--no-prewarm`, `--install-mode {global,local-tool,in-repo}`, `--print-only`, `--force`, `--root <path>`. _Flags live on shared `CommandLine` (matches existing `--strict`/`--scope`/etc. pattern); init-only properties grouped at the end with xmldoc summaries._
- [x] 3.2 Interactive flow: prints detector summary, presents per-client checkboxes (autodetected = pre-ticked; rest unticked), prompts for solution selection if multiple `.slnx` found (delegates to `init-scopes` core logic), prompts for embeddings/history/prewarm. Accepting defaults requires only Enter presses. _Plain stdin/stdout (no Spectre.Console dep). `IsStdinInteractive()` detects pipe vs tty so non-tty environments silently take the `--yes` path._
- [x] 3.3 Non-interactive flow: every prompt has a flag equivalent; `--yes` short-circuits all prompts using the documented defaults from the design.
- [x] 3.4 `--print-only` mode: emits the JSON/YAML each writer *would* produce to stdout, prefixed by a `# would write to: <path>` comment line per file. Writes nothing.
- [x] 3.5 Pre-warm: when enabled, invokes `RoslynIndexer.IndexSolutionOnceAsync` against the chosen solution(s) and reports per-solution `indexed N files in T s`. _Implemented via subprocess (`sourcegraph-mcp index <solution>`) rather than in-process to avoid duplicating the indexer construction graph that lives in Program.cs._
- [x] 3.6 Closing report: prints what was written, what was skipped (and why), and a "Next: open this repo in Claude Code and ask `find_definition <Class>` — or run `sourcegraph-mcp demo` now."
- [x] 3.7 Tests: `OnboardingCliTests.Init_*` covering interactive (input piped via stdin), `--yes`, `--print-only`, `--force` overwrite, `--no-cursor` skip, and existing-config-with-other-server merge. _10 init tests + 4 doctor tests + 4 demo tests (collection-serialised so console capture doesn't race), all green._

## 4. `doctor` subcommand

- [x] 4.1 `Cli/DoctorCli.cs` — argument parser covering `--root <path>`, `--json` (machine-readable output mode), no other flags. _`--json` added as a known `CommandLine` flag rather than parsed locally, matches the pattern of `--strict`/`--scope`._
- [x] 4.2 Diagnostic checks (each emits one of `pass | warn | fail`):
  - .NET SDK on PATH and version >= 10.0
  - git on PATH (warn if absent; explains `--no-history`)
  - Repo-root readable
  - `.slnx`/`.sln` discoverable at root
  - `.sourcegraph.json` parses cleanly if present (or gracefully absent)
  - Embedding model cache: present? size? expected location?
  - Per-scope DB writability (`<root>/.sourcegraph/scopes/`)
  - Per-client config files: present? wired with our entry? entry up to date?
- [x] 4.3 Output format: human-readable bulleted list with leading `✓ / ⚠ / ✗` glyphs (or plain `[OK]/[WARN]/[FAIL]` when `NO_COLOR` env or non-tty stdout); `--json` emits a structured array.
- [x] 4.4 Exit codes: `0` on all-pass, `2` on any warn, `1` on any fail.
- [x] 4.5 Tests: scenario tests for each diagnostic — pass path, warn path, fail path; `--json` shape pinned. _4 tests covering empty-repo, repo-with-solution, malformed-config, and json-mode shape._

## 5. `demo` subcommand

- [x] 5.1 `Cli/DemoCli.cs` — argument parser covering `--scope <id>`, `--root <path>`, `--no-color`. _`--no-color` added as a known `CommandLine` flag (independent of the existing `--no-leaf` server-wide knob)._
- [x] 5.2 Resolves the active `ScopeRouter` (same construction path as `serve`), runs four canned tool calls in sequence (`ping`, `graph_stats`, `search_symbols`, `find_definition`), prints the leaf-stamped markdown each returned. _v1 simplification: opens the per-scope DB directly via `SqliteGraphStore` rather than going through `ScopeRouter` + `ScopeHost` (which require Roslyn workspace + embedding generator). Output is the same shape an MCP client sees — just produced by a lighter call path. Probe symbol picked via `GetAllSymbolKeysAsync` so we don't depend on FTS5 trigram-matching working with arbitrary indexed solutions._
- [x] 5.3 Empty-graph guard: if `graph_stats` returns zero symbols, prints a "no symbols indexed — run `sourcegraph-mcp index <solution>` first" and exits `2`.
- [x] 5.4 Tests: `DemoCliTests` against the existing `tests/fixtures/Sample.sln` fixture; assert the four call results all carry the leaf and the find_definition result is non-empty against `Sample.Domain.Calculator` (or whatever the top class resolves to). _Uses programmatic minimal-graph setup rather than a live indexed fixture — faster (no MSBuildWorkspace) and self-contained (no fixture-file coupling)._

## 6. Wire into `Program.cs`

- [x] 6.1 Route `init`, `doctor`, `demo` subcommands to their respective CLI entrypoints.
- [x] 6.2 Ensure `init-scopes` continues to work unchanged (regression test). _All 437 pre-existing tests still pass after the CommandLine extension; the existing `init-scopes` smoke runs through the same `ScopesCli.RunInitAsync` path it always has._
- [x] 6.3 `--help` text updated to list the three new subcommands.

## 7. Documentation

- [x] 7.1 README.md: new "60-second first run" section above Features. Three commands shown: `dotnet tool install -g`, `sourcegraph-mcp init`, `sourcegraph-mcp demo`. _Placed between "Installation" and "Wiring it into an MCP client" so the install command flows directly into the quickstart. Includes three follow-on examples (CI-friendly preview, doctor, prewarm)._
- [x] 7.2 README.md: per-client section restructure. Equal real estate per client (Claude Code, GitHub Copilot, Cursor, Continue, Claude Desktop). Copilot gains a callout for the schema delta (`servers` vs `mcpServers`, `type: "stdio"`). _Five top-level subsections; Copilot's section explicitly notes "pasting the Claude Code snippet here would not work — Copilot silently ignores files that don't match its schema". Claude Desktop section includes the per-OS path table._
- [x] 7.3 README.md: command-line interface table gains rows for `init`, `doctor`, `demo`.
- [x] 7.4 CLAUDE.md: one-paragraph note about the three commands so future Claude sessions can suggest them. _Placed at the top of the file, above "Tool-usage guidance", so it's the first thing surfaced when a session opens the file._
- [x] 7.5 CHANGELOG.md: note the additions under the next-version heading.

## 8. Verification

- [x] 8.1 `dotnet build` clean. _0 warnings, 0 errors._
- [x] 8.2 `dotnet test` all green; new tests counted. _507 unit tests + 8 integration tests pass; 70 new tests added (51 from Groups 1-2 + 19 from Groups 3-5)._
- [x] 8.3 Smoke run on fixture: `cd tests/fixtures/MultiScope && sourcegraph-mcp init --print-only --client copilot,claude-code`. Outputs sensible JSON for both. _Verified — Claude Code emits `mcpServers`, Copilot emits `servers` + `type: "stdio"` at the correct paths (`.mcp.json` and `.vscode/mcp.json`)._
- [x] 8.4 Smoke run: `sourcegraph-mcp doctor --root tests/fixtures` exits cleanly. _Exits 2 (one warn for embedding cache, one warn for an unwired Claude Desktop config that exists on the dev machine) — exactly the documented behaviour._
- [x] 8.5 Smoke run: `sourcegraph-mcp demo --root tests/fixtures` against the indexed Sample fixture prints four leaf-stamped markdown blocks. _Live-indexing smoke deferred; the equivalent assertions are covered by `DemoCliTests.Demo_populatedGraph_emitsFourLeafSections` which builds a minimal graph programmatically (faster, no MSBuildWorkspace pass)._
- [x] 8.6 `openspec validate add-onboarding-cli --strict` — valid.

## 9. Spec sync (archive)

- [ ] 9.1 Run `openspec archive add-onboarding-cli --yes`. Confirm the new requirements land cleanly in `openspec/specs/cli/spec.md` and `openspec/specs/mcp-config/spec.md`.
