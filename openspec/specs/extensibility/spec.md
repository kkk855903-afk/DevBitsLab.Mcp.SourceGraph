# Extensibility

## Purpose

Open the indexing pipeline and the MCP tool surface to third-party extension
without forking the repo. Three contracts (`ILanguageIndexer`, `ICodeAnalyzer`,
`IMcpToolPlugin`) shipped via a separate NuGet SDK package let plugin authors
add new languages, custom analyzers, and prefix-namespaced MCP tools. Plugin
discovery happens via `.sourcegraph.json` `plugins[]`; per-plugin
`AssemblyLoadContext` isolation keeps failures contained and prevents
dependency conflicts.

## Requirements

### Requirement: ILanguageIndexer plugin contract
The SDK SHALL expose an `ILanguageIndexer` contract that declares the file extensions it handles and produces a stream of `IndexEvent` values for a given document.

#### Scenario: Built-in C# indexer implements the contract
- **WHEN** the server starts
- **THEN** `RoslynLanguageIndexer` is registered for `.cs` files and routes pass-1 / pass-2 work through `IndexEvent` emissions

#### Scenario: Third-party indexer registered
- **WHEN** a plugin in `.sourcegraph.json` declares `ILanguageIndexer` for `.py`
- **THEN** the server dispatches `.py` files to that plugin and ignores them in the C# indexer

### Requirement: ICodeAnalyzer plugin contract
The SDK SHALL expose an `ICodeAnalyzer` contract whose `AnalyzeAsync` is invoked once per indexed document with a context and an `IGraphEmitter`, allowing the analyzer to add symbols, references, edges, or attributes.

#### Scenario: Custom analyzer emits an Endpoint symbol
- **WHEN** an `AspNetCoreEndpointAnalyzer` runs against a document containing `app.MapGet("/api", h)`
- **THEN** it emits a synthetic `Endpoint` symbol via `IGraphEmitter` and the server persists it via the same `UpsertSymbolAsync` path used by the built-in indexer

### Requirement: IMcpToolPlugin contract
The SDK SHALL expose an `IMcpToolPlugin` contract whose `RegisterAsync` adds tools to the MCP server; tool names emitted by a plugin SHALL be prefixed with the plugin's declared `Prefix` followed by `.`.

#### Scenario: Plugin tool name prefixing
- **WHEN** a plugin with `Prefix = "mediatr"` registers `find_handlers`
- **THEN** the MCP server exposes the tool as `mediatr.find_handlers` and built-in tools (e.g. `find_definition`) keep unprefixed names

### Requirement: Plugin discovery via .sourcegraph.json
The host SHALL load plugins listed under `plugins[]` in `.sourcegraph.json`, supporting both NuGet packages (`{package, version}`) and DLL paths (`{path}`).

#### Scenario: NuGet plugin loaded
- **WHEN** `.sourcegraph.json` lists `{ "package": "DevBitsLab.Mcp.SourceGraph.Analyzers.AspNetCore", "version": "1.0.0" }`
- **THEN** the host restores it into `<repo>/.sourcegraph/plugins/restore/` and loads its assembly into a dedicated `AssemblyLoadContext`

#### Scenario: Path plugin loaded
- **WHEN** `.sourcegraph.json` lists `{ "path": ".sourcegraph/plugins/MyAnalyzer.dll" }`
- **THEN** the host loads that DLL directly into a per-plugin ALC

### Requirement: Per-plugin failure isolation
A plugin whose contract methods throw SHALL be marked `failed` in the registry; the rest of the system continues to operate, and the failure is surfaced via `plugins list`.

#### Scenario: Bad plugin doesn't crash the host
- **WHEN** an `ICodeAnalyzer.AnalyzeAsync` throws on a particular document
- **THEN** the analyzer is marked `failed` for that pass, other analyzers complete, the indexer returns success, and `plugins list` reports the failure with the exception type and message

### Requirement: Plugin status introspection
The CLI SHALL expose `plugins list` and `plugins info <name>` subcommands that report every loaded plugin's id, version, status (`loaded | failed | disabled`), implemented contracts, and registered tools / file extensions.

#### Scenario: Inspect plugins
- **WHEN** the user runs `sourcegraph-mcp plugins list` after starting a server with two plugins (one healthy, one failed)
- **THEN** the output is a table showing both plugins with their statuses and versions

### Requirement: IToolRegistry.AddTool trigger overload
`IToolRegistry` SHALL expose two `AddTool` overloads:
- `AddTool(string toolName, string description, Delegate handler)` — the original 3-arg signature, retained unchanged from SDK 1.0.0 so plugins compiled against the earlier interface remain binary-compatible (plugins consume `IToolRegistry`, they don't implement it; adding methods to the interface is therefore safe for the plugin side).
- `AddTool(string toolName, string description, Delegate handler, string trigger)` — a new 4-arg overload added in SDK 1.1.0 that takes a required, non-empty trigger phrase.

When a tool is added via the 4-arg overload, the host SHALL append `Use when: <trigger>` as the final paragraph of the tool's effective description before registering the tool with the underlying MCP server. When a tool is added via the 3-arg overload, the description SHALL pass through unchanged.

#### Scenario: Plugin registers a tool with the trigger overload
- **WHEN** a plugin's `RegisterAsync` calls `registry.AddTool("find_handlers", "Find MediatR handlers for a request type.", handler, trigger: "\"who handles MediatR request X?\"")`
- **THEN** the host's `tools/list` response includes a tool whose description ends with the line `Use when: "who handles MediatR request X?"`

#### Scenario: Plugin registers a tool with the original 3-arg overload
- **WHEN** a plugin's `RegisterAsync` calls `registry.AddTool("find_handlers", "Find MediatR handlers.", handler)` (no trigger)
- **THEN** the host's `tools/list` response includes the tool whose description matches the supplied text verbatim, with no appended line

#### Scenario: Plugin compiled against SDK 1.0.0 stays binary-compatible
- **WHEN** a plugin DLL compiled against SDK 1.0.0 (which only knew the 3-arg `AddTool`) is loaded by the host running SDK 1.1.0
- **THEN** the plugin's calls to the 3-arg overload resolve at runtime to the unchanged interface method, the plugin loads without recompilation, and its tools register normally with no `Use when:` line appended

#### Scenario: Trigger overload rejects an empty trigger
- **WHEN** a plugin calls `registry.AddTool("x", "y", handler, trigger: "  ")` with whitespace-only trigger
- **THEN** the host throws `ArgumentException` (the 4-arg overload's contract is "trigger is required and non-empty"; plugins that don't have a trigger should call the 3-arg overload instead)

### Requirement: Open kind vocabularies on the SDK
The SDK SHALL expose edge kinds and symbol kinds as `string` values at the plugin contract boundary, NOT as closed enums. Static `EdgeKinds` and `SymbolKinds` classes SHALL provide kebab-case constants for the values defined by the host (`"calls"`, `"inherits"`, `"implements"`, `"uses-type"`, `"overrides-member"`, `"implements-member"`, `"instantiates"`, `"throws"`, `"tests"`, `"references"`, `"reads"`, `"writes"`, `"binds-to"`, `"handles-event"`, `"command-executes"`, `"grpc-calls"`, `"implements-rpc"`, `"pinvoke-maps-to"`, `"struct-maps-to"` for edges; `"namespace"`, `"class"`, `"interface"`, `"struct"`, `"enum"`, `"delegate"`, `"method"`, `"constructor"`, `"property"`, `"field"`, `"event"`, `"enum-member"`, `"operator"`, `"record"`, `"function"`, `"type-alias"`, `"native-export"`, `"rpc"`, `"message"`, `"proto-field"` for symbols). Plugins MAY emit any kebab-case identifier; the host stores the kind as TEXT and does NOT reject unknown kebab-case kinds.

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

### Requirement: Additive SDK edge-evidence channel
SDK 2.3 SHALL expose `EvidenceConfidence`, `SourceLocation`, and `EdgeEvidence`, plus an
optional init-only `EdgeEmitted.Evidence` property. The original public four-parameter
`EdgeEmitted` constructor SHALL retain its exact runtime signature so plugins compiled
against SDK 2.2 remain binary-compatible.

When evidence is present, the host SHALL map its absolute file path, valid 1-based range,
producer, metadata, and ordered confidence into storage. The host — not the plugin — SHALL
assign the current indexed file id as the producing file, and storage SHALL reject a path
that does not match that file.

#### Scenario: Existing plugin omits evidence
- **WHEN** a plugin compiled against SDK 2.2 constructs `EdgeEmitted` through the original constructor
- **THEN** it loads unchanged under SDK 2.3 and `Evidence` is `null`

#### Scenario: Plugin supplies exact occurrence evidence
- **WHEN** a plugin emits an edge with `Evidence = EdgeEvidence(location, Exact, "sample-plugin")`
- **THEN** the stored proof has confidence `exact`, producer `sample-plugin`, that source range, and the host-owned producing file id

#### Scenario: Plugin cannot attribute evidence to another file
- **WHEN** the evidence path is relative, malformed, or differs from the current indexed file
- **THEN** the host rejects the emission transactionally before an edge or proof is persisted

### Requirement: Canonical-key URI convention
Every canonical key emitted by an `ILanguageIndexer` SHALL match the format `<scheme>:<rest>`, where `<scheme>` is one of the reserved-and-enforced schemes at this SDK version (`csharp`, `xaml`, `js`, `ts`, `jsx`, `tsx`, `c`, `cpp`, `proto`). Schemes `vbnet`, `fsharp`, `razor`, `vue`, `svelte`, `python`, `go`, `rust` are documented as reserved-for-future-use but are NOT yet accepted by the host; emissions using those schemes SHALL be rejected.

`<rest>` SHALL be plugin-defined, but any path component embedded in `<rest>` SHALL be repo-relative (resolved against `Scope.Root`) and SHALL use forward slashes regardless of operating system.

#### Scenario: Built-in C# indexer emits a Roslyn-shaped key
- **WHEN** the Roslyn indexer emits a `SymbolDeclared` for `Sample.Domain.Calculator`
- **THEN** the `CanonicalKey` value is `"csharp:T:Sample.Domain.Calculator"` (Roslyn `DocumentationCommentId` prefixed with `csharp:`)

#### Scenario: XAML-style key passes validation
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "xaml:element:Views/Main.xaml#ConfirmBtn", ...)`
- **THEN** the host accepts the key (its scheme `xaml` is reserved-and-enforced)

#### Scenario: TypeScript scheme accepted
- **WHEN** the TypeScript indexer emits `SymbolDeclared(CanonicalKey: "ts:M:src/web/foo.ts::greet", ...)`
- **THEN** the host accepts the key; the scheme `ts` is reserved-and-enforced starting at this SDK version

#### Scenario: TSX scheme accepted
- **WHEN** the TypeScript indexer emits an instantiates edge with source key `"tsx:M:src/web/page.tsx::Page"` and target `"tsx:M:src/web/Button.tsx::Button"`
- **THEN** the host accepts both keys; the scheme `tsx` is reserved-and-enforced

#### Scenario: JavaScript / JSX schemes accepted
- **WHEN** the TypeScript indexer emits a key of the form `"js:M:src/foo.js::greet"` or `"jsx:M:src/page.jsx::Page"`
- **THEN** the host accepts both; schemes `js` and `jsx` are reserved-and-enforced

#### Scenario: Reserved-future scheme rejected
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "python:M:foo.bar", ...)` or `"vue:component:Header.vue"`
- **THEN** the host throws `ArgumentException` at emission time naming the unknown scheme, before the row reaches storage. Schemes `vbnet`, `fsharp`, `razor`, `vue`, `svelte`, `python`, `go`, `rust` remain reserved-for-future-use until their respective indexers ship and lift them.

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

The SDK SHALL also expose the additive optional capability `IDeclarationFirstLanguageProject`.
It extends `ILanguageProject` with
`IReadOnlyCollection<string> DeclarationFilePaths { get; }`. Every declared-first path SHALL be
an absolute path already present in `FilePaths`; it is an ordering hint and never expands the
host's scope/privacy boundary. A host that recognizes the capability SHALL dispatch these files
before the remaining files in the same project so cross-file edges can resolve real declaration
symbols without placeholder targets. Projects that do not implement it retain ordinary ordering.

#### Scenario: Built-in C# pathway exposes a MSBuildLanguageProject
- **WHEN** the host opens a solution with two `.csproj` projects
- **THEN** the C# `ILanguageProjectFactory.DiscoverAsync` returns two `MSBuildLanguageProject` instances (one per `.csproj`), each wrapping the existing `MSBuildWorkspace`-loaded project, and `IndexContext.Project` for any `.cs` file in those projects references the matching one

#### Scenario: File outside any project
- **WHEN** the host indexes a file whose path is not in any discovered `ILanguageProject.FilePaths`
- **THEN** `IndexContext.Project` is `null` and the indexer is invoked normally (per-file fallback)

#### Scenario: Plugin owns project state
- **WHEN** a plugin's `ILanguageProject` subclass holds plugin-private state (e.g. a parsed resource cache)
- **THEN** the same `ILanguageProject` instance is passed via `IndexContext.Project` for every file in that project across the same indexing pass, so the plugin can reuse the cached state

#### Scenario: Optional declaration-first ordering
- **WHEN** a discovered project implements `IDeclarationFirstLanguageProject` and returns two allowed project-owned paths
- **THEN** the host dispatches those two files before other files owned by that project
- **AND** duplicate, outside-scope, excluded, or non-project paths do not expand the dispatch set

### Requirement: IndexContext exposes the language project
`IndexContext` SHALL expose `ILanguageProject? Project { get; }`. The constructor SHALL accept the project as an additional parameter. When the host has no project mapping for the file, `Project` SHALL be `null`.

#### Scenario: Project flows through to the indexer
- **WHEN** the host invokes `ILanguageIndexer.IndexAsync(ctx)` for a file owned by `ProjectX`
- **THEN** `ctx.Project` is the same `ILanguageProject` instance the factory returned for `ProjectX`

### Requirement: XAML scheme is exercised by a built-in indexer
The host SHALL ship an in-tree `XamlLanguageIndexer` registered for the `.xaml` file extension. After this change, `xaml` is no longer merely a reserved-and-enforced canonical-key scheme; it is a scheme actively emitted by a built-in indexer and persisted in storage on every scope that loads the indexer.

#### Scenario: XAML indexer registered alongside C# Roslyn indexer
- **WHEN** the host starts a scope that loads both the built-in C# Roslyn indexer and the XAML indexer
- **THEN** the dispatcher routes `.cs` files to the Roslyn indexer and `.xaml` files to the XAML indexer; both indexers' kinds appear in the scope's published `Capabilities.Experimental["sourcegraph.vocabulary"]`

#### Scenario: XAML scheme accepted from the indexer
- **WHEN** the XAML indexer emits `SymbolDeclared(CanonicalKey: "xaml:view:Views/Main.xaml", ...)`
- **THEN** the host accepts the key (`xaml` is reserved-and-enforced) and persists the symbol with that canonical key

### Requirement: ILanguageProjectFactory discovery is required at runtime
After this change, the host SHALL discover `ILanguageProjectFactory` instances from every registered plugin at scope startup, invoke `DiscoverAsync(repoRoot, ct)` once per scope, and cache the resulting `ILanguageProject` instances in a per-scope `Dictionary<string, ILanguageProject>` keyed by absolute file path. The dispatcher SHALL look up the project for each dispatched document and populate `IndexContext.Project` accordingly. This requirement was deferred from the SDK reform; it lands here because XAML is the first non-C# indexer that requires it.

#### Scenario: XAML file dispatched with project context
- **WHEN** the host dispatches `Views/Main.xaml` to the XAML indexer in a scope where `XamlLanguageProjectFactory` discovered a project owning that file
- **THEN** `IndexContext.Project` is the matching `XamlLanguageProject` instance, the indexer can read its `ResourceCache` for resource-resolution lookups, and the same instance flows for every `.xaml` file in the same project

#### Scenario: C# file dispatched with project context (regression check)
- **WHEN** the host dispatches `MainWindow.xaml.cs` to the Roslyn indexer in the same scope
- **THEN** `IndexContext.Project` is a `MSBuildLanguageProject` from `MSBuildLanguageProjectFactory.DiscoverAsync` (regression check that the deferred 5.3 plumbing now functions for the C# pathway too)

#### Scenario: File outside any project
- **WHEN** the host dispatches a `.xaml` file located outside any project's `FilePaths` (e.g. a loose file under `docs/`)
- **THEN** `IndexContext.Project` is `null` and the indexer is invoked with the per-file fallback semantics documented in the original SDK reform

### Requirement: XamlLanguageIndexer plugin contract
The `XamlLanguageIndexer` SHALL implement `ILanguageIndexer` and SHALL emit:

- Five symbol kinds under the `xaml:` URI scheme: `xaml-view`, `xaml-element`, `xaml-resource`, `xaml-style`, `xaml-template`
- Nine edge kinds: `code-behind`, `binds-to`, `binds-path`, `binds-element`, `handles-event`, `uses-resource`, `instantiates-type`, `merges`, `applies-style`
- One annotation flavor: `xaml-attached-property`

Cross-language edges (`code-behind`, `binds-to`, `binds-path`, `handles-event`, `instantiates-type`) SHALL target byte-equal C# canonical keys from the privacy-sanitized Roslyn compilation used by the C# indexer. A scope-specific XAML project factory SHALL obtain that compilation lazily from the current sanitized solution so live edits do not retain a cold-start semantic snapshot.

#### Scenario: Code-behind edge joins XAML view to C# partial class
- **WHEN** the indexer encounters `<Window x:Class="MyApp.Views.Main" ...>` as the root of `Views/Main.xaml`
- **THEN** it emits `SymbolDeclared(CanonicalKey: "xaml:view:Views/Main.xaml", Kind: "xaml-view", ...)` and `EdgeEmitted(Src: "xaml:view:Views/Main.xaml", Dst: "csharp:T:MyApp.Views.Main", EdgeKindName: "code-behind", Metadata: null)` with `Exact` `xaml-semantic` evidence on the `x:Class` attribute; the `dst` matches what the Roslyn indexer emitted for `MyApp.Views.Main`, so a query like `find_references --canonical-key csharp:T:MyApp.Views.Main` returns both the C# declaration and the XAML view

#### Scenario: Explicit view-model context is a first-class graph relation
- **WHEN** a view or XAML element declares a semantically resolvable `x:DataType` or explicit `DataContext`, including the property-element form, even when the document contains no `Binding`
- **THEN** it emits an `EdgeKinds.BindsTo` (`binds-to`) edge from that view/element to the real C# view-model type with Metadata containing `"association" = "data-context"` and `"source"` identifying `x-data-type`, `data-context-attribute`, or `data-context-element`
- **AND** the edge carries `xaml-semantic` evidence at the exact declaring attribute or property-element range
- **AND** an unresolved or ambiguous type produces no edge and no synthetic target symbol

#### Scenario: Event handler edge resolves to C# method
- **WHEN** the indexer encounters `<Button Click="OnSave"/>` inside a view whose root is `<Window x:Class="MyApp.Views.Main">`, and Roslyn resolves exactly one accessible instance `void OnSave(object, EventArgs-derived)` method
- **THEN** it emits `EdgeEmitted(Src: "xaml:element:Views/Main.xaml#<elementId>", Dst: "csharp:M:MyApp.Views.Main.OnSave(System.Object,System.EventArgs)", EdgeKindName: "handles-event", Metadata: { "event": "Click" })` with exact evidence on the XAML attribute occurrence
- **AND** an absent, ambiguous, static, inaccessible, or incompatible handler produces no guessed edge

#### Scenario: Binding emits payload via PayloadKeys
- **WHEN** the indexer encounters `<TextBox Text="{Binding User.Name, Mode=TwoWay, Converter={StaticResource b2v}}"/>` under an inherited `x:DataType` or statically resolved `DataContext`
- **THEN** it resolves every path segment and emits `EdgeEmitted` to the terminal C# property with `EdgeKindName: "binds-path"` and Metadata including (verbatim) the keys `"path" = "User.Name"`, `"mode" = "two-way"`, `"converter" = "BoolToVisibility"`, `"data-type"`, and `"context-source"` (the common keys come from the `PayloadKeys` constants documented by `harden-sdk-pre-xaml`)
- **AND** an unresolved segment produces no synthetic `xaml:binding-target` symbol and no guessed semantic edge

#### Scenario: ElementName binding emits only the relationship it can prove
- **WHEN** the indexer encounters `<TextBox Text="{Binding ElementName=OtherCtrl, Path=Value}"/>`
- **THEN** it emits a `binds-element` edge from the source element to the resolved target element (looked up via `x:Name` within the same XAML view) carrying the path payload and exact occurrence evidence
- **AND** it does not reuse the inherited `DataContext` to fabricate a `binds-path` edge until the target element's CLR property type can be established

#### Scenario: Command binding requires ICommand
- **WHEN** a `Command="{Binding SaveCommand}"` path resolves to a property whose type is `System.Windows.Input.ICommand` or implements it
- **THEN** the indexer emits a `binds-path` edge to that C# property with `"command" = "SaveCommand"` metadata
- **AND** a path resolving to a non-command property emits no command edge

#### Scenario: Unsupported source shape degrades honestly
- **WHEN** a binding or `DataContext` uses an explicit `Source`, `ElementName`, or `RelativeSource` shape for which no CLR source-type resolver exists
- **THEN** the indexer emits no property-target edge from the inherited `DataContext` and creates no placeholder symbol

#### Scenario: Attached property emitted as annotation
- **WHEN** the indexer encounters `<Button Grid.Row="2" Grid.Column="1"/>`
- **THEN** it emits two `AnnotationAttached` events with `Flavor: "xaml-attached-property"`, one named `"Grid.Row"` (args `"2"`) and one named `"Grid.Column"` (args `"1"`); both attach to the button element's symbol

### Requirement: MCP initialize response publishes the active vocabulary
The MCP server's `initialize` response SHALL include three top-level string arrays alongside the existing usage-instructions surface: `edge_kinds`, `symbol_kinds`, `annotation_flavors`. Each top-level array SHALL list the **server-wide union** across every configured scope's vocabulary, sorted lowercase and deduplicated. The response SHALL ALSO include a `scopes` map keyed by scope id, where each value is a `{ edge_kinds, symbol_kinds, annotation_flavors }` triple carrying the per-scope vocabulary; clients that need to validate a tool argument against a specific scope read the per-scope entry rather than the union. Each scope's vocabulary is the union of (a) the SDK's well-known constants (so a built-in kind like `"calls"` is published even on a fresh / never-indexed scope) and (b) the distinct kinds already present in that scope's storage.

#### Scenario: Single-scope server
- **WHEN** an MCP client completes the initialize handshake against a server with one scope (`default`) using the built-in C# Roslyn indexer
- **THEN** the top-level `edge_kinds` contains the built-in C# constants (`"calls"`, `"inherits"`, `"implements"`, `"uses-type"`, `"overrides-member"`, `"implements-member"`, `"instantiates"`, `"throws"`, `"tests"`) and equals `scopes["default"].edge_kinds`; `symbol_kinds` and `annotation_flavors` mirror the same union-equals-per-scope shape

#### Scenario: Vocabulary publishing suppressed via flag
- **WHEN** the server is started with `--no-instructions` (or `SOURCEGRAPH_NO_INSTRUCTIONS=1`)
- **THEN** the `initialize` response carries no `edge_kinds` / `symbol_kinds` / `annotation_flavors` arrays (alongside the existing instructions suppression)

### Requirement: PayloadKeys SDK constants
The SDK SHALL expose a `PayloadKeys` static class containing kebab-case `string` constants for the well-known keys plugins use inside `EdgeEmitted.Metadata` dictionaries: `Path`, `Mode`, `Converter`, `ConverterParameter`, `Event`, `Handler`, `DataType`, `TargetType`, `Key`, `BasedOn`, `ElementName`, `RelativeSource`, `FallbackValue`, `StringFormat`, `UpdateSourceTrigger`. Each constant SHALL hold a kebab-case string (e.g. `PayloadKeys.ConverterParameter == "converter-parameter"`). Plugins are NOT required to use these constants — `EdgeEmitted.Metadata` accepts any string keys — but SHOULD prefer them for cross-plugin payload interop.

#### Scenario: XAML indexer populates a binding payload via constants
- **WHEN** a XAML indexer emits a `binds-path` edge with metadata `{ [PayloadKeys.Path] = "User.Name", [PayloadKeys.Mode] = "two-way", [PayloadKeys.Converter] = "BoolToVisibility" }`
- **THEN** the persisted `payload` JSON value has the keys `"path"`, `"mode"`, and `"converter"` (verbatim from the constant values), and an MCP tool that surfaces payload renders those keys without translation

#### Scenario: All PayloadKeys values are kebab-case
- **WHEN** the SDK is loaded
- **THEN** every `string` constant exposed by `PayloadKeys` matches the kebab-case format `[a-z][a-z0-9]*(-[a-z0-9]+)*` (asserted by a startup test in the SDK test suite)

### Requirement: CanonicalKeys helpers for C# canonical-key construction
The SDK SHALL expose a `CanonicalKeys` static class with helpers that return canonical-key strings for C# language elements, so cross-language plugins do not reimplement Roslyn's `DocumentationCommentId` format. The class SHALL expose at minimum:

- `string ForType(string fullyQualifiedName)` — returns `csharp:T:<doc-comment-id-suffix>`, handling open generics (`MyApp.Foo<T>` → `MyApp.Foo\`1`), nested types via `+`, and `global::` prefix stripping
- `string ForMethod(string typeFullyQualifiedName, string methodName, IReadOnlyList<string>? parameterTypeFullyQualifiedNames = null)` — returns `csharp:M:<type-key-suffix>.<method-name>(<params>)`, with `<params>` rendered per Roslyn doc-comment-id rules (empty parens when null)
- `string ForField(string typeFullyQualifiedName, string fieldName)` — returns `csharp:F:<type-key-suffix>.<field-name>`
- `string ForProperty(string typeFullyQualifiedName, string propertyName)` — returns `csharp:P:<type-key-suffix>.<property-name>`

The keys produced by these helpers SHALL be byte-for-byte equal to those emitted by the built-in `RoslynLanguageIndexer` for the same C# symbol, so cross-language joins reduce to string equality on `symbols.canonical_key`.

#### Scenario: Cross-language plugin points an edge at a C# class
- **WHEN** a XAML indexer emits an edge `EdgeEmitted(src: "xaml:view:Views/Main.xaml", dst: CanonicalKeys.ForType("MyApp.Views.Main"), kind: "code-behind")`
- **THEN** `dst` equals `"csharp:T:MyApp.Views.Main"`, the same string the Roslyn indexer wrote when it emitted `SymbolDeclared` for the partial class, and the host's edge resolver finds both endpoints via `symbols.canonical_key`

#### Scenario: Open generic type
- **WHEN** the helper is called as `CanonicalKeys.ForType("System.Collections.Generic.List<T>")`
- **THEN** the returned key is `"csharp:T:System.Collections.Generic.List\`1"`

#### Scenario: Nested type
- **WHEN** the helper is called as `CanonicalKeys.ForType("MyApp.Outer+Inner")`
- **THEN** the returned key is `"csharp:T:MyApp.Outer.Inner"` (Roslyn doc-comment-id uses `.` separator, not `+`)

#### Scenario: Method with parameter list
- **WHEN** the helper is called as `CanonicalKeys.ForMethod("MyApp.Calculator", "Add", new[] { "System.Int32", "System.Int32" })`
- **THEN** the returned key is `"csharp:M:MyApp.Calculator.Add(System.Int32,System.Int32)"`

#### Scenario: Method with no parameter list provided
- **WHEN** the helper is called as `CanonicalKeys.ForMethod("MyApp.Foo", "Bar", parameterTypeFullyQualifiedNames: null)`
- **THEN** the returned key is `"csharp:M:MyApp.Foo.Bar"` (no parentheses; downstream resolver matches every overload)

### Requirement: Native and protobuf canonical-key helpers
SDK 2.4 SHALL expose `NativeCanonicalKeys` and `ProtoCanonicalKeys` so native and protobuf
adapters share deterministic identities without exposing Clang or protobuf descriptor objects.

Native keys SHALL use
`<c|cpp>:<F|T|A|E>:<repo-relative-path>::<qualified-name>`, where paths are normalised to
forward slashes, `F` covers free/member functions, `T` named types, `A` type aliases, and `E`
native exports. C++ member function identities SHALL include the declaring type and SHOULD include
parameter types to distinguish overloads.

Protobuf keys SHALL use descriptor full names and omit source paths:
`proto:M:<message-full-name>`, `proto:R:<service-full-name>.<rpc-name>`, and
`proto:F:<message-full-name>.<field-name>`. Field numbers are facts used for compatibility
analysis, not part of field canonical identity.

#### Scenario: Windows native path is normalised
- **WHEN** `NativeCanonicalKeys.ForFunction("c", @".\src\native\device.c", "device_open")` is called
- **THEN** it returns `"c:F:src/native/device.c::device_open"`

#### Scenario: C++ member carries its declaring type
- **WHEN** `NativeCanonicalKeys.ForMethod("src/algorithm.cpp", "medical::Algorithm::Run(int)")` is called
- **THEN** it returns `"cpp:F:src/algorithm.cpp::medical::Algorithm::Run(int)"`

#### Scenario: RPC identity contains service and method
- **WHEN** `ProtoCanonicalKeys.ForRpc(".medical.v1.Scanner", "StartScan")` is called
- **THEN** it returns `"proto:R:medical.v1.Scanner.StartScan"`

#### Scenario: Field identity excludes its number
- **WHEN** `ProtoCanonicalKeys.ForField("medical.v1.ScanRequest", "patient_position")` is called for field number `7`
- **THEN** it returns `"proto:F:medical.v1.ScanRequest.patient_position"` and the number `7` is carried separately by the analyzer

### Requirement: Plugin symbol containers resolve after the whole batch
`GraphStoreEmitter` SHALL persist `SymbolDeclared.ContainerCanonicalKey` as
`symbols.container_id` only when the canonical key resolves to an existing symbol id. It SHALL
resolve both earlier and later declarations in the same emission batch and SHALL leave unknown or
stale container keys as `NULL` rather than guessing from names.

#### Scenario: Child precedes its parent in one batch
- **WHEN** a plugin emits a child symbol before its parent and the child names the parent's canonical key
- **THEN** `FlushAsync` inserts both symbols and sets the child's `container_id` to the parent's id

#### Scenario: Container key cannot be resolved
- **WHEN** a symbol names a container canonical key that was neither previously mapped nor emitted in the batch
- **THEN** the symbol is stored with `container_id IS NULL`

### Requirement: TreeSitterLanguageIndexer abstract base
The Indexing.TreeSitter package SHALL expose a `TreeSitterLanguageIndexer<TGrammarConfig>` abstract class that implements `ILanguageIndexer` and provides the parse + walk + emit boilerplate. Concrete per-language indexers SHALL subclass this base and supply their grammar, node-kind mapper, and (optionally) module resolver. The base SHALL emit exactly one `IndexEvent.FileScanned` per indexed file.

#### Scenario: Subclass wires a grammar and emits events
- **WHEN** a subclass binds a grammar via its `TGrammarConfig`, registers `.foo` as its file extension, supplies an `INodeKindMapper` that maps `function_declaration` to `SymbolKinds.Method`, and the host invokes `IndexAsync` on a `.foo` file containing one function
- **THEN** the resulting event list contains exactly one `SymbolDeclared` whose `Kind` equals `"method"`, followed by one `FileScanned` whose `ContentSha256` matches the input bytes' SHA-256

#### Scenario: Parser failure surfaces as empty events
- **WHEN** the grammar's parser returns a tree containing the tree-sitter `ERROR` node for a malformed input file
- **THEN** the base SHALL log at debug level and return an empty event list (matching `XamlLanguageIndexer`'s posture for malformed XAML)

#### Scenario: Cancellation honored mid-walk
- **WHEN** the host cancels the `CancellationToken` while the base is walking a large AST
- **THEN** `IndexAsync` SHALL terminate within one node-visit boundary, throwing `OperationCanceledException`

### Requirement: INodeKindMapper SDK contract
The SDK SHALL expose an `INodeKindMapper` interface that translates tree-sitter node-type strings into the SDK's kebab-case `SymbolKinds` / `EdgeKinds` / reference-kind vocabularies. The interface SHALL declare three orthogonal methods: `TryMapDeclaration(nodeType, out NodeMapping)`, `TryMapReference(nodeType, out string referenceKind)`, and `TryMapEdge(nodeType, out string edgeKindName)`.

#### Scenario: Mapper recognises a declaration node
- **WHEN** a subclass's `INodeKindMapper.TryMapDeclaration("function_declaration", out var mapping)` returns `true` with `mapping.Kind == "method"`
- **THEN** the base SHALL emit a `SymbolDeclared` whose `Kind` equals `"method"` for the corresponding source node

#### Scenario: Mapper does not recognise a node type
- **WHEN** the mapper returns `false` for an encountered node type (e.g. `comment`, `identifier`, an unrecognised grammar node)
- **THEN** the base SHALL skip the node and continue the walk; no `IndexEvent` is emitted for it

#### Scenario: Mapper output validated as kebab-case
- **WHEN** a mapper returns a `NodeMapping` whose `Kind` is not kebab-case (e.g. `"FunctionDeclaration"`, `"function_declaration"`, the empty string)
- **THEN** the base SHALL throw `ArgumentException` at emission time, identifying the offending kind value, before the event reaches storage (matching the existing `KebabCaseValidator` posture)

### Requirement: IModuleResolver SDK contract
The SDK SHALL expose an `IModuleResolver` interface with one method: `string? Resolve(string fromAbsolutePath, string importSpecifier, ILanguageProject? project)`. The method SHALL return the absolute path of the resolved module file, or `null` if the import is unresolvable (a third-party module not in the project, a missing file, an unrecognised specifier shape).

#### Scenario: Resolver maps a relative import to an absolute path
- **WHEN** a subclass's `IModuleResolver.Resolve("/repo/src/foo.ts", "./bar", project)` is called and `/repo/src/bar.ts` exists
- **THEN** the resolver SHALL return `"/repo/src/bar.ts"`; the indexer base uses that path to associate cross-file references with their declaring symbol

#### Scenario: Resolver returns null for an unresolvable import
- **WHEN** the resolver is asked to resolve `"react"` (a third-party module not under the scope's `paths`) and the language has no node_modules indexing in its scope
- **THEN** the resolver SHALL return `null`; the indexer base silently drops cross-file refs whose target cannot be resolved

#### Scenario: Resolver receives the language project
- **WHEN** an indexer that supplies a `Resolver` runs against a file whose owning `ILanguageProject` was discovered by an `ILanguageProjectFactory`
- **THEN** the project handle SHALL be passed through to the resolver so per-project state (e.g. tsconfig `paths` aliases, module-resolution caches) can drive the lookup

### Requirement: LanguageIndexerOptions
The SDK SHALL expose a `LanguageIndexerOptions` class carrying default excludes (glob list), grammar identity, and an optional `IModuleResolver` factory. Concrete `TGrammarConfig` types SHALL surface a typed `Options` property of this shape so the host can apply scope-default excludes without each subclass re-implementing the merge.

#### Scenario: Default excludes merged into scope filtering
- **WHEN** a subclass declares `Options.DefaultExcludes = [ "**/node_modules/**" ]` and a scope's project-set is `paths: [ "src/**/*.ts" ]` with no explicit excludes
- **THEN** the host's file-discovery pass SHALL skip every path under any `node_modules/` directory before dispatching to `IndexAsync`

#### Scenario: Scope-supplied excludes win over default
- **WHEN** a subclass declares `Options.DefaultExcludes = [ "**/dist/**" ]` and a scope explicitly sets `exclude: [ "**/build/**" ]`
- **THEN** the effective exclude list is the union of both, with no precedence inversion (defaults are floors, not ceilings)

### Requirement: SDK target-framework continuity
The new SDK types (`INodeKindMapper`, `IModuleResolver`, `LanguageIndexerOptions`, `ITreeSitterGrammarConfig`) SHALL target `netstandard2.0` like the rest of the SDK so a single plugin DLL works across every supported host version. The `Indexing.TreeSitter` package targeting the host runtime (`net10.0`) MAY consume them as fully-typed APIs.

#### Scenario: Out-of-tree plugin authored against the SDK
- **WHEN** an external NuGet plugin targeting `netstandard2.0` references `DevBitsLab.Mcp.SourceGraph.Sdk` and implements `INodeKindMapper`
- **THEN** the plugin's compiled DLL loads into the host's per-plugin `AssemblyLoadContext` without runtime errors at every supported host version

### Requirement: TypeScript canonical-key kind-prefix convention
TypeScript canonical keys SHALL follow the form `<scheme>:<kind-prefix>:<repo-relative-path>::<lexical-path>` where `<kind-prefix>` is one of the documented single-letter kind prefixes:

| Prefix | Kind family |
|--------|-------------|
| `T` | type, class, interface, enum, type-alias |
| `M` | function, method |
| `V` | const, let, var |
| `P` | property, field, enum-member |
| `N` | namespace, module |

The lexical path uses `::` for container nesting and `#` for type-property separation. The helper class `TypeScriptCanonicalKeys` builds keys in this shape; subclassed indexers SHOULD use the helper rather than constructing keys by hand so the format stays consistent.

#### Scenario: Function key shape
- **WHEN** the indexer emits `SymbolDeclared` for an exported function `greet` in `src/web/foo.ts`
- **THEN** `CanonicalKey == "ts:M:src/web/foo.ts::greet"`

#### Scenario: TSX file uses tsx scheme
- **WHEN** the indexer emits `SymbolDeclared` for a function `Page` in `src/web/page.tsx`
- **THEN** the canonical key starts with `tsx:M:` (matching the file's extension)

#### Scenario: JavaScript file uses js scheme
- **WHEN** the indexer emits `SymbolDeclared` for a function `greet` in `src/web/foo.js`
- **THEN** the canonical key starts with `js:M:`
