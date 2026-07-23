## ADDED Requirements

### Requirement: Scope-management subcommands
The CLI SHALL accept `sourcegraph-mcp scopes list`, `sourcegraph-mcp scopes add <name> ...`, and `sourcegraph-mcp scopes remove <name>` to inspect and edit `.sourcegraph.json`.

#### Scenario: List scopes
- **WHEN** the user runs `sourcegraph-mcp scopes list` in a repo with three configured scopes
- **THEN** the command prints each scope's id, name, kind (solutions/projects/paths), isolation flag, and last-indexed timestamp

### Requirement: init-scopes scaffolder
The CLI SHALL accept `sourcegraph-mcp init-scopes` that discovers .slnx files at the repo root and writes a `.sourcegraph.json` listing one scope per discovered solution.

#### Scenario: Bootstrap from siblings
- **WHEN** the user runs `init-scopes` in a repo containing `frontend.slnx` and `backend.slnx` at the root
- **THEN** `.sourcegraph.json` is written with two scopes (`frontend`, `backend`), each pointing at its solution, and no `default_scope` is set
