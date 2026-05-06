# DevBitsLab.Mcp.SourceGraph

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

## Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Wiring it into an MCP client](#wiring-it-into-an-mcp-client)
- [MCP tools](#mcp-tools)
- [Resource templates](#resource-templates)
- [Scopes (multi-solution monorepos)](#scopes-multi-solution-monorepos)
- [Command-line interface](#command-line-interface)
- [How the index stays live](#how-the-index-stays-live)
- [Observability](#observability)
- [Building from source](#building-from-source)
- [License](#license)

## Features

- **Roslyn-backed C# indexing.** Symbols, references, call/uses-type/overrides/
  implements/instantiates/throws edges, XML doc summaries, accessibility, and
  modifiers — all queryable in one round-trip.
- **FTS5 name search.** Trigram fragment matching for cases where you only
  remember "`…Greet…Async`".
- **Optional code-aware semantic search.** ONNX embeddings (default model:
  `jinaai/jina-embeddings-v2-base-code`) stored in `sqlite-vec` for
  natural-language queries like *"find the rate-limiting code"*. Disable with
  `--no-embeddings` to skip the model download entirely.
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

## Wiring it into an MCP client

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

### Pin a version per repo

```bash
dotnet new tool-manifest
dotnet tool install DevBitsLab.Mcp.SourceGraph.Tool
git add .config/dotnet-tools.json
```

Collaborators run `dotnet tool restore` once. Your `.mcp.json` then invokes
`dotnet sourcegraph-mcp serve …` — no global install required.

### Cursor / Claude Desktop / Continue

Use the same `command` + `args` shape inside each client's configuration file
(for example `~/.cursor/mcp.json`, `claude_desktop_config.json`, or
Continue's MCP block).

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
| `list_callers` | Inbound edges into X — default `kind=calls`; also `uses_type`, `overrides`, `implements_member`, `instantiates`, `throws`, `all` |
| `list_callees` | Outbound edges from X (same `kind` taxonomy) |
| `list_symbols_in_file` | What's in this file? (kind, accessibility, modifiers, XML summary) |
| `list_members` | Direct members of a class / struct / interface / namespace by FQN, optionally filtered by accessibility |
| `find_implementations` | Concrete members satisfying an interface member |
| `neighborhood` | Inbound + outbound edges around X for one `kind` layer at a time (default `calls`; pass `kind=uses_type`, `overrides`, `implements_member`, `instantiates`, `throws`, or `all` to inspect other layers) |
| `module_summary` | Top symbols in a namespace or directory by inbound call count |
| `impact_of_change` | Transitive upstream callers of X up to `maxDepth` |

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
| `graph_stats` | Counts of files / symbols / references / edges — confirm the index is populated |
| `usage_stats` | Per-tool call count, error count, latency, average response size, last-called time for the current process |
| `ping` | Health check — returns `pong @ <UTC ISO-8601>` |

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
```

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
- A `_meta.db` registry tracks per-scope status (`ok | degraded | indexing`)
  and last-indexed timestamp.
- `isolated: true` excludes a scope from `scope = "*"` fan-out — useful for
  vendored or generated code that shouldn't pollute references on production
  symbols.
- Without a `.sourcegraph.json` and without `--solution`, a synthesised
  `default` scope keeps single-solution users working unchanged.
- The legacy single-database layout (`.sourcegraph/graph.db`) is migrated to
  `scopes/default.db` automatically on first start.
- Live indexing currently resolves a Roslyn workspace per scope only for
  `solutions`-based scopes. Scopes declared via `projects` or `paths` are
  accepted by the config loader but are not indexed by the live server yet —
  prefer `solutions` for now.

Every tool accepts an optional `scope` parameter — pass an id, a
comma-separated list, or `"*"` to fan out.

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
| `init-scopes` | Discover `.slnx`/`.sln` files at `--root` (default: CWD) and write a starter `.sourcegraph.json`. |
| `scopes list [--root <path>]` | List the scopes declared in `.sourcegraph.json`. |
| `scopes add <name> --solution <path> [--root <path>] [--isolated]` | Add a scope. The file is created on first use. |
| `scopes remove <name> [--root <path>]` | Remove a scope. |
| `plugins list [--root <path>]` | List plugins declared in `.sourcegraph.json` with their version, status, registered contracts, and source path. |
| `plugins info <name> [--root <path>]` | Show the full record for one plugin: status reason, declared interfaces, registered tool names. |

Common flags:

| Flag | Effect |
|---|---|
| `--solution <path>`, `-s` | Path to a `.sln` / `.slnx`. |
| `--db <path>` | Override the database path for the **one-shot** commands (`index`, `stats`, `clear`). Ignored by `serve`, which always uses the per-scope layout under `<root>/.sourcegraph/scopes/<id>.db`. |
| `--root <path>` | Repository root used for `.sourcegraph.json` discovery and scope databases. Defaults to the directory holding `--solution`, then CWD. |
| `--model <id>` | Override the embedding model identity (default `jinaai/jina-embeddings-v2-base-code`). Applies to `serve` and `index`. |
| `--no-embeddings` | Skip the embedding pipeline entirely (no model download, no `vec0` writes). `semantic_search` returns a disabled message; every other tool works as before. |
| `--no-history` | Disable the git-blame history pipeline. Use in environments without `git` on `PATH` or in CI where per-symbol history isn't needed. |

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

## Observability

- Every tool call appends to `<solution>/.sourcegraph/usage.jsonl` (or the
  per-scope equivalent), so you can audit how agents are using the graph.
- The `usage_stats` tool returns in-process counts and latencies on demand —
  use it at the end of a turn to confirm the agent reached for the graph
  instead of falling back to `Grep` + `Read`.

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

## License

Released under the [MIT License](https://opensource.org/licenses/MIT).
