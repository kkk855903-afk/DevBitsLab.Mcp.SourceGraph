## ADDED Requirements

### Requirement: Partial scope reports per-project failures
A scope whose cold-index completed and produced symbols for at least one project, but where one or more projects or files failed, SHALL be marked with status `partial`. A partial scope SHALL be queryable: tools targeting `scope = "<id>"` against a partial scope SHALL return whatever symbols were indexed (best-effort); `scope = "*"` fan-out SHALL include partial scopes alongside `ok` scopes (excluding only `indexing` and `degraded`). The registry SHALL persist the failure lists alongside the scope row so `list_scopes` returns accurate failure detail even after a server restart that hasn't yet re-triggered indexing.

A scope status SHALL be:
- `ok` — every project and file indexed cleanly; `failed_projects` and `failed_files` are empty
- `indexing` — cold index in progress
- `partial` — at least one project produced symbols and at least one project or file failed; `failed_projects` and/or `failed_files` are non-empty
- `degraded` — workspace failed to open, OR every project failed (zero files indexed), OR an unanticipated exception escaped to the scope-level safety net; tools return `"scope is degraded: <error>"`

#### Scenario: Solution with one bad project lands `partial`, not `degraded`
- **GIVEN** a solution containing two projects where one fails to compile
- **WHEN** `LiveIndexService` cold-indexes the scope
- **THEN** the scope's status is `partial` (not `degraded`); `list_scopes` reports `failed_projects` containing the failed project's name and reason; tools targeting the scope return symbols from the working project; `scope = "*"` fan-out includes this scope's results

#### Scenario: Partial-scope failure lists survive restart
- **GIVEN** a scope previously cold-indexed to `partial` status with one entry in `failed_projects`
- **WHEN** the server is restarted and `list_scopes` is invoked before any re-index runs
- **THEN** the partial status, the failed project's name, and the reason are returned from the persisted registry row — operators see accurate failure detail without waiting for a re-index

#### Scenario: All-projects-fail scope is `degraded`, not `partial`
- **GIVEN** a solution where every project's compilation fails
- **WHEN** `LiveIndexService` cold-indexes the scope
- **THEN** the scope's status is `degraded` (because zero files were indexed); `failed_projects` enumerates every project; tools targeting the scope return the existing degraded-scope error message; `scope = "*"` excludes this scope as today

## MODIFIED Requirements

### Requirement: Degraded scope doesn't crash the host
If a scope's initial index fails with no recoverable output (workspace error, missing solution, every project failed to compile, or an unanticipated exception escaped to the scope-level safety net), the registry SHALL mark that scope as `degraded`; queries against it return an empty result with a status note, while every other scope continues to serve. A scope with at least one project that produced symbols SHALL be marked `partial` instead — `degraded` is reserved for the no-recoverable-output case.

#### Scenario: Bad solution path
- **WHEN** `.sourcegraph.json` lists a `tools.slnx` that fails to load
- **THEN** `list_scopes` reports `tools` with `status: degraded` and an error message; queries with `scope = "tools"` return `"scope is degraded: <error>"`; queries with `scope = "*"` succeed against the healthy scopes

#### Scenario: Boundary between degraded and partial
- **GIVEN** a solution that opens successfully but where every project's compilation fails
- **WHEN** `LiveIndexService` cold-indexes the scope
- **THEN** the scope is `degraded` (not `partial`) because zero files were indexed; the `failed_projects` list still enumerates every project so operators see why every project failed

### Requirement: list_scopes tool
The server SHALL expose a `list_scopes` tool that returns each scope's id, name, root, project count, last-indexed timestamp, isolation flag, status, and (when non-empty) the lists of failed projects and failed files.

The structured output schema SHALL include:
- `failed_projects: { name: string, reason: string }[]` — projects whose compilation could not be obtained during the most recent cold index
- `failed_files: { path: string, reason: string }[]` — files whose Pass 1 walk threw during the most recent cold index

Both arrays SHALL be omitted when empty (or rendered as empty arrays — the JSON shape is consistent), so healthy scopes' output is unchanged from the prior contract. The markdown rendering SHALL surface the failure detail (e.g., as a sub-list under the affected scope's row) when the arrays are non-empty so operators reading the human-friendly output see the failure attribution without needing to inspect `structuredContent`.

#### Scenario: Discover available scopes
- **WHEN** the agent invokes `list_scopes()`
- **THEN** the response is a markdown table with one row per registered scope; healthy scopes show only id, name, root, project count, last-indexed timestamp, isolation flag, and `status: ok` — the failure-list columns are suppressed

#### Scenario: List a partial scope
- **GIVEN** a scope `backend` with `status: partial` whose `failed_projects` contains `Legacy.WebForms` (reason: `compilation null`)
- **WHEN** `list_scopes` is invoked
- **THEN** the markdown row for `backend` shows `status: partial` and a sub-list (or column) carrying `Legacy.WebForms — compilation null`; the `structuredContent.failed_projects` array contains exactly one entry with `name: "Legacy.WebForms"` and a non-empty `reason`; `failed_files` is empty
