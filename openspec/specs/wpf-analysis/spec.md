# WPF analysis specification

## Purpose

Define evidence-first WPF/XAML resolution results. The analyzer reports a defect only when the
available project snapshot proves that a referenced member or static resource does not exist.
Unsupported or incomplete analysis remains queryable without being mislabeled as a defect.

## Requirements

### Requirement: Unified explicit resolution outcomes

Binding, command, and resource resolution SHALL return one of `resolved`, `missing`,
`ambiguous`, `unsupported`, `incomplete`, or `unknown`. Every outcome SHALL include a stable,
non-empty machine-readable `reason`.

- `resolved` means exactly one supported semantic target is proven.
- `missing` means the relevant static search space is complete and contains no target.
- `ambiguous` means at least two viable targets are proven.
- `unsupported` means the source uses a known syntax or runtime lookup mode that this analyzer
  does not model.
- `incomplete` means required project, compilation, or resource-cascade input is unavailable or
  incomplete.
- `unknown` means the analyzer lacks a unique source context or type even though it did not
  observe a concrete incomplete-input failure.

The indexer SHALL NOT manufacture a graph symbol for any unresolved outcome. Existing resolved
edge kinds and canonical-key contracts SHALL remain compatible.

#### Scenario: Unsupported analysis is not a defect

- **WHEN** an otherwise valid XAML attribute uses a known but unsupported source shape
- **THEN** the indexer emits no semantic target edge and no missing finding
- **AND** it preserves an outcome with status `unsupported` and a reason

### Requirement: Complete project resource snapshots

Each `XamlLanguageProject` SHALL atomically retain an immutable resource snapshot containing:

- all visible keyed `Definitions`;
- every real `ContributorPath` visited from `App.xaml`, `Themes/Generic.xaml`, or their supported
  same-project relative merge cascade, including contributors with no keyed definitions;
- `IsComplete`; and
- deterministic `UnknownReasons`.

Every collection layer exposed by the snapshot SHALL be a defensive copy behind a read-only
wrapper. Downcasting the definitions map, an individual candidate list, contributor paths, or
unknown reasons SHALL NOT permit mutation of the published snapshot.

Any non-excluded directory enumeration, project/XAML read, or XML parse error SHALL still abort
factory discovery; a partial project list or snapshot SHALL NOT be published. An explicitly
excluded merge target SHALL not be read, but SHALL make the attempted cascade incomplete.
Pack URIs, cross-project `;component` references, rooted/remote sources, missing relative merge
targets, and other unsupported merge forms SHALL add an unknown reason instead of being treated
as an empty visible dictionary.

`DeclarationFilePaths` SHALL expose the duplicate-free real contributor paths so hosts can
invalidate consumers when a declaration-only or merge-only contributor changes.

For an ordinary live edit of a declaration contributor, the registered-language dispatcher SHALL
successfully rebuild the resource snapshot before changing any stored file facts, then reindex the
affected project's XAML consumers (reindexing all scope-approved project `FilePaths` is allowed).
On an empty graph, every declaration symbol SHALL be available before declaration-bearing files
finalize cross-file edges of their own. A deterministic declaration retry pass is allowed, but
dispatch metrics SHALL count each physical file once.
When one live batch affects multiple projects, every replacement snapshot SHALL be prepared
successfully before any affected project publishes its replacement.
Contributor create/delete/rename SHALL first rebuild and publish complete project membership and
its resource snapshot. A rebuild/discovery failure SHALL remain visible as a file/project failure,
retain the prior snapshot and project map, and SHALL NOT delete or replace prior consumer facts.
Fanout paths SHALL remain inside the project, scope, and privacy boundary and SHALL be deduplicated.
If one physical XAML contributor belongs to multiple discovered projects, invalidation SHALL retain
all project owners and rebuild/fan out every affected project; the compatibility file-to-project
lookup SHALL NOT silently discard secondary owners for invalidation.
If one physical XAML consumer belongs to multiple XAML projects, a host that stores only one fact
set per physical file SHALL NOT select the first owner as authoritative. Until project-variant
facts are modeled, project-level resource and Roslyn context SHALL be unavailable for that file,
so only document-local facts can resolve.

Filesystem fallback discovery SHALL prune a nested directory that contains a different project
file; XAML owned by that child project SHALL NOT be inferred as belonging to the parent. An
explicit XAML item that links a file across project directories remains eligible after the normal
scope and privacy checks.

Resource roots SHALL follow project item identity when that identity can be reliably established.
A custom-named explicit `ApplicationDefinition` is an application resource root, while an
explicit normal `Page` named `App.xaml` is not promoted merely because of its filename. The
conventional `App.xaml` fallback SHALL remain available when that file has no explicit conflicting
identity; unrelated `Page Update` metadata MUST NOT disable it. Conditions, property-expanded
paths, imports, removes, or other item evaluation that cannot be modeled reliably SHALL make the
resource snapshot incomplete rather than proving an empty root set.
When an exact explicit `ApplicationDefinition` is excluded by the active scope or privacy policy,
the analyzer SHALL NOT read that file, SHALL mark the resource snapshot incomplete, and SHALL NOT
turn an absent resource into a `missing` finding.

#### Scenario: Merge-only contributor is retained

- **GIVEN** `App.xaml` merges a dictionary that declares no keyed resources
- **WHEN** the project snapshot is built
- **THEN** both real files occur in `ContributorPaths` and `DeclarationFilePaths`

#### Scenario: Factory read failure remains fail-closed

- **WHEN** any non-excluded project or XAML read fails
- **THEN** factory discovery throws and publishes neither a partial project nor a partial snapshot

#### Scenario: Live resource contributor invalidates stored consumers

- **GIVEN** a stored consumer currently resolves a static resource from `App.xaml`
- **WHEN** a live edit makes that key ambiguous, removes the contributor, or restores it
- **THEN** the dispatcher rebuilds first and reindexes the consumer so its stored edge/finding
  transitions to ambiguous, missing, or resolved in the same live batch

#### Scenario: Live snapshot rebuild fails

- **GIVEN** a contributor and its consumers have a last successful stored graph and snapshot
- **WHEN** the contributor becomes unreadable or malformed during live rebuild
- **THEN** the batch reports the contributor failure and retains the prior snapshot, hashes, edges,
  findings, and other consumer facts

#### Scenario: Declaration file consumes a later declaration

- **GIVEN** `App.xaml` consumes a resource declared in its merged `ZColors.xaml`
- **WHEN** an empty graph is cold-indexed and `App.xaml` sorts first
- **THEN** the final graph retains the cross-file resource edge and reports each physical file as
  indexed once

#### Scenario: Later project rebuild fails in a multi-project batch

- **GIVEN** one live batch changes resource contributors in projects A and B
- **WHEN** A's replacement snapshot can be prepared but B's preparation fails
- **THEN** neither project publishes a replacement snapshot and neither stored consumer graph is
  changed

#### Scenario: Shared contributor has multiple project owners

- **GIVEN** two projects explicitly include and merge the same physical XAML dictionary
- **WHEN** that dictionary changes
- **THEN** both project snapshots and both projects' consumers are refreshed

#### Scenario: Shared consumer has divergent project owners

- **GIVEN** one physical view belongs to two XAML projects with different resource cascades
- **WHEN** that view is indexed into a physical-file-keyed graph
- **THEN** neither project's cascade is guessed, no project-level exact edge or missing finding is
  emitted, and the unresolved project context remains queryable

#### Scenario: Nested child project is isolated

- **GIVEN** a parent project directory contains a child directory with its own project file and
  XAML
- **WHEN** filesystem fallback discovery scans the parent
- **THEN** it prunes the child project root and does not add the child's XAML to the parent

#### Scenario: Custom application definition is authoritative

- **GIVEN** a project declares `Bootstrap.xaml` as its `ApplicationDefinition`
- **WHEN** `Bootstrap.xaml` defines an application resource
- **THEN** the resource participates in the project-global cascade regardless of the filename

#### Scenario: Excluded application definition fails closed

- **GIVEN** a project declares `PatientData/Bootstrap.xaml` as its `ApplicationDefinition` and the
  active privacy policy excludes that path
- **WHEN** an allowed view references a key that could be declared by that application root
- **THEN** the excluded file is not read and the lookup is `incomplete`, never `missing`

### Requirement: Conservative resource resolution

Only `StaticResource` is eligible for a statically resolved resource edge in this phase.
`DynamicResource` and `ThemeResource` SHALL be `unsupported` because runtime/theme lookup can
change the selected value. They SHALL emit neither `XAMLRESOURCE001` nor a guessed edge.

A static key with two or more proven visible definitions SHALL be `ambiguous` and SHALL not be a
finding. A project-level static key with one known candidate but an incomplete cascade SHALL be
`incomplete`, because an unobserved branch could change uniqueness. A project-level static key
with zero candidates SHALL be `missing` only when the snapshot is complete. Without a project
snapshot it SHALL be `unknown`.

Canonical identity SHALL distinguish two declarations with the same `x:Key` in different local
scopes of one physical document. The declaration symbol and `ResourceDefinition` target
calculation SHALL use the same deterministic discriminator so each consumer edge points to its
actual visible declaration. Existing unambiguous project-global resource keys SHALL retain their
compatible canonical form.

Document-local resources SHALL be resolved only from the consumer element's own resource scope or
an ancestor resource scope. A declaration owned by a sibling or unrelated descendant SHALL not be
considered visible. The nearest visible scope SHALL shadow outer scopes. A same-scope declaration
at or after the reference SHALL remain `unknown` in this phase because forward
`StaticResource` visibility is not proven.

Inline dictionaries in a local `MergedDictionaries` collection SHALL contribute to that local
owner's scope. A local merge `Source` that is not fully modeled SHALL make the local scope
`incomplete`, even when the separate project-global snapshot is complete.
A keyed nested `ResourceDictionary` SHALL remain one resource in its outer scope and SHALL also
own a private local scope for its direct entries and supported inline merges. Those inner entries
MUST resolve for consumers inside the nested dictionary and MUST NOT leak to outside consumers.

The project-global App/Generic cascade SHALL visit only direct entries of the active root resource
dictionary and recursively reachable inline/external merged dictionaries. Keys or merge links
inside `Style.Resources`, a keyed nested dictionary, or any other private nested resource scope
SHALL NOT leak into the project-global snapshot.
An external merged target SHALL have a `ResourceDictionary` document root; any other root shape
SHALL make the cascade incomplete and SHALL NOT be reinterpreted as a project-global resource
owner.

Only `missing` SHALL emit `xaml-resource-finding` / `XAMLRESOURCE001`. Every other unresolved
status MAY be retained as `xaml-resource-outcome`, but SHALL not use a finding flavor.

#### Scenario: Complete static key is missing

- **GIVEN** a complete static resource cascade with no declaration for key `Absent`
- **WHEN** `{StaticResource Absent}` is indexed
- **THEN** the result is `missing`, exactly one `XAMLRESOURCE001` finding is emitted, and no target
  symbol or resource edge is created

#### Scenario: Duplicate static key is ambiguous

- **GIVEN** two proven visible declarations for the same static key
- **WHEN** that key is indexed
- **THEN** the result is `ambiguous`, both candidates remain queryable, and no finding or target
  edge is emitted

#### Scenario: Sibling-local resource is not visible

- **GIVEN** a key is declared in `StackPanel.Resources`
- **WHEN** a sibling of that `StackPanel` references the key
- **THEN** that declaration is not selected and no exact local-resource edge is emitted

#### Scenario: Repeated local keys retain distinct identities

- **GIVEN** two sibling resource scopes each declare `Accent` and contain their own consumer
- **WHEN** the document is indexed
- **THEN** two distinct resource symbols are stored and each consumer's edge targets the
  declaration in its own ancestor scope

#### Scenario: Private collision does not rename the global resource

- **GIVEN** an application-global `Accent` and a nested `Style.Resources` declaration use the same
  key in one document
- **WHEN** consumers inside and outside the style are indexed
- **THEN** the global declaration retains its compatible canonical key, the private declaration is
  discriminated, and each consumer targets the declaration visible in its own scope

#### Scenario: Local merge source is not modeled

- **GIVEN** a view-local resource scope merges a pack, cross-project, or otherwise unmodeled
  dictionary source
- **WHEN** a key has no directly known declaration
- **THEN** the result is `incomplete`, not project-global `missing`

#### Scenario: Nested style resource stays private

- **GIVEN** `App.xaml` declares a key inside `Style.Resources`
- **WHEN** an unrelated view references that key
- **THEN** the nested key is absent from the project-global snapshot and no exact resource edge is
  emitted

#### Scenario: Keyed nested dictionary has a private scope

- **GIVEN** a keyed nested dictionary declares key `Inner` and an inner style references it
- **WHEN** both an inner and an unrelated outer consumer are indexed
- **THEN** only the inner consumer resolves `Inner`; the outer consumer receives no exact edge to
  the private declaration

#### Scenario: External merge target has the wrong root shape

- **GIVEN** an active project-global merge points to a document rooted at `UserControl` or another
  non-`ResourceDictionary` element
- **WHEN** a key is requested
- **THEN** the cascade is `incomplete`; that document's local resources do not become global

#### Scenario: Unsupported merge prevents a missing claim

- **GIVEN** the project cascade contains a pack, cross-project, remote, or otherwise unsupported
  merge source
- **WHEN** a static key has no known candidate
- **THEN** the result is `incomplete`, not `missing`, and no `XAMLRESOURCE001` finding is emitted

#### Scenario: Runtime resource lookup is unsupported

- **WHEN** a XAML attribute uses `DynamicResource` or `ThemeResource`
- **THEN** the result is `unsupported`, with no resource finding and no static target edge

### Requirement: Conservative binding and command resolution

The binding/command resolver SHALL return an explicit outcome rather than a nullable target.
A missing member is proven only when all of the following hold:

1. exactly one DataContext or `x:DataType` CLR type is known;
2. the binding is ordinary runtime `{Binding}`;
3. the path is a non-empty dot-separated sequence of simple identifiers;
4. no explicit `Source`, `RelativeSource`, or `ElementName` source resolver is required;
5. the Roslyn compilation is available and contains no error diagnostics; and
6. the supported public instance-property walk finds no member for one segment.

For a multi-target project, all Roslyn project iterations with the same project-file path SHALL be
examined. A semantic resolver MAY select one iteration only when exactly one clean iteration has
explicit WPF framework evidence (`System.Windows.Application`). Multiple WPF-capable iterations,
failed iterations, or multiple iterations without a uniquely evidenced WPF target SHALL remain
`incomplete` rather than selecting solution enumeration order.

The privacy-sanitized semantic universe SHALL be compared with the raw workspace input for every
matching target iteration and the transitive `ProjectReference` closure. Removal of any source,
additional, analyzer-config, or referenced-project input makes semantic resolution incomplete.
Generated-looking filenames and SDK-, MSBuild-, or `obj/`-shaped paths SHALL NOT be treated as
proof of provenance. If such a raw source, additional, or analyzer-config input is absent from the
sanitized workspace, semantic resolution SHALL fail closed like any other removed input.

Every project in that reference closure SHALL have complete generator output. Source-generator
discovery failure, a generator exception, or `CS8784`/`CS8785` anywhere in the closure makes the
semantic universe unsafe; no binding, command, DataContext-association, or event-handler edge may
then be emitted even when the remaining compilation appears to contain one target. An error in a
referenced-project compilation likewise blocks all semantic edges from its consumers. Errors in
the matching root project make absence claims incomplete, but MAY retain an exact positive edge
or proven ambiguity when generator output is complete and the target symbols are still present.
Production construction SHALL require an explicit semantic-input completeness probe; omitting the
probe SHALL fail closed. Analyzer-load or generator-discovery failure observed during the first
compilation probe SHALL remain negative evidence for the lifetime of that workspace generation;
later cached compilation access SHALL NOT erase it. Only a structurally re-opened workspace may
start a new completeness generation.

After a successful C# live-index batch with no failed file or project, every scope-approved XAML
consumer SHALL be conservatively refreshed (dependency-graph-narrower fanout is allowed when it
is proven complete). If the Roslyn batch fails, the dispatcher SHALL NOT perform this semantic
fanout and SHALL retain the last successful XAML facts.

No known DataContext SHALL be `unknown`. An unavailable compilation or a missing member in a
compilation with errors SHALL be `incomplete`. Explicit source shapes, compiled binding, indexer
syntax, attached-property path syntax, and other complex paths SHALL be `unsupported`. Multiple
viable context types or members SHALL be `ambiguous`.
A markup extension used as the value of an attached-property attribute SHALL still be analyzed;
the attribute's attached-property classification SHALL NOT suppress a supported `Binding` or
`StaticResource` value. A `StaticResource` with an empty or non-literal key SHALL produce an
`unsupported` outcome with a stable non-empty reason and no synthetic target.
A DataContext binding that resolves a property whose result cannot be modeled as a named CLR type
(for example an array, dynamic value, or type parameter) SHALL be `unsupported`; it SHALL NOT
return `resolved` with a null context.

For a `Command` attribute, the unique terminal property SHALL also implement
`System.Windows.Input.ICommand`. A unique non-command property SHALL be `unsupported`, not
`missing`.

Only `missing` SHALL emit a structured `xaml-binding-finding` / `XAMLBINDING001` or
`xaml-command-finding` / `XAMLCOMMAND001`. Other unresolved statuses MAY be retained as
non-finding outcome annotations. No unresolved result SHALL create a target symbol or
`binds-path` edge.

#### Scenario: Proven binding member is missing

- **GIVEN** one known DataContext, a complete compilation, and `{Binding Missing.Name}`
- **WHEN** `Missing` is absent from the known type
- **THEN** the result is `missing`, a binding finding is emitted, and no target is synthesized

#### Scenario: Context is unknown

- **GIVEN** a binding with no statically known DataContext or `x:DataType`
- **WHEN** its path is indexed
- **THEN** the result is `unknown` and no binding finding is emitted

#### Scenario: Compilation is incomplete

- **GIVEN** a known DataContext type but a compilation containing error diagnostics
- **WHEN** a path segment is not found
- **THEN** the result is `incomplete`, not `missing`, and no finding is emitted

#### Scenario: Sanitized input would expose a different inherited member

- **GIVEN** the remaining compilation resolves `Base.Existing`, but an excluded partial input may
  declare a hiding `Derived.Existing`
- **WHEN** the view binds `Existing`
- **THEN** the result is `incomplete` and no guessed edge to the base member is emitted

#### Scenario: Multi-target WPF selection is unique

- **GIVEN** multiple Roslyn iterations share one project path and exactly one clean iteration
  contains `System.Windows.Application`
- **WHEN** XAML semantic resolution begins
- **THEN** that WPF iteration is selected; if more than one iteration has WPF evidence, resolution
  remains incomplete

#### Scenario: C# edit refreshes stored XAML semantics

- **GIVEN** a stored XAML binding finding or edge depends on a C# member
- **WHEN** a successful C# live batch adds, removes, or renames that member
- **THEN** the XAML file is reindexed in the same batch and its stored finding/edge transitions
  accordingly

#### Scenario: Context or member is ambiguous

- **WHEN** multiple CLR types match the declared context token or multiple viable properties match
  a path segment
- **THEN** the result is `ambiguous`, candidates remain queryable, and no finding or edge is emitted

#### Scenario: Referenced project compilation is broken

- **GIVEN** the view project compiles but its referenced ViewModel project has an error or failed
  source generator
- **WHEN** a binding appears absent or uniquely resolved in the consumer compilation
- **THEN** the result is `incomplete`, with no semantic edge or missing finding

#### Scenario: Bound DataContext has an unsupported type shape

- **GIVEN** a nested DataContext uses `{Binding Items}` and `Items` has an array or type-parameter
  type
- **WHEN** a descendant binding is analyzed
- **THEN** the descendant context outcome is `unsupported`, never targetless `resolved`

#### Scenario: Binding property is inherited from a base interface

- **GIVEN** `x:DataType` names a derived interface whose base interface declares the bound public
  property
- **WHEN** the binding is indexed against a complete compilation
- **THEN** the base-interface property is resolved and no missing finding is emitted

#### Scenario: Command property has the wrong type

- **GIVEN** a command binding whose terminal member is a unique non-`ICommand` property
- **WHEN** it is resolved
- **THEN** the result is `unsupported` and no command finding or target edge is emitted

#### Scenario: Attached property value retains semantic analysis

- **GIVEN** an attached property such as `Validation.ErrorTemplate` contains a supported
  `StaticResource`, or another attached property contains a supported `Binding`
- **WHEN** the owning XAML element is indexed
- **THEN** the attached-property annotation and the resource or binding result are both retained

#### Scenario: Empty static-resource key is unsupported

- **GIVEN** a XAML attribute contains `{StaticResource}` without a literal key
- **WHEN** the value is indexed
- **THEN** the result is `unsupported` with a non-empty reason and no target edge

### Requirement: Evidence-preserving WPF query tools

The server SHALL expose read-only `trace_binding`, `trace_command`, and `check_resources` tools.
They SHALL route through the requested scope, wait for a usable scope snapshot, apply the shared
output budget, and return structured rows backed by stored edge evidence or outcome annotations.
They SHALL preserve all six resolution statuses and a non-empty reason, distinguish not-found
from ambiguous input without guessing, and disclose partial/degraded scope state.
Multi-scope execution SHALL retain one typed structured scope block for every selected scope,
including empty and degraded scopes; prose aggregation SHALL NOT discard the structured rows.
Candidate discovery SHALL apply the XAML-kind predicate before its result limit so unrelated C#
symbols cannot crowd out an existing XAML element. The shared output budget SHALL cover the fully
materialized response (prose, structured rows, and evidence), not merely the number of result rows;
when evidence is omitted or truncated the response SHALL disclose that fact.
Named WPF tools SHALL reject a selection above 200 scopes before fan-out with a bounded diagnostic.
For an accepted selection, every resolved scope SHALL retain a typed scope summary. Repeated
per-scope prose or optional diagnostic notes MAY be compacted when the typed summaries already
retain provenance and status, provided the response marks the compaction as partial/truncated.
Scope-routing and degraded-scope diagnostics SHALL themselves be bounded by the same response
contract.

`check_resources` SHALL report resource relations emitted from XAML elements, views, resources,
styles, and templates, including both `uses-resource` and `applies-style`. A file filter SHALL
match a normalized complete path suffix on a segment boundary; `View.xaml` SHALL NOT match
`Preview.xaml`.

For an `ElementName` binding whose lower-level graph stores
`binding-owner --binds-element(path=P)--> named-element`, `trace_binding` SHALL join that edge to
known WPF framework metadata and return `FrameworkElement.P` when the property is known. If
relevant lower-level edges or outcome annotations exist but the high-level row cannot be
materialized, the result SHALL be `unknown`, completeness SHALL be partial, and
`absence_authoritative` SHALL be false.

#### Scenario: ElementName ActualWidth resolves from a canonical binding owner

- **GIVEN** an anonymous XAML element has a `binds-element` edge with
  `element-name=imageview` and `path=ActualWidth`
- **WHEN** `trace_binding` is called with the anonymous element's canonical key and
  `binding=ActualWidth`
- **THEN** the result contains the stored occurrence evidence and targets
  `System.Windows.FrameworkElement.ActualWidth`

#### Scenario: Unsupported binding remains unsupported through the tool

- **GIVEN** an indexed binding outcome with status `unsupported`
- **WHEN** `trace_binding` returns that occurrence
- **THEN** the structured row retains `unsupported` and its stable non-empty reason

#### Scenario: Resource-to-resource edge is queryable

- **GIVEN** an application resource references a resource declared in another contributor
- **WHEN** `check_resources` runs
- **THEN** it returns the actual relation and occurrence evidence even though the source is not a
  view or element

#### Scenario: Multi-scope structured results survive aggregation

- **GIVEN** two selected scopes each contain a WPF result or a disclosed empty/degraded result
- **WHEN** a WPF query tool runs across both scopes
- **THEN** the structured response contains two typed scope blocks and remains within the
  materialized output budget

### Requirement: Semantic confidence ceiling

A resolved DataContext association, binding-property edge, or event-handler edge depends on
Roslyn semantic interpretation and SHALL carry confidence no stronger than `semantic`.
Purely syntactic evidence such as an `x:Class` token or a local static resource declaration MAY
remain `exact`.

#### Scenario: Resolved binding and event handler

- **WHEN** a binding property and code-behind event handler each resolve uniquely
- **THEN** their emitted edge evidence confidence is `semantic`, not `exact`
