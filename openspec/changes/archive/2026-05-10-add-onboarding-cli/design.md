## Context

The repo today exposes a CLI surface focused on running the server (`serve`), one-shot operations (`index`, `stats`, `clear`), scope management (`init-scopes`, `scopes list/add/remove`), plugin inspection (`plugins list/info`), and a vocabulary diagnostic (`vocabulary list`). Onboarding for the *human evaluator* is not a first-class concept: the assumption is that someone will read README, hand-author config, run `serve`, and trust that the silence between command and first agent response is normal.

Two facts shape the design space:

1. **The MCP client ecosystem has diverged.** Claude Code, Cursor, and Continue all use `mcpServers` as their top-level key. GitHub Copilot — a first-class target for this project per maintainer direction — uses `servers` + an explicit `type: "stdio"` field inside `.vscode/mcp.json`. Claude Desktop has no project-scoped concept at all; its only config slot is the user-scoped `claude_desktop_config.json`. Treating any of these as "the others, with minor variation" leads to subtle config bugs (a writer that emits `mcpServers` into `.vscode/mcp.json` produces a file Copilot silently ignores).
2. **Project-scope is the default safe move.** Writing into a user's home directory — `~/.cursor/mcp.json`, `~/Library/Application Support/Claude/claude_desktop_config.json`, `~/.claude/.mcp.json` — affects state outside the repo and persists after the user has long forgotten they ran `init`. A user evaluating the tool in a sandboxed clone deserves the assurance that nothing in their home tree changed unless they explicitly asked.

The change introduces three independent subcommands that share a common environment-detection layer (`OnboardingDetector`) and a common per-client config-writing layer (`IClientConfigWriter` + per-client implementations). Each subcommand can be developed and tested independently; the writers are independently testable and follow a uniform contract that future clients (JetBrains, Visual Studio, etc.) can plug into.

## Goals / Non-Goals

**Goals:**

- A first-time user runs one command (`sourcegraph-mcp init`) and ends up with a wired, optionally pre-indexed, ready-to-query setup for whichever clients they have or want.
- A user troubleshooting why "it isn't working" runs `sourcegraph-mcp doctor` and gets actionable findings, not stack traces.
- A user wondering whether the index is healthy runs `sourcegraph-mcp demo` and sees the same markdown the agent would see, with the leaf, in <2 seconds.
- Copilot is supported with the same fidelity as Claude Code: dedicated writer, dedicated detection, dedicated README section, dedicated tests.
- Default writes are project-scoped; user-scope writes require an explicit flag per client.
- Existing client config files are merged into, never overwritten.
- The CLI surface is fully flag-driven so CI / scripts can call `init --yes --client claude-code,copilot --print-only` and get deterministic JSON to commit.

**Non-Goals:**

- A TUI framework (Spectre.Console etc.) — keep the prompt layer to plain `Console.ReadLine` + simple printable menus. Adds no dependencies; works headlessly under `--yes`.
- Visual Studio MCP integration writer. Visual Studio's MCP support is in flux; punt to a follow-up change once the schema stabilises.
- JetBrains MCP integration writer. Same reasoning.
- Cross-client schema validation. Each writer emits exactly the schema its target client documents; we don't normalise across them.
- A `sourcegraph-mcp uninstall` / undo command. The merge-by-name semantics make removal trivial via direct edit; an automated remover is over-scope for v1.
- Touching files outside the workspace by default. Even when the user explicitly opts into Claude Desktop with `--claude-desktop`, the writer prints what it's about to write before writing, and `--yes` is required to skip the confirmation.

## Decisions

### Decision 1 — One subcommand per concern (`init`, `doctor`, `demo`)

The temptation is to merge `doctor` and `demo` into `init` as final steps. We resist that for three reasons:

1. **`doctor` runs after the fact too.** A user who set up six months ago and now wonders why `who_authored` is empty wants to type `doctor`, not `init` (which would imply re-wiring).
2. **`demo` is a confidence-moment tool, not an init step.** A CI harness wiring up a server in non-interactive mode shouldn't run an unrequested 4-tool demo; it's noise. `init --yes --client copilot` wires up; `demo` is a separate, opt-in act.
3. **Independent testability.** Three subcommands with independent argument parsers and dependency closures keeps `OnboardingCliTests.cs` clear-headed.

`init` does *suggest* `demo` as the natural next step in its closing report, so users discover the trio organically.

### Decision 2 — Project-scoped writes as default, user-scope as explicit flag

The default file targets per client are:

| Client | Project-scoped (default) | User-scoped (opt-in flag) |
|---|---|---|
| Claude Code | `<root>/.mcp.json` | `--user-claude-code` → `~/.claude/.mcp.json` |
| GitHub Copilot | `<root>/.vscode/mcp.json` | `--user-copilot` → user `settings.json` `chat.mcp.servers` |
| Cursor | `<root>/.cursor/mcp.json` | `--user-cursor` → `~/.cursor/mcp.json` |
| Continue | `<root>/.continue/mcp/sourcegraph.yaml` | `--user-continue` → `~/.continue/mcp/sourcegraph.yaml` |
| Claude Desktop | (no project-scope exists) | `--claude-desktop` (always required) → platform-specific user path |

Claude Desktop is the only client whose checkbox in the interactive picker is *off* by default; selecting it raises a confirmation dialog showing the absolute path that will be written and a summary of the entry being added.

### Decision 3 — Copilot writer schema is distinct from the rest

Copilot in VS Code uses a different shape that the writer emits verbatim:

```json
{
  "servers": {
    "sourcegraph": {
      "type": "stdio",
      "command": "sourcegraph-mcp",
      "args": ["serve", "--solution", "${workspaceFolder}/MySln.slnx"]
    }
  },
  "inputs": []
}
```

Other writers emit:

```json
{
  "mcpServers": {
    "sourcegraph": {
      "command": "sourcegraph-mcp",
      "args": ["serve", "--solution", "${workspaceFolder}/MySln.slnx"]
    }
  }
}
```

Continue uses YAML in `.continue/mcp/sourcegraph.yaml`:

```yaml
name: sourcegraph
command: sourcegraph-mcp
args:
  - serve
  - --solution
  - ${workspaceFolder}/MySln.slnx
```

The writers do *not* try to abstract over these — the `IClientConfigWriter` contract returns a (path, content-bytes) pair and the writers each construct the right shape internally.

### Decision 4 — Merge semantics: read-modify-write, keyed by server name

Every writer follows the same algorithm:

```
if file exists:
    parse it (json/yaml as appropriate)
    if no `sourcegraph` server entry: insert ours, preserve everything else
    if existing `sourcegraph` entry differs from ours and --force not set:
        prompt with diff (in interactive mode) or skip with a warning (in --yes mode)
    if existing `sourcegraph` entry matches ours: no-op, log "already wired"
else:
    write a fresh file containing only our entry
```

Other servers' entries are *never* removed or modified. The `--force` flag bypasses the prompt for our own entry; it does not enable cross-server modification.

In `--yes` mode without `--force`, an existing differing `sourcegraph` entry is left in place and a warning is printed. This is the safe default for CI (which should fail loud rather than silently overwrite a config that someone else committed).

### Decision 5 — Pre-warming the index is opt-in (default on under interactive, off under `--yes`)

After writing configs, `init` *can* invoke `RoslynIndexer.IndexSolutionOnceAsync` against the chosen solution(s) so the first MCP `tools/call` from a connected client returns warm. This costs 10–60s on a real solution.

- Interactive mode: prompts ("Pre-warm the index now? [Y/n]") with default Y.
- `--yes` mode: skips by default; opt in with `--prewarm`. Reasoning: CI harnesses wiring up a server typically don't need warm; users who do can ask for it explicitly.

### Decision 6 — `demo` runs canned, no-arg queries — no `--prompt` form in v1

`demo`'s job is "verify it works." A `--prompt "find me the calculator class"` form that picks a tool heuristically is delightful but introduces a small NLU layer that needs its own correctness story. Defer to a follow-up change.

The v1 demo runs four fixed calls and prints the markdown each one returned, in order:

1. `ping` — proves the server is reachable.
2. `graph_stats` — proves the index is populated.
3. `search_symbols` with a fragment derived from `graph_stats`'s top class. (If `graph_stats` returned zero, demo bails with a "no symbols indexed — run `serve` once first" message.)
4. `find_definition` against the same top class — proves end-to-end identity lookup works.

The output is the *exact* markdown the agent would see (leaf included), wrapped in a thin "Demo: ping / Demo: graph_stats / ..." header per call so a human reading along sees the structure.

### Decision 7 — `doctor` exit codes match `vocabulary list --strict` precedent

- `0` — every check passed.
- `2` — at least one check raised an actionable finding (missing git, unwired client config that exists on disk, etc.). The message names the finding and the suggested fix.
- `1` — a hard environment failure (no .NET SDK, repo not readable, etc.).

This matches the existing `vocabulary list --strict` exit-code convention for "warnings as failures" so CI invocations behave predictably.

### Decision 8 — `init` interactive prompts use plain stdin/stdout, no TUI dependency

Each prompt is a labeled list with numeric or letter selection plus `[default]` indicators. Hitting enter accepts default. This keeps:

- No new NuGet dependency (Spectre.Console etc. are common but their footprint is non-trivial)
- Behaviour identical when stdout is a pipe (CI piping `yes` works)
- No surprising terminal-state mutations on Ctrl+C

`--yes` short-circuits every prompt; combined with the per-client toggles (`--client copilot,claude-code` or `--no-cursor`), every interactive choice has a flag equivalent.

### Decision 9 — `init-scopes` keeps its spec; `init` calls into the same code path

The existing `init-scopes` requirement and command continue to exist verbatim. `init` invokes the same `ScopeBootstrap` logic when it detects multiple `.slnx` / `.sln` files at the root. This keeps backwards compatibility for anyone with `init-scopes` in a setup script and avoids splitting the scope-discovery logic across two CLI surfaces.

## Risks / Trade-offs

- **[Risk] Merge logic on hand-edited configs.** A user might have hand-edited their `.mcp.json` with comments or non-standard indentation; round-tripping through System.Text.Json normalises whitespace and strips comments. → Mitigation: writer detects formatted-with-comments files (presence of `//` outside string literals) and downgrades to `--print-only` mode for that client with a "config has comments — please paste manually" message. Acceptable: the user gets the snippet, no comments are dropped silently.

- **[Risk] Copilot's schema may evolve.** `.vscode/mcp.json` is a relatively new VS Code feature; the `servers`/`type` shape may pick up new fields. → Mitigation: `CopilotWriter` is a thin function that emits exactly what's documented today; future fields land in a follow-up. Doctor's Copilot detector reads the file; it doesn't validate it.

- **[Risk] `demo` failing on freshly-installed installs without an indexed scope.** If the user runs `init` without `--prewarm` and immediately runs `demo`, the scope hasn't been indexed yet. → Mitigation: `demo` detects an empty graph (`graph_stats` returns zero symbols) and prints a "no symbols indexed yet — run `sourcegraph-mcp index <solution>` first, or run `init --prewarm`" message instead of attempting the find_definition call against an empty graph.

- **[Risk] Per-client writer drift over time.** As MCP-aware clients ship updates, their config schemas may diverge further. → Mitigation: each writer is an isolated, named, testable file. Adding/changing one writer is a focused PR. Doctor surfaces "I see your config but it doesn't match what we'd write" as an actionable finding.

- **[Trade-off] No TUI framework, no rich progress bars during pre-warm.** A pre-warm pass takes 10–60s; with no TUI, we print "Pre-warming index..." and then `indexed N files in T s` when done. The `improve-first-run-progress` proposal addresses live indexing progress on the *server* side via MCP `notifications/progress`; the CLI side here keeps it terse. Accepted.

- **[Trade-off] `--print-only` doesn't write `.sourcegraph.json`.** `--print-only` is exclusively about per-client MCP config. The `.sourcegraph.json` file is *our* config; if the user wants a print-only mode for that, they use the existing `init-scopes --dry-run` (which already exists) or piping `cat .sourcegraph.json`. Keeping `--print-only` narrowly defined avoids surprises.

- **[Trade-off] Demo always runs four queries — no `--quick` for one query.** Four queries is fast (<2s typically). A `--quick` form is over-engineered for the value-add. Accepted; revisit if telemetry shows users running demo in tight loops.

## Migration Plan

1. Land `Cli/ClientConfigWriters/IClientConfigWriter.cs` + `ClaudeCodeWriter` + tests against a fake filesystem. Smallest possible vertical slice; CI green.
2. Add `CopilotWriter` + `CursorWriter` + `ContinueWriter` + `ClaudeDesktopWriter` + their tests.
3. Land `Cli/OnboardingDetector.cs` (SDK / git / .slnx / existing-configs detection) + tests.
4. Land `Cli/InitCli.cs` (interactive flow + flag flow) wired to the writers and detector. Includes the `--print-only`, `--yes`, and per-client toggle paths.
5. Land `Cli/DoctorCli.cs` + scenario tests.
6. Land `Cli/DemoCli.cs` against the existing `tests/fixtures/Sample.sln` fixture; covers the four canned calls.
7. README quickstart restructure (separate PR if size warrants — pure docs).
8. CLAUDE.md note + CHANGELOG entry.
9. `openspec validate add-onboarding-cli --strict`; archive.

**Rollback strategy.** Each subcommand and each writer is independently revertable. The CLI's `Program.cs` route table is the only cross-cutting touch; reverting that disables all three subcommands at once if the bundle proves problematic.

## Open Questions

- **Should the interactive `init` autodetect which clients are *installed* on the machine and only show those checkboxes by default?** Pro: less noise. Con: a user setting up to share a config with collaborators may want to write Copilot config even if they don't use Copilot themselves. → Tentative answer: autodetect *and show all*, but pre-tick the autodetected ones and leave the rest unticked. User can untick or tick freely.
- **Should `demo` accept a `--scope <id>` flag to pick a specific scope?** Yes, low cost — wired through to the existing `ScopeRouter`.
- **Should `init`'s pre-warm step also pre-download the embedding model?** No — the embedding model is ~480 MB and explicit `--no-embeddings` is already a recommended path. Pre-warm covers Roslyn indexing only; semantic search comes warm on first call, with progress visibility added by the sibling `improve-first-run-progress` proposal.
- **Should the README quickstart be its own change?** Probably not — it's tightly coupled to the new commands' existence. Ship together.
- **What about `dotnet tool restore`-style integration where `init` writes a `.config/dotnet-tools.json` if `--install-mode local-tool` is selected?** Yes, this is part of the `--install-mode` flag's job. The writer emits / merges into `.config/dotnet-tools.json` and adjusts the resulting `.mcp.json`/`.vscode/mcp.json` to invoke `dotnet sourcegraph-mcp` rather than `sourcegraph-mcp`.
