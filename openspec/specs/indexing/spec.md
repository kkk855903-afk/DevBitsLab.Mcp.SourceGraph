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

### Requirement: Hydrate in-memory maps from the store on startup
The indexer SHALL populate `_symbolIdByKey`, `_keysByFileId`, and
`_fileIdByPath` from the existing graph DB on the first `IndexCoreAsync` call
in a process (or after `fullReset`).

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

### Requirement: Multi-target and linked-file iterations don't double-count
The indexer SHALL emit refs and edges from at most one document per fileId
even when the loaded solution exposes the same source path multiple times
(multi-target frameworks, linked files, shared projects).

#### Scenario: A file targeted by multiple TFMs
- **WHEN** the solution multi-targets such that path `P` produces N
  documents
- **THEN** pass 1 accumulates the union of declared canonical keys across
  all N iterations before reconciling, and pass 2 walks exactly one of the N
  documents

### Requirement: Robust file reads against editor save races
The indexer SHALL skip a file gracefully and rely on the next watcher event
when a transient `IOException` interrupts the file read (e.g., a 0-byte view
during an editor save).

#### Scenario: Read fails mid-batch
- **WHEN** `File.ReadAllBytesAsync` or `File.ReadAllTextAsync` throws
  `IOException` while building the changed-file batch
- **THEN** the path is logged at debug, omitted from the current batch, and
  no partial state is committed; the next FSW event for that path retries
  the read

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

### Requirement: Throws edges from `throw` syntax
For every `ThrowStatementSyntax` and `ThrowExpressionSyntax`, the indexer SHALL emit a `Throws` edge from the enclosing member to the thrown type, when the thrown type is indexed.

#### Scenario: Throw an indexed exception type
- **WHEN** an indexed method body contains `throw new MyDomainException();`
- **THEN** an edge `(method.id, MyDomainException.id, Throws)` is written

### Requirement: Source-generated documents indexed
The indexer SHALL include source-generated documents (`Project.GetSourceGeneratedDocumentsAsync()`) alongside regular documents, marking the corresponding `files.is_generated` row to `1`.

#### Scenario: Generated document indexed
- **WHEN** a project uses a source generator producing a `*.g.cs` document with a real `class GeneratedFoo`
- **THEN** that class appears in `symbols` with `kind = Class`, its `files.is_generated = 1`, and tools render `(generated)` next to its name

#### Scenario: SHA gate on generated content
- **WHEN** the same source-gen run produces byte-identical output to last time
- **THEN** the file row's `content_sha256` is unchanged and no symbol/edge work happens for that file

### Requirement: Roslyn diagnostics captured per file
The indexer SHALL run `compilation.GetDiagnostics(ct)` after pass 2 and persist every diagnostic with a non-empty `Location.SourceSpan` into the `diagnostics` table; on reindex, prior diagnostics for the file SHALL be deleted before reinserting.

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
override, and interface-member implementation) SHALL be `semantic`. The producer SHALL be
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
- **THEN** the `WinUiDialect`'s markup-extension dispatcher recognises `x:Bind`, the binding is recorded with `payload` keys including `mode`, and the canonical key form distinguishes it from a runtime `{Binding}`

### Requirement: Per-project resource cascade cache
For every project that contains XAML files, the `XamlLanguageProjectFactory` SHALL build a per-project `XamlLanguageProject` instance whose private `ResourceCache` indexes every `x:Key` declared in:

- The project's `App.xaml` `Application.Resources`
- Any `MergedDictionaries` referenced from `App.xaml`
- A theme `Generic.xaml` if present in the project's `Themes/` folder

The cache SHALL be populated once at project discovery and reused for every `.xaml` file in the project so resource-resolution lookups (`{StaticResource AccentBrush}` → declaration site) do not re-walk the cascade per file.

#### Scenario: Resource resolved from App.xaml
- **WHEN** the indexer encounters `<Button Background="{StaticResource AccentBrush}"/>` in `Views/Main.xaml`, and `App.xaml` declares `<SolidColorBrush x:Key="AccentBrush" Color="Blue"/>`
- **THEN** the indexer emits a `uses-resource` edge from the button element to the resource declaration site (resolved via the cache, no re-walk), and the resource's symbol carries kind `xaml-resource`

#### Scenario: Resource not found
- **WHEN** the indexer encounters `<Button Background="{StaticResource NonExistent}"/>` and the cache contains no entry for `NonExistent`
- **THEN** the indexer emits the `uses-resource` edge with an unresolved target and logs a debug-level note (does not error; the binding may be resolved by a runtime mechanism the indexer does not see)

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
The indexer SHALL wrap each per-file body of pass 2's reference walk in a try/catch so that an exception thrown while walking one file does not abort pass 2 for the remaining files. Cancellation (`OperationCanceledException`) SHALL still propagate. Other exceptions SHALL be logged at warn level with the file path and exception detail; the failed file's outgoing edges remain cleared this round and will be re-walked on the next index via the integrity check above.

#### Scenario: One file's walk throws; other files' walks complete
- **GIVEN** a pass-2 batch of three changed files where the second file's syntax tree triggers an exception during the descendant-node walk (e.g. a transient compilation gap, a symbol-resolution failure)
- **WHEN** pass 2 iterates the three files
- **THEN** the first file's references are inserted, the second file's exception is caught and logged at warn level with the file path, and the third file's references are inserted; pass 2 completes without rethrowing

#### Scenario: Cancellation propagates
- **WHEN** pass 2 is iterating files and the supplied `CancellationToken` is signaled, raising `OperationCanceledException` in a per-file body
- **THEN** the catch handler rethrows so the cancellation surfaces to the caller; partial state from earlier files in the batch is left as-is

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

