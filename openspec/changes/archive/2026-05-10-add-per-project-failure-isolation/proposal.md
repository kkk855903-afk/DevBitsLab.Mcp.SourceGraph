## Why

Today a single bad project in a multi-project solution can take down an entire scope's index. The Roslyn indexer's failure-isolation surface is uneven:

- **Pass 1B has no per-document try/catch.** The inner foreach over each changed file's docs at [RoslynIndexer.cs:474-546](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:474-546) calls `GetSyntaxTreeAsync`, `GetSemanticModelAsync`, `GetDeclaredSymbol`, and `_store.UpsertSymbolAsync` bare. A throw from any of these unwinds `IndexCoreAsync` entirely. The scope-level catch in `LiveIndexService.RunInitialIndexAsync` then marks the *whole scope* `degraded` — every other project's symbols are lost.
- **Pass 2 and Pass 3 already have per-file/per-project catches.** The gap is Pass 1.
- **`MSBuildWorkspace.OpenSolutionAsync` correctly logs project-load failures** as `WorkspaceDiagnosticKind.Failure` events without throwing, but a project that loaded "partially" can still throw downstream during semantic-model construction or symbol upsert.

The user-facing surface today is binary: `ok | indexing | degraded`. There is no way for a scope to say "I worked, except for these three projects." Operators querying `list_scopes` see `ok` even when 9/10 projects silently failed (under-reported partial loss) or `degraded` even when 1/10 projects failed (over-reported total loss). Neither tells the truth; a real-world solution with one quirky project (legacy MSBuild quirks, missing source generator NuGet, in-progress migration, MSBuild evaluation warnings that escalate to throws) gets either too quiet or too loud.

## What Changes

- **Pre-flight project compilation probe.** Before Pass 1, the indexer SHALL call `Project.GetCompilationAsync` on every C# project in the solution. Projects that throw or return `null` are recorded in a `FailedProjects` set and their documents are removed from the index pass. One log line per failed project; no Pass 1/2/3 attempts on those documents.
- **Per-document try/catch in Pass 1B.** The inner foreach over each changed file's docs SHALL be wrapped to mirror Pass 2's existing pattern. `OperationCanceledException` propagates; other exceptions are logged at warn level with the file path, the file is added to a `FailedFiles` set, and the loop continues with the next file.
- **Reconcile gating.** Pass 1C's `DeleteSymbolsForFileNotInAsync`, Pass 1D's annotation insert, Pass 1E's test-framework flush, and Pass 2/3's per-file work SHALL skip files in `FailedFiles`. This preserves their prior (successful) state instead of corrupting it with a partial walk's incomplete `fileKeys`.
- **`IndexResult` extended** with `FailedProjects` and `FailedFiles` arrays — each entry carries name/path + a short reason string captured from the underlying exception's `Message`.
- **New scope status `partial`.** `LiveIndexService` reads `IndexResult.FailedProjects` / `FailedFiles` after cold-index. If both are empty → `ok`. If at least one project produced symbols but anything failed → `partial`. If zero projects produced symbols (or the workspace itself failed to open) → `degraded`.
- **`list_scopes` reports the failure detail.** New optional fields `failed_projects: [{ name, reason }]` and `failed_files: [{ path, reason }]` on the `list_scopes` markdown row + structured output. Suppressed when empty (zero-cost for healthy scopes).
- **Per-scope persistence.** Failure lists are stored alongside the existing scope row in `_meta.db` so `list_scopes` doesn't depend on the indexer being live.
- **Tools fan-out behaviour.** `scope = "*"` SHALL include `partial` scopes (best-effort results); `degraded` scopes are excluded as today. Per-tool `_partial` warnings on responses are deferred to a follow-up.

## Capabilities

### New Capabilities
<!-- None — every change refines an existing capability. -->

### Modified Capabilities

- `indexing` — gains two new fault-tolerance requirements (per-project probe, per-document Pass 1 isolation) and one modified requirement on `Cold index of a solution` to acknowledge partial outcomes.
- `scoping` — gains a `partial` status alongside the existing three; `Degraded scope doesn't crash the host` is modified to clarify the boundary between `partial` and `degraded`.
- `mcp-tools` — `list_scopes` output schema gains optional `failed_projects` and `failed_files` arrays.

## Impact

- **Code (medium)**: ~150 lines net. Pre-flight probe (~30 LOC), Pass 1B catch + reconcile gating (~50 LOC), `IndexResult` shape (~10 LOC), `LiveIndexService` status handling (~25 LOC), `list_scopes` output shape (~20 LOC), `_meta.db` schema migration for failure persistence (~15 LOC).
- **Spec**: Three capability files modified; six new scenarios across indexing/scoping/mcp-tools.
- **Tests**: New multi-project fixture under `tests/fixtures/PartialFailure/` with one deliberately broken project (e.g., an unresolvable `<PackageReference>` or a missing TFM target). Three test classes: `PartialIndexResultTests`, `PartialScopeStatusTests`, `ListScopesPartialOutputTests` (see tasks.md §6).
- **Performance**: The pre-flight probe runs `GetCompilationAsync` once per project. For a 50-project solution that's ~5–15s of additional cold-index time before Pass 1 begins. The work is amortised — Pass 1 was going to call `GetCompilationAsync` per document anyway via `GetSemanticModelAsync`, so the probe just shifts the cost forward. Steady-state and incremental-index cost is unchanged.
- **Backwards compatibility**: Pure additive on `IndexResult` (new fields default to empty). `list_scopes` output shape is additive (new fields, existing fields unchanged). Existing `ok` / `indexing` / `degraded` semantics are preserved; `partial` is a new state that only appears when failures occur. Existing clients that switch on `status` exhaustively will need to handle `partial` — documented in the migration plan.
- **Recovery**: Operators currently hit by this bug don't need any action — the next `serve` start automatically applies the new behaviour. Failed projects/files reported via `list_scopes` give them the visibility they need to investigate.
- **Out of scope**: Per-tool partial-scope warnings on responses (e.g., a `_partial` field on `find_references` results when the queried scope is partial). Defer to a follow-up; users can check `list_scopes` for now. Diagnosing root causes of project-load failures is also out of scope — the change is defensive, making the indexer self-correct rather than identifying which kinds of project quirks cause throws.
