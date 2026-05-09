## Why

XAML is the next language family queued behind C# — the explicit reason the
SDK contract was reformed by `open-language-contract`. WPF, WinUI 3, UWP,
Avalonia, and Uno apps are dominated by `.xaml` view files paired with C#
codebehind, and today the source graph stops at the `.cs` boundary. An agent
that asks "what calls `OnSave`?" gets back the partial class declaration but
not the `Click="OnSave"` site that triggers it; "where is `Main` referenced?"
misses the `x:Class="MyApp.Views.Main"` that anchors the entire view. This
change ships the first non-C# indexer and exercises every cross-language
contract the SDK reform put in place — open kind vocabularies, the
`<scheme>:<rest>` canonical-key URI convention, the per-edge `Metadata`
channel, the `AnnotationAttached` flavor discriminator, and the
`ILanguageProject` plumbing that was deliberately left half-wired pending
its first real consumer.

## What Changes

- **NEW:** A `XamlLanguageIndexer` plugin (in-tree, ships with the server)
  registered for the `.xaml` extension. Emits five symbol kinds, seven edge
  kinds, and one annotation flavor, all under the `xaml:` URI scheme that
  the SDK reserved-and-enforced at v1.
- **NEW:** Five symbol kinds — `xaml-view` (root with `x:Class`),
  `xaml-element` (`x:Name` / `Name`), `xaml-resource` (`x:Key` in a
  `ResourceDictionary`), `xaml-style` (`Style` with `x:Key`),
  `xaml-template` (`DataTemplate` / `ControlTemplate` /
  `ItemsPanelTemplate` with `x:Key`).
- **NEW:** Seven edge kinds — `code-behind` (xaml-view → csharp:T:...),
  `binds-path` (xaml-element → resolved-target-or-null, payload carries
  binding fields), `handles-event` (xaml-element → csharp:M:...),
  `uses-resource` (xaml-element → xaml-resource), `instantiates-type`
  (xaml-view → csharp:T:...), `merges` (resource-dictionary →
  resource-dictionary), `applies-style` (xaml-element → xaml-style).
- **NEW:** One annotation flavor — `xaml-attached-property` (carries
  `Grid.Row="2"`, `DockPanel.Dock="Top"`, etc. through the existing
  `AnnotationAttached` pathway).
- **NEW:** A `XamlLanguageProject : ILanguageProject` (plugin-private)
  holding the per-project resource cascade cache, populated at project
  load by walking `App.xaml`, `MergedDictionaries`, and theme
  `Generic.xaml`. Paired with `XamlLanguageProjectFactory` declaring
  `ProjectMarkers = ["*.csproj", "*.xaml"]` (piggybacks on
  `MSBuildLanguageProjectFactory` for project discovery).
- **NEW:** Framework-profile auto-detection. A single indexer covers
  WPF / WinUI 3 / UWP / Avalonia / Uno; profile is detected from the
  default `xmlns` and namespace mappings on the root element, and a
  per-profile `IXamlDialect` strategy handles markup-extension dialect
  differences (`{Binding}` vs `x:Bind`).
- **MODIFIED:** `find_definition`, `list_callers` / `list_callees`,
  `find_by_annotation`, `list_symbols_in_file`, `neighborhood`, and
  `module_summary` enumerate the new XAML kinds in their `kind` /
  `flavor` parameter docs and surface them in their result rendering.
- **CARRYOVER:** The deferred 5.3 / 6.1 / 6.2 plumbing from
  `open-language-contract` (wire the dispatcher to populate
  `IndexContext.Project` for every dispatched document; per-scope
  `ILanguageProjectFactory` discovery; per-scope project lookup map)
  lands in this change. These tasks were marked "lands with first
  non-C# language indexer." This is that indexer.

## Capabilities

### New Capabilities

- *(none — this change adds language coverage to existing
  `extensibility` and `indexing` capabilities; it does not introduce a
  new capability)*

### Modified Capabilities

- `extensibility`: `xaml` becomes a reserved-and-enforced canonical-key
  scheme that is now exercised end-to-end by a built-in indexer
  (previously reserved-and-enforced but unexercised); a
  `XamlLanguageIndexer` plugin contract is documented; the
  `ILanguageProjectFactory` discovery loop is required to run and
  populate `IndexContext.Project` for every dispatched document.
- `indexing`: `XamlLanguageIndexer` is registered for `.xaml`; XAML
  files are parsed via `System.Xml.XmlReader`; framework profile is
  auto-detected per file from the root-element `xmlns` set; the
  per-project resource cascade is materialised once at project load.
- `mcp-tools`: the new XAML symbol kinds, edge kinds, and annotation
  flavor are part of the active scope's published vocabulary on every
  scope that loads the indexer; the documented `kind` parameter
  enumerations on `list_callers` / `list_callees` and `find_by_annotation`
  grow to include them.

## Impact

- **Code:** A new project assembly (`DevBitsLab.Mcp.SourceGraph.Indexing.Xaml`,
  or equivalent) holding the indexer, the `IXamlDialect` strategies, and
  the `XamlLanguageProject` / factory; small additions to the dispatcher
  and `PluginHost` to land the carried-over 5.3 / 6.1 / 6.2 work; tests
  under `tests/DevBitsLab.Mcp.SourceGraph.Tests/` plus two new fixture
  solutions (`tests/fixtures/SampleWpf/` and
  `tests/fixtures/SampleAvalonia/`).
- **SDK contract:** Unchanged. Every primitive XAML needs is already in
  the SDK after `open-language-contract` shipped (open kinds, URI keys,
  `Metadata`, annotations with flavor, `ILanguageProject`).
- **Persistence:** No schema bump. `kind_name` and `flavor` columns are
  TEXT and accept new values without migration; the `payload` JSON
  column on `edges` is exactly the binding-fact channel `binds-path`
  needs.
- **Tools:** No new tool surfaces. New vocabulary entries flow through
  the soft-registry path published by `MCP initialize`; the `kind`
  parameter on existing tools accepts the new kebab-case identifiers
  automatically.
- **CLI:** No surface changes; `vocabulary list` (if shipped) shows the
  new kinds on any scope that loads the indexer.
- **Out of scope:** TS / web-stack indexers (a later proposal); a
  visual designer surface; XAML editing or formatting; runtime
  data-binding semantics (the indexer is purely structural).

**Depends on:** `open-language-contract` (already shipped — provides
every contract surface this change consumes); `harden-sdk-pre-xaml`
(recommended before shipping — completes any remaining SDK-side
audits before XAML stress-tests the surfaces in production).

**Unblocks:** `payload-tooling` (the cross-language `binds-path` /
`handles-event` edges produce the first real `payload` data the
tooling story has to render).
