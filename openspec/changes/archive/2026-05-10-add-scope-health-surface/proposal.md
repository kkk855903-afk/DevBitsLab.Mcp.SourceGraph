## Why

The server's failure-handling stance today is **containment, not repair**: a scope that breaks gets marked `degraded` in the registry and stays that way until process restart or a human edits config. That's defensible — silent auto-rebuild is worse than silent quarantine — but it leaves three observable gaps:

1. **Orphan DB files** — `scopes/<id>.db` files exist with no registry row (config edit removed scope) or vice versa (registry row exists but `.db` file was deleted). The system doesn't notice; first call against the orphan gets a cryptic SQLite error.
2. **Stuck `indexing` rows** — process killed (OOM, Ctrl-C, IDE crash) mid-cold-index leaves the registry row at `status='indexing'`. There's no signal on the next boot whether to retry, resume, or give up. Tools that block on `indexing` (`ScopedExecution.WaitForReadyAsync`) wait forever.
3. **No way to introspect health from the agent side** — the agent gets `degraded: <message>` from `list_scopes` and that's it. No row counts, no schema version, no integrity-check result, no drift estimate. The agent has to guess whether the index is fresh or stale, which is exactly the question users keep asking.

This change builds the *substrate* for self-healing: detection + a structured heal-event log + an introspection tool. It is intentionally **detection-only** for the failures it surfaces; it does not autonomously rebuild any scope. Phase 2 (`add-scope-repair-tools`) introduces agent-driven repair on top of this substrate; Phase 3 (`add-corruption-recovery`) adds reactive autonomous recovery for the unambiguous case (SQLITE_CORRUPT).

The substrate-first design means every later heal action — autonomous or agent-driven — writes to the same JSONL log and emits the same metric, so the user can answer "has self-healing fired in the last hour?" without reading source.

## What Changes

- **Boot-time orphan reconciliation** in the scope-host bring-up path. After loading `.sourcegraph.json` and listing `scopes/*.db` files, the host cross-references the three sets (config scopes, registry rows, DB files) and:
  - Archives orphan DB files (file present, no registry row) to `<repo>/.sourcegraph/orphans/<id>-<utc-iso>.db`. The file is moved, not deleted; users can inspect it.
  - Marks scopes with missing DB files (registry row present, file absent) as `degraded` with status_message `"scope DB file missing — call repair_scope or restart"`. No autonomous rebuild.
  - Treats config-only scopes (no registry row, no file) as fresh: the existing cold-index path runs (current behavior, unchanged).

- **Boot-time stuck-`indexing` detection**. Any registry row with `status='indexing'` whose `last_indexed_at` is older than the process start time is treated as a previous-process casualty: marked `degraded` with status_message `"previous index interrupted — call repair_scope or restart"`. No autonomous rebuild.

- **`verify_scope` MCP tool** (read-only). Args: `scope` (string id or `"*"`). Returns markdown + `structuredContent` carrying:
  - `schema_version` — `Schema.Version` and `Views.SchemaVersion`
  - `status` — `ok` / `degraded` / `indexing` plus `status_message`
  - `last_indexed_at` — ISO-8601
  - `row_counts` — `symbols`, `refs`, `edges`, `files`, `annotations`, `diagnostics`
  - `integrity_check` — result of `PRAGMA integrity_check` (`"ok"` or first failure line)
  - `drift_sample` — out of N=20 random files, count whose disk `content_sha256` differs from the DB's stored value. Surfaces watcher-was-offline drift without the cost of a full reconciliation.
  Doesn't mutate state. Doesn't trigger any heal action. Pure read.

- **Heal-event JSONL log** at `<repo>/.sourcegraph/heals.jsonl`. Every heal-related action (orphan archive, missing-DB detection, stuck-indexing detection — and, in Phase 2/3, every other heal kind) appends a line of shape `{"ts":"…","kind":"…","scope":"…","ok":true|false,"ms":…,"details":"…"}`. Best-effort: write failures are swallowed and never surface to the agent (matches the existing `usage.jsonl` contract).

- **`sourcegraph.heal.fired` Counter on the existing `Meter`** (`DevBitsLab.Mcp.SourceGraph`). Tags: `kind` (string, the heal kind), `scope` (string, scope id), `ok` (bool, whether the heal action succeeded). Surfaced through any OTel pipeline scraping the Meter; zero cost when no listener subscribes.

- **`HealLog` static helper** in the observability folder, mirroring the existing `ToolMetrics.Configure` / append pattern. One call site per heal kind.

## Capabilities

### New Capabilities
<!-- None — heal events live under existing `observability`; detection lives under existing `scoping`; the new tool extends `mcp-tools`. -->

### Modified Capabilities

- `scoping`: Adds two requirements — `Boot-time orphan DB reconciliation` and `Boot-time stuck-indexing detection`. The existing `Degraded scope doesn't crash the host` requirement is unchanged; these new requirements bolt on the *detection* layer above it.
- `observability`: Adds two requirements — `Persistent heal-event JSONL log` and `OpenTelemetry Counter for heal events`. Coexists with the existing `usage.jsonl` and `sourcegraph.tool.*` instruments unchanged.
- `mcp-tools`: Adds one requirement — `verify_scope read-only health snapshot tool`.

## Impact

- **Code (small-medium)**:
  - `Server/Scoping/ScopeHost.cs` (~40 lines): add `ReconcileOnBootAsync` called from `ExecuteAsync` before per-scope bring-up; emits heal events for every action taken.
  - `Server/Observability/HealLog.cs` (new, ~80 lines): mirrors `ToolMetrics`. `Configure(path)` + `Append(kind, scope, ok, ms, details)`. Owns the JSONL append + Counter increment.
  - `Server/Observability/Telemetry.cs` (modify): add `HealCounter` instrument alongside the existing four tool instruments.
  - `Server/Tools/ScopeTools.cs` (~120 lines added): `VerifyScopeAsync` tool body + result type. Reuses the existing `ScopedExecution` pattern.
  - `Server/Program.cs` (~5 lines): wire `HealLog.Configure(<.sourcegraph/heals.jsonl>)` next to the existing `ToolMetrics.Configure` call.
  - `Storage/SqliteGraphStore.cs` (~20 lines): a `RowCountsAsync()` and `IntegrityCheckAsync()` method exposed on `IGraphStore` for `verify_scope` to consume.
- **Spec**: 5 new requirements across 3 capabilities (counts above), each with 2-4 scenarios.
- **Tests**:
  - `ScopeReconciliationOnBootTests.cs` — three boot scenarios (orphan DB, missing DB, stuck `indexing` row), assertions on registry state and heals.jsonl content.
  - `HealLogTests.cs` — JSONL line shape, monotone append, write-failure swallow.
  - `VerifyScopeToolTests.cs` — happy path, degraded scope path, drift sample correctness against a fixture.
- **Public API**: One new MCP tool (`verify_scope`). One new metric instrument. One new file path (`heals.jsonl`).
- **Backward compatibility**: Pure additive. Existing scopes that already have matching registry rows + DB files take the no-op path through reconciliation. The heal log and metric are zero-cost when no listener subscribes; the JSONL file is created lazily on first heal event.
- **Documentation**: README + CLAUDE.md gain a paragraph about scope health (`verify_scope`, `heals.jsonl`).
