## Context

The `extensibility-architecture` and `open-language-contract` changes already
shipped a language-agnostic schema (kebab-case `kind` / `flavor` columns,
scheme-prefixed `canonical_key`s, `payload` JSON column) and a plugin SDK
(`ILanguageIndexer`, `IndexEvent`, scope-config plugin discovery). XAML proved
the SDK by landing a second built-in indexer behind it. The next forcing
function is JS/TS, but JS/TS specifically is the *first of many* non-.NET
language families: Python, Go, Rust, Ruby, etc. all sit in the same ergonomic
slot and expose the same shape of question ("find references", "find symbol",
"module summary").

The architectural choice in front of this change is whether the JS/TS indexer
gets hand-rolled (XAML-style) or built on shared infrastructure that the next
language can reuse. Hand-rolling at N=1 is fine; at N=2 it's a tax; at N=4 it's
the project. Tree-sitter is the obvious shared substrate: it is in-process (no
Node, no sidecar — matches the existing zero-runtime-dep stance), grammar
quality is mature for the languages on the roadmap, and the parse → walk → emit
loop is structurally identical to what `XamlLanguageIndexer` already does.

This change is intentionally *just* the host: the per-RID native packaging,
the `TreeSitterLanguageIndexer<T>` base, and the SDK contracts every per-language
indexer would otherwise reinvent (`INodeKindMapper`, `IModuleResolver`). It
ships zero language support on its own — the proof comes when
`add-typescript-language-indexer` lands on top with a small, focused PR.

## Goals / Non-Goals

**Goals:**

- Land a reusable backbone so the marginal cost of "add language N+1" drops
  from "build a parser plugin" to "bind a tree-sitter grammar + write a node-kind
  map".
- Keep the runtime-deps story unchanged: native libs ship inside the NuGet (same
  pattern `sqlite-vec` established), no external installs required.
- Lock the scope-config schema additions (`language`, `enrichment`) before any
  consumer ships, so Change 2's only schema move is lifting reserved schemes.
- Cover the full RID matrix the .NET tool already supports (linux-x64,
  linux-arm64, osx-x64, osx-arm64, win-x64).
- Surface the new fields to operators (`scopes info`) before any indexer reacts
  to them — same "diagnostic before strict" pattern `vocabulary list` used for
  the kind-vocabulary surface.

**Non-Goals:**

- Any concrete language indexer. JS/TS lands in Change 2; everything else later.
- LSP client implementation. `enrichment.lsp` is parsed and surfaced as
  configuration; the first runtime consumer is Change 2's TS indexer (which
  brings its own LSP wiring), generalisable later.
- WebAssembly tree-sitter grammars. `libtree-sitter` and grammars ship as native
  per-RID assets. WASM is a credible alternative (one binary per grammar,
  cross-platform free) but at v1 the simpler choice matches the existing
  `sqlite-vec` precedent and avoids picking a .NET WASM host.
- A `TreeSitterQuery` (S-expression query language) abstraction. Per-language
  indexers can use the raw `ts_query_*` API via P/Invoke; consolidating into an
  ergonomic wrapper is deferred until a second language proves the right shape.
- Rejecting scope configs whose `language` field is not in a closed list. The
  field is informational at v1; closed-list enforcement lands when a registry of
  language plugins exists to enforce against.

## Decisions

### 1. Depend on `TreeSitter.DotNet` for the runtime + bundled grammars

**Choice:** Take a `<PackageReference Include="TreeSitter.DotNet" />` on the
new `Indexing.TreeSitter` project. `TreeSitter.DotNet` (1.3.0, January 2026,
MIT) bundles `libtree-sitter` plus 28+ language grammars (JavaScript,
TypeScript, TSX, Python, Go, Rust, …) as native binaries for Windows
(x86/x64/arm64), Linux (x86/x64/arm/arm64), and macOS (x64/arm64). It
exposes a clean managed API (`Language`, `Parser`, `Tree`, `Node`, `Query`)
that the abstract base wraps directly — no first-party P/Invoke layer
needed. The host package re-exports the dependency transitively to consuming
language plugins so a TS plugin doesn't add a second copy.

**Alternatives considered:**

- *Roll our own per-RID native packaging à la `sqlite-vec`.* Originally what
  this design specified. Costs: building or sourcing five RIDs of
  `libtree-sitter` binaries plus per-grammar binaries from each language
  package, custom `buildTransitive/*.props` plumbing, our own P/Invoke
  layer, ongoing upstream-version-bump maintenance. The shape `sqlite-vec`
  benefits from is "an upstream maintainer ships the NuGet so we don't"; we'd
  be building that upstream-maintainer role from scratch for tree-sitter.
  `TreeSitter.DotNet` *is* that upstream maintainer.
- *Use a thinner package (`tree-sitter` 0.4.x) and write our own grammar
  bindings.* Reduces what we ship by language but each new language joins
  the maintenance burden. Conflicts with goal 1 ("language N+1 should drop
  to grammar binding + node-kind map"); thinner package pushes work back
  onto each language change.
- *WASM grammars + a .NET WASM host.* Single artifact per grammar,
  cross-platform free, no per-RID matrix. Costs: 2-3x parse slowdown, drag
  in `Wasmtime.NET` (or similar), no precedent in the codebase, and
  `TreeSitter.DotNet` already solves the cross-platform-distribution problem
  it was supposed to fix.
- *Require the user to install `libtree-sitter` system-wide.* Violates goal
  2 ("no external runtime deps"). Hard no.

**Rationale:** `TreeSitter.DotNet` collapses the entire native-binary
problem into a single `<PackageReference>`. The 28-grammar bundle adds
package size (~30-40 MB across all RIDs) but no runtime cost, and removes
maintenance work from every future per-language change. The trade-off
accepted is community-maintained dependency vs. self-maintained native
matrix; given the project already takes similar bets on `sqlite-vec` and
`Microsoft.Data.Sqlite` for native binaries, this is consistent.

**Implication for per-language packages:** Language plugins (Change 2's
`Indexing.TypeScript`, future `Indexing.Python`, etc.) do NOT ship their own
grammar binaries — they pick up the bundled grammar from the host's
transitive `TreeSitter.DotNet` dependency. The plugin just declares which
language name (`"TypeScript"`, `"TSX"`, `"JavaScript"`) to load. This
materially simplifies every per-language change after this one.

### 2. `TreeSitterLanguageIndexer<TGrammarConfig>` abstract base, generic over a per-language config object

**Choice:** A new abstract class:

```csharp
public abstract class TreeSitterLanguageIndexer<TGrammarConfig>
    : ILanguageIndexer
    where TGrammarConfig : ITreeSitterGrammarConfig
{
    protected TGrammarConfig Config { get; }
    public abstract IReadOnlyCollection<string> FileExtensions { get; }
    public Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct);
    protected abstract INodeKindMapper Mapper { get; }
    protected virtual IModuleResolver? Resolver => null;
}
```

`IndexAsync` does the boilerplate (parse via `Config.Grammar`, walk the tree,
ask `Mapper` for each interesting node-type's kebab-case kind, emit
`SymbolDeclared` / `EdgeEmitted` / `ReferenceFound`). Subclasses provide:

- `FileExtensions` — what the host dispatches to it.
- `Mapper` — language-specific node-kind translation.
- `Resolver` — optional per-language module-resolver (TS uses tsconfig `paths`,
  Python looks for `__init__.py`, Go uses module paths). When `null`, the
  indexer emits intra-file refs only and leaves cross-file ref emission to a
  future post-pass (or to LSP enrichment).

**Alternatives considered:**

- *No base class — each language re-implements the parse+walk loop.* Repeats
  the most boilerplate-heavy slice across N languages. Deletes the whole point.
- *A `Func<TSNode, IndexEvent?>`-style visitor map instead of a base class.*
  More flexible at the function level but uglier at the codebase level (no
  type-driven discoverability), and gives plugin authors fewer guard rails. The
  abstract-base approach matches `LanguageIndexerBase` in the SDK today.
- *Generic only over the grammar handle, not a config object.* Saves one type
  parameter but loses the place to hang per-language defaults (default excludes,
  grammar version, embedded query strings). The config object is the natural
  carrier.

**Rationale:** The boilerplate that benefits most from sharing is parse +
walk + emit. The variation that *must* live per-language is node-kind
translation and module resolution. Splitting at exactly that line is the design
the abstract base encodes.

### 3. `INodeKindMapper` and `IModuleResolver` as SDK contracts

**Choice:** Both interfaces live in the public SDK
(`DevBitsLab.Mcp.SourceGraph.Sdk`), targeting `netstandard2.0` like the rest of
the SDK so out-of-tree plugins can implement them.

```csharp
public interface INodeKindMapper
{
    bool TryMapDeclaration(string nodeType, out NodeMapping mapping);
    bool TryMapReference(string nodeType, out string referenceKind);
    bool TryMapEdge(string nodeType, out string edgeKindName);
}

public interface IModuleResolver
{
    string? Resolve(string fromAbsolutePath, string importSpecifier, ILanguageProject? project);
}
```

`NodeMapping` carries the kebab-case `Kind`, optional `Modifiers`, optional
`Accessibility`. The host base class consumes both interfaces — no plugin
plumbing.

**Alternatives considered:**

- *Internal-only interfaces.* Closed off the slot for third-party tree-sitter
  language plugins (e.g. someone adding Kotlin via a NuGet plugin). Unnecessary
  walls.
- *Pattern-matched single visitor (`OnNode(string nodeType, …)`).* Conflates the
  three orthogonal questions (declaration vs reference vs edge); plugin authors
  re-derive the dispatch every time. The split mirrors the `IndexEvent` family
  exactly.
- *Ship a built-in C#-like default mapper.* No language is generic enough that
  a default mapper helps; even similar C-family languages (TS vs Java vs Kotlin)
  diverge on basics like `function_declaration` vs `method_declaration` node
  types. Defer until a real reuse pattern emerges.

**Rationale:** The two interfaces capture the *only* per-language work that the
abstract base can't generalise. Anything else (parsing, walking, IO, IndexEvent
construction) collapses into shared code.

### 4. Scope-config additions: `language` (informational) and `enrichment` (forward-declared)

**Choice:** Add two optional fields to scope entries in `.sourcegraph.json`:

```jsonc
{
  "scopes": [
    {
      "name": "frontend",
      "paths": ["src/web/**/*.ts"],
      "language": "typescript",
      "enrichment": {
        "lsp": { "command": "typescript-language-server", "args": ["--stdio"] }
      }
    }
  ]
}
```

`language` is a free-form string at v1, validated only as kebab-case to keep the
field tidy; the loader does NOT reject unknown values, mirroring the soft-registry
posture used elsewhere (kebab-case kinds, payload keys). `enrichment` parses to a
typed `ScopeEnrichmentConfig` and is surfaced via `scopes info`, but the host has
no enrichment runtime in this change — the field is forward-declared so Change 2
can land enrichment-aware indexers without re-touching the loader.

**Alternatives considered:**

- *Defer both fields until Change 2.* Bundles schema work into the language
  change, which has enough surface already (grammar binding, JSX, module
  resolver). Locking the schema first keeps Change 2 about TS, not config.
- *Closed enum for `language`.* No registry to enforce against at v1; an enum
  becomes a moving target every new language. Soft string + kebab-case
  validation matches the project's posture.
- *`enrichment` accepts arbitrary keys, not just `lsp`.* Worth doing eventually
  (an `embeddings` or `static-analysis` block is plausible). Out of scope for v1
  to keep the validator small and the surface honest.

**Rationale:** Lock the on-disk shape before there's a producer to break. Same
playbook the codebase used for `PayloadKeys` and the kind-vocabulary surface.

### 5. Don't lift any reserved canonical-key schemes in this change

**Choice:** The `open-language-contract` requirement reserves several schemes
as *known-rejected* (`vbnet`, `fsharp`, `razor`, `js`, `ts`, `jsx`, `tsx`,
`vue`, `svelte`, plus `python`, `go`, `rust` as unknown-rejected). This change
emits no rows of its own and adds no producers — so it doesn't touch the
scheme list. Each per-language change lifts the schemes it needs (Change 2
lifts `ts`/`tsx`/`js`/`jsx`).

**Alternatives considered:**

- *Lift the entire roadmap's worth of schemes pre-emptively.* Reserves names
  for indexers that may never ship; the rejection-on-emit guarantee weakens
  every time we lift without a producer.
- *Move all "reserved-rejected" to "reserved-warned" instead.* Conflates "this
  scheme is documented as future" with "this scheme is accepted now". The strict
  rejection at v1 is what made the soft registry safe to ship.

**Rationale:** Each language earns its scheme by shipping an indexer that emits
it. Don't promise; deliver.

## Risks / Trade-offs

- **`TreeSitter.DotNet` is community-maintained.** A second-party dependency
  in the indexer's hot path. → Mitigation: pin a specific version in the
  csproj; treat any upstream bump as its own change with a smoke test
  asserting parser results haven't shifted. Fork is available as a fallback
  if the package goes unmaintained — we own the abstract base and SDK
  contracts that sit above it, so swapping the dependency is contained.
- **Bundle ships ~28 grammars even when only a few are used.** Package size
  is ~30-40 MB across all RIDs. → Acceptable for a developer tool the user
  already installs as a global `dotnet tool`. Acts as a forward-investment
  for future per-language changes (Python / Go / Rust grammars are already
  there).
- **Tree-sitter parse memory pressure on huge files.** A 50k-line TS file with
  deep JSX nesting can produce a multi-MB AST. → Mitigation: stream the walk
  rather than materialising the whole tree in memory; cap files at a configurable
  size limit (default 10 MB) with a debug-log skip beyond that. `XamlReader`
  has the same shape today.
- **`enrichment` field accepted but inert at v1.** Operators may set
  `enrichment.lsp` and observe nothing happens. → Mitigation: `scopes info`
  shows the field with a `(no consumer)` annotation when no plugin claims it,
  matching the soft-registry's "diagnostic before strict" pattern. Removed
  automatically once the first consumer (Change 2) ships.
- **Generic `TreeSitterLanguageIndexer<T>` may be over-fitted to TS at v1
  even though no TS code lives in this change.** → Mitigation: design review
  validates the base class signature against at least one second roadmap target
  (Python sketch in design notes, not committed). If Change 2's TS subclass
  needs to override more than three protected members on the base, that's the
  signal to revise the base before any third language joins.
- **Native asset placement is sensitive to publish layout.** `dotnet publish`
  vs `dotnet tool install` resolve `runtimes/<rid>/native/` differently across
  versions. → Mitigation: explicit smoke test in the integration project that
  asserts the binary loads from the published tool layout.
- **Reported columns are byte offsets, not character offsets.** Tree-sitter's
  `Point.Column` is a byte offset within the line, not a UTF-16 char offset
  (the convention Roslyn uses). For ASCII source the two coincide; for code
  containing emoji or wide characters in identifiers the reported column will
  drift right of the visual position. The columns are still positionally
  meaningful — clicks land in the right neighbourhood — but they don't survive
  arithmetic (e.g. "subtract two columns to get span width"). → Mitigation:
  documented as a known limit; deferred until a real consumer needs char
  precision. The XAML indexer has the same convention (XML byte offsets), so
  the codebase's existing tools tolerate the imprecision.

## Migration Plan

1. **No SDK version bump required at the source level**, but the SDK csproj
   bumps minor (1.x → 1.(x+1)) because three new types (`INodeKindMapper`,
   `IModuleResolver`, `LanguageIndexerOptions`) join the public surface. Pure
   addition, no breakage.
2. **No schema bump.** The host emits no rows; storage stays at V11.
3. **Land in one PR** against `main` referencing `add-tree-sitter-language-indexer-host`.
   Pieces (project skeleton, SDK additions, scope-config loader update,
   `scopes info` CLI, native packaging) are independent at the commit level
   but ship together so the next change can rest on the full backbone.
4. **CI matrix:** add `Indexing.TreeSitter` tests to the existing
   linux-x64 / linux-arm64 / osx-x64 / osx-arm64 / win-x64 jobs; verify the
   `TreeSitter.DotNet` runtime loads and a smoke parse succeeds on each.
5. **CHANGELOG entry / SDK csproj XML doc** documents the new types and the
   scope-config additions. ROADMAP.md gains a `Phase 5 — Open language hosts`
   section (or equivalent) listing this change as the unblocker for per-language
   work.

## Open Questions

- **Should `language` validate against a closed list once a few languages have
  shipped?** The soft-registry pattern says no — operators should be able to
  point a scope at a third-party plugin's language. But once we have plugins
  emitting `python` / `go` / `rust` schemes, a typo in `language: "phyton"`
  silently mis-routes. Lean: leave open at v1; revisit if mis-routing becomes
  a real support burden. A `vocabulary list` extension that surfaces
  `scopes_by_language` would be the right diagnostic.
- **Is `IModuleResolver` sufficient for "native" cross-language reference
  resolution** (e.g. a TS file importing a `.json` data file)? Probably not —
  the resolver returns a path, but the *target* must be an indexed file in
  some scope to be ref-able. Cross-extension refs are an open design problem
  the SDK can defer; the resolver returns whatever path it finds, the host
  silently drops the ref if no symbol matches. Documented as a known limit.
- **Should the abstract base own `FileScanned` emission, or push it to
  subclasses?** Owning it centrally guarantees one-and-only-one
  `FileScanned` per indexed file (today's invariant). Pushing it lets
  subclasses choose the timing (e.g. emit *after* a deferred enrichment pass).
  Lean: own it centrally; revisit if a real subclass needs to defer.
- **Per-grammar packaging vs one big `tree-sitter-grammars` package.** Each
  language plugin shipping its own grammar binary is the cleaner story
  (versioning, NuGet metadata) but means a polyglot repo pulls N native
  packages. A single rollup is cheaper for users but harder to maintain.
  Lean: per-language at v1 (matches "one plugin = one NuGet"); revisit when
  the polyglot user shows up.
