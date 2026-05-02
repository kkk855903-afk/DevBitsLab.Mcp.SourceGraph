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
Claude Code (or any client that honours the convention) opens the project. The
`${workspaceFolder}` token is expanded by the client; if your client doesn't
expand it, the server expands `${workspaceFolder}` itself by reading
`WORKSPACE_FOLDER` / `CLAUDE_PROJECT_DIR` / `MCP_WORKSPACE_FOLDER` env vars
(set whichever one is convenient on your machine).

To register the server in another `.NET` solution, copy `.mcp.json` and change
the relative path:

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

Prerequisites on the developer's box:
- `dotnet tool install -g DevBitsLab.Mcp.SourceGraph.Tool` (or whatever build distribution you use).
- `~/.dotnet/tools` on `PATH` so `sourcegraph-mcp` resolves.

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
