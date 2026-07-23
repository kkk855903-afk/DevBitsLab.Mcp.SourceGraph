## Context

`open-language-contract` reformed the SDK with an explicit dependency on
"the next two language families queued behind C#": XAML across five
framework variants (WPF, WinUI 3, UWP, Avalonia, Uno), then the web stack
(JS / TS / JSX / TSX / Vue / Svelte). XAML is the forcing function — it
is the first non-C# indexer and the first cross-language join the
canonical-key URI convention has to actually carry. Until XAML emits a
single row, `xaml:` is reserved-and-enforced in the validator but
unexercised; `payload TEXT NULL` is the channel binding metadata
WILL flow through but holds nothing today; `ILanguageProject` is the
abstraction whose only consumer is the C# pathway.

This change ships the XAML indexer and uses it to validate every
contract surface the SDK reform put in place. The cross-language join
between `xaml:view:Views/Main.xaml` and `csharp:T:MyApp.Views.Main`
becomes a real edge in real storage, the
`Capabilities.Experimental["sourcegraph.vocabulary"]` arrays grow with
new XAML kinds, and the deferred `ILanguageProjectFactory` discovery
loop (5.3 / 6.1 / 6.2 from `open-language-contract`) lands because the
XAML indexer needs `IndexContext.Project` populated for every
dispatched `.xaml` file.

The proposal sits on top of `harden-sdk-pre-xaml` (recommended): the
`PayloadKeys` constants are exactly the keys this indexer populates,
the `CanonicalKeys.ForType` helper is exactly how this indexer
constructs the C# side of the `code-behind` edge, the always-render-
payload markdown change lights up XAML's payload data immediately, and
`vocabulary list` becomes the diagnostic for any drift this indexer
introduces against the existing C# vocabulary.

## Goals / Non-Goals

**Goals:**

- Index `.xaml` files end-to-end across WPF / WinUI 3 / UWP / Avalonia /
  Uno from a single indexer with framework-profile auto-detection.
- Emit cross-language edges (`code-behind`, `handles-event`,
  `instantiates-type`) that resolve to real C# canonical keys via
  string equality on `symbols.canonical_key`.
- Land the deferred dispatcher plumbing from `open-language-contract`
  (5.3 / 6.1 / 6.2) so `IndexContext.Project` is populated for every
  dispatched document, not just `.cs` ones.
- Carry binding metadata (path, mode, converter, …) on `binds-path`
  edges via `EdgeEmitted.Metadata`, using the `PayloadKeys` constants
  from `harden-sdk-pre-xaml`.
- Provide two small fixture solutions (`SampleWpf`, `SampleAvalonia`)
  exercising both the `{Binding}` and `x:Bind` markup-extension dialects.

**Non-Goals:**

- TS / JSX / Vue / Svelte indexers (a later proposal).
- A visual designer or XAML editor; this is purely structural indexing.
- Runtime data-binding semantics (we record what's wired, not whether
  it works at runtime).
- Full Generic.xaml theme cascade across referenced projects (the
  resource cache is per-project for v1; cross-project cascade is an
  open question).
- Compiled-binding evaluation (`x:Bind` payload is recorded as text;
  we do not attempt to validate against the codebehind type).

## Decisions

### 1. Single indexer with framework-profile auto-detection

**Choice:** One `XamlLanguageIndexer` class registered for `.xaml`. A
profile (`Wpf`, `WinUI`, `Uwp`, `Avalonia`, `Uno`) is detected per file
from the default `xmlns` and namespace mappings on the root element. A
per-profile `IXamlDialect` strategy handles the markup-extension
dialect (`{Binding}` for WPF/Avalonia/Uno, `x:Bind` for WinUI 3 / UWP),
namespace-mapping rules (`clr-namespace:` vs. `using:`), and
profile-specific symbol kinds (e.g. Avalonia `Setter` semantics).

**Alternatives considered:**

- *Five indexers (one per framework).* Multiplies the dispatcher table,
  the plugin discovery, and the test surface for ~80% shared code.
  Splitting becomes worth it only if profiles diverge enough to share
  almost nothing — no current evidence supports that.
- *No profile awareness; flatten the differences in storage.* Loses
  information (e.g. `x:Bind` is statically typed and emits different
  edges than `{Binding}`); cross-language join misses break silently.

**Rationale:** Profile detection is cheap (a single root-element
inspection); profile-specific behaviour is contained inside an
`IXamlDialect` strategy that is easy to extend. Framework forking can
happen when evidence demands; today the shared 80% is the design.

### 2. `System.Xml.XmlReader` parser, not vendor parsers

**Choice:** `System.Xml.XmlReader` for the XML grammar; a small
(~50 LOC) markup-extension tokenizer for `{Binding ...}` and `{StaticResource ...}`
syntax. No `System.Xaml`, no Avalonia/Uno/WinUI parser dependencies.

**Alternatives considered:**

- *`System.Xaml`.* Schema-aware but UWP/WinUI-shaped; doesn't load
  Avalonia's vocabulary; drags in `PresentationFramework` for WPF;
  not portable across all five profiles.
- *Vendor parsers (Avalonia.Markup.Xaml, Uno).* Tightly coupled to
  their compilation pipelines; vendoring multiple is heavy.

**Rationale:** The grammar is XML across all five profiles; what
varies is the vocabulary, which lives in `IXamlDialect`. `XmlReader`
+ a tokenizer is ~250 LOC for the day-1 surface and zero new package
references.

### 3. `XamlLanguageProject` as plugin-private state hanger

**Choice:** A new `XamlLanguageProject` class implements
`ILanguageProject` (its plugin-private subclass holds a per-project
resource cascade cache: `Dictionary<string, ResourceDefinition>` keyed
by resource key, populated at project load by walking `App.xaml`,
`MergedDictionaries`, and theme `Generic.xaml`). Paired with
`XamlLanguageProjectFactory` declaring
`ProjectMarkers = ["*.csproj", "*.xaml"]`.

**Alternatives considered:**

- *Widen `ILanguageProject` interface to expose project-private
  state slots.* Premature; SDK reform deliberately kept the interface
  minimal. The plugin-private subclass pattern is exactly the
  contract `open-language-contract` documented.
- *Static state in the indexer.* Breaks per-scope isolation; multi-scope
  monorepos see cross-scope leakage; refactoring out later is expensive.

**Rationale:** YAGNI on the interface body; eager on plumbing point.
The interface stays minimal; resource cache lives where it belongs
(in the plugin's project subclass).

### 4. Cross-language join via canonical-key string equality

**Choice:** XAML emits `code-behind` edges where `dst` is constructed
via `CanonicalKeys.ForType(xClassValue)` (e.g. `x:Class="MyApp.Views.Main"`
→ `csharp:T:MyApp.Views.Main`). The host's edge resolver looks up both
endpoints in `symbols.canonical_key`. Same pattern for
`handles-event` (uses `CanonicalKeys.ForMethod(...)`) and
`instantiates-type` (`CanonicalKeys.ForType(...)`).

**Alternatives considered:**

- *Per-pair adapter logic (XAML→C# join helper).* Two languages:
  tolerable. Six (XAML + 5 web stack flavors): unsustainable. The
  canonical-key URI convention exists precisely so this works without
  per-pair code.
- *Symbol-id-based join (resolve C# id at XAML emission time).*
  Ordering-dependent; fails if XAML is indexed before C# in the same
  pass. The string-equality model is order-independent.

**Rationale:** This is what the SDK reform was for. Exercising it
end-to-end on a real polyglot pair is half the value of this change.

### 5. `ElementName` binding emits two edges

**Choice:** A binding like `<TextBox Text="{Binding ElementName=OtherCtrl,
Path=Value}"/>` carries two facts: a path AND a referenced element. The
SDK's `EdgeEmitted` shape is one `(src, dst, kind, payload)` tuple. The
indexer SHALL emit two edges:

1. `binds-path` edge from the source element to nothing-resolvable (the
   `path` is on the referenced element, but evaluating it requires
   `ElementName` resolution). Payload carries `path`, `mode`,
   `converter`, etc.; `element-name` payload key signals the
   ElementName binding pattern.
2. `binds-element` edge from the source element to the referenced
   element (resolved by `x:Name` lookup within the same XAML view).
   Payload carries the `path` again so a join is unnecessary.

**Alternatives considered:**

- *Extend `EdgeEmitted` to carry a list of facts.* Premature; one
  pattern doesn't justify a contract surface change. If a second
  multi-fact pattern shows up (templated binding context? compound
  bindings?), revisit then.
- *Emit one edge with a synthetic merged target.* Loses information;
  no clean way to filter by "binds against named element".

**Rationale:** The two-edge workaround is mechanically simple and
preserves both facts. The design opens the question for a later SDK
revision; today the workaround is the right unit cost.

### 6. Attached properties as annotations, not edges

**Choice:** `Grid.Row="2"`, `DockPanel.Dock="Top"`, `Canvas.ZIndex="3"`
flow through the existing `AnnotationAttached` pathway with
`Flavor = "xaml-attached-property"`. They are NOT edges.

**Alternatives considered:**

- *Edges from element to attached-property declaration.* The attached
  property is declared on the panel type (e.g. `Grid.RowProperty`); the
  edge would point at a C# `csharp:F:System.Windows.Controls.Grid.RowProperty`
  symbol. Useful, but every element with an attached property would
  emit a redundant edge, swamping the graph.
- *New symbol kind `xaml-attached-property-set`.* Same redundancy
  cost; no clear query that wants it.

**Rationale:** The annotation flavor pattern was designed for exactly
this case (attached metadata that's *attached to* a symbol, not its
own first-class graph node). `find_by_annotation(name="Grid.Row",
flavor="xaml-attached-property")` answers "show me every element with a
non-default Grid.Row" cleanly.

### 7. Wire deferred 5.3 / 6.1 / 6.2 in this change, not separately

**Choice:** The dispatcher plumbing tasks deferred from
`open-language-contract` (`MSBuildLanguageProjectFactory` discovery
through dispatcher; per-scope `ILanguageProjectFactory` registration;
per-scope project lookup map) ride along in this change. The
`XamlLanguageIndexer` cannot function without `IndexContext.Project`
populated; piggybacking the long-deferred plumbing on the forcing
function is cheaper than a separate plumbing-only change.

**Alternatives considered:**

- *Land plumbing first, then XAML.* Adds a PR with no observable user
  outcome and no test that exercises the new plumbing end-to-end. The
  deferred-task labels say "lands with first non-C# language indexer"
  precisely because plumbing without a consumer can't be validated.
- *Skip the plumbing; have XAML do its own project discovery.*
  Defeats the SDK reform's purpose; every future language indexer
  reinvents the wheel.

**Rationale:** This change is the "first non-C# indexer that needs it"
the deferred-task notes anticipated. Bundle.

## Risks / Trade-offs

- **Profile auto-detection drift.** A misclassified profile picks the
  wrong `IXamlDialect`; binding edges land with the wrong markup-
  extension semantics. → Mitigation: detection runs on the root
  element only (cheap); fixtures cover one app per profile;
  `vocabulary list` from `harden-sdk-pre-xaml` surfaces drift in
  emitted kinds across scopes.
- **Resource cascade scope creep.** WPF themed controls library means
  `Generic.xaml` resolution can cross project boundaries. → Mitigation:
  v1 scopes the cache to single-project resources; cross-project
  cascade is a documented open question; queries that miss are
  discoverable via `find_definition` returning unresolved.
- **`ElementName` two-edge workaround sustainability.** If a second
  multi-fact pattern shows up (compiled binding contexts, template
  bindings), the SDK shape strains. → Mitigation: documented in design
  decision 5; SDK widening is purely additive when needed.
- **Perf on large XAML corpora.** Avalonia or Uno apps with hundreds
  of resource dictionaries may stress the parser. → Mitigation: parser
  is forward-only `XmlReader` (cheap); benchmark on the Avalonia
  fixture before merge; profile if regression appears.
- **The new symbol/edge kinds change `vocabulary list` output for every
  scope that loads the indexer.** Anyone who pinned exact strings in
  external tooling (none today) would see drift. → Mitigation:
  none required; soft registry's whole design accepts open vocabulary.

## Migration Plan

1. **No SDK version bump required.** Every primitive XAML needs is
   already in the SDK after `open-language-contract` shipped.
2. **No schema bump.** `kind_name` and `flavor` columns are TEXT and
   accept new values without migration; the `payload` JSON column on
   `edges` is the binding-fact channel `binds-path` needs.
3. **Land the plumbing changes (5.3 / 6.1 / 6.2 from
   `open-language-contract`) and the XAML indexer in one PR** against
   `main`. The plumbing can't be tested without the consumer; the
   consumer can't ship without the plumbing.
4. **New project assembly:**
   `src/DevBitsLab.Mcp.SourceGraph.Indexing.Xaml/` (or equivalent),
   referenced from the slnx, registered via `PluginHost`'s in-tree
   plugin path.
5. **Two new fixture solutions:** `tests/fixtures/SampleWpf/` (covers
   `{Binding}` dialect, `x:Class`, `Click="..."`, attached properties)
   and `tests/fixtures/SampleAvalonia/` (covers Avalonia's
   `clr-namespace:` mapping plus a profile-detection variant).
6. **CHANGELOG entry / SDK csproj XML doc note** documents the new
   kind vocabulary added to existing scopes.

## Open Questions

- **Cross-project Generic.xaml cascade.** WPF themed controls library
  pattern means resource resolution can cross project boundaries. v1
  scopes the cascade to single-project resources; an open question is
  whether to widen `XamlLanguageProject` to know about referenced
  projects' XAML, or to resolve cross-project cascade lazily at query
  time.
- **Should `xaml-template` split by template variety?** `DataTemplate`,
  `ControlTemplate`, and `ItemsPanelTemplate` have different query
  semantics. v1 treats them as one kind; consider splitting if queries
  show the conflation hurts.
- **Avalonia/Uno profiles in v1 vs. behind a flag.** Both are XAML
  dialects but with significant fork divergence (Avalonia's
  `ControlTheme`, Uno's WinUI 3 mapping). Lean: ship all five profiles
  in v1, test against Avalonia and one other (WPF or WinUI 3); revisit
  if profile-specific bugs proliferate.
- **Position-info granularity.** `Click="OnSave"` on line 14 col 9 of
  an element opened on line 12 col 5 — does the `handles-event` edge
  get attribute position or element position? Lean: attribute
  position (matches `find_references` mental model where the edge
  source is the *use site*, not the *containing scope*).
- **`x:Bind` compiled-binding payload depth.** The `x:Bind` markup
  extension supports method calls (`{x:Bind Vm.GetThing()}`) and
  binary expressions. v1 records the source text in payload as
  `path`; semantic resolution is deferred. Open question: do we want
  a `compiled` payload key signalling whether the binding is statically
  resolved?
