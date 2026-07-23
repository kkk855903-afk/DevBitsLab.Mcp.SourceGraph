## Why

`add-tree-sitter-language-indexer-host` lands the substrate. This change is the
first concrete language sitting on it: TypeScript and JavaScript, including JSX
and TSX. JS/TS are the highest-value first language for two reasons. First,
they are the dominant non-.NET language in modern full-stack repos that already
host the C# backend this tool indexes — agents asking "find references to
`useUser`" in a Next.js + ASP.NET monorepo today fall back to `Grep` because
the source-graph server has nothing for them. Second, JS/TS exercise the full
shape of the tree-sitter host: a real grammar (with TSX as a second mode), a
real module resolver (tsconfig `paths` + extension probing), JSX-as-instantiation
edges that motivate the `payload` channel `open-language-contract` shipped, and
optional LSP enrichment for the long tail of TypeScript semantics.

This change is the "first language" deliverable. Python, Go, and Rust will
follow as smaller per-language changes once the patterns this PR establishes
(canonical-key shape, module-resolver mechanics, JSX-style component edges,
LSP enrichment client) are validated against a real codebase.

## What Changes

- **NEW:** `src/DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript/` project
  paralleling `Indexing.Xaml/` and consuming the new `Indexing.TreeSitter`
  base. Contains `TypeScriptLanguageIndexer` (subclass of
  `TreeSitterLanguageIndexer<TypeScriptGrammarConfig>`) registered for `.ts`,
  `.tsx`, `.js`, `.jsx`. Three grammar bindings ship: `tree-sitter-typescript`
  (`.ts`), `tree-sitter-tsx` (`.tsx`), `tree-sitter-javascript` (`.js`,
  `.jsx`).
- **NEW:** `TypeScriptNodeKindMapper` that translates the relevant tree-sitter
  node-types to the SDK's kebab-case kinds: `function_declaration` →
  `"function"`, `class_declaration` → `"class"`, `interface_declaration` →
  `"interface"`, `type_alias_declaration` → `"type-alias"`,
  `enum_declaration` → `"enum"`, `method_definition` → `"method"`,
  `public_field_definition` → `"field"`, `lexical_declaration` (when const)
  → `"constant"`, `import_statement` / `import_specifier` →
  `ReferenceKind.Reference`, JSX `jsx_self_closing_element` /
  `jsx_opening_element` → `EdgeKinds.Instantiates` with a `payload` carrying
  the prop list.
- **NEW:** TypeScript canonical-key scheme `<scheme>:<kind-prefix>:<repo-path>::<lexical-path>`
  where `<scheme>` is one of `ts` / `tsx` / `js` / `jsx` (matching the file
  extension), and `<kind-prefix>` is one of `T` (type/class/interface/enum/
  type-alias), `M` (function/method), `V` (const/let/var), `P` (property/field),
  or `N` (namespace/module). `<repo-path>` is forward-slash repo-relative.
  `<lexical-path>` is `::`-separated identifier path including container
  names. Helper class `TypeScriptCanonicalKeys` builds the keys.
- **NEW:** Default scope excludes for TypeScript/JavaScript scopes:
  `**/node_modules/**`, `**/dist/**`, `**/.next/**`, `**/build/**`,
  `**/coverage/**`, `**/.cache/**`, `**/.parcel-cache/**`, `**/out/**`. Applied
  via `LanguageIndexerOptions.DefaultExcludes`; user-supplied `exclude`
  unions with these (defaults are floors, not ceilings, per the host change).
- **MODIFIED:** Reserved canonical-key schemes lifted from rejected to accepted.
  `open-language-contract` reserves `js`, `ts`, `jsx`, `tsx` as
  reserved-but-rejected. This change moves them into the accepted set so the
  TS indexer can emit them. Other reserved-rejected schemes (`vbnet`,
  `fsharp`, `razor`, `vue`, `svelte`) remain rejected.

## Capabilities

### New Capabilities

- *(none — the TS indexer extends existing capabilities)*

### Modified Capabilities

- `indexing`: registers `.ts`, `.tsx`, `.js`, `.jsx` extension dispatch through
  `TypeScriptLanguageIndexer`. `find_definition`, `find_references`,
  `find_symbol`, `module_summary`, and the rest of the read-side tools "just
  work" against the new symbols — they are storage-driven and language-agnostic.
- `extensibility`: lifts `js`/`ts`/`jsx`/`tsx` from reserved-rejected to
  reserved-accepted in the canonical-key validator.

## Impact

- **Code:** new `Indexing.TypeScript` project + tests; SDK validator update for
  the canonical-key scheme list (lifts `js`/`ts`/`jsx`/`tsx`); registration
  wiring in `Server/Program.cs` for both the `serve` and `index` paths.
  Bundled grammars come for free from `Indexing.TreeSitter`'s transitive
  `TreeSitter.DotNet` dependency; no per-grammar binary packaging in this repo.
- **Public contract:** SDK validator behaviour change is observable — the
  schemes `ts`, `tsx`, `js`, `jsx` previously throwing at emission now
  succeed. No compile-time API break; the validator's accept-list is the
  only delta.
- **Persistence:** No schema changes at this MVP. The deferred LSP-enrichment
  follow-up (see below) carries the additive `enrichment_source` column.
- **Distribution:** `Indexing.TypeScript.nupkg` is a new package referenced
  transitively from the tool. No new native binaries ship from this repo —
  TreeSitter.DotNet's bundle covers the grammars.
- **Out of scope (deferred to follow-up changes so the MVP can ship):**
  - **Cross-file ref resolution.** The MVP emits intra-file references only;
    the `IModuleResolver` slot on the host is unused at this version. A
    follow-up `add-typescript-module-resolver` change will add the resolver
    plus a `tsconfig` scope-config field for `paths` aliases.
  - **LSP enrichment.** `enrichment.lsp` remains forward-declared (parsed but
    inert). A follow-up `add-typescript-lsp-enrichment` change wires the
    client, carries the V11→V12 additive schema bump for the `enrichment_source`
    column, and adds the `(via lsp)` annotation to `find_references`.
  - **Declaration merging via `@L<line>` disambiguators.** The MVP emits one
    symbol per declaration container; rare merging cases produce duplicate
    canonical keys for now. Lands when the resolver follow-up needs them for
    correct cross-file targeting.
  - **`isolated:true` `.d.ts` indexing for `node_modules`.** Default excludes
    skip `node_modules` entirely; opt-in indexing for vendored types is a
    separate change.
- **Out of scope (no plans):**
  - Python / Go / Rust language indexers (each lands as its own change).
  - `tsserver` plugin protocol (the proprietary VS Code extension API).
  - Project references (`{ "references": [...] }` in tsconfig).
  - Migrating XAML or C# indexers to the tree-sitter base. The Roslyn
    indexer is the type-system-quality reference for .NET.

**Depends on:** `add-tree-sitter-language-indexer-host`.

**Unblocks:** `add-typescript-module-resolver`,
`add-typescript-lsp-enrichment`; future per-language tree-sitter indexers
(Python, Go, Rust, …) that reuse the same patterns.
