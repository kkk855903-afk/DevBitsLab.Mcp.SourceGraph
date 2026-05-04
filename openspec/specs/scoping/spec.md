# Scoping

## Purpose

Decompose a repository into named, user-defined indexable units ("scopes")
so multi-solution monorepos, large solutions, and isolation needs (vendor
code, generated code, test fixtures) can be queried separately or together
from one MCP server. Each scope owns a SQLite file under
`<repo>/.sourcegraph/scopes/<id>.db` and is registered in a small
`_meta.db`; queries fan out across one or more scopes and merge results by
canonical key with the originating scope tagged on every row.

## Requirements

### Requirement: Scope as first-class entity
The system SHALL model a scope as `(id, name, root, project_set, isolated, last_indexed_at)` where `id` is a kebab-case slug unique within the repo, `project_set` is one of `solutions[]`, `projects[]`, or `paths[]` (csproj globs), and `isolated` defaults to `false`.

#### Scenario: Synthesise a default scope
- **WHEN** a server starts in a repo with no `.sourcegraph.json` and exactly one `.slnx` discovered at the root
- **THEN** an in-memory scope `{ id: "default", solutions: ["<discovered>"], isolated: false }` is registered and used for every query whose `scope` is omitted

#### Scenario: Read scopes from config
- **WHEN** `.sourcegraph.json` lists three scopes (one solutions-based, one paths-based, one isolated)
- **THEN** all three appear in the registry and `list_scopes` reports each with the right kind, isolation flag, and root

### Requirement: Per-scope physical isolation
Each scope SHALL persist its graph in `<repo>/.sourcegraph/scopes/<id>.db`; a separate `<repo>/.sourcegraph/_meta.db` SHALL hold the `scopes` registry.

#### Scenario: New scope creates a new file
- **WHEN** a new scope `frontend` is added to `.sourcegraph.json` and the server is restarted
- **THEN** `.sourcegraph/scopes/frontend.db` is created on first index, distinct from any other scope's DB

### Requirement: One-shot migration from single-DB layout
On startup, if a legacy `<repo>/.sourcegraph/graph.db` exists and `<repo>/.sourcegraph/scopes/default.db` does not, the system SHALL atomically move the legacy file to the new location.

#### Scenario: Existing user upgrades
- **WHEN** a v0.1.x graph.db is present at startup of the new server
- **THEN** the file is renamed to `scopes/default.db`, no data is lost, and the synthesised `default` scope opens it without re-indexing

### Requirement: Cross-scope query fan-out and merge
Queries that target multiple scopes SHALL execute per-scope and merge results in process by grouping on `canonical_key`; rows attributed to multiple scopes appear once with `scope` listing every scope they came from.

#### Scenario: Shared library appears in two scopes
- **WHEN** scopes `frontend` and `backend` both index a shared library that defines symbol `Foo` with the same canonical key
- **THEN** a `find_definition(symbol = "Foo", scope = "*")` query returns one row whose `scope` field is `["backend", "frontend"]` (sorted)

### Requirement: Isolation flag affects fan-out default
When a scope has `isolated: true`, it SHALL be excluded from `scope = "*"` fan-out by default; it is only queried when listed explicitly in `scope = ["vendor"]`.

#### Scenario: Vendor scope opt-out
- **WHEN** `find_references(symbol = "AuthService", scope = "*")` runs against a config with `frontend, backend, vendor (isolated)`
- **THEN** results come from `frontend` and `backend` only; rows from `vendor` are excluded unless `scope` explicitly includes `"vendor"`

### Requirement: Degraded scope doesn't crash the host
If a scope's initial index fails (workspace error, missing solution, etc.), the registry SHALL mark that scope as `degraded`; queries against it return an empty result with a status note, while every other scope continues to serve.

#### Scenario: Bad solution path
- **WHEN** `.sourcegraph.json` lists a `tools.slnx` that fails to load
- **THEN** `list_scopes` reports `tools` with `status: degraded` and an error message; queries with `scope = "tools"` return `"scope is degraded: <error>"`; queries with `scope = "*"` succeed against the healthy scopes

### Requirement: list_scopes tool
The server SHALL expose a `list_scopes` tool that returns each scope's id, name, root, project count, last-indexed timestamp, isolation flag, and status.

#### Scenario: Discover available scopes
- **WHEN** the agent invokes `list_scopes()`
- **THEN** the response is a markdown table with one row per registered scope
