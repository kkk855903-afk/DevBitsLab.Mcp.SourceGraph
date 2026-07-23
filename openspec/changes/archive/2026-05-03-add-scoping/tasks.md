## 1. Core types

- [x] 1.1 Add `Scope(string Id, string Name, string Root, ScopeProjectSet ProjectSet, bool Isolated, DateTimeOffset LastIndexedAt)` to `Core`.
- [x] 1.2 `ScopeProjectSet` discriminated union: `Solutions(IReadOnlyList<string>)`, `Projects(IReadOnlyList<string>)`, `Paths(IReadOnlyList<string>)`. Plus `IReadOnlyList<string> Exclude`.
- [x] 1.3 `ScopeId = string` (kebab-case). Validation: `^[a-z0-9][a-z0-9-]{0,63}$`.

## 2. Configuration

- [x] 2.1 `.sourcegraph.json` schema + JSON-schema doc generation.
- [x] 2.2 `ScopeConfigLoader.Load(repoRoot)` returns the parsed config or a synthesised single-scope default when absent.
- [x] 2.3 `init-scopes` CLI subcommand scaffolds `.sourcegraph.json` from discovered .slnx siblings.

## 3. Storage

- [x] 3.1 New `_meta.db` with `scopes(id, name, root, project_set_json, isolated, last_indexed_at)` plus migration version.
- [x] 3.2 `IScopeRegistry`: `ListAsync()`, `GetAsync(id)`, `UpsertAsync(scope)`.
- [x] 3.3 Per-scope DB factory: `IGraphStore CreateForScope(ScopeId)` opens `.sourcegraph/scopes/<id>.db`, applies schema.
- [x] 3.4 One-shot migrator: detect existing `graph.db` at the old location and rename it to `scopes/default.db` on first start.

## 4. Indexer

- [x] 4.1 `RoslynIndexer` is now per-scope. `LiveIndexService` holds a `Dictionary<ScopeId, RoslynIndexer>` and a `Dictionary<ScopeId, SolutionWatcher>`.
- [x] 4.2 Scope startup is parallelised (each scope opens its workspace concurrently).
- [x] 4.3 Per-scope try/catch: a failed scope marks itself `degraded` in the registry; the MCP host stays up.

## 5. MCP tool surface

- [x] 5.1 Add optional `scope?: string | string[] | "*"` to every existing tool.
- [x] 5.2 Resolve the scope set: omitted = `default_scope` else single-scope else error pointing at `list_scopes`.
- [x] 5.3 Fan out the query, merge by `canonical_key`, surface `scope` per result row.
- [x] 5.4 New tool `list_scopes` returns scope id, name, root, project count, last_indexed, status (`ok | degraded | indexing`).
- [x] 5.5 `usage_stats` reports per-scope breakdown; `usage.jsonl` records `scope` per row.
- [x] 5.6 The `*` literal means "all non-isolated scopes"; isolated scopes only enter the result when listed explicitly.

## 6. CLI

- [x] 6.1 `sourcegraph-mcp scopes list/add/remove`.
- [x] 6.2 `sourcegraph-mcp init-scopes` scaffolder.
- [x] 6.3 The existing `--solution` flag remains valid and creates an implicit single-scope override.

## 7. Tests

- [x] 7.1 Migration test: a repo with the old `graph.db` layout opens cleanly under the new code, with the DB moved to `scopes/default.db`.
- [x] 7.2 Two-scope fixture (`frontend.slnx` + `backend.slnx`); confirm `find_definition` with `scope = "*"` merges results, `scope = "frontend"` filters.
- [x] 7.3 Isolated-scope test: a `vendor` scope marked `isolated: true` is excluded from `*` fanout.
- [x] 7.4 Degraded-scope test: one scope's solution fails to load; `list_scopes` shows `status = degraded`; queries against other scopes still work.

## 8. Update specs

- [x] 8.1 Sync delta specs into existing capabilities and create `openspec/specs/scoping/spec.md` on archive.
