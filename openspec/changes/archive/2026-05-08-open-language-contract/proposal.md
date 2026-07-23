## Why

The plugin SDK shipped in the v0.6 extensibility work is shaped to one consumer
— the built-in C# Roslyn indexer — and bakes Roslyn's worldview into contract
surfaces that won't survive the languages we know are coming next: XAML
(WPF / WinUI 3 / UWP / Avalonia / Uno), then JS / TS / JSX / TSX / Vue / Svelte.
Closed kind enums, the .NET-attribute-shaped `AttributeAttached` event,
free-form canonical keys with no cross-language join story, and a per-document
`IndexContext` with no project handle all push their costs onto every plugin
we'd add. With zero external plugin authors and one in-tree consumer today,
this is the cheapest possible moment to break those surfaces and re-found them
on a vocabulary that scales.

## What Changes

- **BREAKING:** Replace `EdgeKind` and `PluginSymbolKind` enums with `string`
  at the SDK boundary. Provide `EdgeKinds` and `SymbolKinds` static classes
  with kebab-case constants for the values that exist today (`"calls"`,
  `"inherits"`, `"class"`, `"method"`, …). Plugins MAY emit any kebab-case
  identifier; storage holds the kind as TEXT.
- **BREAKING:** `EdgeEmitted` gains
  `IReadOnlyDictionary<string, string>? Metadata` so per-edge facts (binding
  paths, event names, prop names) have a place to live.
- **BREAKING:** Canonical keys MUST be `<scheme>:<rest>`. Schemes `csharp` and
  `xaml` are reserved-and-enforced at v1; `vbnet`, `fsharp`, `razor`, `js`,
  `ts`, `jsx`, `tsx`, `vue`, `svelte` are reserved-but-not-yet-enforced
  (documented for cross-language joins). Paths in keys are repo-relative with
  forward slashes regardless of OS. Unknown schemes are a plugin error.
- **BREAKING:** Rename `AttributeAttached` → `AnnotationAttached` and add a
  `Flavor` field (`"csharp-attribute"` today; `"ts-decorator"`,
  `"vue-directive"`, `"svelte-action"` later). Storage table renames
  `attributes` → `annotations` with a `flavor TEXT NOT NULL` column.
- **NEW:** Minimal `ILanguageProject` (`Id`, `FilePaths`) and
  `ILanguageProjectFactory` (`ProjectMarkers`, `DiscoverAsync`) interfaces.
  `IndexContext` gains `ILanguageProject? Project`. The C# pathway gets a
  `MSBuildLanguageProject` wrapper fronting the existing `MSBuildWorkspace`
  to prove the contract end-to-end. Heavy plugin state lives in plugin-private
  subclasses; the interface stays minimal so TypeScript can extend it cleanly
  when it lands.
- **NEW:** MCP `initialize` response gains `edge_kinds`, `symbol_kinds`, and
  `annotation_flavors` arrays sourced from the active scope's plugin host.
  Soft registry — the published vocabulary is whatever the loaded indexers
  actually emit.
- **BREAKING:** Storage gains a `_meta.schema_version` row and a
  `SCHEMA_VERSION` constant. On schema mismatch (or absence), the store drops
  its tables and lets the watcher re-index from source. Cache is a derived
  artifact; no data migration is needed.

## Capabilities

### New Capabilities

- *(none — this change reforms existing capabilities only)*

### Modified Capabilities

- `extensibility`: SDK contract surfaces (`EdgeKind`/`SymbolKind` as strings,
  `Metadata` on `EdgeEmitted`, canonical-key URI convention, annotation
  rename, `ILanguageProject`/`ILanguageProjectFactory`).
- `indexing`: `RoslynIndexer` migrates to emit annotation events, string
  kinds, and URI-prefixed canonical keys; flows through a
  `MSBuildLanguageProject`.
- `storage`: schema version row and drop-and-rebuild on mismatch; kind
  columns become TEXT; `payload` JSON column on `edges`; `attributes` table
  renamed to `annotations` with a `flavor` column.
- `mcp-tools`: tool params taking edge/symbol kinds change from int enums to
  string names; `find_by_attribute` is renamed `find_by_annotation` and
  gains a flavor filter; the `initialize` response gains
  `edge_kinds` / `symbol_kinds` / `annotation_flavors` vocabulary arrays
  (alongside the existing usage-instructions string).

## Impact

- **Code:** SDK assemblies, Storage layer (schema + queries), Indexing layer
  (RoslynIndexer + MSBuildHost), Server (initialize response, plugin host,
  tools), every test that referenced `EdgeKind.*` / `PluginSymbolKind.*`
  values or the `attributes` table.
- **Public contract:** The SDK NuGet (`DevBitsLab.Mcp.SourceGraph.Sdk`) and
  every MCP tool exposed by the server. There are no external consumers
  today, so no deprecation cycle is required.
- **Persistence:** Existing `.sourcegraph/scopes/*.db` files are obsoleted;
  the schema-version check drops and rebuilds them on first start of the
  reformed server. No user action needed beyond the next index pass.
- **CLI:** No surface changes, but kind values passed to query commands now
  use string names. Help text + examples update.
- **Out of scope:** The XAML indexer itself (proposal 2:
  `xaml-language-indexer`); strict vocabulary registration; deprecation
  shims; broader `ILanguageProject` shape (TypeScript will push it wider).
