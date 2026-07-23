## Why

Today the indexer ships two `ILanguageIndexer` implementations: `RoslynLanguageIndexer`
for `.cs` and `XamlLanguageIndexer` for `.xaml`. Both are bespoke. Adding a third
language (TypeScript next, then Python, Go, Rust) by hand-rolling another bespoke
indexer per language doesn't scale: each one needs its own parser, its own
canonical-key derivation, its own module resolver, its own AST-walker. The
combinatorics are bad and the per-language quality drifts.

Tree-sitter solves the shared half of that problem. It is a battle-tested,
in-process parser library with mature grammars for ~200 languages. One C library +
N grammar binaries replaces N hand-rolled parsers. Crucially, it stays in-process —
no Node/Python/sidecar runtime — which matches the project's "global `dotnet tool`,
zero external runtime deps" stance.

This change introduces the *host* for tree-sitter-backed language indexers without
shipping any actual language. It locks the SDK contracts (`INodeKindMapper`,
`IModuleResolver`), the scope-config additions (`language`, `enrichment`), and the
native-binary packaging story so that every later per-language change
(`add-typescript-language-indexer`, future `add-python-…`, `add-go-…`) is purely
the language-specific bits — grammar binding, node-kind mapping, JSX/decorator
quirks — sitting on the same backbone.

## What Changes

- **NEW:** `src/DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter/` project paralleling
  `Indexing.Xaml/`. Depends on `TreeSitter.DotNet` (the community-maintained
  NuGet that bundles `libtree-sitter` plus 28+ language grammars across all
  target RIDs). Contains `TreeSitterAdapter` (thin wrapper exposing the
  subset of `TreeSitter.DotNet`'s API the abstract base needs),
  `TreeSitterLanguageIndexer<TGrammarConfig>` (abstract base implementing
  `ILanguageIndexer`; subclass supplies grammar identity + node-kind mapper +
  optional module resolver), a position helper that converts tree-sitter
  byte offsets into 1-based line/column pairs, and a `SymbolWalker` AST
  helper for the common cases.
- **NEW:** SDK contracts on `DevBitsLab.Mcp.SourceGraph.Sdk`:
  - `INodeKindMapper` — translates a tree-sitter node-type string
    (`"function_declaration"`, `"class_declaration"`, ...) into a kebab-case
    `SymbolKinds` / `EdgeKinds` value plus optional `Modifiers` / `Accessibility`.
  - `IModuleResolver` — given an emitted import statement and the current file's
    repo path, returns the absolute path of the resolved module file (or `null` if
    unresolved). Per-language implementations encode tsconfig `paths`, Python
    `__init__.py`, Go module paths, etc.
  - `LanguageIndexerOptions` — strongly-typed options bag (default excludes,
    grammar identity, module-resolver factory) consumed by
    `TreeSitterLanguageIndexer<T>`.
- **NEW:** Native runtime delegated to `TreeSitter.DotNet`. The host
  package takes a single `<PackageReference>` on `TreeSitter.DotNet` (1.3.x)
  which transitively brings `libtree-sitter` plus per-grammar binaries for
  every target RID (Linux x86/x64/arm/arm64, macOS x64/arm64, Windows
  x86/x64/arm64). Per-language plugins (Change 2 onwards) consume the
  bundled grammars via `new Language("TypeScript")` etc.; they do NOT
  ship their own grammar binaries.
- **NEW:** Scope-config additions to `.sourcegraph.json`:
  - `language` (string, optional) — declares the primary language for scopes
    whose project-set is glob-based. Hint to indexer dispatch when the same file
    extension could plausibly be claimed by multiple plugins (`.ts` ambiguity is
    minimal; `.h` between C / C++ / Objective-C is not).
  - `enrichment` (object, optional) — forward-declared field with one nested key
    `lsp: { command, args }`. Loaded and surfaced via `scopes info` but NOT
    acted on by this change; the first concrete consumer is
    `add-typescript-language-indexer`.
- **NEW:** `sourcegraph-mcp scopes info <name>` subcommand displaying a scope's
  resolved language, glob set, plugin claims, and (if present) `enrichment`
  block — companion to existing `scopes list`. Renders the `language` and
  `enrichment` fields so they are visible before any indexer consumes them.

## Capabilities

### New Capabilities

- *(none — every piece extends an existing capability)*

### Modified Capabilities

- `extensibility`: SDK gains `INodeKindMapper`, `IModuleResolver`,
  `LanguageIndexerOptions`, and the `TreeSitterLanguageIndexer<T>` abstract base.
  Plugins consuming the SDK gain access to the tree-sitter runtime through a
  stable contract.
- `scoping`: scope schema gains optional `language` and `enrichment` fields;
  the loader validates shape but does not enforce a closed language list.
- `mcp-config`: `.sourcegraph.json` documents the new fields and their defaults.
- `distribution`: the Indexing.TreeSitter NuGet package takes a transitive
  `TreeSitter.DotNet` dependency that brings `libtree-sitter` + grammar
  binaries for every target RID; no first-party native-asset packaging in
  this repo.

## Impact

- **Code:** new `Indexing.TreeSitter` project + tests; SDK additions in
  `DevBitsLab.Mcp.SourceGraph.Sdk/` (3 interfaces, 1 options class); scope config
  loader extended for the two new fields; `scopes info` CLI subcommand; native
  runtime asset packaging in the new project's csproj.
- **Public contract:** SDK gains additive types only. No breakage to
  `ILanguageIndexer` / `IndexEvent` / existing scope-config readers — both new
  scope fields are optional and absent by default.
- **Persistence:** No schema changes. The host emits no rows on its own; rows
  appear only when a concrete tree-sitter language indexer (Change 2 onwards)
  ships.
- **Distribution:** `Indexing.TreeSitter.nupkg` is small (just managed
  code); the size cost lands in the transitive `TreeSitter.DotNet`
  dependency which carries libtree-sitter + 28 grammars across RIDs (~30-40
  MB total spread across all platforms; only the active RID's binaries
  publish into the tool layout). The base tool ships unchanged in shape
  (still a single `dotnet tool install -g`).
- **Out of scope:**
  - Any actual language indexer (TS, Python, Go, Rust) — deferred to per-language
    follow-up changes.
  - LSP-client implementation (the `enrichment.lsp` field is parsed and surfaced
    but no client wires it; first wiring lands in `add-typescript-language-indexer`).
  - tsconfig / pyproject / go.mod parsing — language-specific, lives in each
    per-language change.
  - Backwards-compatibility shims for v0.x scope configs — there are none in the
    field; `language` and `enrichment` are forward-only additions.

**Depends on:** none.

**Unblocks:** `add-typescript-language-indexer` (Change 2); future
`add-python-language-indexer`, `add-go-language-indexer`, `add-rust-language-indexer`,
and `add-lsp-enrichment-client` (the actual LSP wiring for the
forward-declared field).
