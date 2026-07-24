# Indexing

## Purpose

Provide a Roslyn-backed indexer that turns a .NET solution into a queryable
code graph (symbols, references, calls/inherits/implements edges) and keeps
its in-memory maps and on-disk store consistent across cold runs and live
edits.
## Requirements
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

### Requirement: Registered-language cold and live lifecycle
`LiveIndexService` SHALL derive its watched source-extension set from the
current `LanguageIndexerRegistry`; adding an `ILanguageIndexer` claim SHALL
NOT require a corresponding hard-coded watcher edit. `.cs` remains routed to
the workspace-aware Roslyn path. Every other registered extension SHALL be
discovered from the scope's resolved project set and routed through
`LanguageIndexerDispatcher` during cold indexing and for live
create/change/delete/rename batches. The same behavior SHALL apply when the
scope has no resolvable solution and when its solution resides below the
scope root.

For a `paths` scope, each configured glob SHALL select `.csproj` anchors using
the same matching rules as scope excludes; non-C# files are eligible only
below a matched project directory. For a `projects` scope, only directories
of explicit, existing, scope-approved `.csproj` anchors are eligible. If no
configured project can be resolved, discovery and watching SHALL fail closed
with no eligible source files. A `solutions` scope retains repository-wide
registered-language discovery so sibling native/protobuf sources that
intentionally participate in the solution's interop boundary are not hidden
merely because they have no project anchor.

Anchor discovery for a `paths` glob SHALL NOT traverse any directory reparse
point. A candidate project root reached through a symlink, junction, or other
directory reparse point SHALL be rejected even when its resolved target is an
in-repository sibling; the same rule applies when the target is outside the
repository.

Project factories SHALL be invoked only at the selected project directories
for `projects` and `paths` scopes; they SHALL NOT receive the repository root
or an unselected sibling as a discovery root. Discovery SHALL invoke only
`IExclusionAwareLanguageProjectFactory` implementations and SHALL pass both
rebased scope excludes and the mandatory privacy patterns so the factory can
prune before reading. Discovery SHALL build a temporary file-to-project map.
If any factory/root pair fails, the old live map and all file graphs SHALL be
retained, the failure SHALL be returned as `ProjectFailure`, cold/live scope
status SHALL surface it, and the one-shot command SHALL exit non-zero.
Factories SHALL propagate any non-cancellation directory enumeration, project
read, source read, or parse failure that makes discovery incomplete; an
incomplete result SHALL NOT be published as a successful replacement map or
resource snapshot. Excluded paths SHALL still be pruned before any such read.

A successful map SHALL be reused for ordinary source edits so heavyweight
language-project instances and their immutable caches are retained. Project
control events SHALL rebuild the map. A failed rebuild SHALL mark discovery
pending; the next ordinary source event SHALL retry discovery before dispatch
while the last successful map remains queryable.

Positive project membership SHALL require both lexical descent from a selected
project directory and physical descent from that same project's resolved
directory. Cold walkers SHALL prune unrelated directories and SHALL NOT follow
an in-project symlink or junction to an unselected sibling project.

Before reading or mutating a live path, the dispatcher SHALL apply the
scope's lexical excludes, mandatory privacy policy, repository containment,
and resolved physical-path containment. Invalid, inaccessible, excluded,
privacy-sensitive, and physically escaped paths SHALL fail closed. A path
confirmed missing SHALL be removed via the graph store's transactional file
deletion so the file row, declared symbols, inbound/outbound edges, and
occurrence evidence disappear together. Re-indexing an existing file SHALL
replace its file hash, outgoing facts, annotations, references, and complete
symbol set in one storage transaction even when the indexer returns zero
events. Removing declarations SHALL also sever surviving container and
attribute-symbol links and remove their history and embedding metadata.
Indexer, evidence-validation, canonical-key resolution, cancellation, and
storage failures SHALL leave the last successful file hash and graph intact.
If a stale-file deletion fails, that file SHALL produce a `FileFailure` while
deletion and indexing of other files continue. If a file disappears after cold
enumeration but before its byte read, it SHALL count as exactly one skip and
not as indexed or failed.

For registered non-C# files, each analyzer SHALL emit into its own private
event buffer. Only a successfully completed analyzer's events SHALL be combined
with the language indexer's events and committed in the same atomic file
replacement. A throwing or cooperatively timed-out analyzer SHALL lose all of
its partial events while later analyzers continue. Caller cancellation SHALL
propagate without marking the plugin failed. Cold and one-shot post-index
analyzer scans SHALL process only `.cs`, because registered non-C# analyzer
facts are already part of their atomic dispatcher replacement.
An analyzer that throws a bare or self-cancelled
`OperationCanceledException` while the caller token remains active SHALL be
treated as an analyzer failure, discard its private buffer, and allow later
analyzers to run. Only cancellation requested by the caller token SHALL
propagate out of the pipeline.

The watcher SHALL treat a scope-valid `.csproj` as a project-control event even
when no language indexer claims that extension. Create/change/delete SHALL
refresh the watcher's positive project matcher before subsequent source events,
rebuild the temporary project map, and run a full registered-language
reconciliation: project deletion removes the old graph and project creation
indexes the new subtree. A solution-backed scope SHALL additionally reload and
fully index its Roslyn workspace so compile items and cached non-membership are
reconciled. Roslyn reload and registered-language reconciliation SHALL be
independent failure channels: a non-cancellation failure in either SHALL remain
visible and leave the scope `partial`, but SHALL NOT prevent the other channel
from reconciling. Caller cancellation SHALL propagate and stop the second
channel.

Each dispatch result SHALL report successful replacements, replacements that
produced usable graph output, deletions, skips, and per-file failures
separately. A caught non-cancellation failure SHALL produce `FileFailure`
rather than count as indexed or usable. Cold scope status SHALL be `degraded`
when failures exist and neither Roslyn nor a registered language produced
usable graph output, `partial` when failures coexist with usable output, and
`ok` when there are no failures. The one-shot index command SHALL surface
registered-language failures and return a non-zero exit code.

On cold discovery failure, already persisted usable facts (symbols,
references, edges, or annotations) SHALL keep the scope queryable and
watchable with status `partial` and an explicit stale-graph/discovery-failure
message. File rows or diagnostics alone SHALL NOT count as a usable graph. If
neither the current pass nor the store contains usable facts, status SHALL be
`degraded`.

If a file's project implements
`IDeclarationFirstLanguageProject`, cold and live batches SHALL dispatch its
declared `DeclarationFilePaths` before consumer files; each priority group
SHALL use normalized-path order. The dispatcher SHALL still enumerate only
scope-approved files and SHALL NOT special-case a concrete language.

#### Scenario: Paths-only scope cold-indexes without a solution
- **GIVEN** a scope root containing `contracts/Contracts.csproj` and
  `contracts/service.proto`, a paths glob `contracts/**/*.csproj`, no `.sln`
  or `.slnx`, and a registered indexer claiming `.proto`
- **WHEN** `LiveIndexService` performs initial indexing
- **THEN** it skips only the Roslyn workspace phase, discovers the file from
  the matched project directory through `LanguageIndexerDispatcher`, persists its emitted
  graph facts, settles the scope to `ok`, and starts a watcher for `.proto`

#### Scenario: Paths scope cannot escape its matched projects
- **GIVEN** a paths scope whose glob matches `src/App/App.csproj`, plus a
  registered-language file at `vendor/Other.proto`
- **WHEN** cold discovery and live watcher events are processed
- **THEN** `vendor/Other.proto` is neither opened nor persisted

#### Scenario: Selected project links to an unselected sibling
- **GIVEN** only `src/App/App.csproj` is selected and
  `src/App/LinkedVendor` physically targets `src/Vendor`
- **WHEN** cold discovery walks registered-language sources
- **THEN** it does not traverse the link and opens no Vendor file

#### Scenario: Paths anchor is reachable only through a directory link
- **GIVEN** a paths scope selects `selected/**/*.csproj` and
  `selected/link` targets either an in-repository sibling or an outside
  directory containing a project
- **WHEN** anchor discovery evaluates the glob
- **THEN** neither linked project is selected, entered, opened, or watched

#### Scenario: Factory fails during a live edit
- **GIVEN** a scope has a last successful project map and graph
- **WHEN** one selected-root factory throws during the next discovery pass
- **THEN** the dispatcher returns a `ProjectFailure`, retains the old map and
  graph, and the live scope reports the failure

#### Scenario: Cold factory failure retains a usable stored graph
- **GIVEN** the graph store contains symbols, references, edges, or annotations
  from the last successful pass
- **WHEN** cold project discovery fails before producing a replacement map
- **THEN** the scope is `partial`, queryable, and watchable with an explicit
  stale-graph message; the map remains pending and the next ordinary source
  event retries discovery

#### Scenario: Cold factory failure has no usable stored facts
- **GIVEN** the store contains at most file rows or diagnostics
- **WHEN** cold project discovery fails without usable current-pass output
- **THEN** the scope is `degraded` and the unusable rows do not make it
  watchable

#### Scenario: Project anchor lifecycle refreshes membership
- **GIVEN** a paths scope selects `src/**/*.csproj`
- **WHEN** `src/New/New.csproj` is created and then a registered source below
  it changes
- **THEN** the control event refreshes the matcher first and the source is
  indexed; deleting the anchor later removes its stored file graph and blocks
  subsequent source events from that subtree

#### Scenario: Roslyn reload fails while a project anchor is deleted
- **GIVEN** a solution-backed scope with both C# and registered non-C# facts
- **WHEN** a `.csproj` delete event makes Roslyn reload throw
- **THEN** registered-language reconciliation still deletes stale non-C# facts,
  the Roslyn failure remains visible, and the scope is `partial`

#### Scenario: Solution below the scope root does not hide another language
- **GIVEN** a solution at `src/App.sln`, a registered `.cpp` indexer, and a
  native file at `native/bridge.cpp` under the same scope root
- **WHEN** cold indexing and live watching start
- **THEN** `native/bridge.cpp` is discovered and watched even though it is
  outside the solution directory

#### Scenario: Registered extension changes without watcher hard-coding
- **GIVEN** an `ILanguageIndexer` registered for a previously unknown
  extension `.foo`
- **WHEN** the scope watcher is constructed and `src/new.foo` is created or
  changed
- **THEN** `.foo` is present in the watch filters and the file is routed to
  that registered indexer through `LanguageIndexerDispatcher`

#### Scenario: Rename is delete plus create
- **GIVEN** `old.proto` has indexed symbols and edge evidence
- **WHEN** it is renamed to `new.proto`
- **THEN** the old path is transactionally deleted before the new path is
  indexed; no file, symbol, edge, or evidence owned by `old.proto` remains

#### Scenario: Empty successful result removes stale declarations
- **GIVEN** a registered-language file previously emitted symbols and edges
- **WHEN** a subsequent successful `IndexAsync` call returns zero events
- **THEN** the file row remains with its new content hash while all prior
  symbols, outgoing facts, and evidence for that file are removed, surviving
  cross-file container and attribute joins are severed, and metadata for the
  removed declarations is deleted

#### Scenario: Replacement fails after storage mutation begins
- **GIVEN** a registered-language file has a last successful hash and graph
- **WHEN** its next replacement encounters invalid evidence or another
  storage failure after the transaction has begun
- **THEN** the dispatch result contains a `FileFailure`, reports neither an
  indexed nor usable file, and the prior hash and complete graph remain
  unchanged

#### Scenario: Analyzer emits then times out
- **GIVEN** an analyzer emits a synthetic symbol and then cooperatively waits
  until its per-document token is cancelled, followed by another analyzer
- **WHEN** the first analyzer times out
- **THEN** none of its partial facts are committed, the later analyzer still
  commits with the language facts, and caller cancellation remains distinct
  from analyzer timeout

#### Scenario: Analyzer cancels itself without caller cancellation
- **GIVEN** an analyzer emits a synthetic symbol and then throws a bare
  `OperationCanceledException`, followed by another analyzer
- **WHEN** the caller token remains active
- **THEN** the first analyzer is reported failed, none of its partial facts are
  committed, and the later analyzer still commits; a requested caller token
  instead propagates and leaves analyzer state loaded

#### Scenario: Live path escapes through a directory link
- **GIVEN** `src/external` is a symlink or junction whose physical target is
  outside the scope root
- **WHEN** a watcher path below that link has a registered extension
- **THEN** the dispatcher rejects it before reading bytes or changing the
  graph

#### Scenario: Declaration path sorts after its consumer by name
- **GIVEN** one language project marks `ZResources.foo` as a declaration
  file and `AView.foo` as a consumer
- **WHEN** either cold discovery or one live batch contains both files
- **THEN** the dispatcher invokes the registered indexer for
  `ZResources.foo` first, then dispatches remaining files by normalized path

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
The indexer SHALL wrap each per-changed-file body of Pass 1's symbol-walk loop in try/catch so that an exception walking one file does not abort Pass 1 for the remaining files. Cancellation (`OperationCanceledException`) SHALL still propagate. Other exceptions SHALL be logged at warn level with the file path; the file SHALL be added to `IndexResult.FailedFiles` as `FileFailure(path, reason)` and SHALL be excluded from Pass 1's reconcile (`DeleteSymbolsForFileNotInAsync`), Pass 1's annotation insert, Pass 2, and Pass 3. Symbol and test-framework metadata written before the exception MAY remain as part of the explicitly incomplete state.

`FailedFiles` signals an incomplete graph, not an atomic rollback. Pass 1 phase A
MAY already have stored the new content hash and cleared the file's outgoing
references and edges, and symbol upserts performed before a phase-B exception
MAY remain. Skipping reconcile prevents declarations from the prior successful
pass from being deleted merely because the failed walk did not rediscover them,
but queries MAY be temporarily incomplete. A later successful pass SHALL
reconcile the file. When the failure occurs during a structural forced pass, the
persistent structural-reload flag SHALL remain set; otherwise the unchanged-SHA
integrity check SHALL re-walk a previously symbol-bearing file whose outgoing
facts were cleared.

#### Scenario: One file's Pass 1 walk throws; other files complete
- **GIVEN** a Pass-1 batch of three changed files where the second file's `GetSemanticModelAsync` (or any subsequent call inside the per-file walk) throws
- **WHEN** Pass 1 iterates the three files
- **THEN** the first and third files are walked, their symbols upserted, and they appear in `walkedFileIds`; the second file is logged at warn level with its path, added to `FailedFiles`, and absent from `walkedFileIds`; reconcile (`DeleteSymbolsForFileNotInAsync`) is NOT called for the second file; Pass 2 walks the first and third files but skips the second; the indexing pass returns successfully

#### Scenario: Failed file exposes partial state and self-heals
- **GIVEN** a file `F` that successfully indexed in a prior pass (symbols, refs, and edges present in the store) and whose Pass 1 walk now throws on a re-index
- **WHEN** Pass 1 catches the exception and skips reconcile for `F`
- **THEN** `F` is returned in `FailedFiles`; prior declarations are not reconciled away, but phase A's new hash and cleared outgoing facts plus any symbol upserts completed before the throw may remain; queries may be incomplete until the next structural retry or unchanged-SHA integrity repair completes a successful walk

#### Scenario: Cancellation propagates from Pass 1
- **WHEN** Pass 1 is iterating files and the supplied `CancellationToken` is signaled, raising `OperationCanceledException` in a per-file body
- **THEN** the catch handler rethrows so the cancellation surfaces to the caller; partial state from earlier files in the batch is left as-is (consistent with Pass 2's existing semantics)

### Requirement: Stable symbol identifiers across edits
The indexer SHALL upsert symbols by canonical key (Roslyn
`DocumentationCommentId`) so the integer `id` remains stable across edits and
incoming refs from other files do not get orphaned.

#### Scenario: Edit a defining file
- **WHEN** a file containing symbol `S` is edited and the live indexer
  reprocesses it
- **THEN** `S`'s row in `symbols` is updated in place via
  `INSERT … ON CONFLICT(canonical_key) DO UPDATE`, its integer `id` stays the
  same, and refs from other unchanged files that target `S.id` remain valid

#### Scenario: Remove a symbol from its source file
- **WHEN** a previously indexed symbol is no longer declared anywhere in its
  file across all per-project iterations
- **THEN** `DeleteSymbolsForFileNotInAsync` removes that symbol row and the
  refs/edges that targeted it in the same transaction

### Requirement: C# structural changes reload under the single-pass lock
For an allowed `.cs` watcher batch, a path that exists on disk but has no
`DocumentId` in the sanitized solution SHALL be treated as an addition, and a
known document that no longer exists SHALL be treated as a deletion. Either
condition, including both halves of a rename, SHALL reload the solution and
force a full C# graph pass while holding the indexer's single-pass lock.
Files that leave the reloaded solution SHALL be transactionally deleted before
the full pass. Byte-identical surviving files SHALL still be re-walked so
semantic edges affected by an added or removed declaration are reconciled.
Ordinary edits to existing documents SHALL retain the document-text incremental
path. Excluded or privacy-sensitive paths SHALL trigger neither reload nor
storage mutation, and cancellation SHALL propagate. If stale-file deletion or
the forced full pass throws or is cancelled, the indexer SHALL restore its
previous workspace and sanitized-solution fields. A persistent structural-
reload flag SHALL be set before the first reload attempt and SHALL remain set
after exceptions, cancellation, project/file failures, or incomplete source-
generator discovery. While set, the next allowed C# `IndexChangedFilesAsync`
batch or `IndexAllAsync` call SHALL retry reload plus the forced full pass
before attempting an ordinary incremental update. A non-throwing partial pass
MAY retain its replacement workspace, but only a pass with no project or file
failures SHALL clear the flag.

Every initial or replacement `OpenSolutionAsync` SHALL collect
`WorkspaceFailed` diagnostics emitted while the candidate workspace opens.
Warnings SHALL be logged but SHALL NOT block the candidate. Any diagnostic with
kind `Failure` SHALL reject the candidate before privacy sanitization is
published, before workspace fields are swapped, and (for replacement reloads)
before previous-only files are deleted. A failed initial open SHALL dispose the
candidate and leave the indexer unopened so the same instance can retry. A
failed re-open or replacement reload SHALL dispose the candidate and retain the
previous usable workspace and sanitized solution. A failed re-open SHALL retain
the prior structural-reload state; a failed replacement reload SHALL keep
structural reload pending. A successful re-open SHALL atomically publish the new
workspace and then dispose the previous workspace.

`OpenAsync`, `IndexAllAsync`, `IndexChangedFilesAsync`,
`ReloadAndIndexAllAsync`, and `DisposeAsync` SHALL share the same single-pass
lock. Candidate workspace opening MAY be slow, but publication and disposal of
the previous workspace SHALL be serialized so concurrent opens cannot mix
workspace, solution, path-policy, or diagnostics fields or dispose the same
workspace twice. A successful re-open SHALL preserve an already-set structural-
reload flag; only a complete forced index pass may clear it. `DisposeAsync`
SHALL wait for an active open/index pass, atomically mark the indexer disposed,
clear its workspace/solution/path and diagnostic references, and cause queued
operations to fail as disposed after they acquire the lock.

Read/GetText failures, null Roslyn syntax trees or semantic models, and Pass 2
failures SHALL produce one deduplicated `FileFailure` per affected path.
`OperationCanceledException` SHALL never be converted into a file or generator
failure. Source-generator discovery failure SHALL be visible in the result and
shall keep structural reload pending.

After complete source-generator discovery during any complete all-document
pass, the indexer SHALL compare `ListGeneratedFilesAsync(int.MaxValue)` with
the current privacy-approved generated-owner storage paths and delete persisted
generated files missing from the current set. Current-workspace generated
document identities are authoritative: reopening a solution MAY assign new
Roslyn `DocumentId` values, and a subsequent complete pass SHALL remove the
prior owner rows after indexing the current owners. Reconciliation SHALL run
only when discovery is complete and `IndexResult.FailedProjects` and
`IndexResult.FailedFiles` are both empty. If discovery or any later indexing
phase is incomplete, the indexer SHALL retain every prior generated owner and
its graph, keep structural reload pending, and retry before stale deletion.

#### Scenario: SDK-style compile item is added
- **GIVEN** an SDK-style project whose current sanitized solution does not
  contain `Added.cs`
- **WHEN** `Added.cs` is created inside the allowed project and its path is
  passed to `IndexChangedFilesAsync`
- **THEN** the solution is reloaded, symbols from `Added.cs` are indexed, and
  unchanged callers are re-walked so newly resolvable references and edges
  appear

#### Scenario: C# file is deleted or renamed
- **WHEN** a known C# document is deleted, or renamed as an old-path plus
  new-path batch
- **THEN** the old file row, declarations, references, edges, and evidence are
  removed; the replacement document (if any) is discovered; surviving files
  are fully re-indexed against the replacement solution snapshot

#### Scenario: Excluded new C# file is observed
- **WHEN** a newly created `.cs` path is outside the privacy boundary or matches
  a scope exclude
- **THEN** the batch returns without opening a replacement workspace or
  changing the graph

#### Scenario: Partial forced pass retries on a different file event
- **GIVEN** a structural reload reached Pass 2 but one file's edge write failed
- **WHEN** the pass returns that file in `FailedFiles`, followed by an allowed
  event for a different existing C# document
- **THEN** the replacement snapshot may remain active, but the pending flag
  forces another solution reload and complete full pass before processing the
  different document incrementally

#### Scenario: Workspace returns a partial solution with a failure diagnostic
- **GIVEN** an existing indexed graph and a replacement workspace whose
  `OpenSolutionAsync` returns a partial `Solution` while emitting one warning and
  one `Failure` diagnostic
- **WHEN** a structural reload attempts to open that replacement
- **THEN** the warning does not appear as the rejection reason, the failure
  diagnostic causes a controlled exception, no previous-only file is deleted,
  the prior workspace and graph remain available, and the next allowed C# event
  retries the structural reload

#### Scenario: Initial partial workspace open is retryable
- **WHEN** the first `OpenAsync` returns a partial solution with a `Failure`
  diagnostic
- **THEN** the candidate workspace is disposed, `Workspace` and
  `SanitizedSolution` remain null, and a later `OpenAsync` on the same indexer
  can succeed

#### Scenario: Successful re-open preserves a pending forced repair
- **GIVEN** an incremental content read failed and set structural reload pending
- **WHEN** product retry successfully calls `OpenAsync` again, followed by
  `IndexAllAsync` or an event for a different existing C# file
- **THEN** publishing the new workspace does not clear the flag; the next index
  call performs a forced full repair, and only its complete result clears the
  flag

#### Scenario: Generated output disappears
- **GIVEN** the store contains an `is_generated` row from the prior complete
  pass and complete generator discovery no longer returns that path
- **WHEN** a complete all-document pass succeeds
- **THEN** the stale generated file, its symbols, references, edges, and
  evidence are deleted; if generator discovery was incomplete, the row is
  retained

#### Scenario: Reopen changes generated document owners
- **GIVEN** a completed pass persisted generated-owner rows and reopening the
  same solution assigns different current-workspace generated `DocumentId`
  values
- **WHEN** the next complete all-document pass succeeds
- **THEN** current generated owners are indexed, prior owner rows are deleted
  as stale, and canonical symbols and their current references remain
  queryable

#### Scenario: Current generated-owner indexing fails after owner churn
- **GIVEN** prior generated owners have usable rows, symbols, and references,
  and current discovery returns a different owner set
- **WHEN** the current pass reports a generated-owner collision or another
  project/file indexing failure
- **THEN** none of the prior generated owners are stale-reconciled, their
  existing graph remains queryable, and structural reload stays pending
- **AND** the first later pass with complete discovery and no project/file
  failures indexes the unique current owners before deleting the prior owners

#### Scenario: Existing C# file is outside the loaded solution
- **GIVEN** an allowed existing `.cs` path that remains outside the solution
  after a successful reload
- **WHEN** the same path is observed again without an explicit reload
- **THEN** the cached non-membership prevents another structural reload; an
  explicit `ReloadAndIndexAllAsync` invalidates this cache so membership can be
  evaluated again

### Requirement: Hydrate in-memory maps from the store on startup
The indexer SHALL populate `_symbolIdByKey`, `_keysByFileId`, and
`_fileIdByPath` from the existing graph DB on the first `IndexCoreAsync` call
in a process (or after `fullReset`). It SHALL also retain each file row's exact
persisted path spelling so later path-identity matches can mutate or delete the
existing row rather than inserting a differently spelled alias.

#### Scenario: Server restart with an existing DB
- **WHEN** `sourcegraph-mcp serve` starts against a solution whose
  `.sourcegraph/graph.db` was populated by a prior cold index
- **THEN** the indexer reads every `(canonical_key, id, file_id)` from
  `symbols` and `(path, id)` from `files`, logs
  `"Hydrated N symbol(s) and M file(s) from graph store"`, and every file
  whose SHA matches the stored value AND has either zero declared symbols
  or at least one outgoing pass-2 artifact (a `refs` row, or an outgoing
  edge from a symbol declared in that file) is skipped in pass 1 (per the
  self-heal integrity check); files that match the SHA but have declared
  symbols with zero outgoing refs AND zero outgoing edges are bypassed
  and re-walked

### Requirement: Physical source-path identity follows the host OS
Regular source paths SHALL use case-insensitive identity on Windows and
case-sensitive identity on Linux. Every path-keyed in-memory set and map used
for discovery, SHA gating, structural reload, and incremental dispatch SHALL
use the same host-OS rule. On Windows, when a differently cased path matches a
hydrated or current file row, the indexer SHALL reuse that row's integer id and
exact persisted path spelling for storage operations. Structural deletion SHALL
likewise resolve a watcher spelling to the exact persisted spelling before
deleting. Linux SHALL continue to treat differently cased paths as distinct.

#### Scenario: Windows casing-only reload reuses the file row
- **GIVEN** a Windows index contains `CaseConsumer.cs` with file id `F`
- **WHEN** the file is renamed through a casing-only change to
  `CASECONSUMER.cs` and the old/new watcher paths trigger structural reload
- **THEN** exactly one case-equivalent file row remains, its id is still `F`,
  its hash and outgoing evidence describe the new bytes, and evidence from the
  old content is absent

#### Scenario: Windows casing variant survives hydration and incremental edit
- **GIVEN** a Windows process restarts with one persisted mixed-case file row
- **WHEN** an incremental watcher event supplies another casing of that path
- **THEN** hydrated identity resolves the exact persisted row, the edit updates
  that same id without creating an alias row, and a later restart remains
  stable

#### Scenario: Linux paths remain case-sensitive
- **GIVEN** a Linux solution legitimately contains both `Consumer.cs` and
  `consumer.cs`
- **WHEN** both documents are indexed
- **THEN** they retain distinct file rows and file ids

### Requirement: Multi-target and linked-file iterations don't double-count
For regular documents, Phase A SHALL group documents by host-OS source-path
identity and make the stored-SHA changed/unchanged decision exactly once per
path/fileId. When the file changed, every document iteration for that path
SHALL be added to Pass 1 before reconciliation so declarations visible only
under a later target framework or linked-project parse configuration are
retained. Generated documents SHALL instead be grouped by generated-owner
storage identity and SHALL never be merged merely because their display
`FilePath` values match. The indexer SHALL emit refs and edges from at most one
document per fileId even when the loaded solution exposes the same regular
source path multiple times (multi-target frameworks, linked files, shared
projects). Pass 2 SHALL carry the selected document's resolved file id
directly; it SHALL NOT reverse-map `Document.FilePath`, because display paths
are not unique for generated documents.

#### Scenario: A file targeted by multiple TFMs
- **WHEN** the solution multi-targets such that path `P` produces N
  documents
- **THEN** pass 1 accumulates the union of declared canonical keys across
  all N iterations before reconciling, and pass 2 walks exactly one of the N
  documents

#### Scenario: Declaration exists only in a later target framework
- **GIVEN** a previously indexed path with no declarations, whose first target
  framework does not define `SECOND_TFM` and whose later target framework does
- **WHEN** the path is edited to declare `OnlyInSecondTarget` inside
  `#if SECOND_TFM`
- **THEN** Phase A decides that the path changed once, Pass 1 walks both
  document iterations even though the first discovers no declaration, and the
  stored symbol set contains `OnlyInSecondTarget`

### Requirement: Robust file reads against editor save races
Before compilation probing or graph mutation, the indexer SHALL capture each
regular source path selected for the pass once as an exact byte snapshot. It
SHALL decode that same buffer into a BOM-aware `SourceText`, bind the same text
to every `DocumentId`
for that path (including linked and multi-target iterations), and pass both the
text and original bytes to Phase A. Phase A SHALL validate that every regular
document iteration still has equivalent bound text and SHALL hash the captured
bytes without reading the disk again. Thus the stored SHA and semantic graph
always describe the same version and preserve the original encoding/BOM in the
hash.

`IOException`, `InvalidDataException`,
`UnauthorizedAccessException`, `SecurityException`, and
`DecoderFallbackException` SHALL be converted into one deduplicated
`FileFailure`; `OperationCanceledException` SHALL propagate. The failed file's
owning projects SHALL be excluded from regular-document indexing, touched
project probing, and source-generator discovery for that batch. The indexer
SHALL leave the stored graph and content hash for those projects unchanged and
keep structural reload pending so the next allowed C# event, even for a
different path, retries a complete snapshot.

Invalid path strings SHALL be rejected independently as `FileFailure` entries;
one malformed path SHALL NOT abort valid paths in the same watcher batch.

#### Scenario: Read fails mid-batch
- **GIVEN** a known changed file whose prior graph and content hash are stored
- **WHEN** its byte-snapshot read or decode fails transiently
- **THEN** the path is logged at debug and returned in `FailedFiles`, its owning
  project contributes no regular or generated documents to the batch, its graph
  and hash remain unchanged, and the next allowed C# event forces a full reload
  that repairs the file once it is readable

#### Scenario: Disk changes after the snapshot is captured
- **GIVEN** an incremental read captures version A, including a UTF BOM, and
  the editor replaces the on-disk file with version B before Phase A begins
- **WHEN** the current batch indexes the already-bound document
- **THEN** Phase A performs no second disk read, stores the exact hash and
  semantics of A, and a subsequent watcher event reads and replaces the graph
  with B

#### Scenario: Full-pass snapshot read fails
- **WHEN** snapshot acquisition for one regular path fails during a full pass
- **THEN** the path is returned once in `FailedFiles`, all projects owning that
  path are excluded from regular and generated indexing for the pass, structural
  reload remains pending, and an event for a different allowed C# path retries
  the full snapshot

### Requirement: Symbol modifiers and accessibility recorded
The indexer SHALL capture every Roslyn modifier (`static`, `async`, `virtual`, `abstract`, `sealed`, `override`, `extern`, `readonly`, `partial`) and the `DeclaredAccessibility` of every indexed symbol, and SHALL persist both via `UpsertSymbolAsync`.

#### Scenario: Public async method
- **WHEN** an indexed C# file contains `public async Task DoAsync()`
- **THEN** the symbol's `accessibility` column is `Public` and `modifiers` is `"async"`

#### Scenario: Private readonly field
- **WHEN** an indexed C# file contains `private readonly string _x;`
- **THEN** `accessibility = Private` and `modifiers = "readonly"`

### Requirement: XML doc summary captured
The indexer SHALL parse the `<summary>` of each symbol's XML documentation comment (resolving `<inheritdoc/>` up the override chain when present) and SHALL store the parsed plain text on the symbol row.

#### Scenario: Documented method
- **WHEN** a method has `/// <summary>Publishes the feed.</summary>`
- **THEN** its `xml_summary` column equals `"Publishes the feed."`

#### Scenario: Inherited summary
- **WHEN** an override has `/// <inheritdoc/>` and its base method has a non-empty summary
- **THEN** the override's `xml_summary` equals the base's parsed summary

#### Scenario: No summary available
- **WHEN** a symbol has no XML doc, no inheritdoc, or inheritdoc points at an external assembly without XML docs
- **THEN** `xml_summary` is `NULL` (not the empty string)

### Requirement: Container hierarchy populated
The indexer SHALL set `symbols.container_id` to the row id of each symbol's containing symbol (`ContainingSymbol`) using a two-phase pass.

#### Scenario: Method inside a class
- **WHEN** `class Foo { void Bar() {} }` is indexed
- **THEN** `Bar.container_id` equals `Foo.id`

#### Scenario: Top-level type
- **WHEN** a class has no containing type (its container is a namespace)
- **THEN** the class row's `container_id` is the namespace's row id

#### Scenario: Symbol whose parent isn't indexed
- **WHEN** a symbol's containing symbol is filtered out by `IsIndexable` (e.g., a global namespace)
- **THEN** `container_id` is `NULL`

### Requirement: Capture every annotation on indexed symbols
The indexer SHALL record every attribute (`ISymbol.GetAttributes()`) attached to an indexed symbol by emitting an `AnnotationAttached` event with `Flavor = "csharp-attribute"`, `AnnotationName` set to the attribute's short name, `FullName` set to the attribute's fully qualified name, `ArgsJson` containing the constructor arguments and named arguments, and `TargetCanonicalKey` linking back to the user-defined attribute symbol if it's in the graph (else `null`).

The host SHALL persist each emission as an `annotations` row.

#### Scenario: Method with a route attribute
- **WHEN** an indexed method is decorated `[HttpGet("/api/users")]`
- **THEN** an `annotations` row is written with `name = "HttpGet"`, `full_name = "Microsoft.AspNetCore.Mvc.HttpGetAttribute"`, `flavor = "csharp-attribute"`, `args_json` whose `ctor[0]` is the literal string `"/api/users"`, and `attribute_symbol_id` linking back to the user-defined attribute symbol if it's in the graph (else `NULL`)

#### Scenario: Multiple attributes
- **WHEN** a symbol has `[Authorize, Obsolete("Use Foo")]`
- **THEN** two `annotations` rows are written, in source order, both with `flavor = "csharp-attribute"`

### Requirement: Annotation reconciliation on file reindex
When a file is reindexed, the indexer SHALL delete every `annotations` row attached to that file's symbols before reinserting the new annotation set, in the same transaction as the symbol-set reconciliation.

#### Scenario: Attribute removed from source
- **WHEN** a file is edited to remove `[Obsolete]` from a method
- **THEN** after the live reindex, no `annotations` row remains for that method with `name = "Obsolete"` and `flavor = "csharp-attribute"`

### Requirement: UsesType edges between indexed members and types
The indexer SHALL emit a `UsesType` edge from every indexed member symbol to every indexed type symbol that appears in its signature (parameter types, return type, generic arguments) and in its body's `new T()` and locally-declared types.

#### Scenario: Method that consumes a CancellationToken parameter
- **WHEN** an indexed method `void M(CancellationToken ct)` is processed and `CancellationToken` is itself indexed (or the agent has chosen to also index BCL types)
- **THEN** an edge `(M.id, CancellationToken.id, UsesType)` is written

#### Scenario: External / non-indexed type ignored
- **WHEN** the parameter type is not in the graph (e.g. an unindexed BCL type)
- **THEN** no edge is emitted for that type

### Requirement: Read vs Write reference kinds for field/property access
The indexer SHALL distinguish `Read` and `Write` reference kinds based on syntactic position (assignment LHS, `++`/`--`, `out`/`ref` argument).

#### Scenario: Plain read
- **WHEN** the source contains `var y = _x;`
- **THEN** the reference at `_x` is recorded with `kind = Read`

#### Scenario: Assignment LHS
- **WHEN** the source contains `_x = 1;`
- **THEN** the reference at `_x` is recorded with `kind = Write`

#### Scenario: Increment is read+write
- **WHEN** the source contains `_x++;`
- **THEN** two reference rows are written at the same position: one `Read`, one `Write`

#### Scenario: out parameter
- **WHEN** the source contains `Method(out _x)`
- **THEN** the reference at `_x` is `Write`

#### Scenario: ref parameter
- **WHEN** the source contains `Method(ref _x)`
- **THEN** two rows are written: one `Read`, one `Write`

### Requirement: Member-level Override edges
The indexer SHALL emit `OverridesMember` edges for methods, properties, and events whose `Overridden*` Roslyn property is set and points at an indexed symbol.

#### Scenario: Override of a virtual method
- **WHEN** `class B { public virtual void F() {} }` and `class D : B { public override void F() {} }` are both indexed
- **THEN** an edge `(D.F.id, B.F.id, OverridesMember)` is written

### Requirement: Member-level ImplementsMember edges
The indexer SHALL emit `ImplementsMember` edges from each implementing member to the interface member it satisfies, using `FindImplementationForInterfaceMember`.

#### Scenario: Class implements an interface method
- **WHEN** `interface IG { void Greet(); }` and `class G : IG { public void Greet() {} }` are both indexed
- **THEN** an edge `(G.Greet.id, IG.Greet.id, ImplementsMember)` is written

#### Scenario: Explicit interface implementation
- **WHEN** the implementing member is `void IG.Greet() {}` (explicit)
- **THEN** an `ImplementsMember` edge is still emitted with the explicit member as source

### Requirement: Instantiates edges from `new T()`
For every `ObjectCreationExpressionSyntax`, the indexer SHALL emit an `Instantiates` edge from the enclosing member to the constructed type (in addition to the existing `Call` edge to the constructor).

#### Scenario: Construct an indexed type
- **WHEN** a method body contains `new MyClass()` and `MyClass` is indexed
- **THEN** an edge `(method.id, MyClass.id, Instantiates)` is written alongside the existing constructor `Call` edge

### Requirement: ICommand properties identify their execution methods
The Roslyn indexer SHALL emit a `CommandExecutes` (`"command-executes"`) edge from an indexed
`ICommand`-like property to an indexed source method only when an initializer or simple assignment
constructs an `ICommand` implementation and Roslyn's operation tree exposes exactly one delegate
creation whose target is a method reference. The evidence range SHALL identify that method-group
expression and carry `semantic` confidence from producer `roslyn`.

The indexer SHALL require semantic `System.Windows.Input.ICommand` implementation evidence for
both the property type and constructed type. It SHALL NOT infer this relation from names, and SHALL
fail closed for invalid or dynamic operations, overload ambiguity, multiple delegate arguments,
non-command properties, metadata-only handlers, and lambdas without a stable indexed method
endpoint. The ordinary `calls` edge to the command constructor SHALL remain independently
queryable.

#### Scenario: Command assigned in a constructor
- **WHEN** an `ICommand RunCommand` property is assigned
  `new AsyncRelayCommand(RunAsync)` and `RunAsync` resolves uniquely to an indexed source method
- **THEN** `(RunCommand.id, RunAsync.id, "command-executes")` is written with semantic evidence at
  `RunAsync`, alongside the enclosing constructor's ordinary `calls` edge to the
  `AsyncRelayCommand` constructor

#### Scenario: Ambiguous or non-command assignment
- **WHEN** a method group is overload-ambiguous, more than one delegate argument is present, the
  property is not `ICommand`-like, or the value is a lambda with no indexed method endpoint
- **THEN** no `command-executes` edge is emitted

#### Scenario: Command assignment changes or disappears
- **WHEN** a command property assignment is edited to select another proven handler, removed, or
  its declaring file is deleted
- **THEN** normal Roslyn per-file reconciliation replaces or removes the prior
  `command-executes` edge and its occurrence evidence

### Requirement: Throws edges from `throw` syntax
For every `ThrowStatementSyntax` and `ThrowExpressionSyntax`, the indexer SHALL emit a `Throws` edge from the enclosing member to the thrown type, when the thrown type is indexed.

#### Scenario: Throw an indexed exception type
- **WHEN** an indexed method body contains `throw new MyDomainException();`
- **THEN** an edge `(method.id, MyDomainException.id, Throws)` is written

### Requirement: Source-generated documents indexed
The indexer SHALL include source-generated documents
(`Project.GetSourceGeneratedDocumentsAsync()`) alongside regular documents,
marking the corresponding `files.is_generated` row to `1`.

A generated file's persisted identity SHALL be independent of its display
`Document.FilePath` and its content hash. The indexer SHALL derive a
privacy-approved, absolute synthetic storage path below the solution's reserved
`obj/.sourcegraph-generated/v1` area from project identity, generator/document
owner identity, and hint/display metadata. The current workspace's generated
`DocumentId` SHALL distinguish owners within that workspace. Consequently,
different projects or generators that report the same display path and hint
SHALL retain separate file rows, and a regular source whose physical path
matches that display path SHALL remain a separate non-generated row.

If two current generated documents resolve to the same generated-owner storage
identity, the indexer SHALL fail that owner group once as a `FileFailure`, skip
merging its documents, and keep structural reload pending. Generated synthetic
paths SHALL not be sent to disk/git history callbacks. Their stored SHA SHALL
still be computed from the generated `SourceText` encoded as UTF-8, so the hash,
symbols, references, and edges describe the same generated content.

#### Scenario: Generated document indexed
- **WHEN** a project uses a source generator producing a `*.g.cs` document with a real `class GeneratedFoo`
- **THEN** that class appears in `symbols` with `kind = Class`, its `files.is_generated = 1`, and tools render `(generated)` next to its name

#### Scenario: SHA gate on generated content
- **WHEN** the same source-gen run produces byte-identical output to last time
- **THEN** the file row's `content_sha256` is unchanged and no symbol/edge work happens for that file

#### Scenario: Projects, generators, and a regular file share a display path
- **GIVEN** two projects each produce generated documents from multiple
  generators using the same display path and hint, and a regular source file
  also has that physical display path
- **WHEN** a complete pass indexes the solution
- **THEN** every generated owner has a distinct synthetic `is_generated = 1`
  row, the regular source has one separate `is_generated = 0` row, each stored
  hash matches that owner's bytes, and each project's symbols and references
  retain their own semantics

#### Scenario: Generated owner collision fails closed
- **GIVEN** two current generated documents resolve to the same synthetic owner
  identity
- **WHEN** Phase A groups the documents
- **THEN** it records one file failure for that owner, performs no merged
  symbol reconciliation for the group, and leaves structural reload pending

### Requirement: Roslyn diagnostics captured per file
The indexer SHALL run `compilation.GetDiagnostics(ct)` after pass 2 and persist
every diagnostic with a non-empty `Location.SourceSpan` into the `diagnostics`
table; on reindex, prior diagnostics for the file SHALL be deleted before
reinserting. Diagnostic ownership SHALL be resolved by exact Roslyn
`SyntaxTree` object identity captured during Phase A, not by source/display
path, so equal generated display paths cannot cross-attribute diagnostics.

#### Scenario: Warning attached to a symbol
- **WHEN** a method calls an `[Obsolete("Use Foo")]`-tagged member and Roslyn emits `CS0618`
- **THEN** a diagnostics row exists with `code = "CS0618"`, `severity = 2 (Warning)`, the message text, line/col, and `symbol_id` resolving to the calling method

#### Scenario: Diagnostic without symbol attribution
- **WHEN** a diagnostic's location lies between symbol boundaries (e.g. an unused-using warning)
- **THEN** the row's `symbol_id` is `NULL` and the diagnostic is file-scoped

#### Scenario: Diagnostic reconciliation
- **WHEN** a file is edited to remove the cause of a warning
- **THEN** after live reindex, no `diagnostics` row remains with that file_id and the resolved code

### Requirement: Test framework detection
The indexer SHALL set `symbols.test_framework` to one of `xunit | nunit | mstest` on every method whose attached attributes match the corresponding framework's discriminator (e.g. `[Fact]`, `[Theory]`, `[Test]`, `[TestCase]`, `[TestMethod]`).

#### Scenario: xUnit test method
- **WHEN** a method is decorated `[Fact]`
- **THEN** its symbol row has `test_framework = "xunit"`

#### Scenario: NUnit test method
- **WHEN** a method is decorated `[Test]` and lives inside a `[TestFixture]` class
- **THEN** its symbol row has `test_framework = "nunit"`

### Requirement: Tests edge from test methods to first non-trivial production call
The indexer SHALL emit a `Tests` edge from each test method to the first non-trivial production-code symbol it calls; "non-trivial" excludes other test methods, test fixtures, and test-helper utilities.

#### Scenario: Direct call into production code
- **WHEN** an `[Fact]` test calls `var c = new Calculator(); c.Add(2, 3);`
- **THEN** an edge `(test.id, Calculator.Add.id, Tests)` is emitted

#### Scenario: Test that calls only into test helpers
- **WHEN** a test only calls test-fixture or arrange/assert utilities
- **THEN** no `Tests` edge is emitted; agents fall back to `find_references` for analysis

### Requirement: Git history per symbol
The indexer SHALL maintain a `symbol_history` row per symbol containing the most recent commit sha, author, authored time, and blamed line count, derived from `git blame --line-porcelain` over the symbol's span and cached against `(file_path, content_sha256)`.

#### Scenario: First-time blame
- **WHEN** a file is first indexed in a git working tree
- **THEN** for each indexed symbol in that file, `symbol_history` has a row whose `last_commit_sha` and `last_author` match `git blame` output, and `blamed_content_sha` equals the file's current `content_sha256`

#### Scenario: Cache hit on unchanged file
- **WHEN** the file's `content_sha256` matches `blamed_content_sha`
- **THEN** no `git blame` invocation occurs

#### Scenario: Disable history
- **WHEN** the server is started with `--no-history` or the repo isn't a git working tree
- **THEN** `symbol_history` rows are not written; `who_authored` returns "git history unavailable" and no `git` subprocess is invoked

### Requirement: Roslyn indexer emits scheme-prefixed canonical keys
The built-in C# indexer SHALL emit `CanonicalKey` values prefixed with `"csharp:"`. The body after the prefix SHALL match the Roslyn `DocumentationCommentId` for the symbol (e.g. `csharp:T:Sample.Domain.Calculator`, `csharp:M:Sample.Domain.Calculator.Add(System.Int32)`).

#### Scenario: Type symbol key
- **WHEN** the indexer emits a `SymbolDeclared` for the class `Sample.Domain.Calculator`
- **THEN** the emitted `CanonicalKey` is `"csharp:T:Sample.Domain.Calculator"`

#### Scenario: Method symbol key
- **WHEN** the indexer emits a `SymbolDeclared` for `Sample.Domain.Calculator.Add(int)`
- **THEN** the emitted `CanonicalKey` is `"csharp:M:Sample.Domain.Calculator.Add(System.Int32)"`

#### Scenario: Hydrated keys also conform
- **WHEN** the indexer hydrates `_symbolIdByKey` from the store on startup
- **THEN** every loaded canonical key starts with `"csharp:"` (data written by an older server is dropped by the schema-version check before hydrate runs)

### Requirement: Roslyn pathway flows through MSBuildLanguageProject
The C# indexing pathway SHALL provide an `MSBuildLanguageProject` implementation of `ILanguageProject` that fronts the existing `MSBuildWorkspace`-loaded project, and an `MSBuildLanguageProjectFactory` whose `ProjectMarkers` includes `"*.csproj"`, `"*.fsproj"`, `"*.vbproj"`, and the various `.slnx` / `.sln` markers.

`IndexContext.Project` SHALL be set to the `MSBuildLanguageProject` for every `.cs` document the indexer processes.

#### Scenario: IndexContext for a regular .cs document
- **WHEN** the indexer dispatches a `.cs` document from project `MyApp.csproj` to itself
- **THEN** `IndexContext.Project` is the `MSBuildLanguageProject` whose `Id` equals the absolute path of `MyApp.csproj`

#### Scenario: Source-generated documents
- **WHEN** the indexer dispatches a source-generated document to itself
- **THEN** `IndexContext.Project` is the `MSBuildLanguageProject` of the project whose generators produced the document

### Requirement: Roslyn indexer emits string-typed kinds
The Roslyn indexer SHALL emit edge and symbol kinds as the kebab-case string constants exposed by `EdgeKinds` and `SymbolKinds` (e.g. `EdgeKinds.Calls = "calls"`, `SymbolKinds.Method = "method"`), not as integer enum values.

#### Scenario: Calls edge emission
- **WHEN** the indexer encounters a method invocation that resolves to an indexed target
- **THEN** the emitted `EdgeEmitted.EdgeKindName` equals `"calls"` (the value of `EdgeKinds.Calls`)

#### Scenario: Class symbol emission
- **WHEN** the indexer emits a `SymbolDeclared` for a class declaration
- **THEN** the emitted `SymbolDeclared.Kind` (now `string`) equals `"class"` (the value of `SymbolKinds.Class`)

### Requirement: Roslyn edges preserve occurrence evidence
Every relationship emitted by the Roslyn indexing path SHALL include host-owned evidence
whose producing file is the current document and whose source range is the relevant syntax.
Call, construction, throw, and base-type syntax resolved to an indexed symbol SHALL be
`exact`; relationships inferred through symbol semantics at a declaration (signature type,
override, interface-member implementation, and command delegate wiring) SHALL be `semantic`. The producer SHALL be
`roslyn`.

Logical edge deduplication SHALL include the evidence range. Two invocations from the same
caller to the same callee SHALL therefore produce one logical edge with two evidence rows,
while duplicate visits to the same syntax node remain idempotent.

#### Scenario: Repeated calls retain both locations
- **WHEN** one method calls the same indexed target on two separate lines
- **THEN** the store contains one `calls` edge and two `exact` evidence rows whose ranges point to the two call sites

#### Scenario: Signature relationship is semantic
- **WHEN** an indexed method's return or parameter signature names another indexed type
- **THEN** its `uses-type` edge carries `semantic` evidence at the member declaration

### Requirement: XAML file discovery and dispatch
The indexer dispatcher SHALL route every `.xaml` file in an indexed solution to the registered `XamlLanguageIndexer`. Documents are discovered via the same project-walking logic the C# pathway uses; XAML files appear in `.csproj` `<Page>`, `<ApplicationDefinition>`, or `<EmbeddedResource>` items and SHALL be enumerated alongside `.cs` documents during cold and live indexing.

#### Scenario: Cold index of a WPF solution
- **WHEN** `sourcegraph-mcp index <wpf-solution>` is invoked against a solution that contains `MainWindow.xaml`, `App.xaml`, and `Themes/Generic.xaml`
- **THEN** every `.xaml` document is dispatched to the `XamlLanguageIndexer`, every `.xaml.cs` codebehind is dispatched to the Roslyn indexer, and the resulting `IndexResult` reports per-language file counts merged

#### Scenario: Live edit of a XAML file
- **WHEN** the live indexer detects a change to `Views/Main.xaml` while the server is running
- **THEN** the XAML indexer is invoked with the changed document, prior emissions for that file are removed (the `DeleteSymbolsForFileNotInAsync` pattern applies to XAML symbols too), and the fresh emissions replace them in storage

### Requirement: XAML parser shape
XAML files SHALL be parsed via `System.Xml.XmlReader` with position tracking; markup-extension values (`{Binding ...}`, `{StaticResource ...}`, etc.) SHALL be parsed by a separate `MarkupExtensionParser` operating on the attribute value string. The implementation SHALL NOT depend on `System.Xaml`, `PresentationFramework`, or any vendor-specific XAML parser (Avalonia.Markup.Xaml, Uno, etc.) so the indexer remains portable across all five framework profiles.

#### Scenario: XmlReader-based parsing preserves position
- **WHEN** the indexer parses an element `<Button x:Name="SaveBtn" Click="OnSave"/>` at line 14 col 5 of `Views/Main.xaml`
- **THEN** the emitted `SymbolDeclared` carries position fields equal to the line/column where the element opens

#### Scenario: Markup extension parsed without vendor dependencies
- **WHEN** the indexer parses an attribute value `{Binding Path=User.Name, Mode=TwoWay, Converter={StaticResource b2v}}`
- **THEN** the resulting structured representation carries `Name = "Binding"`, named args `Path = "User.Name"`, `Mode = "TwoWay"`, `Converter = nested {StaticResource b2v}` — and no PresentationFramework or Avalonia assembly is loaded as part of parsing

### Requirement: Framework profile auto-detection
The XAML indexer SHALL detect the framework profile (one of `Wpf`, `WinUi`, `Uwp`, `Avalonia`, `Uno`) per file from the root element's namespace mappings. Detection rules:

- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` + WinUI controls namespace → `WinUi`
- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` (no WinUI) → `Wpf`
- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` + UWP-only namespaces → `Uwp`
- `xmlns="https://github.com/avaloniaui"` → `Avalonia`
- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` + `xmlns:nventive=...` → `Uno`

The detected profile selects an `IXamlDialect` strategy that handles markup-extension and namespace-mapping differences for the file.

#### Scenario: WPF file detected from default xmlns
- **WHEN** the indexer parses a file whose root has `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` and no other vendor-specific namespaces
- **THEN** the detected profile is `Wpf`, the `WpfDialect` is selected, and `clr-namespace:` mappings are resolved per WPF rules

#### Scenario: Avalonia file detected from Avalonia xmlns
- **WHEN** the indexer parses a file whose root has `xmlns="https://github.com/avaloniaui"`
- **THEN** the detected profile is `Avalonia`, the `AvaloniaDialect` is selected, and `clr-namespace:` mappings use Avalonia's resolution rules

#### Scenario: Profile-specific markup extension dialect
- **WHEN** a WinUI 3 file uses `Text="{x:Bind ViewModel.Name}"` (compiled binding)
- **THEN** the `WinUiDialect`'s markup-extension dispatcher recognises `x:Bind` as a compiled-binding shape
- **AND** until the code-behind source type and full path can be resolved semantically, the indexer emits no inherited-`DataContext` edge and no synthetic target

### Requirement: Per-project resource cascade cache
For every project that contains XAML files, the `XamlLanguageProjectFactory` SHALL build a per-project `XamlLanguageProject` instance whose resource snapshot indexes every `x:Key` visible from:

- The project's `App.xaml` `Application.Resources`
- Any same-project, relative-URI `MergedDictionaries` transitively referenced from `App.xaml`
- A theme `Generic.xaml` if present in the project's `Themes/` folder

Discovery SHALL use two passes: first parse every scope-approved project XAML file into resource declarations and merge links, then walk the project-global cascade roots. The implementation SHALL NOT open a file excluded by the scope/privacy policy, reached through a reparse point outside the physical scope, or not owned by the project. Duplicate visible declarations SHALL be retained as an ambiguous candidate set; filesystem enumeration order SHALL NOT silently choose one.

Any non-excluded directory enumeration, project/XAML read, or XML parse failure
SHALL make XAML discovery incomplete and propagate to the dispatcher; the
factory SHALL NOT return a partial project list or publish a partial resource
snapshot. Scope/privacy exclusions SHALL still be checked before each read.
Explicit `Include` and `Update` items SHALL be unioned with a complete,
policy-pruned `*.xaml` scan and SHALL NOT suppress discovery of SDK-default or
otherwise implicit XAML files. A safely identified lexical scope/privacy
exclusion MAY be pruned, but a non-excluded root, directory, or file whose
physical identity cannot be resolved SHALL fail discovery rather than appear
as an empty or partial result. Caller cancellation SHALL be checked at the
factory entry and around each synchronous enumeration, read, XML parse, and
per-project build boundary; it SHALL propagate without publishing a result.

The snapshot SHALL be reused for every `.xaml` file in the project so resource-resolution lookups (`{StaticResource AccentBrush}` → declaration site) do not re-walk the cascade per file. `XamlLanguageProject` SHALL implement `IDeclarationFirstLanguageProject`; its `DeclarationFilePaths` SHALL be the deterministic, duplicate-free set of real files that own the snapshot's resource declarations. A capable host dispatches that subset before consuming XAML files, so a cold index never depends on filesystem enumeration order. `XamlLanguageProject.RebuildResourceCache()` SHALL atomically replace the snapshot from the same scope-filtered project file set after an incremental resource edit; callers already using the prior immutable snapshot may finish against it.

#### Scenario: Resource resolved from App.xaml
- **WHEN** the indexer encounters `<Button Background="{StaticResource AccentBrush}"/>` in `Views/Main.xaml`, and `App.xaml` declares `<SolidColorBrush x:Key="AccentBrush" Color="Blue"/>`
- **THEN** the indexer emits a `uses-resource` edge from the button element to the real resource declaration symbol (resolved via the snapshot, no re-walk), and the resource's symbol carries kind `xaml-resource`
- **AND** the indexer does not synthesize another declaration in `Views/Main.xaml`
- **AND** the edge carries `resource-lookup=static` plus `exact` evidence at the consuming attribute with producer `xaml-resource`

#### Scenario: Resource resolved from a merged dictionary
- **WHEN** `App.xaml` merges `Resources/Palette.xaml` and that dictionary declares `AccentBrush`
- **THEN** a resource use in `Views/Main.xaml` targets canonical key `xaml:resource:Resources/Palette.xaml#AccentBrush`
- **AND** the target symbol's declaration file and range are those of `Resources/Palette.xaml`, not the consuming view

#### Scenario: Dynamic resource remains distinguishable
- **WHEN** an element uses `{DynamicResource AccentBrush}`
- **THEN** its resolved edge carries `resource-lookup=dynamic`
- **AND** the exact evidence retains the consuming attribute range

#### Scenario: Local forward resource reference
- **WHEN** a view-local resource and a consuming element occur in the same document, in either declaration order
- **THEN** the indexer's first document pass collects the declaration and its second pass resolves the use to that real local resource symbol

#### Scenario: Resource not found
- **WHEN** the indexer encounters `<Button Background="{StaticResource NonExistent}"/>` and the cache contains no entry for `NonExistent`
- **THEN** the indexer emits no `uses-resource` edge and creates no target symbol
- **AND** it attaches a queryable annotation finding to the consuming element with flavor `xaml-resource-finding`, name `Resource不存在`, code `XAMLRESOURCE001`, key, lookup kind, exact source range, confidence, and producer in its structured JSON payload

#### Scenario: Resource key is ambiguous
- **WHEN** two reachable merged dictionaries declare the same key and no narrower local declaration disambiguates the use
- **THEN** the indexer emits no resource edge and creates no target symbol
- **AND** it attaches a `xaml-resource-finding` annotation with name `Resource不明确`, code `XAMLRESOURCE002`, and every candidate declaration in its structured payload

#### Scenario: Resource snapshot rebuild
- **WHEN** an allowed merged dictionary changes and the host invokes `RebuildResourceCache()`
- **THEN** subsequent lookups see one atomically rebuilt snapshot in which removed keys are missing and new keys are resolvable

#### Scenario: Privacy-excluded merged dictionary
- **WHEN** `App.xaml` names `PatientData/Secret.xaml` as a merged dictionary source
- **THEN** that target is neither opened nor included in the resource snapshot, and its keys remain unresolved

### Requirement: Self-heal stranded reference edges
The indexer SHALL detect and recover from a "zombie" file state where pass 1's `ClearFileOutgoingAsync` cleared a file's outgoing refs/edges but pass 2's reference walk did not repopulate them. On every `IndexCoreAsync` call, the pass-1 unchanged-file skip path SHALL bypass the skip when the file declares one or more symbols but the store reports zero outgoing pass-2 artifacts (refs AND edges) for that file. The bypassed file SHALL be re-walked in pass 2 so its refs/edges are regenerated.

The integrity check SHALL be implemented via a new storage method `IGraphStore.HasOutgoingReferencesAsync(long fileId, CancellationToken ct)` that returns `true` when at least one outgoing-reference row exists for the given file OR at least one outgoing edge originates from a symbol declared in that file (in `SqliteGraphStore`'s schema, the `refs` table or the `edges` table joined to `symbols.file_id`). Checking edges as well as refs avoids spurious re-walks of files that legitimately produce zero refs but emit edges from member signatures (`uses-type`, `inherits`, `implements-member`). Default implementation SHALL return `true` so existing storage implementations preserve today's behaviour.

#### Scenario: Stranded file is re-walked on next index
- **GIVEN** a file `F` whose row, declared symbols, and content SHA exist in the store, but for which `refs.file_id = F.id` has zero rows AND no edges originate from symbols declared in `F`
- **WHEN** `IndexCoreAsync` runs against a workspace containing `F` whose on-disk SHA matches the stored SHA (no edit since last index)
- **THEN** pass 1's "unchanged file" skip is bypassed for `F` (because `HasOutgoingReferencesAsync(F.id) == false` while `_keysByFileId[F.id].Any() == true`), pass 2 walks `F`, and at least one outgoing-reference row appears for `F` after the call returns

#### Scenario: Healthy unchanged file is still skipped
- **GIVEN** a file `F` with declared symbols and at least one outgoing-reference row OR at least one outgoing edge from a symbol declared in `F`
- **WHEN** `IndexCoreAsync` runs against a workspace containing `F` whose on-disk SHA matches the stored SHA
- **THEN** pass 1's "unchanged file" skip applies as today; pass 2 does NOT walk `F`; the EXISTS-style integrity check fires once with negligible cost

#### Scenario: Symbol-less file does not loop on the integrity check
- **GIVEN** a file `F` with no declared symbols (an empty file or one containing only `using` directives)
- **WHEN** `IndexCoreAsync` runs against a workspace containing `F` whose SHA matches the stored SHA
- **THEN** the integrity check's "file declares symbols" guard short-circuits — `_keysByFileId[F.id]` is empty so the check doesn't fire — and pass 2 does not re-walk; the file is skipped as today

#### Scenario: Recovery is logged
- **WHEN** the integrity check forces pass 2 to walk a file that would have been SHA-skipped
- **THEN** the indexer emits an info-level log entry of the form `"Re-walking references for {Path}: file SHA matches but no outgoing references in store …"` so operators can observe recoveries; healthy installs never see this line

### Requirement: Pass 2 file-walk failures don't abort the loop
The indexer SHALL wrap each per-file body of pass 2's reference walk in a
try/catch so that an exception thrown while walking one file does not abort
pass 2 for the remaining files. Cancellation (`OperationCanceledException`)
SHALL still propagate. Other exceptions SHALL be logged at warn level with the
file path and exception detail.

Because reference and edge batches are separate store transactions, any Pass 2
failure or cancellation SHALL first persist an impossible content-hash marker
(an empty blob, which cannot equal a SHA-256 digest) using
`CancellationToken.None`, then clear the affected file's outgoing references
and edges using `CancellationToken.None`. The marker SHALL be written before
the clear so a process exit between the operations still forces a later walk.
A non-cancellation failure SHALL mark and clear that file, keep structural
reload pending, and continue. On cancellation, every changed file that
completed Pass 1 but has not completed both Pass 2 commits SHALL be marked and
cleared before the exception propagates. Reference counts SHALL be credited
only after both reference and edge writes complete. A subsequent index,
including one in a new process with the same database and unchanged source
bytes, SHALL observe the marker/SHA mismatch and rebuild the file even when it
declares zero symbols.

#### Scenario: One file's walk throws; other files' walks complete
- **GIVEN** a pass-2 batch of three changed files where the second file's syntax tree triggers an exception during the descendant-node walk (e.g. a transient compilation gap, a symbol-resolution failure)
- **WHEN** pass 2 iterates the three files
- **THEN** the first file's references are inserted, the second file's exception
  is caught and logged at warn level with the file path, its retry marker is
  persisted and partial outgoing facts are cleared, and the third file's
  references are inserted; pass 2 completes without rethrowing

#### Scenario: Cancellation propagates
- **WHEN** pass 2 is iterating files and the supplied `CancellationToken` is signaled, raising `OperationCanceledException` in a per-file body
- **THEN** files that already completed both Pass 2 commits remain intact;
  the current and remaining Pass-1-complete files receive the durable retry
  marker and have partial outgoing facts cleared with a non-cancelled cleanup
  token; then the catch handler rethrows so cancellation surfaces to the caller

#### Scenario: Cancellation after references commit self-heals after restart
- **GIVEN** a changed file whose reference batch commits and whose caller token
  is cancelled before its edge batch commits
- **WHEN** the cancelled indexer is disposed and a new indexer opens the same
  database against identical source bytes
- **THEN** the file row holds the empty retry marker and has no partial outgoing
  references before restart; the new index bypasses the SHA fast path, restores
  the real SHA, references, and edges, and reports no file failure

### Requirement: TypeScript / JavaScript file dispatch
The indexer SHALL register `TypeScriptLanguageIndexer` for the file extensions `.ts`, `.tsx`, `.js`, and `.jsx`. Each extension dispatches to the appropriate tree-sitter grammar (TypeScript / TSX / JavaScript). The indexer SHALL emit `IndexEvent`s for declarations, references, JSX usages, and the standard `FileScanned` sentinel.

#### Scenario: Plain TypeScript file produces declarations
- **WHEN** a `src/foo.ts` file declares `export function greet(name: string): string`
- **THEN** the indexer emits a `SymbolDeclared` for `greet` with `Kind = "method"` and canonical key `ts:M:src/foo.ts::greet`

#### Scenario: TSX file produces JSX-instantiation edges for PascalCase components
- **WHEN** a `src/page.tsx` file contains `<Button onClick={handler} disabled />`
- **THEN** the indexer emits an `EdgeEmitted` with `EdgeKindName = "instantiates"` whose target canonical key contains `Button`, and whose `Metadata` carries a `props` entry listing the prop names (`onClick`, `disabled`)

#### Scenario: HTML-cased JSX tag does not produce an edge
- **WHEN** the same file contains `<div className="foo" />`
- **THEN** the indexer SHALL NOT emit an `EdgeEmitted` whose target contains `div`; lower-cased JSX tags are filtered out as not referencing any user symbol

#### Scenario: JavaScript file uses the JavaScript grammar
- **WHEN** a `src/foo.js` file contains `function greet(name) { return name; }`
- **THEN** the indexer emits a `SymbolDeclared` whose canonical key starts with `js:M:` (matching the file extension's scheme)

#### Scenario: const distinguishes from let/var
- **WHEN** a file contains both `const API_BASE = "..."` and `let counter = 0;`
- **THEN** the indexer emits two `SymbolDeclared` events with `Kind = "constant"` and `Kind = "variable"` respectively

#### Scenario: Call expression produces a reference event
- **WHEN** a `src/foo.ts` file contains a call `greet("hello")`
- **THEN** the indexer emits a `ReferenceFound` whose `Kind = "call"` and whose target canonical key references `greet`

### Requirement: Default excludes for TypeScript / JavaScript scopes
The TypeScript indexer's `LanguageIndexerOptions.DefaultExcludes` SHALL include `**/node_modules/**`, `**/dist/**`, `**/.next/**`, `**/build/**`, `**/coverage/**`, `**/.cache/**`, `**/.parcel-cache/**`, `**/out/**`. The host applies these as floors — operator-supplied `exclude` patterns add to the list, never override it.

#### Scenario: Default excludes are accessible at runtime
- **WHEN** a caller reads `TypeScriptGrammarConfig.StandardExcludes`
- **THEN** the eight documented patterns are present, in the documented order

### Requirement: FileScanned sentinel emitted exactly once per indexed file
The indexer SHALL emit exactly one `IndexEvent.FileScanned` per `IndexAsync` call, regardless of whether the parse produced any other events. The sentinel carries the SHA-256 of the source bytes.

#### Scenario: Empty source still produces FileScanned
- **WHEN** the indexer is invoked on a zero-byte file
- **THEN** the resulting event list contains exactly one `FileScanned` and no other events

#### Scenario: Files above the size cap are skipped entirely
- **WHEN** a file's content exceeds `LanguageIndexerOptions.MaxFileSizeBytes` (default 10 MB)
- **THEN** the indexer returns an empty event list — no `FileScanned`, no symbols

### Requirement: gRPC projection and baseline share every index lifecycle

Cold indexing, live `.cs`/`.proto` refresh, project-control refresh, and the
one-shot `index` command SHALL all run the same strict `GrpcContractLinker`
after managed and protobuf inputs settle. A complete candidate SHALL establish
new first-observation baselines transactionally before replacing linker-owned
`grpc-calls` / `implements-rpc` evidence. An incomplete source universe or any
malformed/partial contract fact SHALL retain both the last-good edge projection
and every prior baseline.

The linker SHALL preserve generated-member mismatch diagnostics only when one
generated client/base candidate is associated with one exact proto RPC by the
available generated-container, descriptor type-shape, request/response, and
streaming-signature evidence. A merely similar method or service name SHALL
not create a relationship or contract finding.

#### Scenario: Cold and one-shot index establish the same baseline
- **GIVEN** the same complete C# and `.proto` source universe in two fresh scope databases
- **WHEN** one is indexed by server cold start and the other by the one-shot `index` command
- **THEN** both persist the same exact proto canonical keys, gRPC relations, and contract payload/range baselines

#### Scenario: Partial live save retains last-good state
- **GIVEN** a complete prior gRPC projection and baseline
- **WHEN** a watched `.proto` save temporarily contains an incomplete descriptor payload
- **THEN** the linker reports `partial` with `retained_last_good = true`; prior gRPC edge evidence and the baseline remain unchanged, and contract checks emit no change finding from the incomplete input

#### Scenario: Deleted proto declaration is no longer current
- **GIVEN** an indexed RPC with client/server relationships and a baseline
- **WHEN** its owning `.proto` file is successfully deleted and the complete projection reruns
- **THEN** the current RPC symbol and its relationships are absent; the dormant baseline history may remain but is not returned as a current contract or finding

