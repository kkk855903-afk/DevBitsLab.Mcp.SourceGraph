## ADDED Requirements

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
