## Context

The plugin SDK that shipped with the v0.6 extensibility work has one in-tree
consumer (`RoslynLanguageIndexer` / `RoslynIndexer` for `.cs`) and zero
external plugin authors. Its surface — `EdgeKind`, `PluginSymbolKind`,
`AttributeAttached`, the per-document `IndexContext` — was implicitly
designed against Roslyn's data model, because Roslyn was the only consumer
on hand. The next two language families queued behind it (XAML across five
framework variants, then JS / TS / JSX / TSX / Vue / Svelte) will not fit
those surfaces cleanly. The cost of changing them is bounded today (one
in-tree consumer, internal-only NuGet) and unbounded later (every language
plugin would be migrated).

This change reforms the SDK contract before XAML lands, so XAML — and the
web stack after it — can sit on a stable shape. It is intentionally a
contract reform, not a feature: no new query reaches the agent, no new
symbol kind is indexed, no plugin gets loaded that wasn't loaded before.
The only externally visible improvement is that the MCP `initialize`
response now publishes the active vocabulary so agents know what's
queryable in this scope.

## Goals / Non-Goals

**Goals:**

- A canonical-key shape that lets cross-language joins (XAML's `x:Class`
  → C# class; future JSX's `<Button>` → TS export) reduce to string
  equality, with no per-pair adapter logic.
- Open vocabularies for edges, symbols, and annotation flavors so each new
  language adds new kinds without churning the SDK enum.
- A per-edge metadata channel rich enough to carry binding paths, event
  names, and prop names — the things templated UI dialects need to attach
  to edges that today's `(src, dst, kind)` triple cannot hold.
- A minimal place to hang per-project plugin state (`ILanguageProject`),
  large enough to host the C# `MSBuildWorkspace` end-to-end and small
  enough that TypeScript can widen it later without breaking XAML.
- A storage migration that costs the user nothing — the cache is derived,
  the schema bump triggers an automatic rebuild on first start.

**Non-Goals:**

- Implementing XAML support (proposal `xaml-language-indexer`, lands
  next).
- Implementing TS / Vue / Svelte support, or pre-designing the language
  service abstractions those will require.
- Adding strict vocabulary registration (no plugin author exists to enforce
  against; soft registry is the right default).
- Maintaining backward compatibility with the v0.6 SDK contract or with
  on-disk DBs from prior versions.
- Adding new MCP tools or new symbol/edge semantics. The reform is
  internal-shape only.

## Decisions

### 1. Strings (with constants) for kinds, not closed enums

**Choice:** `EdgeKind` and `PluginSymbolKind` enums become `string` at the
SDK boundary. Static `EdgeKinds` and `SymbolKinds` classes hold kebab-case
constants for the values that exist today. Storage stores them as TEXT.

**Alternatives considered:**

- *Keep extending the closed enum.* Every new language requires an SDK bump
  and a coordinated rebuild of every plugin. Untenable once we have more
  than one external plugin author.
- *Strict registry (plugins declare kinds in a manifest at load time).*
  Stronger guarantees but adds a manifest mechanism for marginal value
  before a real second consumer exists. Soft registry is forward-compatible
  with a future strict one.

**Rationale:** Migration cost from closed enum to string is paid once, by
us, today. Migration cost in the opposite direction grows monotonically
with every language plugin we add.

### 2. `IReadOnlyDictionary<string, string>?` on `EdgeEmitted`, not JSON

**Choice:** `EdgeEmitted` gains an optional metadata dictionary. Stored on
the `edges` table as a `payload TEXT NULL` JSON column.

**Alternatives considered:**

- *`string? Json`.* Forces every consumer to parse, no IDE discoverability
  on the writer side, easy to produce malformed JSON.
- *Side table for metadata.* Joinable but heavier — one extra table for
  data that's only meaningful read-back-with-the-edge.

**Rationale:** Dict on the wire, JSON in storage. Best ergonomics for
plugin authors, smallest schema impact.

### 3. Canonical-key URI convention with reserved schemes

**Choice:** Keys MUST be `<scheme>:<rest>`. Schemes `csharp` and `xaml` are
reserved-and-validated at v1. Schemes `vbnet`, `fsharp`, `razor`, `js`,
`ts`, `jsx`, `tsx`, `vue`, `svelte` are reserved-but-not-yet-enforced
(documented for cross-language joins). Paths inside keys are repo-relative
and use forward slashes regardless of OS. Unknown schemes are a plugin
error.

**Alternatives considered:**

- *Free-form keys per plugin.* Cross-language joins become per-pair
  custom code. Two languages: tolerable. Six: a maintenance disaster.
- *Hash-based identity (sha of declaration text).* Not stable across
  edits; defeats the upsert design.
- *Open scheme list (no enforcement).* Typo proliferation; cross-language
  joins silently miss when one side typos `cshapr:`.

**Rationale:** A small, enforced, documented prefix vocabulary is the
cheapest known mechanism that makes cross-language symbol identity
work. Repo-relative forward-slash paths kill an entire class of
cross-platform key drift.

### 4. `AnnotationAttached` rename with `Flavor` field

**Choice:** `AttributeAttached` becomes `AnnotationAttached(SymbolKey,
Name, Flavor, FullName?, ArgsJson?, TargetKey?)`. Storage table
`attributes` becomes `annotations` with a `flavor TEXT NOT NULL` column.
The C# indexer emits `Flavor: "csharp-attribute"`.

**Alternatives considered:**

- *Add `string? Flavor` to `AttributeAttached` without renaming.* Carries
  the `Attribute` name forward into Vue directives and Svelte actions
  where it doesn't belong. Cleaner to rename now.
- *One event type per flavor (`AttributeAttached`, `DecoratorAttached`,
  `DirectiveAttached`).* N+1 explosion in the SDK; each new language
  pattern adds a new event type.

**Rationale:** A single `AnnotationAttached` event with a flavor
discriminator is the simplest model that absorbs every annotation pattern
across languages.

### 5. Minimal `ILanguageProject` interface

**Choice:** Two SDK interfaces. `ILanguageProject` exposes only `Id` and
`FilePaths`. `ILanguageProjectFactory` exposes `ProjectMarkers` (e.g.
`["*.csproj", "tsconfig.json"]`) and `DiscoverAsync(repoRoot)`.
`IndexContext` gains `ILanguageProject? Project`. Heavy state lives in
plugin-private subclasses (e.g. `MSBuildLanguageProject` wraps the
existing `MSBuildWorkspace`; future `TsLanguageProject` will hold a
tsserver instance).

**Alternatives considered:**

- *No abstraction; let each plugin do its own project discovery in
  static state.* Generalising N ad-hoc patterns later is more expensive
  than designing one minimal interface now.
- *Rich interface designed for TS up front.* Premature: we don't know
  what TS will actually need from the host. Better to widen when TS
  arrives with a real demand.

**Rationale:** YAGNI on the interface body; eager on the plumbing point.
Refactoring `IndexContext` later is the expensive change; adding members
to a minimal interface is the cheap one.

### 6. Soft vocabulary registry, published in `initialize`

**Choice:** The MCP `initialize` response gains `edge_kinds`,
`symbol_kinds`, and `annotation_flavors` arrays. Values are sourced from
what the active scope's loaded indexers actually emit (or from their
declared constants when known up front). No strict manifest.

**Alternatives considered:**

- *Strict manifest declared per plugin at load time.* Catches typos at
  load time but adds a registration mechanism with no consumer to
  enforce against today.
- *Don't publish.* Agents would have to guess what kinds are queryable
  in this scope; loses the natural extension of the existing
  tool-usage instructions block.

**Rationale:** Mirrors how MCP itself surfaces tools — the server
declares, the client adapts. Strict registry can be added later without
breaking any consumer.

### 7. Schema bump → drop and rebuild

**Choice:** `SqliteGraphStore` gains a `SCHEMA_VERSION` constant and a
`_meta.schema_version` row. On `EnsureSchemaAsync`, if the existing DB
reports a lower version (or has no version row), drop all tables and
recreate from `Schema.V1` + `Schema.V2`. The watcher rebuilds the cache on
the next index pass.

**Alternatives considered:**

- *Write a real ALTER TABLE migration that preserves data.* The cache is
  derived from source; migration logic is pure liability for zero benefit.
- *Detect version mismatch and refuse to start.* Worse user experience;
  forces a manual `rm -rf .sourcegraph/scopes/`.

**Rationale:** The DB is a derived artifact. Treat it as cache. The next
index pass is the source of truth.

### 8. Atomic single-PR landing, single major version bump

**Choice:** All seven changes (1–7 above) ship in one PR and one major
SDK version bump. No intermediate state where (e.g.) edge kinds are
strings but `Metadata` isn't there yet.

**Alternatives considered:**

- *Sequential proposals (one per decision).* Each intermediate state is
  useless to anyone — there's no plugin author to consume them. Splits
  review effort across N PRs without a corresponding split in coupling.

**Rationale:** Coupling is real (validation needs URI scheme; storage
needs kind columns; tools need string-typed params), so coherent landing
is the natural unit.

## Risks / Trade-offs

- **TEXT kind columns are slower than INT comparisons.** → Mitigation: add
  covering indexes on `(kind_name)` for `edges` and `symbols`. The
  cardinality is low (currently 9 edge kinds, 14 symbol kinds; up to
  ~30 / ~30 once XAML and the web stack land). Benchmark on the existing
  fixture solution and on this repo's own self-index before merge; tune
  if any tool's p95 regresses by more than 10%.

- **Soft registry permits typos to silently proliferate.** A plugin
  emitting `bind-path` and another emitting `binds-path` would never
  collide and queries against either would miss the other. → Mitigation:
  publish the live vocabulary in `initialize` (a typo would show up in
  the diff between expected constants and actual emitted values); add a
  CLI command `vocabulary list` for offline inspection. Strict registry
  is a future-compatible upgrade if needed.

- **Repo-relative path in canonical keys breaks if the repo is moved or
  if a scope's root sits inside a worktree.** → Mitigation: the host
  resolves all paths against `Scope.Root` before normalising; paths in
  keys are always relative to that root, never to the absolute filesystem
  position. Document this invariant at the SDK contract surface.

- **`ILanguageProject` may turn out to be the wrong shape once
  TypeScript arrives.** → Mitigation: kept minimal precisely for that
  reason. The interface has two members; widening it (adding `string
  TsConfigPath`, `Task<TextDocument> GetGeneratedTextAsync(...)`, etc.)
  is purely additive from the C# side and only forces the as-yet-
  unwritten TS plugin to implement the new members.

- **`AnnotationAttached` rename touches every test and tool that built
  on `AttributeAttached`.** → Mitigation: mechanical rename, caught at
  compile time. The existing C# indexer's call sites (`AttributeExtractor`,
  `BulkInsertAttributesAsync`, `find_by_attribute` tool, `attributes`
  table queries) are all in this repo and migrate together with the SDK.

- **Vocabulary publishing in `initialize` exposes "implementation noise"
  if a scope has plugins that emit experimental kinds.** → Mitigation:
  this is intentional. Publishing what's actually emittable (not what's
  "officially supported") is the honest answer to "what can I query in
  this repo?"

## Migration Plan

1. **Bump SDK version** from current 0.6.x to 0.7.0 (or major version per
   project convention).
2. **Land all changes in one PR** against `main`. The PR refactors:
   - `src/DevBitsLab.Mcp.SourceGraph.Sdk/` (event types, kind constants,
     language project interfaces)
   - `src/DevBitsLab.Mcp.SourceGraph.Storage/` (schema version, table
     renames, kind column type changes, payload column)
   - `src/DevBitsLab.Mcp.SourceGraph.Indexing/` (RoslynIndexer rewires
     emissions to new event shapes; `MSBuildLanguageProject` wrapper)
   - `src/DevBitsLab.Mcp.SourceGraph.Server/` (PluginHost discovers
     `ILanguageProjectFactory`; initialize response builder; tools use
     string kinds)
   - `tests/` (mechanical rename of every kind constant and event type
     reference)
3. **Schema-version check** in `EnsureSchemaAsync` automatically drops
   and rebuilds the DB on first start of the new server. No user
   intervention.
4. **CHANGELOG entry** documents the rename and the kind-name list.
5. **No rollback strategy needed** — the only consumers are this repo
   and the test fixtures; rolling back means reverting the PR and
   re-running the index.

## Open Questions

- **Should the host's URI-scheme validation be a hard error or a
  warning** for unreserved-but-not-yet-enforced schemes (e.g. a plugin
  emits `python:M:foo`)? Current decision in this proposal: hard error
  (only reserved-and-enforced schemes accepted at v1). Alternative:
  warn and accept, expanding the enforced set with each language we
  bring in. Decide before the URI-scheme validator is implemented.

- **Should `EdgeKinds.Tests` and other "framework-aware" constants
  remain in the core SDK constants class**, or move to a per-language
  constants class as we add languages? Today they're C#-shaped
  (`Tests` came from xUnit/NUnit/MSTest detection). Probably fine in
  core for now; revisit when a non-C# test framework arrives.
