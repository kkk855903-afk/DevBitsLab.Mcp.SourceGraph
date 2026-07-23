## Context

The Roslyn incremental indexer is multi-pass:

1. **Pass 1A** ([RoslynIndexer.cs:332-444](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:332-444)): SHA scan; identify changed files; clear their outgoing refs/edges; build `docsByChangedFile`.
2. **Pass 1B** ([RoslynIndexer.cs:465-547](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:465-547)): walk every changed file's docs; upsert symbols; collect `fileKeys[fileId]`, `pendingAttrs`, `testFrameworkBySymbolId`, `parentKeyByChildKey`.
3. **Pass 1C** ([RoslynIndexer.cs:553-566](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:553-566)): `DeleteSymbolsForFileNotInAsync(fileId, fileKeys)` — reconcile each changed file's symbol set against what was just walked.
4. **Pass 1D** ([RoslynIndexer.cs:594-599](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:594-599)): bulk-insert annotations now that `_symbolIdByKey` is fully populated.
5. **Pass 1E** ([RoslynIndexer.cs:605-609](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:605-609)): flush detected `test_framework` values.
6. **Pass 2** ([RoslynIndexer.cs:620-849](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:620-849)): per-document reference + edge walk. **Already wrapped in try/catch.**
7. **Pass 3** ([RoslynIndexer.cs:861-951](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:861-951)): per-project diagnostics persistence. **Already wrapped per project + per diagnostic.**

The scope-level catch in [LiveIndexService.cs:326-333](src/DevBitsLab.Mcp.SourceGraph.Server/LiveIndexService.cs:326-333) is the safety net of last resort: any uncaught throw inside `IndexCoreAsync` lands here, marks the scope `degraded`, and persists the message. Scope-level isolation is intact — but Pass 1B's bare loop means a single quirky project triggers the safety net for the entire scope.

The user-facing scope-status surface today is `ok | indexing | degraded`, defined in `openspec/specs/scoping/spec.md`. There is no third state for "scope worked, but with caveats." Real-world solutions almost always have *something* quirky — a legacy project, a vendored library with missing source generators, a project mid-migration to a new TFM — and operators currently see those as either silent success or total failure.

## Goals / Non-Goals

**Goals:**

- **A single bad project no longer takes down its scope.** The indexer SHALL survive both project-level and document-level failures and produce a usable graph for the surviving projects' files.
- **Failures are visible.** `list_scopes` SHALL report a `partial` status with a `failed_projects` / `failed_files` summary so operators can see which projects' symbols are missing and why.
- **No silent corruption.** A file whose Pass 1B walk threw mid-way SHALL NOT have its prior (successful) symbol state reconciled away by Pass 1C, since `fileKeys` for it is incomplete. Failed files keep their last-known-good store state until the next index.
- **Healthy installs see no behavioural change.** When every project's compilation succeeds and every Pass 1B walk completes, the new code paths are no-ops — empty `FailedProjects` / `FailedFiles` arrays, scope status stays `ok`.

**Non-Goals:**

- **Per-tool partial-scope warnings.** A `_partial` field on every fan-out tool's response (so `find_references(scope = "*")` flags that some scopes were partial) is a coherent next step but expands the diff into every tool surface. Defer; users can check `list_scopes` for now.
- **Root-cause diagnosis of project-load throws.** Knowing *why* a specific project's compilation fails is left to the operator (the failure `reason` field shows the exception message). The change is defensive, not investigative.
- **Per-symbol failure granularity.** A single document might have one failing symbol mid-walk; treating each symbol as a failure unit would explode complexity for marginal benefit. The file is the right granularity — same as Pass 2.
- **Restructuring `LiveIndexService`'s scope-level catch.** The outermost `catch (Exception ex)` at [LiveIndexService.cs:326-333](src/DevBitsLab.Mcp.SourceGraph.Server/LiveIndexService.cs:326-333) stays as the safety net for unanticipated throws (e.g., the workspace itself fails to open). Only the inner Pass 1B failures are now isolated below it.

## Decisions

### Decision 1 — Pre-flight project compilation probe (vs lazy probe)

Before Pass 1A begins, the indexer iterates every C# project once and asks for its `Compilation`:

```csharp
private async Task<(IReadOnlyDictionary<ProjectId, Compilation> ok,
                   IReadOnlyList<ProjectFailure> failed)>
    ProbeProjectCompilationsAsync(CancellationToken ct)
{
    var ok = new Dictionary<ProjectId, Compilation>();
    var failed = new List<ProjectFailure>();
    foreach (var project in _workspace!.CurrentSolution.Projects
                 .Where(p => p.Language == LanguageNames.CSharp))
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                _logger.LogWarning("Project {Project} returned null compilation; skipping", project.Name);
                failed.Add(new ProjectFailure(project.Name, "compilation null"));
                continue;
            }
            ok[project.Id] = compilation;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Project {Project} compilation threw; skipping", project.Name);
            failed.Add(new ProjectFailure(project.Name, Truncate(ex.Message)));
        }
    }
    return (ok, failed);
}
```

`AllCSharpDocumentsAsync` then filters out documents whose `Project.Id` isn't in `ok`. The same filter applies to `GetSourceGeneratedDocumentsAsync` calls.

**Rationale:** A single pre-flight pass produces one log line per failed project and one entry in `FailedProjects`. Lazy probing (catching `GetSemanticModelAsync` per document) would emit N log lines for the same project failure (one per file in the project) and inflate `FailedFiles` with what is really a project-level issue. Operators want "Legacy.WebForms.csproj failed to compile," not "Legacy.WebForms.csproj/A.cs, /B.cs, /C.cs … all threw."

**Alternatives considered:**
- *Lazy probe*: only catch at the document level. Simpler implementation but loses the project attribution.
- *MSBuildWorkspace's `WorkspaceFailed` events as the source of truth*: those fire on workspace evaluation, not on `GetCompilationAsync`. A project that evaluated cleanly can still fail to produce a compilation (e.g., generator throws during compilation construction). Insufficient.

**Cost:** `GetCompilationAsync` is the most expensive call in the indexing pipeline. The probe shifts the cost forward; it doesn't add net work, since Pass 1B was going to call `GetSemanticModelAsync` (which lazily builds the same Compilation) per document anyway. Roslyn caches Compilation per project for the lifetime of the workspace.

### Decision 2 — Per-document Pass 1B catch + `walkedFileIds` set

Pass 1B's outer `foreach (var (fileId, docs) in docsByChangedFile)` becomes:

```csharp
var walkedFileIds = new HashSet<long>();
var failedFiles = new List<FileFailure>();

foreach (var (fileId, docs) in docsByChangedFile)
{
    var path = _fileIdByPath.FirstOrDefault(kv => kv.Value == fileId).Key ?? "<unknown>";
    var fileKeys = new HashSet<string>(StringComparer.Ordinal);
    var pendingAttrs = new List<PendingAnnotation>();
    var attrSeen = new HashSet<string>(StringComparer.Ordinal);

    try
    {
        foreach (var document in docs)
        {
            // ... existing per-doc walk: GetSyntaxTreeAsync, GetSemanticModelAsync,
            // EnumerateDeclarations, UpsertSymbolAsync, AppendAnnotations ...
        }
        walkedFileIds.Add(fileId);
        newKeysForFile[fileId] = fileKeys;
        pendingAttrsByFile[fileId] = pendingAttrs;
        seenSymbolForAttr[fileId] = attrSeen;
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        _logger.LogWarning(ex,
            "Pass 1 walk failed for {Path}; preserving prior symbol state, file will be re-attempted on the next index",
            path);
        failedFiles.Add(new FileFailure(path, Truncate(ex.Message)));
        // Deliberately DO NOT add to walkedFileIds, newKeysForFile, pendingAttrsByFile.
    }
}
```

**The `walkedFileIds` set is the gate** for every downstream pass's per-file work:

- **Pass 1C** iterates `newKeysForFile` (already only contains successfully walked files) — no behavioural change needed if we only populate it in the success branch (above).
- **Pass 1D** iterates `pendingAttrsByFile` — same, only populated on success.
- **Pass 1E**'s test-framework flush is symbol-keyed; failed-file symbols never made it into `_symbolIdByKey` so they can't appear here.
- **Pass 2** filters `docsToIndexRefs`: `docsByChangedFile.Values.Where(list => walkedFileIds.Contains(list[0].FileId)).Select(list => list[0])`.
- **Pass 3** is project-keyed; failed projects are already excluded by the pre-flight probe. For Pass 3's per-file diagnostic bucketing, the `if (!changedFileIds.Contains(fileId)) continue` check at [RoslynIndexer.cs:909](src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs:909) is extended to also require `walkedFileIds.Contains(fileId)` — diagnostics for files we couldn't walk shouldn't reconcile against the file's diagnostic table either.

**Rationale:** Without this gate, Pass 1C's `DeleteSymbolsForFileNotInAsync(fileId, /*incomplete*/ fileKeys)` would corrupt the store for failed files. The next index re-attempts the file; if Pass 1B succeeds the second time, normal reconcile happens and the state is correct. If it fails again, the prior good state stays.

**Note on partial in-walk commits:** Even within a single document's walk, `UpsertSymbolAsync` commits per-symbol. If `GetDeclaredSymbol` succeeds for symbols A, B, C, then throws on D, then symbols A/B/C are already in the store but `fileKeys` is `{A, B, C}` (D isn't there). Excluding the file from reconcile means the store keeps A/B/C (and any prior D from the previous successful index). On next index: re-walk; if it succeeds end-to-end, reconcile drops anything no longer present. Acceptable.

### Decision 3 — Status semantics: `partial` vs `degraded`

`LiveIndexService.RunInitialIndexAsync` post-cold-index logic:

```csharp
var initial = await host.Indexer.IndexAllAsync(ct).ConfigureAwait(false);

string status;
if (initial.FilesIndexed == 0 && (initial.FailedProjects.Count > 0 || initial.FailedFiles.Count > 0))
{
    // Workspace opened, but every project / file failed. Effectively dead.
    status = "degraded";
}
else if (initial.FailedProjects.Count > 0 || initial.FailedFiles.Count > 0)
{
    // At least one project produced symbols, but some failed.
    status = "partial";
}
else
{
    // Clean run.
    status = "ok";
}
host.Status = status;
host.FailedProjects = initial.FailedProjects;
host.FailedFiles = initial.FailedFiles;
```

The pre-existing `degraded` arms (catch-all for thrown exceptions, "no resolvable solution") stay intact; they're hit when the workspace itself fails to open.

**Rationale:** Three distinct user-facing meanings:
- `ok` — every project indexed cleanly. Tools return complete results.
- `partial` — at least one project's symbols are in the graph; some are missing. Tools return best-effort results from healthy projects.
- `degraded` — scope is unusable (workspace failed to open, every project failed, OR an unanticipated throw escaped to the safety net). Tools return `"scope is degraded: <error>"`.

`partial` is the new state. The boundary case "100% of projects failed but the workspace opened" lands on `degraded` because no symbols are queryable — practically indistinguishable from "workspace failed to open" from the user's perspective.

**Tools fan-out behaviour:** `scope = "*"` includes `ok` and `partial` scopes, excludes `indexing` (per existing semantics) and `degraded`. `scope = "<id>"` against a `partial` scope returns whatever symbols were indexed — clients are expected to consult `list_scopes` when they need to know if results may be incomplete.

### Decision 4 — Persist failures to `_meta.db`

`SqliteScopeRegistry`'s scope row gains two TEXT columns (or one JSON-blob column) for `failed_projects` and `failed_files`. The schema migration is forward-only (existing rows get empty arrays). On scope re-bring-up after restart, the registry returns the last-known failure list immediately; if the indexer hasn't re-run yet, `list_scopes` still shows accurate data.

**Schema sketch (option: separate columns):**
```sql
ALTER TABLE scopes ADD COLUMN failed_projects_json TEXT NOT NULL DEFAULT '[]';
ALTER TABLE scopes ADD COLUMN failed_files_json TEXT NOT NULL DEFAULT '[]';
```

**Rationale:** `list_scopes` is invoked while scopes are still indexing or degraded. Reading the failure list from the in-memory `ScopeHost` works for live scopes but loses the data on restart. Persisting to `_meta.db` is cheap, the JSON column avoids a new normalisation, and there's no querying against the failure list itself (it's display-only).

**Alternatives:**
- *Compute on demand from indexer state*: requires the indexer to be alive, which isn't true for `degraded` scopes after a server restart that doesn't re-trigger indexing.
- *Separate `scope_failures` table*: cleaner schema but no benefit for the current usage pattern (display-only, bounded list size).

### Decision 5 — Failure record shape

```csharp
public sealed record ProjectFailure(string Name, string Reason);
public sealed record FileFailure(string Path, string Reason);
```

`Reason` is the failing exception's `Message`, truncated to 256 chars (a constant in `IndexResult`). Stack traces are logged via `_logger.LogWarning(ex, …)` but not surfaced to clients. `Path` for `FileFailure` is the full file path (matches the path key in `_fileIdByPath`).

**Rationale:** Short, machine-friendly, no PII risk beyond what the source paths already expose (which `list_scopes` already shows in the `root` field). Truncation prevents pathological exception messages (e.g., a stack-trace-as-message) from blowing up `list_scopes` output. 256 chars is enough for "Could not find SDK version 'X' for project 'Y' targeting framework 'Z'" — the typical shape.

## Risks / Trade-offs

- **[Risk] Pre-flight probe doubles cold-index startup latency** for solutions where Roslyn's compilation cache is cold. Mitigation: the probe shares the cache with Pass 1's `GetSemanticModelAsync` calls — it's the same underlying Compilation. Net impact is "we pay cold-cache cost up-front instead of mid-pass-1." For a 50-project solution with ~10s per cold compilation, that's 8–10 minutes of compilation work either way, just front-loaded. If this becomes a real issue, parallelise the probe (`Task.WhenAll`) — out of scope for this change.

- **[Risk] `partial` status surprises clients that exhaustively switch on `status`.** Existing in-tree code switches on `ok`/`indexing`/`degraded`; adding `partial` could create an unhandled-state branch. Mitigation: search the codebase for `Status ==` and `switch (Status)` during implementation; map any missing branches to "treat partial as ok for query purposes" (see Decision 3's fan-out behaviour).

- **[Risk] `walkedFileIds` set drifts out of sync with the dictionaries it gates.** If a future change adds a new Pass-1-aligned dictionary (e.g., a new annotation kind) and forgets to gate it, failed files leak partial state. Mitigation: comment block at the Pass-1B catch handler enumerating every gated dict; tasks.md §2.5 adds a unit test that constructs a failing-Pass-1B scenario and asserts every dictionary is empty for the failed file.

- **[Risk] Reason strings leak sensitive paths or build secrets.** Exception messages from MSBuild can include full file paths, NuGet auth URLs, etc. Mitigation: 256-char truncation reduces but doesn't eliminate. The information is no more sensitive than what's already in the indexer's logs. Documented in the spec as a trade-off; out-of-scope to redact.

- **[Trade-off] No per-tool `_partial` warning.** Users querying a `partial` scope through `find_references` won't know results are incomplete unless they separately check `list_scopes`. Accepted for this change; deferred to a follow-up. The `list_scopes` markdown rendering makes the partial state easy to spot.

- **[Trade-off] Defensive vs root-cause fix.** This change makes the indexer self-correcting; it doesn't identify why specific projects fail. Accepted — the surface area of "every quirky thing that can break a project's MSBuild evaluation or compilation construction" is unbounded, and defensive correctness is more valuable than identifying the long tail.

## Migration Plan

This is a code-only change with one forward-only schema migration on `_meta.db`:

1. **Land core types first** — `ProjectFailure` / `FileFailure` records in `Core/`, `IndexResult` extended fields. CI green; no callers yet.
2. **Schema migration on `_meta.db`** — `ALTER TABLE scopes ADD COLUMN failed_projects_json …` and `failed_files_json …`. Storage layer's existing schema-version probe handles the upgrade on first open. CI green; columns exist but stay empty.
3. **Pre-flight probe in `RoslynIndexer.OpenAsync`/`IndexCoreAsync`** — `ProbeProjectCompilationsAsync` and document filtering. CI green; failed-project list populates but is otherwise unused.
4. **Pass 1B catch + `walkedFileIds` gate** — wrap inner foreach, gate downstream passes. Add a regression test that constructs a Pass-1B-throwing fixture and asserts surviving files index cleanly.
5. **`LiveIndexService` reads `IndexResult.FailedProjects` / `FailedFiles`** — set scope status to `partial` per Decision 3; persist to registry. Existing scope tests should still pass; add new `PartialScopeStatusTests`.
6. **`list_scopes` rendering + structured output** — extend the markdown row + JSON schema with the new optional fields. Update `ListScopesResult` shape; verify the existing `ListScopesTests` still pass and the new `ListScopesPartialOutputTests` passes.
7. **Documentation note in `README.md`** — under "Scopes (multi-solution monorepos)" or as a new "Partial indexing" subsection. Mention that the indexer self-isolates per-project failures and what `partial` status means.

**Rollback strategy:** Revert the per-step commits in reverse order. The schema migration's new columns are forward-only; rolling back leaves them unused — they're ignored by old code. No data loss; the failure lists are display-only.

## Open Questions

- **Should the probe run in parallel?** `Task.WhenAll` over `GetCompilationAsync` per project would reduce cold-start latency for large solutions, but Roslyn's MSBuildWorkspace serialises some MSBuild evaluation under the hood. Probably no win in practice. Defer; revisit if cold-start becomes a complaint.

- **Should `failed_projects` / `failed_files` carry a timestamp?** "When did this fail?" is useful for distinguishing transient from persistent failures. Adding `failed_at: DateTimeOffset` is one extra field per record. Probably yes, but a small follow-up; not blocking.

- **Should the `partial` state be explicitly visible in the `ScopeHost.Ready` task semantics?** Today `Ready` completes when status is `ok` or `degraded`; should `partial` be a third terminal state with a different semantic, or treated like `ok` (queries are allowed)? The latter is simpler and matches "tools fan-out behaviour" in Decision 3. Going with treat-like-`ok` for now.

- **Should Pass 1B failures count toward `partial` status, or only Pass 1A / pre-flight probe failures?** Currently both — any non-empty `FailedProjects` OR `FailedFiles` flips to `partial`. Strict reading: maybe only project-level failures should flip the status, since file-level failures are local. But in practice "10 files in 10 different projects all failed" is meaningfully partial. Sticking with "any failure → partial" for now.
