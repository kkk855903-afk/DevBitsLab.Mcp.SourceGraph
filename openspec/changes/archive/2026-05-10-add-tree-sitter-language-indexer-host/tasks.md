## 1. New `Indexing.TreeSitter` project skeleton

- [x] 1.1 Create `src/DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter/DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter.csproj` targeting `net10.0`, referencing `DevBitsLab.Mcp.SourceGraph.Sdk` and `DevBitsLab.Mcp.SourceGraph.Storage`
- [x] 1.2 Add the new project to `DevBitsLab.Mcp.SourceGraph.slnx` and to the host server's project references
- [x] 1.3 Set up `tests/DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter.Tests/` mirroring the existing test-project layout
- [x] 1.4 NuGet metadata (`PackageId = DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter`, `PackageDescription`, `Version` aligned with the rest of the suite)

## 2. `TreeSitter.DotNet` runtime dependency

- [x] 2.1 Add `<PackageReference Include="TreeSitter.DotNet" Version="1.3.*" />` to the new project; pin to a tested upstream version in `Directory.Packages.props` if the repo uses central package management, otherwise pin in the csproj
- [x] 2.2 Smoke test: integration test that constructs `new Language("JavaScript")` and `new Parser(language).Parse("console.log(1)")`, asserts the root node is non-null and reports the expected named-child count on every CI RID

## 3. Tree-sitter adapter layer

- [x] 3.1 `TreeSitterAdapter` static helpers exposing the subset of `TreeSitter.DotNet` the abstract base needs (parser construction, document parse, root-node traversal, named-child enumeration, byte-offset access). Thin wrappers — the goal is one place to absorb upstream API churn, not a re-implementation
- [x] 3.2 `Grammar` value type wrapping `TreeSitter.DotNet`'s `Language` with the SDK's `ITreeSitterGrammarConfig` shape so the abstract base can be language-agnostic
- [x] 3.3 `using`-disposable wrapper conventions for `Parser` and `Tree` lifetimes (TreeSitter.DotNet's types are already `IDisposable`; the adapter ensures we always wrap them so subclasses can't accidentally leak)
- [x] 3.4 Position helper: convert tree-sitter byte offsets into 1-based line/column pairs given the source text bytes (UTF-8-aware; multi-byte sequences map to a single column)

## 4. SDK contracts

- [x] 4.1 Add `src/DevBitsLab.Mcp.SourceGraph.Sdk/INodeKindMapper.cs` with `TryMapDeclaration`, `TryMapReference`, `TryMapEdge` and the `NodeMapping` value type
- [x] 4.2 Add `src/DevBitsLab.Mcp.SourceGraph.Sdk/IModuleResolver.cs` with `Resolve(fromAbsolutePath, importSpecifier, project)`; document the `null` return for unresolved imports
- [x] 4.3 Add `src/DevBitsLab.Mcp.SourceGraph.Sdk/LanguageIndexerOptions.cs` (default excludes, grammar identity, optional resolver factory)
- [x] 4.4 Add `src/DevBitsLab.Mcp.SourceGraph.Sdk/ITreeSitterGrammarConfig.cs` (the config-bag generic constraint for the abstract base)
- [x] 4.5 SDK csproj minor-version bump and XML doc note describing the additions
- [x] 4.6 Public-API surface unit test: assert the new types' XML docs are non-empty and the kebab-case constants pass `KebabCaseValidator`

## 5. `TreeSitterLanguageIndexer<TGrammarConfig>` abstract base

- [x] 5.1 Implement the abstract class in `Indexing.TreeSitter/TreeSitterLanguageIndexer.cs`
- [x] 5.2 `IndexAsync` boilerplate: parse via the grammar, walk the tree, dispatch each named node through the subclass's `INodeKindMapper`, emit corresponding `IndexEvent`s
- [x] 5.3 `FileScanned` emitted exactly once at the end with the SHA-256 of the source bytes (use the existing helper if there is one in `Indexing/`)
- [x] 5.4 Cancellation honored at the per-node-walk granularity
- [x] 5.5 Malformed source: parse errors surface as a debug log + empty event list (mirror `XamlLanguageIndexer`'s posture)
- [x] 5.6 Unit test against a synthetic grammar / fixture: a tiny JSON-like grammar with three node types, asserting the abstract base emits the expected sequence when wired up to a stub mapper

## 6. Scope-config: `language` and `enrichment` fields

- [x] 6.1 Extend `ScopeEntryJson` (in `Storage/ScopeConfig.cs`) with `language: string?` and `enrichment: ScopeEnrichmentJson?`
- [x] 6.2 Add `ScopeEnrichmentJson` with a single nested `lsp: LspEnrichmentJson?` for v1; `LspEnrichmentJson` carries `command: string` and `args: string[]?`
- [x] 6.3 Validation: `language` must be kebab-case if present; `enrichment.lsp.command` must be a non-empty string if present; `args` defaults to `[]`
- [x] 6.4 Surface both on the in-memory `Scope` record (or a sister `ScopeRuntimeConfig` if the existing record is too narrow)
- [x] 6.5 Loader unit tests: round-trip the new fields through `Load` and `Save`; assert default-omission when both fields are unset
- [x] 6.6 Loader negative tests: empty `language` rejected; non-kebab-case `language` rejected; `enrichment` without `lsp` rejected; `lsp` without `command` rejected

## 7. `scopes info <name>` CLI subcommand

- [x] 7.1 Add the subcommand to the existing scopes verb dispatcher in `Server/Cli/`
- [x] 7.2 Output sections: `Identity` (id, name, root), `Project set` (kind + globs/solutions), `Language` (the `language` field; `(unset)` when absent), `Enrichment` (the `enrichment` block; `(no consumer at this version)` when set but inert), `Status` (last-indexed-at, current registry status)
- [x] 7.3 `--json` flag emits a stable JSON shape mirroring the markdown sections
- [x] 7.4 Snapshot test: `scopes info default` against the single-scope fixture; `scopes info frontend` against `tests/fixtures/MultiScope/`
- [x] 7.5 Update CLI help text and the README's CLI section

## 8. Documentation

- [x] 8.1 Update `CLAUDE.md` to document the `language` and `enrichment` scope fields and the new SDK types (one paragraph each)
- [x] 8.2 Update `README.md`'s scope-config example to show the new fields, with a note that `enrichment` is forward-declared at v1
- [x] 8.3 Update `openspec/ROADMAP.md` with a "Phase 5 — Open language hosts" section listing this change and what it unblocks
- [x] 8.4 SDK csproj XML doc note (the `<Description>` element) updated for the new minor

## 9. End-to-end smoke + validation

- [x] 9.1 `openspec validate add-tree-sitter-language-indexer-host --strict` passes
- [x] 9.2 Cross-platform CI smoke: tests pass on every RID in the matrix; per-RID native binary loads
- [x] 9.3 In-tree consumers (`RoslynLanguageIndexer`, `XamlLanguageIndexer`) keep passing — this change is purely additive, but verify no scope-config loader regression
- [x] 9.4 The `scopes info` subcommand returns the documented shape against both fixture solutions
