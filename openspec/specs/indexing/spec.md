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
`RoslynLanguageIndexer` is registered automatically for `.cs`.

#### Scenario: Index a fresh solution end-to-end
- **WHEN** `sourcegraph-mcp index <solution>` is invoked against a solution
  whose graph DB is empty or absent
- **THEN** every regular `.cs` document with `File.Exists(path) == true` is
  dispatched to `RoslynLanguageIndexer`, plus any document whose extension
  matches a third-party `ILanguageIndexer`; an `IndexResult` is returned
  with the per-language file counts merged

#### Scenario: Document with no matching language indexer
- **WHEN** the workspace contains a file whose extension has no registered
  `ILanguageIndexer`
- **THEN** the file is skipped with a debug log and no error

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
  whose SHA matches the stored value is skipped in pass 1

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

### Requirement: Capture every attribute on indexed symbols
The indexer SHALL record every attribute (`ISymbol.GetAttributes()`) attached to an indexed symbol, including its short name, fully qualified name, constructor arguments, and named arguments.

#### Scenario: Method with a route attribute
- **WHEN** an indexed method is decorated `[HttpGet("/api/users")]`
- **THEN** an `attributes` row is written with `name = "HttpGet"`, `full_name = "Microsoft.AspNetCore.Mvc.HttpGetAttribute"`, `args_json` whose `ctor[0]` is the literal string `"/api/users"`, and `attribute_symbol_id` linking back to the user-defined attribute symbol if it's in the graph (else `NULL`)

#### Scenario: Multiple attributes
- **WHEN** a symbol has `[Authorize, Obsolete("Use Foo")]`
- **THEN** two `attributes` rows are written, in source order

### Requirement: Attribute reconciliation on file reindex
When a file is reindexed, the indexer SHALL delete every `attributes` row attached to that file's symbols before reinserting the new attribute set, in the same transaction as the symbol-set reconciliation.

#### Scenario: Attribute removed from source
- **WHEN** a file is edited to remove `[Obsolete]` from a method
- **THEN** after the live reindex, no `attributes` row remains for that method with `name = "Obsolete"`

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
