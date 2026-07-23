> **MVP scope note.** This design originally bundled module-resolver + LSP-enrichment +
> declaration-merging-disambiguators with the indexer. The implementation factored those
> out so the MVP could ship as a focused unit; the design decisions below are preserved as
> the thinking that informs the follow-up changes (`add-typescript-module-resolver`,
> `add-typescript-lsp-enrichment`). Decisions 3 (module resolver), 5 (declaration merging),
> and 7 (LSP enrichment) describe deferred work; decisions 1, 2, 4, 6, 8 describe what
> shipped.

## Context

JS/TS is the highest-value first language to add on top of the tree-sitter
host (Change 1). The agent UX problem this change closes is concrete: in a
modern monorepo the C# backend has full source-graph coverage today, the
TS frontend has none. Agents fall back to `Grep` for everything frontend-
shaped — find usages, find component definitions, impact-of-change analysis —
which is exactly the failure mode the source-graph server exists to prevent.

This change is also the proof of the host's design. If the per-language
subclass turns out to need three more protected hooks on
`TreeSitterLanguageIndexer<T>`, the host change wasn't quite right; better
to find that out at N=1 (TS) than N=4 (TS + Py + Go + Rust). The four
file extensions covered (`.ts`, `.tsx`, `.js`, `.jsx`) and the three grammar
modes (typescript, tsx, javascript) exercise enough surface to validate the
generic shape: multiple grammars per indexer, JSX as a flavor of the JS
grammar, TSX as a separate grammar, the same `INodeKindMapper` reused across
all three.

The semantic-depth tradeoff is the dominant decision. TypeScript's
*type system* is genuinely impossible to replicate without `tsc`; ask "find
references to `User` where `User` is a type-only import re-exported from a
namespace alias" and the syntactic resolver loses. The change accepts that
80% of common queries (function calls, class instantiations, JSX components,
exported-identifier usages) are syntactically resolvable, ships that, and
defers full semantics to the optional LSP enrichment slot. The contract
with operators is honest: zero external deps gets you 80%; an installed
`typescript-language-server` gets you the long tail.

## Goals / Non-Goals

**Goals:**

- Ship a TypeScript / JavaScript / JSX / TSX indexer that produces useful
  `find_references`, `find_definition`, `find_symbol`, and `module_summary`
  output against a real-world Next.js / Vite / CRA repo without requiring
  any external runtime.
- Validate the host change's `TreeSitterLanguageIndexer<T>` /
  `INodeKindMapper` / `IModuleResolver` shape under one real consumer.
- Establish the canonical-key, module-resolver, and JSX-edge patterns that
  later per-language changes (Python, Go, Rust) reuse.
- Wire the `enrichment.lsp` slot to its first runtime consumer
  (`typescript-language-server`), so operators who want type-perfect refs
  have a documented path.
- Default-exclude the noisy build-output and dependency directories that
  every TS repo has, so a fresh `dotnet tool` install on a Next.js repo
  doesn't index `node_modules`.

**Non-Goals:**

- Type-system-quality references in the syntactic-only path. Documented as a
  known limit; LSP enrichment is the escape hatch.
- Indexing third-party `.d.ts` files in `node_modules` by default. Available
  via `isolated: true` scope, off by default.
- Project references (`{ "references": [...] }` in tsconfig).
  Multi-tsconfig TS monorepos use per-root scopes.
- The `tsserver` proprietary VS Code extension API. Only public LSP.
- Generic LSP client decoupled from TS. Lives under `Indexing.TreeSitter/Lsp/`
  but is wired here against TS specifically; generalisation is a follow-up.
- Backporting the tree-sitter approach to `.cs` or `.xaml`. Roslyn is the
  type-system-quality reference for .NET; XAML's profile-aware parser is
  bespoke for reasons that don't translate.

## Decisions

### 1. Three grammar modes: typescript, tsx, javascript

**Choice:** Bind three tree-sitter grammars and dispatch by file extension:

| Extension | Grammar |
|-----------|---------|
| `.ts` | `tree-sitter-typescript` (typescript mode) |
| `.tsx` | `tree-sitter-tsx` (typescript-with-jsx mode) |
| `.js`, `.jsx` | `tree-sitter-javascript` (handles JSX inline) |

The three grammars share most node-types (`function_declaration`,
`class_declaration`, etc.), so a single `TypeScriptNodeKindMapper` covers
all three with a small set of grammar-specific branches for JSX-only nodes
(`jsx_element`, `jsx_self_closing_element`, `jsx_attribute`,
`jsx_expression`).

**Alternatives considered:**

- *One grammar (`tree-sitter-tsx`) for everything.* The TSX grammar parses
  TS, but it parses TS slightly slower and produces nodes for ambiguities
  that TS-only files don't have. Saves a binary; costs parse precision.
- *Separate indexers per extension.* Conflates dispatch with implementation;
  the node-kind mapper is mostly shared so the duplication would be silly.
- *Bundle all three into one custom mega-grammar.* Custom grammars need
  per-version maintenance and lose the upstream tree-sitter ecosystem. Not
  worth the binary savings.

**Rationale:** The grammar count matches the language family's natural
shape — three modes corresponding to the three real dialects users write.
Maintenance per grammar is small (each is a tagged release of an upstream
crate); the win is parse fidelity per dialect.

### 2. Canonical-key scheme: `ts:<kind-prefix>:<repo-path>::<lexical-path>`

**Choice:** TypeScript canonical keys mirror the Roslyn doc-comment-id
pattern but in TS-native terms:

```
ts:T:src/web/types.ts::User              # interface or class or type-alias
ts:M:src/web/foo.ts::greet               # function
ts:V:src/web/config.ts::API_BASE         # const
ts:P:src/web/types.ts::User#name         # property of a type
ts:N:src/web/utils.ts                    # the module/file as a namespace
ts:M:src/web/foo.ts::Counter::tick       # method on a class
```

`<kind-prefix>` is one of `T`, `M`, `V`, `P`, `N`. `<repo-path>` is
forward-slash repo-relative (per the existing canonical-key URI convention).
`<lexical-path>` uses `::` for container nesting and `#` for type-property
separation. Declaration merging produces multiple symbols with the same
canonical-key form and a position-disambiguator suffix
(`ts:T:src/web/foo.ts::User@L42` for the second declaration of `User` if
two appear at non-equal lines), and a `merges` edge wires them together.

**Alternatives considered:**

- *Mirror Roslyn's exact format (`ts:T:Module.Type` with module dotted).*
  Module dotting requires module identity that TS doesn't reliably have
  (a file's "module name" is its path, possibly aliased). Repo-path beats
  module-name for stability.
- *No kind-prefix; one flat shape.* Loses the human-readability that
  `csharp:T:` / `csharp:M:` provides — operators reading raw graph rows
  benefit from the prefix.
- *Hash-based identity (`ts:<sha256-of-decl-shape>`)* for stability across
  renames. Stability across renames is overrated; the source-graph rebuilds
  on edit anyway, and hashed keys are unreadable in logs.

**Rationale:** Familiar shape (Roslyn-like) + repo-path identity (stable in
the way the existing host expects) + kind prefix (operator-readable). Position
disambiguator is a rare edge case; reusing the `merges` edge from XAML keeps
the schema work nil.

### 3. Module resolver: hand-rolled with tsconfig paths + extension probing

**Choice:** `TypeScriptModuleResolver` implements the four cases listed in
the proposal:

1. Relative imports → file system probe in declared order: `<base>.ts` →
   `<base>.tsx` → `<base>/index.ts` → `<base>/index.tsx` → (for `.js` files)
   `<base>.js` → `<base>/index.js`. Order matters — TS prefers `.ts` over
   `.tsx` over `.d.ts` per the spec.
2. tsconfig `paths` aliases: load the scope's `tsconfig.json` once at
   indexer warmup, build a sorted-by-prefix-length alias table, match the
   longest matching alias, substitute, retry the relative-import probe.
3. Re-exports: when an import target file's symbol is itself a re-export
   (`export { foo } from './bar'`), follow up to 8 hops to find the
   originating declaration. Cap prevents pathological cycles.
4. Bare specifiers without alias match → return `null`. node_modules
   indexing is an explicit `isolated: true` scope, not a default behavior.

The resolver memoises file-existence checks per-warmup pass to keep the
probe sequence fast on large repos.

**Alternatives considered:**

- *Use `tsc --traceResolution` output.* Most accurate but requires Node;
  violates the no-external-deps stance for the default path.
- *Skip resolution entirely; emit only intra-file refs.* Loses the
  highest-value query ("find all callers of this exported function") on
  the syntactic path. Accepting an 80%-correct hand-rolled resolver beats
  shipping with no cross-file refs.
- *LSP-only resolution.* Couples cross-file refs to having an LSP installed.
  Defeats the syntactic-first design.

**Rationale:** Hand-rolled covers most patterns. The 80/20 break is fine
because LSP enrichment exists for the long tail. The resolver's
correctness boundary is documented; users who need more turn on LSP.

### 4. JSX as instantiation edges with prop payload

**Choice:** A JSX element `<Button onClick={handler} disabled />` becomes:

- A `ReferenceFound` for the `Button` identifier (kind `"reference"`)
- An `EdgeEmitted` `<calling-symbol> -[instantiates]-> <Button-canonical-key>`
  with `Metadata` carrying the prop list as JSON:

```json
{ "props": [
    { "name": "onClick", "kind": "expression" },
    { "name": "disabled", "kind": "boolean-flag" }
  ]
}
```

The `instantiates` edge already exists (`open-language-contract` declared it
for general-purpose use). The metadata channel is exactly what
`harden-sdk-pre-xaml`'s `PayloadKeys` motivated; JSX adds two new keys
(`props`, `kind`) declared as `JsxPayloadKeys` in the TS indexer's namespace
(not in the SDK — TS-specific).

**Alternatives considered:**

- *No payload, just the edge.* Loses prop info that downstream tools
  (`module_summary`, future `find_components_using_prop`) want. Cheap to
  fill, expensive to backfill later.
- *Each prop becomes its own edge.* Edge explosion on dense JSX trees;
  hard to query coherently. The payload-as-batch pattern matches XAML's
  binding-payload approach.
- *Synthetic "ComponentInstantiation" symbol per JSX element.* Symbol
  table inflation for low query value; the edge + payload is sufficient.

**Rationale:** Reuse the `instantiates` edge + `payload` channel. Doesn't
introduce new edge kinds for JSX-specific concepts; matches the
"templated UI" vocabulary XAML established. `find_callers` of a component
returns its JSX usages with the prop list inline, which is exactly what an
agent asking "where is `<Button>` used and with what props?" wants.

### 5. Declaration merging via per-position disambiguators + `merges` edges

**Choice:** When TypeScript declaration merging produces multiple
declarations of the same name in the same file (a class + interface, a
namespace + function, etc.), the indexer emits one `SymbolDeclared` per
declaration with disambiguated canonical keys (`ts:T:src/foo.ts::User@L12`
vs `ts:T:src/foo.ts::User@L42`) and one `EdgeEmitted` `<first>
-[merges]-> <second>` for each pairwise merge.

When only a single declaration is present, the canonical key has no
disambiguator suffix.

**Alternatives considered:**

- *Single canonical key, no merges edge.* Conflates the merged entity into
  one symbol; loses the per-declaration source position; breaks `find_definition`
  for the secondary declarations.
- *Always disambiguate (suffix even for single declarations).* Noisy on the
  common case; existing canonical-key shape elsewhere doesn't carry positions.
- *Synthesise a "merged-symbol" virtual symbol that wraps the participants.*
  Schema explosion; the `merges` edge is exactly the existing relationship
  vocabulary.

**Rationale:** Common case (one declaration) stays clean. Rare case
(declaration merging) is explicit and queryable via the existing edge
machinery. Reuses the `merges` kind, no new vocabulary.

### 6. Default scope excludes for TS scopes

**Choice:** `LanguageIndexerOptions.DefaultExcludes` for TS/JS scopes:

```
**/node_modules/**
**/dist/**
**/.next/**
**/build/**
**/coverage/**
**/.cache/**
**/.parcel-cache/**
**/out/**
```

User-supplied `exclude` unions with these (per the host change's "defaults
are floors" decision). An operator who *wants* `node_modules` indexed adds
an explicit `isolated: true` scope rooted at the package(s) of interest.

**Alternatives considered:**

- *No defaults, leave it to the operator.* Every TS user re-types the same
  globs; the first index pass without them is a multi-minute disaster on
  any non-trivial repo.
- *Configurable defaults via a separate field.* Premature flexibility;
  the union behavior covers the override case.
- *Hard-coded — operators cannot un-exclude `node_modules` even with an
  explicit scope.* Defeats the point of `isolated`.

**Rationale:** The default catches the 99% case. The escape hatch
(`isolated: true`) covers the rare case. Matches the "good defaults, override
explicit" pattern already established for `add-scoping`.

### 7. LSP enrichment as a post-pass

**Choice:** When a TS scope has `enrichment.lsp.command` set, the indexer
runs the tree-sitter pass first (always, regardless of LSP), then spawns
the configured language server, drives `initialize`, sends `didOpen` for
every indexed file, and queries `textDocument/references` for each symbol
the syntactic resolver flagged ambiguous (paths-alias collisions, re-export
chains beyond depth cap, type-only ambiguity). LSP results merge as
additional `refs` rows tagged `enrichment_source = 'lsp'`.

The schema gains one column on `refs`: `enrichment_source TEXT NULL`.
Schema bumps V11 → V12; the migration is additive `ALTER TABLE` (no drop).
This is the first schema bump that doesn't drop data — but the column is
nullable and additive, so the existing `EnsureSchemaAsync` "drop-and-rebuild"
path keeps working as a fallback for older DBs.

**Alternatives considered:**

- *LSP-only path (no syntactic pass).* Slow startup, fragile (LSP crashes
  block indexing), defeats the no-external-deps goal.
- *LSP runs concurrently with tree-sitter, results race-merged.* Race
  conditions on the symbol map; ordering matters for canonical-key
  resolution. Sequential post-pass is simpler.
- *Track LSP-enrichment refs in a separate table.* More schema work, no
  query benefit; the column tag is sufficient.

**Rationale:** LSP enrichment is opt-in and additive. Operators who don't
configure it never pay; operators who do get type-perfect refs in exchange
for the LSP install. The schema column is the smallest possible addition
to keep the two ref sources distinguishable.

### 8. Lift `js`, `ts`, `jsx`, `tsx` from reserved-rejected to reserved-accepted

**Choice:** `CanonicalKeyValidator` accepts the four TS-family schemes
starting with this change. `vbnet`, `fsharp`, `razor`, `vue`, `svelte`,
`python`, `go`, `rust` remain rejected — each lifts its own scheme when its
indexer ships.

**Alternatives considered:** *(see host change's decision 5; the same
"earn the scheme by shipping the producer" rationale applies.)*

**Rationale:** Pay the validator update only when there's a real producer
to credit it to. Avoids a "reserved scheme that nobody emits" bloat.

## Risks / Trade-offs

- **Hand-rolled module resolver misses long-tail TypeScript features.** Path
  aliases with overlapping prefixes, conditional types-as-imports, namespace
  re-exports beyond the hop cap, `paths` patterns with multiple match
  positions. → Mitigation: documented limits, LSP enrichment is the escape
  hatch, and the resolver's miss surfaces as an unresolved import (no
  silent wrong answer).
- **JSX prop payload could explode on dense component trees.** A page with
  100 components × 10 props each adds 1k payload-bearing edges. →
  Mitigation: cap rendered payload at 5 keys per edge (existing
  `harden-sdk-pre-xaml` decision); large prop lists store fully but render
  truncated. Storage cost is JSON in `edges.payload` — well within SQLite's
  comfortable range.
- **LSP-server startup cost on cold first-query.** typescript-language-server
  takes 5-30s to type-check a non-trivial repo. → Mitigation: the tree-sitter
  pass completes first and surfaces results immediately; LSP enrichment is
  an async post-pass that updates results live (via the existing
  `notifications/progress` slot from `report-progress-on-slow-tools`). Users
  see syntactic-quality answers within seconds and enriched ones once tsc
  finishes.
- **Default excludes might over-exclude in non-standard repos.** A repo that
  ships final builds in `dist/` but also has source called `dist/` (rare).
  → Mitigation: operators override via explicit `paths` that overlaps the
  default exclude, plus `scopes info` shows the effective union so
  diagnosis is fast.
- **Schema bump to V12 is the first additive-only bump.** Existing
  `EnsureSchemaAsync` drops-and-rebuilds on any version mismatch; an
  additive column change should be ALTER, not DROP. → Mitigation: extend
  `EnsureSchemaAsync` to attempt `ALTER TABLE ADD COLUMN` first when the
  delta is purely additive; fall back to drop+rebuild on failure. The
  drop-and-rebuild remains correct if the ALTER fails (data is rebuildable),
  so the change is safety-preserving.
- **Three grammars × five RIDs = 15 native binaries shipped per release.**
  Maintenance cost. → Mitigation: build script automates per-RID
  cross-compile; release pipeline already handles `libtree-sitter`'s
  five binaries from the host change, the marginal cost of three more is
  CI time, not engineering time.

## Migration Plan

1. **SDK version bump** (minor) — the validator's accept list changes; this
   is observable behavior even though no compile-time API moves.
2. **Schema bump V11 → V12.** Additive `ALTER TABLE refs ADD COLUMN
   enrichment_source TEXT NULL`. `EnsureSchemaAsync` extended for
   additive-only deltas. Rebuild-on-mismatch preserved as fallback.
3. **Land in one PR** against `main` referencing
   `add-typescript-language-indexer`. Pieces (project skeleton, grammar
   bindings, mapper, resolver, LSP client, default excludes, scope-config
   `tsconfig` field, schema bump) ship together so the indexer is
   functional end-to-end on day one.
4. **Add `tests/fixtures/TypeScript/`** — a small Next.js-style fixture
   with `.ts`, `.tsx`, `.js`, `.jsx` files plus a `tsconfig.json` declaring
   `paths` aliases. Used by unit tests and the integration smoke.
5. **CHANGELOG / SDK csproj XML doc** documents the canonical-key format,
   the lifted schemes, the schema bump, and the LSP enrichment opt-in.
6. **README update**: add a "TypeScript / JavaScript" section under
   "Languages" with the syntactic-only and LSP-enriched usage modes
   side-by-side.
7. **ROADMAP.md update**: mark this change as the first "Phase 5 — Open
   language hosts" delivery; sketch the next-language target.

## Open Questions

- **Should `find_references` UI show enrichment-sourced refs separately or
  unified?** Unified is simpler; separated lets users filter
  syntactic-only when they distrust the LSP. Lean: unified by default with a
  small `(via lsp)` annotation per ref; revisit if the annotation is too
  noisy in practice.
- **Should the indexer skip ambient declaration files (`.d.ts`) outside
  `node_modules`?** A user-authored `.d.ts` in `src/types/` likely wants
  indexing. Default: index user `.d.ts` files (anything inside the scope's
  `paths` glob), exclude only those under default-excluded directories.
  Documented in scope-info output.
- **Does `paths` resolution need to respect `baseUrl`?** Yes for
  pre-pathsAliases-only configs (rare today, common in 2017-era TS).
  Defer until we see one in the wild; resolver currently assumes
  `baseUrl: "."` when unset. Document the limit.
- **Is the `enrichment_source` column the right place to put a future
  `enrichment_version` field for cache invalidation when LSP results
  change?** Probably, but the second column adds before the use case is
  proven. Lean: ship one column, add a sibling later if needed. The
  schema is a baseline, not a forecast.
- **Should JSX usage produce `ReferenceFound` events even for HTML-cased
  identifiers (`<div>`, `<span>`)?** No — HTML-element JSX doesn't
  reference any user symbol; the noise floor would swamp the signal.
  Skip lower-cased JSX tags; emit refs only for capitalised
  (PascalCase / camelCase identifier-style) tags. Document the heuristic.
- **Tree-sitter-tsx vs tree-sitter-typescript for `.ts` files containing
  TSX syntax via `// @ts-ignore`-style escapes?** Falls back to tsx mode
  on parse failure of typescript mode? Or fail-fast? Lean: typescript-mode
  primary, tsx-mode fallback if the typescript parser hits an `ERROR`
  node within a JSX-shaped subtree. Document as an implementation detail.
