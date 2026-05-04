## Why

Today one MCP server serves one .slnx into one SQLite database. Real-world repos break this in three ways:

1. **Multi-solution monorepos** with `frontend.slnx`, `backend.slnx`, `tools.slnx` side by side that should be queryable from the same Claude Code session.
2. **Huge solutions** where the agent should be able to scope a query to one project or namespace (e.g. *"`search_symbols 'retry'` but only inside `DevBitsLab.Feeds.Persistence`"*).
3. **Isolation needs**: vendored dependencies, generated code, test fixtures shouldn't pollute `find_references` on production code, but should remain reachable when explicitly requested.

The fix is **scopes** — named, user-defined indexable units, each living in its own per-scope SQLite database, with a thin in-process router that fans out queries by scope id. Prior art (Glean's per-revision DBs, Kythe's corpus, Rider's named scopes, VS Code multi-root) all converges on this model.

## What Changes

- New capability `scoping`. A `Scope` is `(id, name, root_directory, project_set, isolated_bool, last_indexed_at)`.
- New per-repo config file `.sourcegraph.json` at the repo root listing scopes (solutions, projects, or path globs). Absent config → a single synthesised scope `"default"` rooted at the discovered solution (today's behaviour, zero config required).
- Storage layout becomes `<repo>/.sourcegraph/scopes/<id>.db` plus a small `_meta.db` registry. The single-solution case migrates by renaming the existing `graph.db` into `scopes/default.db` on first run.
- Indexer learns to bind to a scope. `LiveIndexService` holds N scope hosts, one watcher per scope.
- Every existing MCP tool gains an optional `scope` parameter (string or string[] or `"*"`). Omitted → uses `default_scope` from config or "all" when none configured. Result rows gain a `scope` field so the agent can filter on its side.
- New tool `list_scopes` returns each scope's id, root, project count, last-indexed timestamp, and isolation flag.
- New CLI subcommands `sourcegraph-mcp scopes list/add/remove` plus `init-scopes` (scaffolds `.sourcegraph.json` from discovered .slnx siblings).

## Capabilities

### New Capabilities

- `scoping`: scope registry, configuration loader, per-scope DB layout, query routing.

### Modified Capabilities

- `indexing`: every operation is parameterised by `ScopeId`.
- `storage`: physical layout becomes per-scope; new `_meta.db` registry.
- `cli`: new subcommands and the migrator.
- `mcp-tools`: every tool gains optional `scope`; new `list_scopes` tool; rows include `scope`.
- `mcp-config`: `.sourcegraph.json` schema is documented alongside `.mcp.json`.

## Impact

- One-shot migration for existing single-solution users (rename DB into `scopes/default.db`); transparent to users.
- Memory footprint scales linearly with scope count (one `MSBuildWorkspace` per scope). Realistic ceiling ~50 scopes/process; typical 2-5.
- All tool result schemas additively gain a `scope` field — clients that ignore it work unchanged.
- Test surface grows: every existing tool needs a "single-scope default" test plus a "multi-scope merge" test.
