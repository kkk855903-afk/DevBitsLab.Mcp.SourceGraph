## MODIFIED Requirements

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

## ADDED Requirements

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
