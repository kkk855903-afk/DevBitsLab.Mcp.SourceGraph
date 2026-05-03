# DevBitsLab.Mcp.SourceGraph

A **live code source graph MCP server** for .NET solutions. Indexes C# via Roslyn into
SQLite + FTS5, exposes graph queries to MCP clients (Claude Code, Cursor, Continue, …)
over stdio, and keeps itself fresh as files change.

Lets coding agents replace dozens of `Grep` + `Read` calls with one structured tool
call: *"who calls `Foo.PublishAsync`?"*, *"what's in `MyNamespace`?"*,
*"what would change if I edit this method?"*.

## Install

```bash
dotnet tool install -g DevBitsLab.Mcp.SourceGraph.Tool
```

Make sure `~/.dotnet/tools` is on your `PATH`.

## Wire it up

### Claude Code (project-scoped, committed)

Drop `.mcp.json` at the repo root:

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

Open the directory in Claude Code; approve the server when prompted. Done.

### Pin a version per repo (`.config/dotnet-tools.json`)

```bash
dotnet new tool-manifest
dotnet tool install DevBitsLab.Mcp.SourceGraph.Tool
git add .config/dotnet-tools.json
```

Collaborators run `dotnet tool restore` once, then `.mcp.json` invokes
`dotnet sourcegraph-mcp serve …` (no global install needed).

### Cursor / Claude Desktop / Continue

Same `command` + `args` shape, just in each client's config file
(`~/.cursor/mcp.json`, `claude_desktop_config.json`, etc.).

## Tools

| Tool | Question it answers |
|---|---|
| `find_definition` | Where is X defined? |
| `find_references` | Who uses / calls X? |
| `list_callers`, `list_callees` | Callers/callees of X (named members only) |
| `list_symbols_in_file` | What's in this file? |
| `search_symbols` | I only have a fragment of the name (FTS5 trigram) |
| `neighborhood` | Quick orientation around X |
| `module_summary` | What's important in this namespace? |
| `impact_of_change` | Transitive callers — what would break if I edit X? |
| `graph_stats`, `usage_stats` | Verify the index / observe agent usage |

Plus three resource templates (`graph://symbol/{id}`, `graph://file/{path}`,
`graph://namespace/{name}`) for hosts that surface MCP resources.

## How it stays live

- File watcher (`*.cs` recursive, ignores `obj/` `bin/` `.git/` `.sourcegraph/`).
- `.git/HEAD` watcher — also handles git worktrees (parses `gitdir:` from the
  `.git` file).
- 200ms debounce, batched re-index. Each canonical symbol keeps a stable id
  across edits so refs from other files stay valid.

A persistent JSONL log of every tool call lives at
`<solution>/.sourcegraph/usage.jsonl` and the in-process `usage_stats` tool
returns counts/latencies on demand — useful for confirming agents actually use
the graph instead of falling back to `Grep` + `Read`.

## License

MIT.
