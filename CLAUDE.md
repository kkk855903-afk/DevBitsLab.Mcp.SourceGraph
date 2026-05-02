# DevBitsLab.Mcp.SourceGraph

Live code source graph MCP server for .NET solutions. Indexes C# via Roslyn into
SQLite + FTS5 and exposes graph queries to MCP clients (Claude Code, Cursor) over
stdio.

## When working in any indexed .NET solution: prefer source-graph tools

If a `sourcegraph` MCP server is connected to the current solution, use its tools
**before** reaching for `Grep` + `Read` for symbol-level questions. The graph
gives you authoritative answers in one call instead of dozens of file reads:

| Question | Tool |
|---|---|
| "where is X defined?" | `find_definition` |
| "who calls / references X?" | `find_references` (all uses) or `list_callers` (named callers only) |
| "what does X call?" | `list_callees` |
| "what's in this file?" | `list_symbols_in_file` |
| "I only have a fragment of the name" | `search_symbols` (FTS5 trigram) |
| "what would change if I edit X?" | `impact_of_change` (transitive callers) |
| "give me a quick overview around X" | `neighborhood` (callers + callees in one call) |
| "what's important in this namespace?" | `module_summary` (top symbols by inbound calls) |

Fall back to `Grep` / `Read` only when the graph genuinely doesn't cover the
question (config files, plain text, comments, PR descriptions, etc.) or when a
tool returns nothing relevant.

## Verifying the MCP is doing its job

Call `usage_stats` at the end of a turn to see how many graph queries actually
happened. If you walked through `Grep`+`Read` and the counts didn't budge, you
skipped tools that would have been faster — try the same question with `find_*`
or `search_symbols` next time.

A persistent JSONL log of every tool call lives at
`<solution>/.sourcegraph/usage.jsonl` for offline analysis.

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

- `src/DevBitsLab.Mcp.SourceGraph.Core/` — domain types
- `src/DevBitsLab.Mcp.SourceGraph.Storage/` — SQLite graph store + FTS5
- `src/DevBitsLab.Mcp.SourceGraph.Indexing/` — Roslyn workspace + indexer
- `src/DevBitsLab.Mcp.SourceGraph.Watcher/` — file + git HEAD watcher
- `src/DevBitsLab.Mcp.SourceGraph.Server/` — stdio MCP host + CLI
- `tests/fixtures/Sample.sln` — fixture solution for smoke tests

Plan / milestones: `/Users/jacques/.claude/plans/create-a-plan-to-soft-pizza.md`
