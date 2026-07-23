## ADDED Requirements

### Requirement: XAML scheme is exercised by a built-in indexer
The host SHALL ship an in-tree `XamlLanguageIndexer` registered for the `.xaml` file extension. After this change, `xaml` is no longer merely a reserved-and-enforced canonical-key scheme; it is a scheme actively emitted by a built-in indexer and persisted in storage on every scope that loads the indexer.

#### Scenario: XAML indexer registered alongside C# Roslyn indexer
- **WHEN** the host starts a scope that loads both the built-in C# Roslyn indexer and the XAML indexer
- **THEN** the dispatcher routes `.cs` files to the Roslyn indexer and `.xaml` files to the XAML indexer; both indexers' kinds appear in the scope's published `Capabilities.Experimental["sourcegraph.vocabulary"]`

#### Scenario: XAML scheme accepted from the indexer
- **WHEN** the XAML indexer emits `SymbolDeclared(CanonicalKey: "xaml:view:Views/Main.xaml", ...)`
- **THEN** the host accepts the key (`xaml` is reserved-and-enforced) and persists the symbol with that canonical key

### Requirement: ILanguageProjectFactory discovery is required at runtime
After this change, the host SHALL discover `ILanguageProjectFactory` instances from every registered plugin at scope startup, invoke `DiscoverAsync(repoRoot, ct)` once per scope, and cache the resulting `ILanguageProject` instances in a per-scope `Dictionary<string, ILanguageProject>` keyed by absolute file path. The dispatcher SHALL look up the project for each dispatched document and populate `IndexContext.Project` accordingly. This requirement was deferred from the SDK reform; it lands here because XAML is the first non-C# indexer that requires it.

#### Scenario: XAML file dispatched with project context
- **WHEN** the host dispatches `Views/Main.xaml` to the XAML indexer in a scope where `XamlLanguageProjectFactory` discovered a project owning that file
- **THEN** `IndexContext.Project` is the matching `XamlLanguageProject` instance, the indexer can read its `ResourceCache` for resource-resolution lookups, and the same instance flows for every `.xaml` file in the same project

#### Scenario: C# file dispatched with project context (regression check)
- **WHEN** the host dispatches `MainWindow.xaml.cs` to the Roslyn indexer in the same scope
- **THEN** `IndexContext.Project` is a `MSBuildLanguageProject` from `MSBuildLanguageProjectFactory.DiscoverAsync` (regression check that the deferred 5.3 plumbing now functions for the C# pathway too)

#### Scenario: File outside any project
- **WHEN** the host dispatches a `.xaml` file located outside any project's `FilePaths` (e.g. a loose file under `docs/`)
- **THEN** `IndexContext.Project` is `null` and the indexer is invoked with the per-file fallback semantics documented in the original SDK reform

### Requirement: XamlLanguageIndexer plugin contract
The `XamlLanguageIndexer` SHALL implement `ILanguageIndexer` and SHALL emit:

- Five symbol kinds under the `xaml:` URI scheme: `xaml-view`, `xaml-element`, `xaml-resource`, `xaml-style`, `xaml-template`
- Seven edge kinds: `code-behind`, `binds-path`, `binds-element`, `handles-event`, `uses-resource`, `instantiates-type`, `merges`, `applies-style`
- One annotation flavor: `xaml-attached-property`

Cross-language edges (`code-behind`, `handles-event`, `instantiates-type`) SHALL construct C# canonical keys via `CanonicalKeys.ForType` / `CanonicalKeys.ForMethod` (from `harden-sdk-pre-xaml`) so the resulting `dst` is byte-equal to the key the Roslyn indexer wrote for the same symbol.

#### Scenario: Code-behind edge joins XAML view to C# partial class
- **WHEN** the indexer encounters `<Window x:Class="MyApp.Views.Main" ...>` as the root of `Views/Main.xaml`
- **THEN** it emits `SymbolDeclared(CanonicalKey: "xaml:view:Views/Main.xaml", Kind: "xaml-view", ...)` and `EdgeEmitted(Src: "xaml:view:Views/Main.xaml", Dst: "csharp:T:MyApp.Views.Main", EdgeKindName: "code-behind", Metadata: null)`; the `dst` matches what the Roslyn indexer emitted for `MyApp.Views.Main`, so a query like `find_references --canonical-key csharp:T:MyApp.Views.Main` returns both the C# declaration and the XAML view

#### Scenario: Event handler edge resolves to C# method
- **WHEN** the indexer encounters `<Button Click="OnSave"/>` inside a view whose root is `<Window x:Class="MyApp.Views.Main">`
- **THEN** it emits `EdgeEmitted(Src: "xaml:element:Views/Main.xaml#<elementId>", Dst: "csharp:M:MyApp.Views.Main.OnSave", EdgeKindName: "handles-event", Metadata: { "event": "Click" })`

#### Scenario: Binding emits payload via PayloadKeys
- **WHEN** the indexer encounters `<TextBox Text="{Binding User.Name, Mode=TwoWay, Converter={StaticResource b2v}}"/>`
- **THEN** it emits `EdgeEmitted` with `EdgeKindName: "binds-path"` and Metadata including (verbatim) the keys `"path" = "User.Name"`, `"mode" = "two-way"`, `"converter" = "BoolToVisibility"` (the keys come from the `PayloadKeys` constants documented by `harden-sdk-pre-xaml`)

#### Scenario: ElementName binding emits two edges
- **WHEN** the indexer encounters `<TextBox Text="{Binding ElementName=OtherCtrl, Path=Value}"/>`
- **THEN** it emits two edges: a `binds-path` edge from the source element to nothing-resolvable carrying `Metadata = { "path": "Value", "element-name": "OtherCtrl" }`, plus a `binds-element` edge from the source element to the resolved target element (looked up via `x:Name` within the same XAML view) carrying the same `path` payload

#### Scenario: Attached property emitted as annotation
- **WHEN** the indexer encounters `<Button Grid.Row="2" Grid.Column="1"/>`
- **THEN** it emits two `AnnotationAttached` events with `Flavor: "xaml-attached-property"`, one named `"Grid.Row"` (args `"2"`) and one named `"Grid.Column"` (args `"1"`); both attach to the button element's symbol
