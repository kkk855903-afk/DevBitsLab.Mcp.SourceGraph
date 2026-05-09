## ADDED Requirements

### Requirement: Open kind vocabularies on the SDK
The SDK SHALL expose edge kinds and symbol kinds as `string` values at the plugin contract boundary, NOT as closed enums. Static `EdgeKinds` and `SymbolKinds` classes SHALL provide kebab-case constants for the values defined by the host (`"calls"`, `"inherits"`, `"implements"`, `"uses-type"`, `"overrides-member"`, `"implements-member"`, `"instantiates"`, `"throws"`, `"tests"` for edges; `"namespace"`, `"class"`, `"interface"`, `"struct"`, `"enum"`, `"delegate"`, `"method"`, `"constructor"`, `"property"`, `"field"`, `"event"`, `"enum-member"`, `"operator"`, `"record"` for symbols). Plugins MAY emit any kebab-case identifier; the host stores the kind as TEXT and does NOT reject unknown kebab-case kinds.

#### Scenario: Built-in indexer emits a known kind via constants
- **WHEN** the C# Roslyn indexer emits an edge between two methods at a call site
- **THEN** the emitted `EdgeEmitted.EdgeKindName` equals `EdgeKinds.Calls` (the literal string `"calls"`), and the storage layer persists the row with that string

#### Scenario: Plugin emits a previously unknown kind
- **WHEN** a plugin emits an `EdgeEmitted` with `EdgeKindName = "renders-component"`
- **THEN** the host accepts it, stores the row with `kind_name = "renders-component"`, and a subsequent query filtered on that kind returns it

#### Scenario: Non-kebab-case kind rejected
- **WHEN** a plugin emits an `EdgeEmitted` with `EdgeKindName = "RendersComponent"` or `"renders_component"` or `""`
- **THEN** the host throws `ArgumentException` at emission time, identifying the offending kind value, before the row reaches storage

### Requirement: Per-edge metadata channel
`EdgeEmitted` SHALL expose an optional `IReadOnlyDictionary<string, string>? Metadata` property that plugins use to attach per-edge facts (binding paths, event names, prop names, …). When non-null, the host SHALL persist the dictionary as a JSON object on the storage row's `payload` column. When null, no payload is stored.

#### Scenario: Edge emitted without metadata
- **WHEN** a plugin emits `EdgeEmitted(src, dst, "calls", Metadata: null)`
- **THEN** the resulting `edges` row has `payload IS NULL`

#### Scenario: Edge emitted with metadata
- **WHEN** a plugin emits `EdgeEmitted(src, dst, "binds-path", Metadata: { ["path"] = "User.Name", ["mode"] = "two-way" })`
- **THEN** the resulting `edges` row has `payload` equal to the JSON serialization of that dictionary, and a query for the row deserializes the same key-value pairs

### Requirement: Canonical-key URI convention
Every canonical key emitted by an `ILanguageIndexer` SHALL match the format `<scheme>:<rest>`, where `<scheme>` is one of the reserved-and-enforced schemes at this SDK version (`csharp`, `xaml`). Schemes `vbnet`, `fsharp`, `razor`, `js`, `ts`, `jsx`, `tsx`, `vue`, `svelte` are documented as reserved-for-future-use but are NOT yet accepted by the host; emissions using those schemes SHALL be rejected at v1.

`<rest>` SHALL be plugin-defined, but any path component embedded in `<rest>` SHALL be repo-relative (resolved against `Scope.Root`) and SHALL use forward slashes regardless of operating system.

#### Scenario: Built-in C# indexer emits a Roslyn-shaped key
- **WHEN** the Roslyn indexer emits a `SymbolDeclared` for `Sample.Domain.Calculator`
- **THEN** the `CanonicalKey` value is `"csharp:T:Sample.Domain.Calculator"` (Roslyn `DocumentationCommentId` prefixed with `csharp:`)

#### Scenario: XAML-style key passes validation
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "xaml:element:Views/Main.xaml#ConfirmBtn", ...)`
- **THEN** the host accepts the key (its scheme `xaml` is reserved-and-enforced)

#### Scenario: Unknown scheme rejected
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "python:M:foo.bar", ...)`
- **THEN** the host throws `ArgumentException` at emission time naming the unknown scheme, before the row reaches storage

#### Scenario: Backslash path in key rejected
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "xaml:element:Views\\Main.xaml#X", ...)`
- **THEN** the host throws `ArgumentException` identifying the backslash as an invalid separator

### Requirement: AnnotationAttached event with flavor discriminator
The SDK SHALL expose an `AnnotationAttached(string SymbolCanonicalKey, string AnnotationName, string Flavor, string? FullName = null, string? ArgsJson = null, string? TargetCanonicalKey = null)` event that plugins emit to record any annotation attached to a symbol. `Flavor` SHALL be a non-empty kebab-case identifier (e.g., `"csharp-attribute"`, `"ts-decorator"`, `"vue-directive"`, `"svelte-action"`) that lets queries discriminate annotation patterns across languages without conflating them.

The host SHALL persist each emission as a row in the `annotations` storage table; the row's `flavor` column equals the emitted `Flavor` value.

#### Scenario: C# indexer emits a .NET attribute
- **WHEN** the Roslyn indexer encounters `[HttpGet("/api/users")]` on a method
- **THEN** it emits `AnnotationAttached(SymbolCanonicalKey: <method-key>, AnnotationName: "HttpGet", Flavor: "csharp-attribute", FullName: "Microsoft.AspNetCore.Mvc.HttpGetAttribute", ArgsJson: <json>, TargetCanonicalKey: <attr-class-key-or-null>)`

#### Scenario: Annotation with empty flavor rejected
- **WHEN** a plugin emits `AnnotationAttached` with `Flavor = ""` or whitespace
- **THEN** the host throws `ArgumentException` at emission time

### Requirement: ILanguageProject and ILanguageProjectFactory
The SDK SHALL expose two interfaces. `ILanguageProject` SHALL declare `string Id { get; }` (a stable identifier such as the absolute project file path or `tsconfig.json` path) and `IReadOnlyCollection<string> FilePaths { get; }` (the absolute paths of files the project owns). `ILanguageProjectFactory` SHALL declare `IReadOnlyCollection<string> ProjectMarkers { get; }` (glob patterns identifying files that anchor a project of this type, e.g. `["*.csproj", "*.fsproj"]` or `["tsconfig.json"]`) and `Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(string repoRoot, CancellationToken ct)`.

The host SHALL load `ILanguageProjectFactory` instances from registered plugins, invoke `DiscoverAsync` once per scope at startup (and again on `.sourcegraph.json` changes), and route `IndexAsync` calls so the resulting `IndexContext.Project` references the project that owns the file.

#### Scenario: Built-in C# pathway exposes a MSBuildLanguageProject
- **WHEN** the host opens a solution with two `.csproj` projects
- **THEN** the C# `ILanguageProjectFactory.DiscoverAsync` returns two `MSBuildLanguageProject` instances (one per `.csproj`), each wrapping the existing `MSBuildWorkspace`-loaded project, and `IndexContext.Project` for any `.cs` file in those projects references the matching one

#### Scenario: File outside any project
- **WHEN** the host indexes a file whose path is not in any discovered `ILanguageProject.FilePaths`
- **THEN** `IndexContext.Project` is `null` and the indexer is invoked normally (per-file fallback)

#### Scenario: Plugin owns project state
- **WHEN** a plugin's `ILanguageProject` subclass holds plugin-private state (e.g. a parsed resource cache)
- **THEN** the same `ILanguageProject` instance is passed via `IndexContext.Project` for every file in that project across the same indexing pass, so the plugin can reuse the cached state

### Requirement: IndexContext exposes the language project
`IndexContext` SHALL expose `ILanguageProject? Project { get; }`. The constructor SHALL accept the project as an additional parameter. When the host has no project mapping for the file, `Project` SHALL be `null`.

#### Scenario: Project flows through to the indexer
- **WHEN** the host invokes `ILanguageIndexer.IndexAsync(ctx)` for a file owned by `ProjectX`
- **THEN** `ctx.Project` is the same `ILanguageProject` instance the factory returned for `ProjectX`

### Requirement: MCP initialize response publishes the active vocabulary
The MCP server's `initialize` response SHALL include three string arrays alongside the existing usage-instructions surface: `edge_kinds`, `symbol_kinds`, `annotation_flavors`. Each array SHALL list the distinct kebab-case identifiers that the active scope's loaded indexers are configured to emit (sourced from the constants the indexers reference plus any kinds already present in the scope's storage from a previous index). The arrays SHALL be sorted, lowercase, and deduplicated.

#### Scenario: Single-language scope
- **WHEN** an MCP client completes the initialize handshake against a scope that only has the built-in C# Roslyn indexer loaded
- **THEN** `edge_kinds` contains the built-in C# constants (`"calls"`, `"inherits"`, `"implements"`, `"uses-type"`, `"overrides-member"`, `"implements-member"`, `"instantiates"`, `"throws"`, `"tests"`); `symbol_kinds` contains the built-in symbol constants; `annotation_flavors` contains `["csharp-attribute"]`

#### Scenario: Vocabulary publishing suppressed via flag
- **WHEN** the server is started with `--no-instructions` (or `SOURCEGRAPH_NO_INSTRUCTIONS=1`)
- **THEN** the `initialize` response carries no `edge_kinds` / `symbol_kinds` / `annotation_flavors` arrays (alongside the existing instructions suppression)
