## 1. New `Indexing.TypeScript` project skeleton

- [x] 1.1 Create `src/DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript/DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript.csproj` referencing `Indexing.TreeSitter` (which transitively provides the grammar bundle).
- [x] 1.2 Add the new project to `DevBitsLab.Mcp.SourceGraph.slnx`; wire registration into the host so `.ts` / `.tsx` / `.js` / `.jsx` extensions dispatch to the new indexer.
- [x] 1.3 Add the new project as a `ProjectReference` from the main Tests project so unit tests can target it directly.

## 2. Per-extension grammar dispatch (no first-party grammar binaries)

- [x] 2.1 `TypeScriptLanguageIndexer.GetGrammarName(IndexContext)` overrides the base hook to return `"TypeScript"` for `.ts`, `"TSX"` for `.tsx`, `"JavaScript"` for `.js` / `.jsx`. Bundled grammars come from `TreeSitter.DotNet`'s native bundle.
- [x] 2.2 Smoke test: parse a fixture `.ts`, `.tsx`, `.js` source with `TypeScriptLanguageIndexer` and assert the expected declaration kind is produced.

## 3. `TypeScriptNodeKindMapper`

- [x] 3.1 Implement `TypeScriptNodeKindMapper : INodeKindMapper`.
- [x] 3.2 Declaration mappings: `function_declaration` → `method`, `class_declaration` → `class`, `interface_declaration` → `interface`, `type_alias_declaration` → `type-alias`, `enum_declaration` → `enum`, `method_definition` → `method`, `public_field_definition` → `field`, `lexical_declaration` → `variable` (subclass refines to `constant` when the keyword is `const`), `internal_module` / `module` / `namespace_declaration` → `namespace`, `enum_assignment` → `enum-member`.
- [x] 3.3 Reference mappings: `call_expression` → `call`, `type_identifier` → `reference`. Bare `identifier` is not mapped — the walk only treats *container* nodes as references so the inner identifier of a declaration's name doesn't double-emit.
- [x] 3.4 Edge mappings: `new_expression` and JSX `jsx_self_closing_element` / `jsx_opening_element` → `instantiates`.
- [x] 3.5 Unit test: parse a fixture, walk the tree, dispatch each named node to the mapper, assert the expected `IndexEvent` sequence (covered by the integration tests).

## 4. TypeScript canonical-key derivation

- [x] 4.1 Implement `TypeScriptCanonicalKeys` static helper with `Build`, `BuildProperty`, and `SchemeFromExtension` methods.
- [x] 4.2 Lift `js`, `ts`, `jsx`, `tsx` schemes from reserved-rejected to reserved-accepted in `CanonicalKeyValidator` (in the SDK).
- [x] 4.3 Update existing validator tests: lifted-scheme positive cases pass, the previously-rejected case is moved to `vue:component:Header.vue`.
- [x] 4.4 The MVP emits one symbol per declaration; declaration-merging position-disambiguators (`@L<line>`) are deferred to the cross-file resolver follow-up where they actually matter for ref targeting.

## 5. JSX edge emission with prop payload

- [x] 5.1 Recognise JSX element nodes (`jsx_self_closing_element`, `jsx_opening_element`) and extract the tag identifier.
- [x] 5.2 Skip lowercase tag identifiers (HTML elements, not user symbols).
- [x] 5.3 For PascalCase / capitalised tag identifiers, emit a `ReferenceFound` for the tag plus an `EdgeEmitted` `<file-namespace> -[instantiates]-> <component-key>` carrying a `props` metadata entry listing the prop names.
- [x] 5.4 Snapshot test: a fixture `.tsx` file with a `<Button onClick={handler} disabled />` usage produces the documented edge.

## 6. Default-excludes integration

- [x] 6.1 `TypeScriptGrammarConfig.Options.DefaultExcludes` ships the eight documented patterns (`**/node_modules/**`, `**/dist/**`, `**/.next/**`, `**/build/**`, `**/coverage/**`, `**/.cache/**`, `**/.parcel-cache/**`, `**/out/**`).
- [x] 6.2 Surface the static list as `TypeScriptGrammarConfig.StandardExcludes` for tests and tooling that need to introspect the value without instantiating the config.

## 7. Wire indexer into the Server

- [x] 7.1 Add `Indexing.TypeScript` as a `ProjectReference` from `Server.csproj`.
- [x] 7.2 Register `TypeScriptLanguageIndexer` in both the `serve` and `index` paths of `Program.cs`, alongside the existing Roslyn-stub and XAML registrations.

## 8. Tests

- [x] 8.1 Nine `TypeScriptLanguageIndexerTests` covering function declarations, class + method, interface + type-alias, const / let distinction, JSX edge with prop payload, lowercase JSX skipped, JavaScript grammar, call-expression reference emission, and the FileScanned sentinel invariant.
- [x] 8.2 Update `ValidatorTests` to reflect the lifted scheme set: positive cases for `ts:` / `tsx:` / `js:` / `jsx:`, negative case shifted to `vue:`, `EnforcedSchemes` set asserts `csharp` / `xaml` / `js` / `ts` / `jsx` / `tsx`.
- [x] 8.3 Full test suite stays green (475/475 passing).

## 9. Documentation

- [x] 9.1 Update `README.md`: add a "TypeScript / JavaScript / TSX / JSX indexing" feature bullet; update the "Languages" comparison row in the Roslyn-vs-this-server table.
- [x] 9.2 Update `CLAUDE.md`: project layout shows the new `Indexing.TreeSitter` and `Indexing.TypeScript` folders; tagline mentions tree-sitter alongside Roslyn and XAML.
- [x] 9.3 Update `openspec/ROADMAP.md`: Phase 5 entry annotated "MVP shipping"; deferred follow-ups (`add-typescript-module-resolver`, `add-typescript-lsp-enrichment`) listed explicitly so the next change has a clear name.

## 10. End-to-end smoke + validation

- [x] 10.1 `openspec validate add-typescript-language-indexer --strict` passes.
- [x] 10.2 In-tree consumers (`RoslynLanguageIndexer`, `XamlLanguageIndexer`) keep passing — additive change, no scope-config loader regression, no schema change.
- [x] 10.3 The TS indexer emits the documented event shapes against fixtures inside the test project.

## Deferred to follow-up changes

These were originally part of this change's plan but are now scoped out so the
MVP can ship as a standalone, internally-consistent unit:

- **`add-typescript-module-resolver`** — `TypeScriptModuleResolver`
  implementing relative-import resolution + tsconfig `paths` aliases +
  re-export chase. Adds `tsconfig` field on scope-config. Adds declaration-
  merging `@L<line>` disambiguators so cross-file refs target the right
  declaration when names collide.
- **`add-typescript-lsp-enrichment`** — LSP client wiring under
  `Indexing.TreeSitter/Lsp/`; spawns `typescript-language-server` post-pass
  when `enrichment.lsp.command` is set; merges results as `refs` rows tagged
  `enrichment_source = 'lsp'`. Carries the additive schema bump V11→V12 for
  the new column. `find_references` rendering gains a `(via lsp)` annotation.
