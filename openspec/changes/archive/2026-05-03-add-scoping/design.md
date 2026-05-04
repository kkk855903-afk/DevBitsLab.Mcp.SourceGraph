## Context

The single-DB model maps cleanly to single-solution; for multi-solution monorepos and intra-solution isolation the only sensible primitive is named scopes that physically separate their data. This design follows Glean's per-database, Kythe's per-corpus, and Rider's named-scopes patterns. The compatibility constraint is hard: the existing single-solution case must remain zero-config.

## Goals / Non-Goals

**Goals:**
- A scope is a self-contained unit: its own SQLite file, its own watcher, its own `MSBuildWorkspace`. Cross-scope queries fan out and merge in process.
- Single-solution users keep working with no config change. They get one synthesised scope `default`.
- Adding a new scope (new .slnx in the same repo) is one line in `.sourcegraph.json`.
- Cross-scope identity uses the existing `canonical_key` so the same symbol shared across scopes can be matched.
- Tools degrade safely: `scope` omitted = current behaviour.

**Non-Goals:**
- Cross-repo / cross-process federation. Out of scope; one server, one repo.
- Namespace-prefix scoping. Filterable at query time; not a scope kind in v1.
- Permission/ACL filtering between scopes. We're not building a security boundary, only an isolation hint (`isolated: true` excludes a scope from `*`-fanout queries by default).
- Hot reconfiguration. Editing `.sourcegraph.json` requires a server restart in v1; live config-watch is a future change.

## Decisions

**1. One DB per scope, plus a `_meta.db` registry.**
`.sourcegraph/scopes/<id>.db` for each scope; `.sourcegraph/_meta.db` holds the `scopes(id, name, root, project_set, isolated, last_indexed_at)` registry. Cross-scope queries iterate open stores and merge by `canonical_key`. Memory ceiling: SQLite handles are ~1-2 MB each; 50 scopes is fine.

**2. Cross-scope identity via canonical_key.**
Symbol ids are only unique *within* a scope. When merging cross-scope results, we group by `canonical_key`. Two scopes that index the same shared library will report it twice; the merger collapses by key.

**3. Config: `.sourcegraph.json` at repo root, three scope kinds.**
```json
{
  "scopes": [
    { "name": "frontend", "solutions": ["src/frontend.slnx"] },
    { "name": "backend",  "solutions": ["src/backend.slnx"], "exclude": ["**/Generated/**"] },
    { "name": "vendor",   "include": ["third_party/**/*.csproj"], "isolated": true }
  ],
  "default_scope": "backend"
}
```
Three composable definitions: `solutions[]`, `projects[]`, `paths[]` (csproj globs). Internally all three resolve to a project-set. `isolated: true` excludes the scope from `*`-fanout unless the agent passes it explicitly.

**4. Single-solution back-compat via synthesis + one-shot migrator.**
On startup: if `.sourcegraph.json` is absent, synthesise `{ scopes: [{ name: "default", solutions: [<discovered.slnx>] }], default_scope: "default" }`. If `.sourcegraph/graph.db` exists from an older version, atomically rename it to `.sourcegraph/scopes/default.db` and continue. No user action.

**5. Tool surface: optional `scope` parameter.**
Every existing tool gains `scope?: string | string[] | "*"`. Default behaviour: use `default_scope` when present, otherwise the only scope, otherwise return an error pointing at `list_scopes`. Result rows gain a `scope` field. One new tool `list_scopes`.

**6. One server instance per repo, N scopes inside.**
Reject "one server per scope": it'd fragment `.mcp.json`, multiply the watcher overhead, lose cross-scope visibility, and force the agent to discover sibling scopes externally. The recommended pattern stays "register one `sourcegraph` server per repo".

## Risks / Trade-offs

- **Initial-index time scales with N scopes.** Mitigated by parallel scope startup (each scope's `MSBuildWorkspace` opens on its own thread).
- **`canonical_key` collisions across scopes are by design** (a shared library appears in both frontend and backend). The merger collapses; the agent sees the symbol once with `scope: ["frontend", "backend"]`.
- **One scope crashing on initial index shouldn't bring down the others.** Per-scope try/catch + a `degraded` status surfaced via `list_scopes`. The MCP host process never dies because of one bad scope.
- **Cross-scope FTS5 queries fan out**, returning N result lists merged in C#. At our cardinalities (a few thousand FTS hits per scope) this is single-digit-ms in process — acceptable.
- **`.sourcegraph.json` schema versioning.** Add a `"version"` field; we own the schema, can evolve it.
