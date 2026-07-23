# 🌿 DevBitsLab.Mcp.SourceGraph

Live code source graph MCP server. Indexes C# via Roslyn, XAML via a custom
profile-aware parser, and TypeScript / JavaScript / TSX / JSX via tree-sitter
into SQLite + FTS5 and exposes graph queries to MCP clients (Claude Code,
Cursor) over stdio.

## Onboarding CLI: `init`, `doctor`, `demo`

Three subcommands handle first-run setup. `sourcegraph-mcp init` is interactive
by default; flag-driven (`--yes`) for CI. It detects environment, picks MCP
clients (project-scope by default, user-scope opt-in via `--user-<client>`),
and writes per-client config files with merge-by-name semantics — first-class
support for Claude Code, **GitHub Copilot** (distinct `servers`/`type` schema in
`.vscode/mcp.json`), Cursor, Continue, and Claude Desktop. `doctor` runs a
read-only environment diagnostic with `pass | warn | fail` exit-code semantics.
`demo` runs four canned operations (`ping`, `graph_stats`, `search_symbols`,
`find_definition`) against the active scope and prints leaf-stamped markdown —
the same shape an agent sees, available without an agent loop.

## Tool-usage guidance ships with the server

When this MCP server connects, it publishes "prefer source-graph tools over
`Grep` + `Read` for symbol-level questions" plus a closing "verify with
`usage_stats`" directive in the `initialize` response. Each tool's description
also carries a `Use when:` line documenting the question shape it answers.
Suppress with `--no-instructions` or `SOURCEGRAPH_NO_INSTRUCTIONS=1` if you
prefer to drive guidance from your own `CLAUDE.md`.

Every built-in tool's response begins with a green-leaf glyph `🌿` — that's
the at-a-glance signal that the answer came from this server (and not from
`Grep` + `Read` or another MCP server). The same `🌿` also rides on each
built-in tool's catalog identity in `tools/list`: `Tool.Title` is set to
`🌿 <name>` and `Tool.Description` is `🌿 `-prefixed, so the brand surfaces
in client UIs that render tool selectors and hover cards rather than the
prose response. Suppress all three (per-call response, `ServerInstructions`
head, and per-tool `Title`/`Description`) with `--no-leaf` or
`SOURCEGRAPH_NO_LEAF=1` if your terminal doesn't render emoji well or you
prefer unbranded output.

Built-in `find_*` / `list_*` / `search_*` tools ship typed `structuredContent`
(snake-case fields, `outputSchema` declared on `tools/list`) alongside the
prose, so agents chaining tool calls can consume `result.structuredContent`
directly without re-parsing markdown — see README's "Structured output and
resource links" for the shape and a worked example.

A persistent JSONL log of every tool call lives at
`<solution>/.sourcegraph/usage.jsonl` for offline analysis. `query_graph` calls log
the SQL text; the log is the evidence base for which ad-hoc queries deserve to be
promoted into curated tools.

A second JSONL log at `<solution>/.sourcegraph/heals.jsonl` records internal
state changes — boot reconciliation today (orphan DBs archived, missing DBs
detected, stuck `indexing` rows demoted), repair-tool invocations and
corruption-recovery actions in later phases. Same shape as `usage.jsonl`
(`{ts, kind, scope, ok, ms, details}`) but in a separate file so the two
streams are independently scannable. The matching `sourcegraph.heal.fired`
Counter on the existing OpenTelemetry meter exposes the same events for live
scraping.

If a scope looks stale or returns empty results, call `verify_scope` first —
it's a read-only health snapshot (schema version, row counts, integrity check,
20-file drift sample) that doesn't mutate any state. Then dispatch on what it
reports:

- `drift_sample.changed` is high → call `reconcile_drift` (walks the source
  tree, applies the symmetric difference). `dry_run = true` previews.
- `integrity_check` is non-`"ok"` → call `repair_scope mode="rebuild"`
  (archives the current DB and cold-indexes from sources).
- Status is `"degraded"` from a transient cause (workspace race, stuck
  `indexing` row from a prior crash) → call `repair_scope mode="minimal"`
  first; it runs integrity check + prune + retry-wrap a workspace reload.

Cold-index transient failures get a free 3-attempt bounded retry under
`[1s, 5s, 25s]` backoff before the scope lands in `degraded` — don't intervene
in the first ~30 seconds of startup unless the user is actively waiting.

When a tool call fails with a SQLite corruption error, the dispatch layer
auto-runs `integrity_check`: a clean check leaves the scope alone (false
alarm — try again); a confirmed failure marks the scope `degraded` with
`status_message` hinting `repair_scope mode=rebuild`. The next call against
that scope returns the degraded short-circuit (no SQLite contact). The
autonomous-rebuild env var (`SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS`) is opt-in;
on production it's off, so the agent decides when to escalate to rebuild.

For ad-hoc questions that don't fit a curated tool — aggregations, joins, "how many
public types use X", "which classes implement IDisposable but lack `Dispose`",
"which `[Obsolete]` types have outstanding CS-warnings", "which methods authored
> 6 months ago grew beyond 100 lines" — the server exposes a stable view layer
(`v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`, `v_annotations`,
`v_diagnostics`, `v_history`) plus two tools: `describe_schema` (returns the views
with column types and descriptions, plus the live `symbol_kinds`/`edge_kinds`
vocabularies) and `query_graph` (read-only single SQL statement, named `@param`
bindings, scope-aware, 5 s timeout / 5000-row cap configurable via
`--query-timeout-seconds` / `--query-row-limit` or the matching `SOURCEGRAPH_QUERY_*`
env vars). Cross-view JOINs use the composite `(scope, id)` tuple. The view layer
is versioned (`view_schema_version`, currently `2`); it bumps on any view-set
change so cache-aware clients re-introspect after a server upgrade.

`semantic_search`, `impact_of_change`, `module_summary`, and `find_definition`
emit MCP `notifications/progress` when the originating `tools/call` request
includes a `progressToken` — useful for live status indicators on the slow
paths (cold-start ONNX model load, deep recursive CTE walks). When any of
these tools is called against a scope whose initial indexing is still in
flight, the server forwards per-scope cold-start phase progress (`opening
workspace` → `indexing` → `ready`) for the duration of the wait, so first-
call latency narrates itself instead of presenting as a silent spinner.
Clients that don't opt in see today's silent-then-result behaviour.

The embedding model cache is inspectable from both surfaces: the CLI verbs
`sourcegraph-mcp embeddings status / pull / remove / verify` (operator-facing,
human-readable) and the matching MCP tools `embeddings_status` / `embeddings_pull`
/ `embeddings_remove` / `embeddings_verify` (agent-facing, structured output).
Mutating tools carry MCP `destructiveHint` annotations so spec-aware hosts can
require explicit confirmation before invocation.

## Scopes (multi-solution monorepos)

A `.sourcegraph.json` at the repo root opts a project into multi-scope mode:

```json
{
  "scopes": [
    { "name": "frontend", "solutions": ["src/frontend.slnx"] },
    { "name": "backend",  "solutions": ["src/backend.slnx"], "exclude": ["**/Generated/**"] },
    { "name": "vendor",   "paths": ["third_party/**/*.csproj"], "isolated": true }
  ],
  "default_scope": "backend"
}
```

Each scope owns its own SQLite DB at `.sourcegraph/scopes/<id>.db`; a separate
`_meta.db` registry tracks status (`ok | partial | degraded | indexing`) and
last-indexed time. `partial` means one or more projects/files failed to index
but at least one project produced symbols; `list_scopes` carries
`failed_projects` / `failed_files` arrays so operators see which projects'
symbols are missing without scraping logs. An `isolated` scope is excluded from
`scope = "*"` fan-out — useful for vendored / generated code that shouldn't
pollute `find_references` on production.

Every existing tool gains an optional `scope` parameter (string id, comma-separated
list, or `"*"`). Call `list_scopes` to discover the configured scopes. Without a
`.sourcegraph.json` and without `--solution`, a synthesised `default` scope keeps
single-solution users working unchanged.

Two optional fields beyond the project-set declaration:

- `language` (kebab-case string) — primary language for glob-based scopes;
  hint to indexer dispatch when an extension could plausibly be claimed by
  multiple plugins. Soft-registry: any kebab-case value loads, unknown
  values mis-route silently.
- `enrichment` (object) — forward-declared block carrying `lsp: { command, args }`.
  Surfaced via `scopes info` but no first-party plugin consumes it at this
  version; the first runtime use lands with the TypeScript indexer.

CLI helpers:

- `sourcegraph-mcp init-scopes` — scaffold a `.sourcegraph.json` from the .slnx
  files at the repo root.
- `sourcegraph-mcp scopes list` / `info <name> [--json]` / `add <name> --solution <path>` / `remove <name>`.

A running server picks up `.sourcegraph.json` edits live — no restart required.
The four delta kinds are: **add scope** (new per-scope DB + cold index), **remove
scope** (registry row deleted, on-disk DB preserved as a re-add cache), **modify
scope** (atomic-swap of the host with a 5-second grace window for in-flight
queries against the old host), and **change `default_scope`** (router metadata
flip, no scope is reindexed). A malformed save is tolerated: the watcher logs
at info level and leaves the running scope set untouched until the next valid
save. **Plugin changes (`plugins[]`) still require a restart** — hot-loading
`AssemblyLoadContext`-isolated plugins is out of scope; a save that touches
`plugins[]` logs a warning and otherwise applies any concurrent scope deltas.

The legacy single-DB layout (`.sourcegraph/graph.db`) is migrated automatically
on first start of the new server — the file is renamed into `scopes/default.db`
and the synthesised default scope picks it up.

## Project-scoped MCP config

`.mcp.json` at the repo root registers the `sourcegraph` server automatically when
Claude Code (or any client that honours the convention) opens the project.

The committed `.mcp.json` runs the **in-repo source** via `dotnet run`, so a
fresh clone doesn't need a global `dotnet tool install`. The only prerequisite
is that the project has been built once:

```bash
git clone https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph
cd DevBitsLab.Mcp.SourceGraph
dotnet build              # one-time; rebuild after pulls
```

Then opening the directory in Claude Code surfaces the `sourcegraph` server,
which Claude Code launches via `dotnet run --no-build --project src/.../Server`.
After every code change, `dotnet build` again so the next launch picks it up.

The `${workspaceFolder}` token is expanded by Claude Code; if your client doesn't
expand it, the server expands `${workspaceFolder}` itself by reading
`WORKSPACE_FOLDER` / `CLAUDE_PROJECT_DIR` / `MCP_WORKSPACE_FOLDER` env vars.

### Alternative: global `dotnet tool` install

If you prefer the published tool over the in-repo source (smaller startup
overhead, no `dotnet build` step), swap the `.mcp.json` body to:

```json
{
  "mcpServers": {
    "sourcegraph": {
      "command": "sourcegraph-mcp",
      "args": ["serve", "--solution", "${workspaceFolder}/MySolution.slnx"]
    }
  }
}
```

and `dotnet tool install -g DevBitsLab.Mcp.SourceGraph.Tool` (with
`~/.dotnet/tools` on `PATH`).

### Using `sourcegraph-mcp` in a different repo

Three patterns:

1. **Global tool** — `dotnet tool install -g DevBitsLab.Mcp.SourceGraph.Tool`,
   then `.mcp.json` invokes `sourcegraph-mcp serve --solution ...`.
2. **Git submodule** — `git submodule add <this-repo-url> tools/sourcegraph-mcp`.
   Their `.mcp.json` invokes
   `dotnet run --project ${workspaceFolder}/tools/sourcegraph-mcp/src/.../Server --no-build -- serve --solution ${workspaceFolder}/Their.slnx`.
3. **Local tool manifest** — `dotnet new tool-manifest && dotnet tool install
   DevBitsLab.Mcp.SourceGraph.Tool`. Commits `.config/dotnet-tools.json`. Each
   collaborator runs `dotnet tool restore` once.

Any `${X}` placeholder in `--solution` / `--db` values that isn't expanded by
the client is also resolved by the server against the process env, so paths like
`${HOME}/repos/my.slnx` work too.

## Project layout

- `src/DevBitsLab.Mcp.SourceGraph.Core/` — domain types (Scope, ScopeProjectSet, ScopeIdValidator, ScopeEnrichmentConfig, LspEnrichmentConfig)
- `src/DevBitsLab.Mcp.SourceGraph.Storage/` — SQLite graph store + FTS5; per-scope `IGraphStore` factory + `IScopeRegistry`; `ScopeConfigLoader`
- `src/DevBitsLab.Mcp.SourceGraph.Indexing/` — Roslyn workspace + indexer
- `src/DevBitsLab.Mcp.SourceGraph.Indexing.Xaml/` — XAML profile-aware parser indexer (WPF/WinUI/UWP/Avalonia/Uno)
- `src/DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter/` — generic tree-sitter host (`TreeSitterLanguageIndexer<TGrammarConfig>` abstract base, `TreeSitterAdapter`); transitively brings `TreeSitter.DotNet` + bundled grammars
- `src/DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript/` — TS/JS/JSX/TSX indexer subclass of the tree-sitter host
- `src/DevBitsLab.Mcp.SourceGraph.Watcher/` — file + git HEAD watcher
- `src/DevBitsLab.Mcp.SourceGraph.Server/` — stdio MCP host + CLI; `Scoping/` (router + per-scope hosts)
- `tests/fixtures/Sample.sln` — single-scope fixture for smoke tests
- `tests/fixtures/MultiScope/` — multi-scope fixture (frontend.sln + backend.sln + .sourcegraph.json)
