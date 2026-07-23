## ADDED Requirements

### Requirement: XAML file discovery and dispatch
The indexer dispatcher SHALL route every `.xaml` file in an indexed solution to the registered `XamlLanguageIndexer`. Documents are discovered via the same project-walking logic the C# pathway uses; XAML files appear in `.csproj` `<Page>`, `<ApplicationDefinition>`, or `<EmbeddedResource>` items and SHALL be enumerated alongside `.cs` documents during cold and live indexing.

#### Scenario: Cold index of a WPF solution
- **WHEN** `sourcegraph-mcp index <wpf-solution>` is invoked against a solution that contains `MainWindow.xaml`, `App.xaml`, and `Themes/Generic.xaml`
- **THEN** every `.xaml` document is dispatched to the `XamlLanguageIndexer`, every `.xaml.cs` codebehind is dispatched to the Roslyn indexer, and the resulting `IndexResult` reports per-language file counts merged

#### Scenario: Live edit of a XAML file
- **WHEN** the live indexer detects a change to `Views/Main.xaml` while the server is running
- **THEN** the XAML indexer is invoked with the changed document, prior emissions for that file are removed (the `DeleteSymbolsForFileNotInAsync` pattern applies to XAML symbols too), and the fresh emissions replace them in storage

### Requirement: XAML parser shape
XAML files SHALL be parsed via `System.Xml.XmlReader` with position tracking; markup-extension values (`{Binding ...}`, `{StaticResource ...}`, etc.) SHALL be parsed by a separate `MarkupExtensionParser` operating on the attribute value string. The implementation SHALL NOT depend on `System.Xaml`, `PresentationFramework`, or any vendor-specific XAML parser (Avalonia.Markup.Xaml, Uno, etc.) so the indexer remains portable across all five framework profiles.

#### Scenario: XmlReader-based parsing preserves position
- **WHEN** the indexer parses an element `<Button x:Name="SaveBtn" Click="OnSave"/>` at line 14 col 5 of `Views/Main.xaml`
- **THEN** the emitted `SymbolDeclared` carries position fields equal to the line/column where the element opens

#### Scenario: Markup extension parsed without vendor dependencies
- **WHEN** the indexer parses an attribute value `{Binding Path=User.Name, Mode=TwoWay, Converter={StaticResource b2v}}`
- **THEN** the resulting structured representation carries `Name = "Binding"`, named args `Path = "User.Name"`, `Mode = "TwoWay"`, `Converter = nested {StaticResource b2v}` — and no PresentationFramework or Avalonia assembly is loaded as part of parsing

### Requirement: Framework profile auto-detection
The XAML indexer SHALL detect the framework profile (one of `Wpf`, `WinUi`, `Uwp`, `Avalonia`, `Uno`) per file from the root element's namespace mappings. Detection rules:

- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` + WinUI controls namespace → `WinUi`
- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` (no WinUI) → `Wpf`
- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` + UWP-only namespaces → `Uwp`
- `xmlns="https://github.com/avaloniaui"` → `Avalonia`
- `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` + `xmlns:nventive=...` → `Uno`

The detected profile selects an `IXamlDialect` strategy that handles markup-extension and namespace-mapping differences for the file.

#### Scenario: WPF file detected from default xmlns
- **WHEN** the indexer parses a file whose root has `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` and no other vendor-specific namespaces
- **THEN** the detected profile is `Wpf`, the `WpfDialect` is selected, and `clr-namespace:` mappings are resolved per WPF rules

#### Scenario: Avalonia file detected from Avalonia xmlns
- **WHEN** the indexer parses a file whose root has `xmlns="https://github.com/avaloniaui"`
- **THEN** the detected profile is `Avalonia`, the `AvaloniaDialect` is selected, and `clr-namespace:` mappings use Avalonia's resolution rules

#### Scenario: Profile-specific markup extension dialect
- **WHEN** a WinUI 3 file uses `Text="{x:Bind ViewModel.Name}"` (compiled binding)
- **THEN** the `WinUiDialect`'s markup-extension dispatcher recognises `x:Bind`, the binding is recorded with `payload` keys including `mode`, and the canonical key form distinguishes it from a runtime `{Binding}`

### Requirement: Per-project resource cascade cache
For every project that contains XAML files, the `XamlLanguageProjectFactory` SHALL build a per-project `XamlLanguageProject` instance whose private `ResourceCache` indexes every `x:Key` declared in:

- The project's `App.xaml` `Application.Resources`
- Any `MergedDictionaries` referenced from `App.xaml`
- A theme `Generic.xaml` if present in the project's `Themes/` folder

The cache SHALL be populated once at project discovery and reused for every `.xaml` file in the project so resource-resolution lookups (`{StaticResource AccentBrush}` → declaration site) do not re-walk the cascade per file.

#### Scenario: Resource resolved from App.xaml
- **WHEN** the indexer encounters `<Button Background="{StaticResource AccentBrush}"/>` in `Views/Main.xaml`, and `App.xaml` declares `<SolidColorBrush x:Key="AccentBrush" Color="Blue"/>`
- **THEN** the indexer emits a `uses-resource` edge from the button element to the resource declaration site (resolved via the cache, no re-walk), and the resource's symbol carries kind `xaml-resource`

#### Scenario: Resource not found
- **WHEN** the indexer encounters `<Button Background="{StaticResource NonExistent}"/>` and the cache contains no entry for `NonExistent`
- **THEN** the indexer emits the `uses-resource` edge with an unresolved target and logs a debug-level note (does not error; the binding may be resolved by a runtime mechanism the indexer does not see)
