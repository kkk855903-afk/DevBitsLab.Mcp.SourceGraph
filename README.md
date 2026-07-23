# 🌿 DevBitsLab.Mcp.SourceGraph

[![CI](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/actions/workflows/ci.yml/badge.svg)](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/actions/workflows/codeql.yml/badge.svg)](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/actions/workflows/codeql.yml)
[![Release](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/actions/workflows/publish-nuget.yml/badge.svg?event=push)](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/releases)
[![NuGet](https://img.shields.io/nuget/v/DevBitsLab.Mcp.SourceGraph.Tool.svg)](https://www.nuget.org/packages/DevBitsLab.Mcp.SourceGraph.Tool/)

A live code source graph [Model Context Protocol](https://modelcontextprotocol.io)
server for .NET solutions. It indexes your C# code with Roslyn into a SQLite +
FTS5 database, exposes structured graph queries to MCP-aware clients (Claude
Code, Cursor, Continue, Claude Desktop, …) over stdio, and keeps the index
fresh as files change on disk.

The goal is to let coding agents replace dozens of ad-hoc `Grep` + `Read`
calls with a single structured tool call:

> *"Where is `OrderService.PublishAsync` defined?"*
> *"Who calls it transitively, and what would change if I rename it?"*
> *"Find every controller action attributed `[HttpPost]` whose route contains `/v2/`."*
> *"Which tests cover this method, and who authored it last?"*

> **Note on the 🌿 icon:** This server uses a green-leaf emoji (`🌿`) as a brand
> mark to indicate that responses come from the source graph tools. You'll see it
> in tool responses, tool titles, and descriptions. Some AI agents may strip or
> filter emoji from their output, so the icon might not always be visible in your
> chat interface. You can disable it entirely with `--no-leaf` if preferred.

## Contents

- [Features](#features)
- [Why not just use Roslyn directly?](#why-not-just-use-roslyn-directly)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quickstart (60 seconds)](#quickstart-60-seconds)
- [Wiring it into an MCP client](#wiring-it-into-an-mcp-client)
- [MCP tools](#mcp-tools)
- [Structured output and resource links](#structured-output-and-resource-links)
- [Resource templates](#resource-templates)
- [Scopes (multi-solution monorepos)](#scopes-multi-solution-monorepos)
- [Command-line interface](#command-line-interface)
- [How the index stays live](#how-the-index-stays-live)
- [Observability](#observability)
- [Resource limits and tunables](#resource-limits-and-tunables)
- [Platform support](#platform-support)
- [Building from source](#building-from-source)
- [Contributing & security](#contributing--security)
- [License](#license)

## Features

- **Roslyn-backed C# indexing.** Symbols, references, call/uses-type/overrides/
  implements/instantiates/throws edges, XML doc summaries, accessibility, and
  modifiers — all queryable in one round-trip.
- **Cross-language XAML indexing.** Built-in `.xaml` indexer covering WPF /
  WinUI 3 / UWP / Avalonia / Uno from a single per-file profile detection
  step. Cross-language joins (`code-behind`, `handles-event`,
  `instantiates-type`) point at C# canonical keys via string equality, so
  `find_references` on the C# class returns the XAML view that binds it.
  Five XAML symbol kinds, eight cross-language edge kinds, plus
  `xaml-attached-property` annotations for `Grid.Row` / `DockPanel.Dock` /
  etc.
- **TypeScript / JavaScript / TSX / JSX indexing.** Built-in tree-sitter-backed
  indexer for `.ts` / `.tsx` / `.js` / `.jsx`, covering function / class /
  interface / type-alias / enum / namespace / const / let declarations, call-site
  references, and JSX `instantiates` edges (with prop-list payload) for
  PascalCase components. Default excludes (`node_modules`, `dist`, `.next`,
  `build`, `coverage`, `.cache`, `.parcel-cache`, `out`) keep a fresh
  install from indexing dependency trees. Cross-file ref resolution is
  intra-file at this version; tsconfig `paths`-aware module resolution +
  optional `typescript-language-server` enrichment ship as follow-ups.
- **FTS5 name search.** Trigram fragment matching for cases where you only
  remember "`…Greet…Async`".
- **Optional code-aware semantic search.** ONNX embeddings (default model:
  `jinaai/jina-embeddings-v2-base-code`) stored in `sqlite-vec` for
  natural-language queries like *"find the rate-limiting code"*. The model
  (~640 MB) is loaded from a per-user cache
  directory resolved by `ModelStore.DefaultCacheDir()` (honours `XDG_CACHE_HOME` /
  `LOCALAPPDATA` / `~/.cache` per platform — e.g. `~/.cache/devbitslab.sourcegraph/models/`
  on Linux/macOS, `%LOCALAPPDATA%\devbitslab.sourcegraph\models\` on Windows).
  `serve` and `index` make no model-download network request by default. Populate the
  cache explicitly with `embeddings pull`, or opt into first-run fetching with
  `--allow-model-download`. Disable the pipeline entirely with `--no-embeddings`;
  `--no-model-download` remains as an explicit/legacy fail-closed switch.
- **Attribute search.** Find every symbol carrying a given attribute, optionally
  filtered by serialised argument substring.
- **Roslyn diagnostics indexing.** Query analyzer warnings/errors captured at
  index time, by severity, code, or symbol.
- **Source-generator awareness.** Symbols emitted by incremental generators
  (regex, MVVM Toolkit, ASP.NET routing, JSON source-gen, …) are tracked and
  filterable.
- **Test discovery & git history.** `Tests` edges from xUnit/NUnit/MSTest tests
  to the production members they exercise; cached per-symbol git-blame summary
  (last commit, author, time).
- **Multi-solution monorepo scopes.** One database per scope, isolation flag
  for vendored/generated code, fan-out queries with `scope = "*"`.
- **Live updates.** File watcher + `.git/HEAD` watcher (worktree-aware), 200 ms
  debounce, batched re-index. Symbol ids stay stable across edits so existing
  references remain valid.
- **Stable plugin SDK.** `DevBitsLab.Mcp.SourceGraph.Sdk` exposes
  `IMcpToolPlugin` for adding bespoke tools that share the same scope router.

## Why not just use Roslyn directly?

Roslyn is the right tool when you're writing an analyzer, a refactor, or
anything that needs full type-system access live inside the compiler. This
server is the right tool when an LLM (or any out-of-process client) needs many
cheap structural queries against a stable solution.

| Dimension | Roslyn directly (`MSBuildWorkspace` / `SymbolFinder`) | This server |
|---|---|---|
| **Where it runs** | In-process API — every client hosts its own workspace | Cross-process MCP server — one host, many clients (Claude Code, Cursor, scripts) |
| **Initial indexing** | `MSBuildWorkspace` load (10–60 s on a real solution), paid in every consumer process | Scope open + full indexing on host start (and after `clear` or workspace reloads); tool calls await `ScopeHost.Ready` until the pass completes. Borne once by the host, shared across every connected client. |
| **Steady-state query** | Fast in-memory queries against the loaded workspace | Milliseconds — SQLite query against the warm DB; incremental re-indexing handled by the watcher (see *Freshness* below) |
| **Search shape** | Exact-identity lookups (`SymbolFinder.FindReferencesAsync`) | Same exact lookups *plus* FTS5 fragment search and ONNX semantic search |
| **Languages** | C# / VB only | C# + XAML + TypeScript / JavaScript / TSX / JSX today, with cross-language joins; plugin SDK for more |
| **Multi-solution** | One workspace per solution | Native scope router with isolation flags for vendored / generated code |
| **Freshness** | Caller's problem | File watcher + `.git/HEAD` watcher with 200 ms debounce |
| **Semantic accuracy** | 100% live | Snapshot-accurate, refreshed on file changes |
| **Type system access** | Full (overload resolution, conversions, generic substitution) | Not exposed — graph queries only |

In one line: Roslyn is a compiler API; this is a query layer tuned for agents
that ask *"where does this go?"* forty times an hour.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json`).
- A `.sln` or `.slnx` solution file for the codebase you want to index.
- An MCP-aware client (Claude Code, Cursor, Continue, Claude Desktop, …).

## Installation

Install the published .NET tool globally:

```bash
dotnet tool install -g DevBitsLab.Mcp.SourceGraph.Tool
```

Make sure `~/.dotnet/tools` is on your `PATH`. The installed command is
`sourcegraph-mcp`. You can also pin a version per repository — see
[Pin a version per repo](#pin-a-version-per-repo) below.

## Quickstart (60 seconds)

From a fresh clone of any .NET solution:

```bash
dotnet tool install -g DevBitsLab.Mcp.SourceGraph.Tool
sourcegraph-mcp init        # interactive: detects clients, writes .mcp.json / .vscode/mcp.json / etc.
sourcegraph-mcp demo        # canned probe: ping → graph_stats → search_symbols → find_definition
```

`init` writes only **project-scoped** files by default (`.mcp.json`,
`.vscode/mcp.json`, `.cursor/mcp.json`, `.continue/mcp/sourcegraph.yaml`).
User-scope writes (or Claude Desktop) require explicit per-client flags.
`demo` reads the indexed scope and prints the same leaf-stamped markdown
your agent will see — instant verification.

Other useful first-run commands:

```bash
sourcegraph-mcp init --yes --client copilot,claude-code --print-only   # CI-friendly preview
sourcegraph-mcp doctor                                                  # environment diagnostic
sourcegraph-mcp init --prewarm                                          # also pre-build the index
```

## Wiring it into an MCP client

`sourcegraph-mcp init` writes the right config for each client below
automatically. The snippets here document what each writer produces — useful
if you'd rather paste manually, or if you want to understand the schema delta
between clients.

### Claude Code (project-scoped, committed to the repo)

Drop a `.mcp.json` at the repository root:

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

Open the directory in Claude Code and approve the server when prompted. Claude
Code expands `${workspaceFolder}` automatically; if your client doesn't, the
server falls back to the `WORKSPACE_FOLDER`, `CLAUDE_PROJECT_DIR`, or
`MCP_WORKSPACE_FOLDER` environment variable. Any other `${VAR}` token is
expanded against the process environment, so paths like
`${HOME}/repos/my.slnx` work too.

### GitHub Copilot (`.vscode/mcp.json`)

Copilot's VS Code MCP integration uses a **distinct schema** from Claude Code's
— top-level key is `servers` (not `mcpServers`), and each server entry carries
an explicit `type: "stdio"` field:

```json
{
  "servers": {
    "sourcegraph": {
      "type": "stdio",
      "command": "sourcegraph-mcp",
      "args": ["serve", "--solution", "${workspaceFolder}/MySolution.slnx"]
    }
  }
}
```

Place this at `.vscode/mcp.json` at the repo root. Pasting the Claude Code
snippet here would not work — Copilot silently ignores files that don't match
its schema.

### Cursor

Cursor uses Claude Code's `mcpServers` shape. Place at `.cursor/mcp.json`
(project-scope) or `~/.cursor/mcp.json` (user-scope):

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

### Continue

Continue uses YAML, one server per file. Place at
`.continue/mcp/sourcegraph.yaml`:

```yaml
name: sourcegraph
command: sourcegraph-mcp
args:
  - serve
  - --solution
  - ${workspaceFolder}/MySolution.slnx
```

### Claude Desktop

Claude Desktop has no project-scoped config — all MCP servers live in the
platform-specific user config:

| OS | Path |
|---|---|
| macOS | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Windows | `%APPDATA%\Claude\claude_desktop_config.json` |
| Linux | `~/.config/Claude/claude_desktop_config.json` |

The shape matches Claude Code:

```json
{
  "mcpServers": {
    "sourcegraph": {
      "command": "sourcegraph-mcp",
      "args": ["serve", "--solution", "/abs/path/to/MySolution.slnx"]
    }
  }
}
```

Note: `${workspaceFolder}` doesn't apply at the user-scope; use absolute paths
or set `MCP_WORKSPACE_FOLDER` in your shell init. `init --claude-desktop`
generates the correct file for you.

### Pin a version per repo

```bash
dotnet new tool-manifest
dotnet tool install DevBitsLab.Mcp.SourceGraph.Tool
git add .config/dotnet-tools.json
```

Collaborators run `dotnet tool restore` once. Pass `--install-mode local-tool`
to `init` to have the writers emit `command: "dotnet"` + `args: ["sourcegraph-mcp", ...]`
instead of the global-install shape.

### Multi-scope monorepo

Run `serve` without `--solution` from the repo root and let it discover
`.sourcegraph.json`:

```json
{
  "mcpServers": {
    "sourcegraph": {
      "command": "sourcegraph-mcp",
      "args": ["serve", "--root", "${workspaceFolder}"]
    }
  }
}
```

See [Scopes](#scopes-multi-solution-monorepos) below for the configuration
format.

## MCP tools

Every tool accepts an optional `scope` parameter (a scope id, a comma-separated
list, or `"*"` for fan-out). Detailed parameter docs are emitted by the server
to the client at handshake time.

### Discovery & navigation

| Tool | Question it answers |
|---|---|
| `find_definition` | Where is X defined? |
| `find_references` | Who uses or calls X? (file:line list, optionally including source-generated files) |
| `list_callers` | Inbound edges into X — default `kind=calls`; also `uses_type`, `overrides`, `implements_member`, `instantiates`, `throws`, `all`. When an edge carries per-edge metadata (e.g. a future XAML `binds-path` edge with `path`, `mode`, `converter` fields), the markdown shows an indented `payload: { … }` sub-line under the row, capped at 5 keys with `(N more)` if elided. |
| `list_callees` | Outbound edges from X (same `kind` taxonomy; same `payload:` sub-line behaviour as `list_callers`). |
| `list_symbols_in_file` | What's in this file? (kind, accessibility, modifiers, XML summary) |
| `list_members` | Direct members of a class / struct / interface / namespace by FQN, optionally filtered by accessibility |
| `find_implementations` | Concrete members satisfying an interface member |
| `neighborhood` | Inbound + outbound edges around X for one `kind` layer at a time (default `calls`; pass `kind=uses_type`, `overrides`, `implements_member`, `instantiates`, `throws`, or `all` to inspect other layers) |
| `module_summary` | Top symbols in a namespace or directory by inbound call count |
| `impact_of_change` | Transitive upstream callers of X up to `maxDepth` |
| `find_data_bindings` | Walks `binds-path` edges with payload-aware filters (`path`, `mode`, `converter`, plus optional `target` / `source` canonical keys). Answers "where does this property bind?", "find every TwoWay binding", "which views use this converter?". Soft-empty `note:` when the active scope hasn't loaded an indexer that emits `binds-path`. |
| `find_event_handlers` | Walks `handles-event` edges with `event` / `command` payload filters and optional `handler` / `element` canonical keys. Answers "find all Click handlers", "where is OnSave wired up?". Same soft-empty pattern as `find_data_bindings`. |

### Search

| Tool | Question it answers |
|---|---|
| `search_symbols` | I only have a fragment of the name (FTS5 trigram match on name / FQN / signature) |
| `semantic_search` | Natural-language intent search over code embeddings (returns a top-k list with similarity scores) |
| `find_by_attribute` | Every symbol carrying an attribute (`HttpGet`, `Obsolete`, `Authorize`, …), optionally filtered by an `argValue` substring against serialised arguments |

### Diagnostics, generation, tests, history

| Tool | Question it answers |
|---|---|
| `find_diagnostics` | Roslyn analyzer/compiler diagnostics captured during indexing — filter by severity, code (e.g. `CS0618`), or symbol |
| `list_generated_files` | Every source-generated file the index tracks, with the count of symbols emitted from each |
| `list_tests_for` | Test methods exercising a production symbol (xUnit/NUnit/MSTest), with framework + class |
| `who_authored` | Cached git-blame summary for a symbol: last commit sha, author, ISO-8601 time, lines blamed |
| `recent_changes` | Symbols whose last authored time falls within the last N days, optionally filtered by author substring |

### Operations

| Tool | Purpose |
|---|---|
| `list_scopes` | Enumerate registered scopes (id, name, root, project count, last-indexed time, status, isolation flag) |
| `verify_scope` | Read-only per-scope health snapshot: schema version, status + message, row counts, `PRAGMA integrity_check` result, and a 20-file drift sample (count of files whose on-disk SHA-256 differs from the DB). Call before invoking repair tools. |
| `repair_scope` | Recover one named scope. `mode = "minimal"` runs integrity check (refusing on corruption), prunes orphan embeddings, and retry-wraps a workspace reload. `mode = "rebuild"` archives the current DB to `orphans/<id>-rebuild-<ts>.db` and cold-indexes from sources. Single-scope only. |
| `reconcile_drift` | Walk a scope's source tree, compare each file's on-disk SHA-256 to the DB, and apply the symmetric difference (reindex changed + index added + remove vanished). Use when results look stale or after a long offline period. `dry_run` previews without applying. |
| `graph_stats` | Counts of files / symbols / references / edges — confirm the index is populated |
| `usage_stats` | Per-tool call count, error count, latency, average response size, last-called time for the current process |
| `embeddings_status` | Inspect the embedding model cache (read-only): cache directory, model id + dimension, per-file presence/size/SHA, free disk |
| `embeddings_pull` | Synchronously download the embedding model manifest into the cache (idempotent; mutating tool — host should confirm) |
| `embeddings_remove` | Delete the cache directory for the active model (default), one specific model, or every cached model with `all=true` (**destructive** — host should confirm) |
| `embeddings_verify` | Recompute SHAs of every cached file and compare against the manifest. Default model has pinned SHAs (mismatch → `isError=true`); override `--model` paths report `match=null` (informational only) |
| `ping` | Health check — returns `pong @ <UTC ISO-8601>` |

### Ad-hoc queries (escape hatch)

When no curated tool fits the question — aggregations, joins, "how many public types use X", "which classes implement IDisposable but lack `Dispose`", "which types have > 50 methods", "which `[Obsolete]` types have outstanding CS-warnings" — the server exposes a stable view layer over the SQLite tables and a tool to run read-only SQL against it.

| Tool | Purpose |
|---|---|
| `describe_schema` | Returns the queryable view layer (`v_symbols`, `v_files`, `v_edges`, `v_references`, `v_scopes`, `v_annotations`, `v_diagnostics`, `v_history`) with each column's type and description, plus the live `symbol_kinds` and `edge_kinds` vocabularies present in the resolved scope set. Call this first when composing `query_graph` SQL. |
| `query_graph` | Runs a single read-only `SELECT` or `WITH` statement against the views. Named parameter binding via `@name` placeholders. Read-only at the SQLite connection level, single-statement enforced at prepare, 5-second statement timeout (configurable), 5000-row cap (configurable). Returns tabular `{columns, rows}` structured content plus a markdown table. Logged into `.sourcegraph/usage.jsonl` with the SQL text — the call log is the evidence base for which queries deserve to be promoted into curated tools. |

The view layer is versioned (`view_schema_version`, currently `2`); the underlying tables remain implementation details and may evolve without bumping it. The version bumps on **any** view-set change — addition, removal, column rename, or column-type change — so cache-aware clients always re-introspect after a server upgrade.

The eight views cover: code structure (`v_symbols`/`v_files`/`v_edges`/`v_references`), scope metadata (`v_scopes`), attribute / decorator metadata (`v_annotations`), Roslyn diagnostics (`v_diagnostics`), and per-symbol git history (`v_history`). Cross-view JOINs use the composite `(scope, id)` tuple — see `describe_schema`'s response for the per-column documentation.

### Example tool calls

```jsonc
// Where is OrderService.PublishAsync defined?
{ "tool": "find_definition", "args": { "symbol": "OrderService.PublishAsync" } }

// Who would I break if I changed it?
{ "tool": "impact_of_change",
  "args": { "symbol": "OrderService.PublishAsync", "maxDepth": 4 } }

// Every POST controller action whose route contains "/v2/"
{ "tool": "find_by_attribute",
  "args": { "name": "HttpPost", "argValue": "/v2/" } }

// "Find the retry/back-off code"
{ "tool": "semantic_search",
  "args": { "query": "exponential backoff retry policy", "k": 10 } }

// Compiler/analyzer warnings on a specific symbol
{ "tool": "find_diagnostics",
  "args": { "severity": "warning", "symbol": "Legacy.Helpers.OldShim" } }

// What tests cover this before I refactor it?
{ "tool": "list_tests_for", "args": { "symbol": "OrderService.PublishAsync" } }

// Who last touched it, and when?
{ "tool": "who_authored", "args": { "symbol": "OrderService.PublishAsync" } }

// Fan a query out across every non-isolated scope in a monorepo
{ "tool": "find_references",
  "args": { "symbol": "ILogger.LogError", "scope": "*" } }

// Cross-language XAML join — find me the codebehind for this view.
// Returns the C# `csharp:T:SampleWpf.Views.MainWindow` partial-class symbol
// because the XAML indexer wired `<Window x:Class="SampleWpf.Views.MainWindow">`
// to a `code-behind` edge.
{ "tool": "list_callees",
  "args": { "symbol": "Views/MainWindow.xaml", "kind": "code-behind" } }

// Cross-language reverse lookup: every XAML view that points at this codebehind type.
{ "tool": "list_callers",
  "args": { "symbol": "SampleWpf.Views.MainWindow", "kind": "code-behind" } }

// Every element that bound to a viewmodel property (XAML binds-path edge).
// The `payload` sub-line in the response shows the binding's `path`, `mode`,
// `converter`, and friends (see `harden-sdk-pre-xaml`).
{ "tool": "list_callers",
  "args": { "symbol": "MainViewModel.UserName", "kind": "binds-path" } }

// Specialised payload-aware variant: every TwoWay binding to "User.Name".
// Against the SampleWpf fixture this resolves the
// `<TextBox Text="{Binding User.Name, Mode=TwoWay}" />` line in MainWindow.xaml,
// returning one row whose payload carries `path: "User.Name"` and `mode: "two-way"`.
{ "tool": "find_data_bindings",
  "args": { "path": "User.Name", "mode": "two-way" } }

// Every Click handler in the active XAML scope. Against SampleWpf this returns
// the `SaveButton.Click → SampleWpf.Views.MainWindow.OnSave` wiring.
{ "tool": "find_event_handlers",
  "args": { "event": "Click" } }

// Every element with `Grid.Row` set (XAML attached-property annotation).
{ "tool": "find_by_annotation",
  "args": { "name": "Grid.Row", "flavor": "xaml-attached-property" } }

// Ad-hoc SQL: how many public types use Sample.Domain.Calculator?
// Aggregates v_edges through v_symbols.container_id and filters by accessibility=Public.
// No curated tool answers this shape; query_graph composes it from the view layer.
{ "tool": "query_graph",
  "args": {
    "sql": "SELECT COUNT(DISTINCT t.id) AS public_user_count FROM v_edges e JOIN v_symbols m ON m.id = e.src AND m.scope = e.scope JOIN v_symbols t ON t.id = m.container_id AND t.scope = m.scope WHERE e.dst = (SELECT id FROM v_symbols WHERE fqn = @fqn LIMIT 1) AND e.kind = 'uses-type' AND t.is_public = 1 AND t.is_type = 1",
    "parameters": { "@fqn": "Sample.Domain.Calculator" }
  } }

// Schema discovery — list views, columns, and live kind vocabularies.
{ "tool": "describe_schema", "args": {} }

// Composability across the extended views: every public type decorated with
// [Obsolete] that ALSO has at least one outstanding CS-warning. Joins
// v_annotations + v_diagnostics + v_symbols. No curated tool answers the
// intersection; query_graph composes it from the view layer in one round-trip.
{ "tool": "query_graph",
  "args": {
    "sql": "SELECT DISTINCT s.fqn, COUNT(d.id) AS warnings FROM v_annotations a JOIN v_symbols s ON s.id = a.symbol_id AND s.scope = a.scope JOIN v_diagnostics d ON d.symbol_id = s.id AND d.scope = s.scope WHERE a.name = 'Obsolete' AND s.is_public = 1 AND s.is_type = 1 AND d.severity_name = 'warning' GROUP BY s.fqn ORDER BY warnings DESC"
  } }

// Per-symbol git history composability: methods authored > 6 months ago that
// have grown beyond 100 lines (refactor candidates). Joins v_history + v_symbols.
{ "tool": "query_graph",
  "args": {
    "sql": "SELECT s.fqn, h.last_author, h.line_count, datetime(h.last_authored_at / 1000, 'unixepoch') AS last_touched FROM v_history h JOIN v_symbols s ON s.id = h.symbol_id AND s.scope = h.scope WHERE s.kind = 'method' AND h.line_count > 100 AND h.last_authored_at < (strftime('%s', 'now', '-6 months') * 1000) ORDER BY h.line_count DESC"
  } }
```

## Structured output and resource links

Every tool whose result is naturally typed — the symbol-list, edge,
diagnostics, history, and singleton tools above — ships two parallel views
in each `tools/call` response:

1. **Renderable prose** in `content` — the markdown the human reads in chat. A
   leading text block carries the substantive answer; per-row `resource_link`
   items point at the corresponding graph resources (see the URI table below);
   a trailing `audience: ["assistant"]` text block carries diagnostic metadata
   (resolved scope, query latency, edge-kind defaults, row counts) for the
   model only.
2. **Typed `structuredContent`** — the same data as a JSON object with
   snake-case field names matching the `outputSchema` declared on
   `tools/list`. Agents that want to chain calls or post-process results can
   `JSON.parse(...)` the structured payload directly without re-parsing prose.

The `outputSchema` for each tool is derived from the C# DTO at registration
time, so the wire-level schema stays in lockstep with the implementation.
Older MCP clients that don't recognise `structuredContent` see a complete
prose answer; clients that don't recognise `resource_link` items skip them;
clients that respect `audience` annotations filter the metadata block out
of the user view.

### `graph://` URI scheme

Each `resource_link.uri` follows one of three shapes:

| URI | What it serves |
|---|---|
| `graph://symbol/<id>` | Markdown card for one symbol — signature, summary, location, attributes, top neighbours |
| `graph://file/<url-encoded-path>` | Symbol outline for a file — every class/method/property declared, with line numbers |
| `graph://namespace/<name>` | Namespace summary — top symbols by inbound call count |

A client that supports `resources/read` can dereference any emitted URI for
an expanded card; the server resolves it against the active scope. See
[Resource templates](#resource-templates) for the underlying MCP templates.

### Sample `find_definition` payload

A `find_definition({"symbol": "Calculator"})` call against the Sample fixture
returns:

```jsonc
{
  "content": [
    {
      "type": "text",
      "text": "🌿 6 hits for 'Calculator':\n- **Sample.Domain.Calculator** (public class)\n  - /abs/path/Calculator.cs:12:18\n  - …"
    },
    {
      "type": "resource_link",
      "uri": "graph://symbol/12",
      "name": "Sample.Domain.Calculator",
      "title": "Sample.Domain.Calculator",
      "description": "public class — /abs/path/Calculator.cs:12:18",
      "mimeType": "text/markdown"
    },
    // … one resource_link per hit, in the same order as the prose rows …
    {
      "type": "text",
      "text": "_meta: scope=`default`, latency_ms=12, hits=6_",
      "annotations": { "audience": ["assistant"], "priority": 0.2 }
    }
  ],
  "structuredContent": {
    "hits": [
      {
        "fqn": "Sample.Domain.Calculator",
        "kind": "class",
        "file_path": "/abs/path/Calculator.cs",
        "line": 12,
        "column": 18,
        "signature": "public class Calculator",
        "xml_summary": "Multiply, divide, add — the four basics."
      }
      // … one entry per resource_link, same order …
    ]
  }
}
```

A downstream tool that chains on this result reads
`result.structuredContent.hits[i]` directly — the typed array length always
equals the number of `resource_link` items and the number of prose rows.
Plugin tools that opt into the same shape return a `CallToolResult` from
their handler; the SDK marshals the wire shape identically to the built-ins.

## Resource templates

Hosts that surface MCP resources can dereference these URIs:

| URI template | Returns |
|---|---|
| `graph://symbol/{symbolId}` | Markdown card for one symbol (signature, summary, location, attributes, neighbours) |
| `graph://file/{path}` | Markdown listing of every symbol declared in a file |
| `graph://namespace/{name}` | Markdown summary of a namespace (members, top inbound symbols) |

## Scopes (multi-solution monorepos)

A `.sourcegraph.json` at the repo root opts a project into multi-scope mode:

```json
{
  "scopes": [
    { "name": "frontend", "solutions": ["src/frontend.slnx"] },
    { "name": "backend",  "solutions": ["src/backend.slnx"], "exclude": ["**/Generated/**"] }
  ],
  "default_scope": "backend"
}
```

- Each scope owns its own SQLite database at `.sourcegraph/scopes/<id>.db`.
- A `_meta.db` registry tracks per-scope status
  (`ok | partial | degraded | indexing`) and last-indexed timestamp.
- `isolated: true` excludes a scope from `scope = "*"` fan-out — useful for
  vendored or generated code that shouldn't pollute references on production
  symbols.
- `language` (string, optional, kebab-case) declares the primary language
  for scopes whose project-set is glob-based; hint to indexer dispatch when
  the same file extension could plausibly be claimed by multiple plugins.
  No closed-list enforcement (soft-registry posture).
- `enrichment` (object, optional) is a forward-declared block carrying one
  nested `lsp: { command, args }` field. Loaded and surfaced via
  `scopes info`, but no first-party plugin consumes it at this version —
  the first runtime use lands with the TypeScript indexer.
- Without a `.sourcegraph.json` and without `--solution`, a synthesised
  `default` scope keeps single-solution users working unchanged.
- The legacy single-database layout (`.sourcegraph/graph.db`) is migrated to
  `scopes/default.db` automatically on first start.
- Live indexing currently resolves a Roslyn workspace per scope only for
  `solutions`-based scopes. Scopes declared via `projects` or `paths` are
  accepted by the config loader but are not indexed by the live server yet —
  prefer `solutions` for now.
- A running server picks up `.sourcegraph.json` edits live (no restart). Adds,
  removes, modifications, and `default_scope` changes flow through immediately;
  malformed saves are tolerated and never tear down working scopes. Plugin
  entries (`plugins[]`) still require a restart to apply.

Every tool accepts an optional `scope` parameter — pass an id, a
comma-separated list, or `"*"` to fan out.

### Partial indexing (one bad project doesn't take down the scope)

Real-world solutions often contain at least one quirky project (legacy MSBuild
quirks, missing source generator NuGet, in-progress migration). Rather than
marking the whole scope `degraded` when a single project's compilation fails,
the indexer isolates per-project and per-file failures and surfaces them on
`list_scopes`:

- A project whose `Compilation` cannot be obtained (probe failure) is recorded
  in `failed_projects` with a short reason string. Its documents are excluded
  from every subsequent indexing pass; the rest of the solution indexes
  normally.
- A file whose Pass 1 symbol walk throws (rare — a transient Roslyn state, a
  generator-affected source going wonky) is recorded in `failed_files`. The
  file's prior store state is preserved untouched until the next successful
  walk.

Status semantics:

| Status     | Meaning                                                                                           |
|------------|---------------------------------------------------------------------------------------------------|
| `ok`       | Every project and file indexed cleanly. `failed_projects` / `failed_files` are empty.             |
| `partial`  | At least one project produced symbols and at least one project or file failed. Tools serve best-effort results from healthy projects; consult `list_scopes` for the failure detail. |
| `degraded` | Workspace failed to open, OR every project failed. Tools return `"scope is degraded: <error>"`.   |
| `indexing` | Cold index in progress.                                                                           |

Tool fan-out (`scope = "*"`) targets every non-isolated scope regardless
of status. Healthy and `partial` scopes return query results; `degraded`
scopes contribute a per-scope error block (`scope is degraded: <message>`)
to the merged response so operators see why a scope returned no data
without the call failing as a whole. Querying a `partial` scope by id
returns the indexed symbols (best-effort); use `list_scopes` to see
what's missing.

## Command-line interface

```text
sourcegraph-mcp <subcommand> [options]
```

| Subcommand | Description |
|---|---|
| `serve` | Run the MCP stdio server. With `--solution` registers an implicit `default` scope; otherwise reads `.sourcegraph.json` from `--root` (or CWD). |
| `index <solution>` | Build/refresh the database for a single solution, then exit. Useful in CI. |
| `stats` | Print counts of files / symbols / references / edges in the database. |
| `clear` | Delete all rows from the database (schema preserved). |
| `init [--yes] [--client <id>] [--no-<client>] [--user-<client>] [--claude-desktop] [--print-only] [--force] [--prewarm] [--install-mode <mode>]` | Interactive (default) or flag-driven onboarding flow. Detects environment, picks MCP clients, writes per-client config files (project-scoped by default), and optionally pre-warms the index. First-class clients: `claude-code`, `copilot`, `cursor`, `continue`, `claude-desktop`. Use `--print-only` for a CI-friendly preview that writes nothing. |
| `doctor [--json]` | Read-only environment diagnostic. Reports SDK / git / solution / config / per-client status. Exit `0` = all-pass; `2` = at least one warning; `1` = hard failure. `--json` emits a machine-readable `{checks, exit_code}` document. |
| `demo [--scope <id>] [--no-color]` | Run four canned operations (`ping`, `graph_stats`, `search_symbols`, `find_definition`) against the active scope and print leaf-stamped markdown — the same shape an MCP client would see. Provides the "ah, it works" confidence moment without an agent loop. Exits `2` if the scope has zero symbols indexed. |
| `init-scopes` | Discover `.slnx`/`.sln` files at `--root` (default: CWD) and write a starter `.sourcegraph.json`. Continues to work standalone; `init` invokes the same scaffolding internally when multi-solution is detected. |
| `scopes list [--root <path>]` | List the scopes declared in `.sourcegraph.json`. |
| `scopes info <name> [--root <path>] [--json]` | Detailed view of one scope: identity, project set, optional `language` field, optional `enrichment` block. With `--json`, emits a stable JSON shape. |
| `scopes add <name> --solution <path> [--root <path>] [--isolated]` | Add a scope. The file is created on first use. |
| `scopes remove <name> [--root <path>]` | Remove a scope. |
| `plugins list [--root <path>]` | List plugins declared in `.sourcegraph.json` with their version, status, registered contracts, and source path. |
| `plugins info <name> [--root <path>]` | Show the full record for one plugin: status reason, declared interfaces, registered tool names. |
| `vocabulary list [--root <path>] [--scope <id>] [--strict]` | Per-scope diagnostic over the soft-registry kind vocabulary. Lists `edge_kinds` / `symbol_kinds` / `annotation_flavors` with each entry tagged by source (`sdk` constant vs `plugin: <id>@<version>` vs `unknown`) and live emission count, plus a "Drift candidates" section flagging Levenshtein-near pairs (`bind-path` ~ `binds-path`) within the same scope. Default exit `0`; `--strict` exits `2` on any drift candidate so CI can wire it as a gate. |
| `embeddings status [--model <id>]` | Inspect the embedding model cache: cache directory, active model + dimension, per-file presence/size/SHA-256, free disk on the cache volume. Useful when the default offline mode reports an empty cache. |
| `embeddings pull [--model <id>]` | Synchronously download the active (or `--model`) manifest into the cache. Idempotent — a populated cache is a no-op. Useful as a pre-flight before air-gapping. |
| `embeddings remove [--model <id>] [--all]` | Clear the cache for the active model (default), one specific `--model`, or every cached model with `--all`. Combining `--model` and `--all` is rejected. |
| `embeddings verify [--model <id>]` | Recompute SHA-256 of every cached file and compare against the manifest. Default model has pinned SHAs — exits `2` on mismatch. Override `--model <id>` paths use a best-effort manifest with no pinned SHAs; in that case prints "informational only" beside each computed hash and exits `0`. |

Common flags:

| Flag | Effect |
|---|---|
| `--solution <path>`, `-s` | Path to a `.sln` / `.slnx`. |
| `--db <path>` | Override the database path for the **one-shot** commands (`index`, `stats`, `clear`). Ignored by `serve`, which always uses the per-scope layout under `<root>/.sourcegraph/scopes/<id>.db`. |
| `--root <path>` | Repository root used for `.sourcegraph.json` discovery and scope databases. Defaults to the directory holding `--solution`, then CWD. |
| `--model <id>` | Override the embedding model identity (default `jinaai/jina-embeddings-v2-base-code`). Applies to `serve` and `index`. |
| `--no-embeddings` | Skip the embedding pipeline entirely (no model download, no `vec0` writes). `semantic_search` returns a disabled message; every other tool works as before. |
| `--allow-model-download` | Explicitly allow `serve`/`index` to fetch the embedding model from Hugging Face when the local cache is empty. Automatic network access is disabled by default. Equivalent to `SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1`. |
| `--no-model-download` | Explicitly retain the default offline mode. A populated cache remains usable; an empty cache degrades to the same shape as `--no-embeddings`. `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` is retained as a fail-closed operator setting and takes precedence over the allow flag/environment variable. |
| `--no-history` | Disable the git-blame history pipeline. Use in environments without `git` on `PATH` or in CI where per-symbol history isn't needed. |
| `--no-instructions` | Don't publish server-side usage guidance in the MCP `initialize` response. By default the server tells the connected model to prefer source-graph tools over `Grep` + `Read` for symbol-level questions and to call `usage_stats` at end-of-turn to verify. Equivalent to setting `SOURCEGRAPH_NO_INSTRUCTIONS=1`. |
| `--no-leaf` | Don't prefix the brand mark `🌿` onto any of the three surfaces the server stamps: per-call response prose (the first user-visible text block of every built-in tool's result), the published `ServerInstructions` string, and the per-tool catalog identity (`Tool.Title` becomes `🌿 <name>` and `Tool.Description` is prefixed with `🌿 ` in `tools/list`). By default the brand mark surfaces in all three places so the agent (and the human reading the chat) can tell at a glance that the answer came from this server. Use this knob if your terminal renders emoji as monospaced fallback boxes or if you simply prefer unbranded output. Equivalent to setting `SOURCEGRAPH_NO_LEAF=1`. Independent of `--no-instructions`. |
| `--no-tool-triggers` | Don't append the `Use when: …` line to tool descriptions in `tools/list`. By default each built-in tool's description carries a one-line trigger phrase (e.g. *Use when: "where is X defined?"*) so agents pick the closest match before reaching for `Grep` + `Read`. Suppressing trims the upfront `tools/list` payload at the cost of less guidance for tool selection — useful when you're driving tool-pick guidance from your own `CLAUDE.md` instead. Equivalent to setting `SOURCEGRAPH_NO_TOOL_TRIGGERS=1`. |

Examples:

```bash
sourcegraph-mcp index ./MySln.sln
sourcegraph-mcp serve --solution ./MySln.sln
sourcegraph-mcp serve --root ./repo --no-embeddings
sourcegraph-mcp init-scopes
sourcegraph-mcp scopes add backend --solution ./backend.slnx
sourcegraph-mcp stats --db ./.sourcegraph/scopes/default.db
```

## How the index stays live

- Recursive `*.cs` file watcher that ignores `obj/`, `bin/`, `.git/`, and
  `.sourcegraph/`.
- `.git/HEAD` watcher — also handles git worktrees by parsing `gitdir:` from
  the `.git` file — so branch switches trigger a re-blame.
- 200 ms debounce window with batched re-indexing.
- Each canonical symbol keeps a stable id across edits, so references from
  other files remain valid even after rapid local changes.

### Recovery from incomplete indexing

The indexer self-heals from incomplete prior passes on the next start; no
operator action is needed. Pass 1's "unchanged file" SHA-skip path verifies
that each symbol-bearing file has at least one pass-2 artifact in the store
(an outgoing reference row, or an outgoing edge from a symbol declared in
the file) before skipping pass 2 — files whose refs and edges were cleared
but never repopulated (transient compilation gaps, exceptions partway
through a walk) are detected and re-walked automatically.

When the integrity check forces a recovery, the indexer emits an info-level
log line per affected file: `"Re-walking references for {Path}: file SHA
matches but no outgoing references in store …"`. Healthy installs never see
this line. Repeated recoveries on the same files would indicate a regression
in the upstream indexing flow worth investigating.

## Corruption recovery

When SQLite reports an on-disk corruption error (codes `11 = SQLITE_CORRUPT`
or `26 = SQLITE_NOTADB`) during a tool call, the dispatch layer's
`CorruptionGuard` runs `PRAGMA integrity_check` to verify. Three outcomes:

- **Integrity check returns `"ok"`** — false alarm (transient I/O or VACUUM
  race). Records `corruption-suspected-but-clean` and rethrows; the scope is
  not marked degraded. The next call may succeed.
- **Integrity check returns a failure string** — corruption confirmed. Records
  `corruption-detected`, marks the scope `degraded` with status_message
  `"corruption detected: <result>; call repair_scope mode=rebuild"`, rethrows.
  Subsequent calls hit the degraded short-circuit until `repair_scope` runs.
- **Integrity check itself throws** — DB so broken even the check fails.
  Records `corruption-detected` with `ok=false` and the verification
  exception, marks degraded, rethrows.

By default the agent decides whether to recover (via `repair_scope mode=rebuild`).
Setting `SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS=1` (or `true` / `yes`) opts into
**autonomous rebuild**: on confirmed corruption the server fires
`LiveIndexService.RebuildScopeAsync` on a background task, archiving the
corrupt DB to `orphans/<id>-corrupt-<utc-iso>.db` and cold-indexing from
sources. The original tool call still fails (the rebuild runs after), but the
scope recovers without agent intervention. Heal log records
`corrupt-db-rebuild-started` and `corrupt-db-rebuilt` (with `ok=true|false`)
for the lifecycle. Off by default: an autonomous rebuild on a misclassified
corruption silently destroys index state, so production deployments leave it
off and let the agent escalate.

## Observability

The server emits five signals you can hook into:

1. **JSONL audit log** — every tool call appends one line to
   `<root>/.sourcegraph/usage.jsonl`, capturing timestamp, tool name, args,
   scope, latency, response size, and error state. Suitable for offline
   analysis or compliance archival.
2. **`usage_stats` MCP tool** — returns in-process counters (call count, error
   count, average / max latency, average response size, last-called time) for
   the current process. Use it at end-of-turn to verify the agent reached for
   the graph instead of falling back to `Grep` + `Read`.
3. **OpenTelemetry signals** — the server emits spans on
   `ActivitySource("DevBitsLab.Mcp.SourceGraph")` and metrics on
   `Meter("DevBitsLab.Mcp.SourceGraph")`. Counters: `sourcegraph.tool.calls`,
   `sourcegraph.tool.errors`, `sourcegraph.heal.fired`. Histograms:
   `sourcegraph.tool.duration` (ms), `sourcegraph.tool.response_size` (bytes).
   Tool tags: `mcp.tool`, `mcp.tool.ok`, `mcp.tool.scope`. Heal tags: `kind`
   (e.g. `orphan-db-archived`, `missing-db-detected`, `stuck-indexing-detected`),
   `scope`, `ok`. All signals are zero-cost when no listener is attached;
   pick them up with the OpenTelemetry SDK or `dotnet-counters monitor --name
   sourcegraph-mcp DevBitsLab.Mcp.SourceGraph`.
4. **Heal-event JSONL log** — internal state changes (boot reconciliation,
   repair-tool invocations, corruption detection, embeddings prune) append
   one line to `<root>/.sourcegraph/heals.jsonl` with shape
   `{"ts","kind","scope","ok","ms","details"}`. Separate from `usage.jsonl`
   (which tracks tool calls) to keep the two streams independently scannable.
   Best-effort: write failures are swallowed and never surface to the agent.

   Heal kinds across the three self-healing phases:

   | Kind | Trigger | Phase |
   |---|---|---|
   | `orphan-db-archived` | Boot: scope DB file with no registry row, moved to `orphans/` | 1 |
   | `missing-db-detected` | Boot: registry row but DB file missing | 1 |
   | `stuck-indexing-detected` | Boot: prior process died with `status='indexing'` | 1 |
   | `workspace-open-retried` | Cold-index: bounded retry succeeded (or all 4 attempts failed — 1 initial + 3 retries at `[1s, 5s, 25s]`) | 2 |
   | `repair-scope-invoked` | `repair_scope` tool fired (mode + outcome in `details`) | 2 |
   | `reconcile-drift-invoked` | `reconcile_drift` tool fired (counts in `details`) | 2 |
   | `embeddings-pruned` | Orphan embedding rows removed (after cold-index, after `repair_scope minimal`) | 2/3 |
   | `corruption-suspected-but-clean` | SQLite error code 11/26 surfaced but `integrity_check` passed (false alarm) | 3 |
   | `corruption-detected` | SQLite error code 11/26 confirmed by `integrity_check`; scope marked degraded | 3 |
   | `corrupt-db-rebuild-started` | Autonomous rebuild kicked off (env var enabled) | 3 |
   | `corrupt-db-rebuilt` | Autonomous rebuild completed (success or failure in `ok`) | 3 |
5. **MCP `notifications/progress`** — four tools opt in to live progress
   reporting: `semantic_search` (three checkpoints around ONNX-model load +
   vector search + formatting), `impact_of_change`, `module_summary` (one
   starting checkpoint each), and `find_definition` (cold-start phase
   progress only — see below). **Cold-start visibility**: when one of the
   progress-aware tools is invoked against a scope whose initial indexing
   isn't finished, the server forwards per-scope `IIndexingProgressSource`
   events as `notifications/progress` for the duration of the wait — three
   coarse phases (`opening workspace` → `indexing` → `ready`) so the chat
   panel sees motion instead of a silent spinner. Clients opt in by sending
   a `progressToken` field on the originating `tools/call` request:

   ```json
   {
     "method": "tools/call",
     "params": {
       "name": "semantic_search",
       "arguments": {"query": "retry on transient errors"},
       "_meta": {"progressToken": "any-string-or-int"}
     }
   }
   ```

   When no `progressToken` is set, the server emits zero progress messages
   — the wire fast-path is unchanged. When set, the server emits one
   `notifications/progress` message per checkpoint with a normalised
   `progress` value in `[0, 1]` and a short `message` (`encoding query`,
   `searching`, `formatting results`, `querying`).

## Resource limits and tunables

The server is designed to stay inside a single process and a single SQLite
database per scope. The current limits are:

| Limit | Default | How to change |
|---|---|---|
| Roslyn analyzer timeout per document | 30 s | Hard-coded in `AnalyzerPipeline`; override via fork. |
| File-watcher debounce window | 200 ms | Hard-coded in `SolutionWatcher`. |
| Default `SearchSymbols` / `find_references` / `list_members` result limit | 25 / 50 / 100 rows | Pass `limit` on the MCP tool call. A soft serialized-size cap (~50K chars) trims further if a larger `limit` would exceed Claude Code's per-call ceiling; trim is signalled via `omitted_size=N` in the audience-restricted `_meta:` block. |
| `impact_of_change` max depth | 4 hops | Pass `maxDepth` on the tool call. |
| `semantic_search` top-k default | 10 | Pass `k` on the tool call. |
| Embedding model download | ~640 MB, automatic download disabled by default | Populate deliberately with `embeddings pull`, or opt in with `--allow-model-download`; use `--no-embeddings` to disable the pipeline. |
| Per-symbol `git blame` shellout | enabled | Disable with `--no-history`. |
| MCP `initialize` instructions payload | enabled | Disable with `--no-instructions` or `SOURCEGRAPH_NO_INSTRUCTIONS=1`. |
| Green-leaf brand mark on tool responses, `ServerInstructions`, and per-tool `Title`/`Description` in `tools/list` | enabled | Disable with `--no-leaf` or `SOURCEGRAPH_NO_LEAF=1`. |
| `Use when: …` trigger line on tool descriptions in `tools/list` | enabled | Disable with `--no-tool-triggers` or `SOURCEGRAPH_NO_TOOL_TRIGGERS=1`. |
| SQLite database size per scope | unbounded | Use `clear` to wipe; databases live under `<root>/.sourcegraph/scopes/<id>.db`. |
| `query_graph` statement timeout | 5 s | `--query-timeout-seconds <int>` or `SOURCEGRAPH_QUERY_TIMEOUT_SECONDS=<int>`. |
| `query_graph` row cap | 5000 rows | `--query-row-limit <int>` or `SOURCEGRAPH_QUERY_ROW_LIMIT=<int>`. The tool surfaces `truncated: true` when the cap is hit. |

The curated tools have no built-in timeout — they honour the MCP client's `CancellationToken` through every async graph operation. The `query_graph` tool DOES enforce a per-call statement timeout (above) so an accidental Cartesian join doesn't pin the server.

## Platform support

| Platform | Build / test in CI | Distribution |
|---|---|---|
| Linux x64 / arm64 | Ubuntu (latest) on every push and PR | `dotnet tool install -g` |
| macOS arm64 / x64 | macOS (latest) on every push and PR | `dotnet tool install -g` |
| Windows x64       | Windows (latest) on every push and PR | `dotnet tool install -g` |

The published tool targets **`net10.0`**. Earlier .NET runtimes (8, 9) are not
currently supported — see [GOVERNANCE.md](GOVERNANCE.md#roadmap-items-currently-parked)
for the LTS-multi-TFM roadmap.

The configuration schema for `.sourcegraph.json` is published as JSON Schema
at [`schema/sourcegraph.schema.json`](schema/sourcegraph.schema.json) — most
editors will validate your config if you add a top-level
`"$schema": "./schema/sourcegraph.schema.json"`.

## Building from source

```bash
git clone https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph.git
cd DevBitsLab.Mcp.SourceGraph
dotnet build
dotnet test
```

To point a project's `.mcp.json` at your local checkout instead of the
published tool, swap `command` / `args` for:

```json
{
  "command": "dotnet",
  "args": [
    "run", "--project",
    "${workspaceFolder}/path/to/DevBitsLab.Mcp.SourceGraph/src/DevBitsLab.Mcp.SourceGraph.Server",
    "--no-build", "--no-launch-profile", "--verbosity", "quiet",
    "--", "serve", "--solution", "${workspaceFolder}/MySolution.slnx"
  ]
}
```

Re-run `dotnet build` after each change so the next launch picks it up.

## Contributing & security

- Contribution workflow, coding conventions, and the MCP-tool authoring
  checklist live in [CONTRIBUTING.md](CONTRIBUTING.md).
- The architecture overview and module layout live in
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- Vulnerability disclosure is documented in [SECURITY.md](SECURITY.md) — please
  do **not** open public issues for security problems.
- Project governance, decision-making, and the deprecation policy live in
  [GOVERNANCE.md](GOVERNANCE.md).
- A running history of changes is in [CHANGELOG.md](CHANGELOG.md).

## License

Released under the [MIT License](https://opensource.org/licenses/MIT).
