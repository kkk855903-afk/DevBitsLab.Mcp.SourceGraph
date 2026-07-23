## ADDED Requirements

### Requirement: Per-project compilation failure isolation
The indexer SHALL probe each C# project's `Compilation` once before Pass 1 begins. Projects whose `GetCompilationAsync` throws or returns `null` SHALL be recorded as `ProjectFailure(name, reason)` entries in `IndexResult.FailedProjects` and their documents SHALL be excluded from Pass 1, Pass 2, and Pass 3. The indexer SHALL emit one warn-level log entry per failed project; subsequent passes SHALL NOT re-attempt the project's documents in the same indexing pass.

The probe's per-project scope is the unit of attribution: a single project failure produces one `ProjectFailure` entry rather than N per-document failures, even if the failure would surface as a throw on every document's semantic-model construction.

#### Scenario: One project's compilation throws; other projects index cleanly
- **GIVEN** a solution with two C# projects: `Good` (compiles cleanly) and `Broken` (whose `GetCompilationAsync` throws because of an unresolvable `<PackageReference>`)
- **WHEN** `IndexAllAsync` runs against the solution
- **THEN** `IndexResult.FailedProjects` contains exactly one entry whose `Name` is `Broken` and whose `Reason` is the truncated exception message; `IndexResult.FailedFiles` is empty; the store contains symbols for `Good`'s files; the store contains zero rows whose `file_id` resolves to a path under `Broken/`

#### Scenario: Probe is cancelled
- **WHEN** the supplied `CancellationToken` is signaled while `ProbeProjectCompilationsAsync` is mid-iteration
- **THEN** the indexer rethrows `OperationCanceledException` so cancellation surfaces to the caller; no partial `IndexResult` is returned

#### Scenario: All projects fail to compile
- **GIVEN** a solution where every project's `GetCompilationAsync` throws
- **WHEN** `IndexAllAsync` runs
- **THEN** `IndexResult.FailedProjects` lists every project, `IndexResult.FilesIndexed` is `0`, and the result is returned successfully (the indexer does not throw); the calling layer (`LiveIndexService`) is responsible for translating "zero files indexed but failures present" into the `degraded` scope status

### Requirement: Per-document failure isolation in Pass 1
The indexer SHALL wrap each per-changed-file body of Pass 1's symbol-walk loop in try/catch so that an exception walking one file does not abort Pass 1 for the remaining files. Cancellation (`OperationCanceledException`) SHALL still propagate. Other exceptions SHALL be logged at warn level with the file path; the file SHALL be added to `IndexResult.FailedFiles` as `FileFailure(path, reason)` and SHALL be excluded from Pass 1's reconcile (`DeleteSymbolsForFileNotInAsync`), Pass 1's annotation insert, Pass 1's test-framework flush, Pass 2, and Pass 3.

A file in `FailedFiles` SHALL retain its prior store state (symbols, refs, edges, annotations, diagnostics) untouched until the next indexing pass. The indexer SHALL NOT delete-then-fail-to-repopulate any file's data — partial walks SHALL leave the prior good state intact.

#### Scenario: One file's Pass 1 walk throws; other files complete
- **GIVEN** a Pass-1 batch of three changed files where the second file's `GetSemanticModelAsync` (or any subsequent call inside the per-file walk) throws
- **WHEN** Pass 1 iterates the three files
- **THEN** the first and third files are walked, their symbols upserted, and they appear in `walkedFileIds`; the second file is logged at warn level with its path, added to `FailedFiles`, and absent from `walkedFileIds`; reconcile (`DeleteSymbolsForFileNotInAsync`) is NOT called for the second file; Pass 2 walks the first and third files but skips the second; the indexing pass returns successfully

#### Scenario: Failed file preserves prior state
- **GIVEN** a file `F` that successfully indexed in a prior pass (symbols, refs, and edges present in the store) and whose Pass 1 walk now throws on a re-index
- **WHEN** Pass 1 catches the exception and skips reconcile for `F`
- **THEN** `F`'s prior symbols, refs, and edges remain in the store; `find_definition` and `find_references` against symbols in `F` continue to return the prior results until the next successful Pass-1 walk reconciles fresh state

#### Scenario: Cancellation propagates from Pass 1
- **WHEN** Pass 1 is iterating files and the supplied `CancellationToken` is signaled, raising `OperationCanceledException` in a per-file body
- **THEN** the catch handler rethrows so the cancellation surfaces to the caller; partial state from earlier files in the batch is left as-is (consistent with Pass 2's existing semantics)

## MODIFIED Requirements

### Requirement: Cold index of a solution
The indexer SHALL dispatch each indexable document to a registered
`ILanguageIndexer` matching the document's file extension; the built-in
`RoslynLanguageIndexer` is registered automatically for `.cs`. The indexer
SHALL skip documents belonging to projects whose `Compilation` could not be
obtained (per the per-project failure isolation requirement); skipped
projects SHALL be reported in `IndexResult.FailedProjects` rather than
causing the cold index to throw. The cold index SHALL return successfully
even when one or more projects or files failed; the calling layer is
responsible for translating the failure lists into a scope status.

#### Scenario: Index a fresh solution end-to-end
- **WHEN** `sourcegraph-mcp index <solution>` is invoked against a solution
  whose graph DB is empty or absent
- **THEN** every regular `.cs` document with `File.Exists(path) == true`
  whose owning project compiled successfully is dispatched to
  `RoslynLanguageIndexer`, plus any document whose extension matches a
  third-party `ILanguageIndexer`; an `IndexResult` is returned with the
  per-language file counts merged and any failed projects/files attributed

#### Scenario: Document with no matching language indexer
- **WHEN** the workspace contains a file whose extension has no registered
  `ILanguageIndexer`
- **THEN** the file is skipped with a debug log and no error

#### Scenario: Document in a failed project is not dispatched
- **GIVEN** a solution containing a project whose compilation could not be obtained
- **WHEN** the cold index runs
- **THEN** none of that project's documents are dispatched to any
  `ILanguageIndexer`; the project is recorded in `IndexResult.FailedProjects`;
  the cold index returns successfully so other projects' documents are
  indexed normally
