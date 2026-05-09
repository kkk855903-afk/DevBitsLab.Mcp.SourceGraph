# DevBitsLab.Mcp.SourceGraph

Live code source graph MCP server for .NET solutions. Indexes C# via Roslyn into
SQLite + FTS5 and exposes graph queries to MCP clients (Claude Code, Cursor) over
stdio.

## Tool-usage guidance ships with the server

When this MCP server connects, it publishes "prefer source-graph tools over
`Grep` + `Read` for symbol-level questions" plus a closing "verify with
`usage_stats`" directive in the `initialize` response. Each tool's description
also carries a `Use when:` line documenting the question shape it answers.
Suppress with `--no-instructions` or `SOURCEGRAPH_NO_INSTRUCTIONS=1` if you
prefer to drive guidance from your own `CLAUDE.md`.

Every built-in tool's response begins with a green-leaf glyph `🌿` — that's
the at-a-glance signal that the answer came from this server (and not from
`Grep` + `Read` or another MCP server). Suppress with `--no-leaf` or
`SOURCEGRAPH_NO_LEAF=1` if your terminal doesn't render emoji well or you
prefer unbranded output.

A persistent JSONL log of every tool call lives at
`<solution>/.sourcegraph/usage.jsonl` for offline analysis.

`semantic_search`, `impact_of_change`, and `module_summary` emit MCP
`notifications/progress` when the originating `tools/call` request includes
a `progressToken` — useful for live status indicators on the slow paths
(cold-start ONNX model load, deep recursive CTE walks). Clients that don't
opt in see today's silent-then-result behaviour.

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
`_meta.db` registry tracks status (`ok | degraded | indexing`) and last-indexed
time. An `isolated` scope is excluded from `scope = "*"` fan-out — useful for
vendored / generated code that shouldn't pollute `find_references` on production.

Every existing tool gains an optional `scope` parameter (string id, comma-separated
list, or `"*"`). Call `list_scopes` to discover the configured scopes. Without a
`.sourcegraph.json` and without `--solution`, a synthesised `default` scope keeps
single-solution users working unchanged.

CLI helpers:

- `sourcegraph-mcp init-scopes` — scaffold a `.sourcegraph.json` from the .slnx
  files at the repo root.
- `sourcegraph-mcp scopes list` / `add <name> --solution <path>` / `remove <name>`.

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

- `src/DevBitsLab.Mcp.SourceGraph.Core/` — domain types (Scope, ScopeProjectSet, ScopeIdValidator)
- `src/DevBitsLab.Mcp.SourceGraph.Storage/` — SQLite graph store + FTS5; per-scope `IGraphStore` factory + `IScopeRegistry`; `ScopeConfigLoader`
- `src/DevBitsLab.Mcp.SourceGraph.Indexing/` — Roslyn workspace + indexer
- `src/DevBitsLab.Mcp.SourceGraph.Watcher/` — file + git HEAD watcher
- `src/DevBitsLab.Mcp.SourceGraph.Server/` — stdio MCP host + CLI; `Scoping/` (router + per-scope hosts)
- `tests/fixtures/Sample.sln` — single-scope fixture for smoke tests
- `tests/fixtures/MultiScope/` — multi-scope fixture (frontend.sln + backend.sln + .sourcegraph.json)
