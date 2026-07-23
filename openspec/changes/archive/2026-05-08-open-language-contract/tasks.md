## 1. SDK contract — kind vocabulary

- [x] 1.1 Delete `src/DevBitsLab.Mcp.SourceGraph.Core/EdgeKind.cs` and remove every reference to the enum across the solution
- [x] 1.2 Delete `PluginSymbolKind` enum from `src/DevBitsLab.Mcp.SourceGraph.Sdk/IndexEvent.cs`
- [x] 1.3 Add `EdgeKinds` static class to the SDK with kebab-case constants: `Calls`, `Inherits`, `Implements`, `UsesType`, `OverridesMember`, `ImplementsMember`, `Instantiates`, `Throws`, `Tests`
- [x] 1.4 Add `SymbolKinds` static class to the SDK with kebab-case constants matching the existing 14 values (`Namespace`, `Class`, ..., `Record`)
- [x] 1.5 Update `IndexEvent.SymbolDeclared` so its `Kind` property is `string` (not `PluginSymbolKind`)
- [x] 1.6 Update `IndexEvent.EdgeEmitted` so its `EdgeKindName` property is `string` and add `IReadOnlyDictionary<string, string>? Metadata` as the trailing parameter
- [x] 1.7 Update `Core/Models.cs` `Edge` record so its `Kind` is `string` and add an optional `IReadOnlyDictionary<string, string>? Metadata`

## 2. SDK contract — canonical keys, annotations, language project

- [x] 2.1 Add `CanonicalKeyValidator` to the SDK (or a `Sdk/Validation/` folder): validates `<scheme>:<rest>`, kebab-case scheme, reserved-and-enforced set `{ "csharp", "xaml" }`, repo-relative forward-slash paths
- [x] 2.2 Document the reserved-but-not-yet-enforced scheme list (`vbnet`, `fsharp`, `razor`, `js`, `ts`, `jsx`, `tsx`, `vue`, `svelte`) in the SDK XML doc comments on `CanonicalKeyValidator`
- [x] 2.3 Add `KebabCaseValidator` to the SDK and have `EdgeEmitted` / `SymbolDeclared` / `AnnotationAttached` constructors validate kind / flavor strings against it (throw `ArgumentException` on violation)
- [x] 2.4 Replace `IndexEvent.AttributeAttached` with `IndexEvent.AnnotationAttached(string SymbolCanonicalKey, string AnnotationName, string Flavor, string? FullName = null, string? ArgsJson = null, string? TargetCanonicalKey = null)`
- [x] 2.5 Add `ILanguageProject { string Id; IReadOnlyCollection<string> FilePaths }` interface to the SDK
- [x] 2.6 Add `ILanguageProjectFactory { IReadOnlyCollection<string> ProjectMarkers; Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(string repoRoot, CancellationToken ct) }` interface
- [x] 2.7 Update `IndexContext` constructor and properties to include `ILanguageProject? Project`

## 3. Storage layer — schema

- [ ] 3.1 Bump `Schema.Version` from `5` to `6` in `src/DevBitsLab.Mcp.SourceGraph.Storage/Schema.cs`
- [ ] 3.2 Update `Schema.V1` / `Schema.V2` SQL: change `edges.kind INTEGER` → `kind_name TEXT NOT NULL`; add `payload TEXT NULL` column on `edges`; add `idx_edges_kind_name` index
- [ ] 3.3 Update `Schema` SQL: change `symbols.kind INTEGER` → `kind_name TEXT NOT NULL`; add `idx_symbols_kind_name` index
- [ ] 3.4 Replace `attributes` / `attributes_fts` table schema with `annotations` / `annotations_fts`; add `flavor TEXT NOT NULL` column and `idx_annotations_flavor` index
- [ ] 3.5 Verify `EnsureSchemaAsync`'s drop-and-rebuild path correctly nukes a v5 DB and recreates the v6 schema (mechanism is already there per `Self-applying schema migrations` requirement; only the version bump should be needed)

## 4. Storage layer — IGraphStore + SqliteGraphStore

- [x] 4.1 Update every `IGraphStore` method that takes / returns a kind to use `string` instead of an enum (e.g. `BulkInsertEdgesAsync`, `ListCallersAsync`, `ListCalleesAsync`)
- [x] 4.2 Add `payload` parameter handling to `BulkInsertEdgesAsync`: when `Edge.Metadata` is non-null, serialise to JSON and write to `payload`; when null, write SQL `NULL`
- [x] 4.3 Replace `BulkInsertAttributesAsync` with `BulkInsertAnnotationsAsync` whose row type carries `flavor`
- [x] 4.4 Replace `FindByAttributeAsync` with `FindByAnnotationAsync(string name, string? flavor, string? argSubstring, string? kindFilter, int limit)` — `flavor = null` matches across all flavors; result rows carry the matched flavor
- [x] 4.5 Update every `IGraphStore` query that previously joined to `attributes` / `attributes_fts` to join to `annotations` / `annotations_fts` instead
- [x] 4.6 Update query result types (`SymbolHit`, `EdgeHit`, etc.) where they expose kind as enum — switch to string
- [ ] 4.7 Add a covering index probe / EXPLAIN QUERY PLAN check to ensure `WHERE kind_name = ?` resolves through the new index, not a scan _(deferred — `idx_edges_kind_name` and `idx_symbols_kind_name` indexes added in Schema, but explicit EXPLAIN benchmark not yet run)_

## 5. Indexing layer

- [x] 5.1 Create `src/DevBitsLab.Mcp.SourceGraph.Indexing/MSBuildLanguageProject.cs` implementing `ILanguageProject`, wrapping a `Microsoft.CodeAnalysis.Project`; `Id` = absolute project path; `FilePaths` = enumerated `Documents.FilePath` values
- [x] 5.2 Create `MSBuildLanguageProjectFactory` implementing `ILanguageProjectFactory`; `ProjectMarkers = ["*.csproj", "*.fsproj", "*.vbproj", "*.sln", "*.slnx"]`; `DiscoverAsync` returns one `MSBuildLanguageProject` per loaded `Project` in the workspace
- [x] 5.3 Wire `MSBuildLanguageProjectFactory` into the C# pathway so `IndexContext` for every dispatched `.cs` document carries the matching project _(landed in `xaml-language-indexer` change — `LiveIndexService.OpenScopeAsync` constructs a per-scope `MSBuildLanguageProjectFactory(workspace)` after `RoslynIndexer.OpenAsync` and registers it alongside the in-tree XAML factory; the resulting projects feed `ScopeHost.ProjectByFilePath`, which the new `LanguageIndexerDispatcher` reads to populate `IndexContext.Project` for every dispatched non-C# file. The C# bulk path keeps its workspace-aware solution walk for back-compat.)_
- [x] 5.4 Update `RoslynIndexer.IndexCoreAsync` to emit `EdgeKinds.*` string constants (not `EdgeKind.*` enum values) into all edge batches
- [x] 5.5 Update `RoslynIndexer` symbol emissions to use `SymbolKinds.*` constants
- [x] 5.6 Replace every `AttributeExtractor.AppendAttributes` / `BulkInsertAttributesAsync` site with `AppendAnnotations` / `BulkInsertAnnotationsAsync` that emits `Flavor = "csharp-attribute"`
- [x] 5.7 Update `RoslynIndexer.SymbolMapping.CanonicalKey(...)` to prefix every emitted key with `"csharp:"` (e.g. `T:Foo` → `csharp:T:Foo`)
- [x] 5.8 Update `RoslynIndexer.HydrateMapsFromStoreAsync` to expect `csharp:`-prefixed keys (no migration needed — schema bump drops the old data first, but assert the prefix on hydrate to catch mistakes early)
- [x] 5.9 Update `RoslynIndexer.ExtractEventsFromSyntaxTree` (the `ILanguageIndexer.IndexAsync` per-document path) to also emit prefixed canonical keys and string-typed kinds

## 6. Server — plugin host + initialize response

- [x] 6.1 Update `PluginHost` (or `LanguageIndexerRegistry`) to discover `ILanguageProjectFactory` instances from registered plugins (alongside the existing `ILanguageIndexer` discovery) and run `DiscoverAsync` per scope at startup _(landed in `xaml-language-indexer` — `PluginHost.LoadPluginAsync` now activates `ILanguageProjectFactory` types from plugin assemblies; new `LanguageProjectFactoryRegistry` sits alongside `LanguageIndexerRegistry` and feeds `LanguageIndexerDispatcher.BuildProjectMapAsync`, invoked once per scope at startup.)_
- [x] 6.2 Build a per-scope `Dictionary<string, ILanguageProject>` keyed by file path so the dispatcher can populate `IndexContext.Project` cheaply _(landed in `xaml-language-indexer` — `ScopeHost.ProjectByFilePath` is the per-scope map; `LanguageIndexerDispatcher.BuildProjectMapAsync` populates it; `DispatchOneAsync` reads it to set `IndexContext.Project` on every dispatched document.)_
- [x] 6.3 Add a `VocabularyCollector` (or extend `StartupLogging`) that gathers the active scope's edge kinds, symbol kinds, and annotation flavors from registered indexers' constants and from a one-shot `SELECT DISTINCT kind_name FROM edges` / `... FROM symbols` / `... flavor FROM annotations` against existing storage _(landed as `ServerVocabulary.cs` reflecting on `EdgeKinds`/`SymbolKinds` and querying `GetDistinct{Edge,Symbol}KindsAsync` + `GetDistinctAnnotationFlavorsAsync` per scope)_
- [x] 6.4 Extend the MCP `initialize` response builder so it adds `edge_kinds`, `symbol_kinds`, `annotation_flavors` arrays alongside the existing `ServerInstructions` field; arrays sorted, lowercase, deduped _(wired via `ServerCapabilities.Experimental["sourcegraph.vocabulary"]`)_
- [x] 6.5 Suppress vocabulary arrays when `--no-instructions` / `SOURCEGRAPH_NO_INSTRUCTIONS=1` is in effect (mirror existing behaviour)

## 7. Server — tools

- [x] 7.1 Update `list_callers` / `list_callees` tool parameter docs and runtime handling to accept the kebab-case kind strings (`calls`, `uses-type`, `overrides-member`, `implements-member`, `instantiates`, `throws`, `tests`, `all`)
- [x] 7.2 When `list_callers` / `list_callees` receives an unknown kind, return an empty result with a one-line note that the kind is not in the active scope's published `edge_kinds` vocabulary
- [x] 7.3 Rename the `find_by_attribute` MCP tool to `find_by_annotation`; add a `flavor` parameter (default `null` = match across all flavors); update the parameter docs
- [x] 7.4 Update `ToolDescriptionFormatter` to render `annotations:` (not `attributes:`) lines on results from `find_definition`, `list_symbols_in_file`, `neighborhood`, `module_summary`
- [x] 7.5 When the active scope has more than one annotation flavor present, suffix each rendered annotation with its flavor in parentheses; otherwise omit the suffix

## 8. Tests

- [x] 8.1 Update `tests/DevBitsLab.Mcp.SourceGraph.Tests/` to use `EdgeKinds.*` / `SymbolKinds.*` constants; assertions on enum values become string equality
- [x] 8.2 Update tests that referenced `attributes` table or `FindByAttributeAsync` to use the new `annotations` / `FindByAnnotationAsync` shape
- [x] 8.3 Add tests for `CanonicalKeyValidator`: accepts `csharp:T:Foo` and `xaml:element:Views/Main.xaml#X`; rejects unknown schemes (e.g. `python:M:foo`), backslash paths, missing scheme, empty key _(landed in `ValidatorTests.cs`)_
- [x] 8.4 Add tests for `KebabCaseValidator`: accepts `calls`, `binds-path`; rejects empty, whitespace, `RendersComponent`, `renders_component` _(landed in `ValidatorTests.cs`)_
- [x] 8.5 Add a test that emits `EdgeEmitted` with `Metadata` and round-trips through storage — `payload` column populated on insert, dictionary recovered on read _(landed in `EdgeMetadataRoundTripTests.cs`)_
- [x] 8.6 Add a test that opens a v5-schema DB and confirms it is dropped + rebuilt to v6 _(landed in `SchemaVersionRebuildTest.cs`, adapted for v10 → v11)_
- [x] 8.7 Add a test against a fixture solution that asserts every emitted canonical key starts with `csharp:` _(landed in `CSharpKeyPrefixTests.cs`)_
- [ ] 8.8 Add an integration test that drives `MCP initialize` against a freshly-started server and asserts `edge_kinds` / `symbol_kinds` / `annotation_flavors` arrays are present, sorted, deduped _(deferred — vocabulary plumbing is unit-tested via `ServerVocabulary` indirectly; full MCP-init integration test would require an out-of-process harness)_
- [ ] 8.9 Add a `--no-instructions` regression test confirming the vocabulary arrays are also suppressed _(deferred — paired with 8.8)_

## 9. Validation and finishing

- [x] 9.1 Run `openspec validate open-language-contract --strict` and resolve any reported issues _(`Change 'open-language-contract' is valid`)_
- [x] 9.2 Run `dotnet build` from repo root and resolve every compile error introduced by the contract rename _(full solution: 0 warnings, 0 errors)_
- [x] 9.3 Run the test suite (`dotnet test`) and resolve every test that broke _(175 passed, 0 failed, 0 skipped)_
- [ ] 9.4 Smoke-test against `tests/fixtures/Sample.sln` end-to-end: cold index → `find_definition` → `list_callers --kind calls` → `find_by_annotation --name Fact --flavor csharp-attribute` _(deferred — the underlying paths are exercised by `IndexFixtureTests` and the new in-process tests; full out-of-process smoke deferred to a manual run)_
- [ ] 9.5 Smoke-test against the multi-scope fixture (`tests/fixtures/MultiScope/`) confirming each scope publishes its own vocabulary in `initialize` _(deferred — paired with 9.4)_
- [ ] 9.6 Update CHANGELOG.md with the SDK rename / version bump and the kind name list _(no CHANGELOG.md exists in the repo; not creating one unprompted — version-bump notes live in the SDK csproj XML doc and this proposal)_
- [x] 9.7 Bump SDK package version in `src/DevBitsLab.Mcp.SourceGraph.Sdk/DevBitsLab.Mcp.SourceGraph.Sdk.csproj` and the matching server tool version _(SDK 1.1.0 → 2.0.0; server 0.7.0 → 0.8.0)_
