## 1. Project setup

- [ ] 1.1 Create `src/DevBitsLab.Mcp.SourceGraph.Indexing.Xaml/` csproj targeting `net10.0`, referencing the SDK and Storage projects
- [ ] 1.2 Add to `DevBitsLab.Mcp.SourceGraph.slnx`
- [ ] 1.3 Register in the in-tree plugin discovery list (whatever startup path `PluginHost` uses for built-in indexers)

## 2. XAML parser core

- [ ] 2.1 Create `XamlReader` wrapping `System.Xml.XmlReader` with position tracking (line/col captured on every element start, attribute, and text node)
- [ ] 2.2 Implement element-tree visitor with parent-stack so the indexer can compute element ancestry (needed for resource scope, attached properties)
- [ ] 2.3 Implement attribute walker that distinguishes:
  - x:Class / x:Name / x:Key / Name (well-known XAML namespace)
  - clr-namespace mapping declarations on the root
  - attached properties (presence of `.` in attribute local name and a containing-namespace match)
  - markup-extension values (attribute value starts with `{` and ends with `}`)
  - regular attribute values (any other string)
- [ ] 2.4 Add unit tests against synthetic XAML strings covering each branch

## 3. Markup-extension tokenizer

- [ ] 3.1 Add `MarkupExtensionParser` that takes an attribute value like `{Binding Path=User.Name, Mode=TwoWay, Converter={StaticResource b2v}, ConverterParameter=Inverse}` and returns a structured representation: extension name, positional args, named args (a dictionary)
- [ ] 3.2 Handle nested extensions (e.g. `Converter={StaticResource b2v}` inside a `{Binding}`)
- [ ] 3.3 Handle escaped braces (`\{`, `\}`) and quoted strings inside the extension
- [ ] 3.4 Tokenizer tests covering: `{Binding Path=Foo}`, `{Binding Foo}` (positional path), `{Binding Path=Foo, Mode=TwoWay}`, `{Binding ElementName=Other, Path=Value}`, `{StaticResource MyKey}`, nested extensions, escapes

## 4. Framework profile dialects

- [ ] 4.1 Add `IXamlDialect` interface: `string Name { get; }`, `string XClassNamespace { get; }`, `MarkupExtensionDialect MarkupExtensionDialect { get; }`, `string ClrNamespacePrefix { get; }`
- [ ] 4.2 Implement `WpfDialect`, `WinUiDialect`, `UwpDialect`, `AvaloniaDialect`, `UnoDialect`
- [ ] 4.3 Implement `XamlProfileDetector.Detect(IReadOnlyDictionary<string, string> rootNamespaceMappings)` returning the matching dialect
- [ ] 4.4 Detector tests covering one fixture root per profile
- [ ] 4.5 Document the detection rules in XML doc comments on each dialect class

## 5. XamlLanguageProject + factory

- [ ] 5.1 Implement `XamlLanguageProject : ILanguageProject` (plugin-private subclass) with `Id` = absolute project file path, `FilePaths` = enumerated `.xaml` files in the project, plus a private `Dictionary<string, ResourceDefinition> ResourceCache`
- [ ] 5.2 Implement `XamlLanguageProjectFactory : ILanguageProjectFactory` with `ProjectMarkers = ["*.csproj", "*.xaml"]`
- [ ] 5.3 In `DiscoverAsync`, walk every `.csproj` under `repoRoot`, parse minimally to extract `Page` / `ApplicationDefinition` items, return one `XamlLanguageProject` per project that contains XAML files
- [ ] 5.4 At project construction time, walk `App.xaml`'s `Application.Resources` plus any `MergedDictionaries`, plus theme `Generic.xaml` if present in `Themes/`, populate `ResourceCache` with `(key → resource declaration site)`
- [ ] 5.5 Tests against the SampleWpf fixture confirming `XamlLanguageProject.ResourceCache` is populated correctly

## 6. XamlLanguageIndexer plugin

- [ ] 6.1 Implement `XamlLanguageIndexer : ILanguageIndexer` with `SupportedExtensions = [".xaml"]`
- [ ] 6.2 In `IndexAsync(IndexContext ctx)`:
  - cast `ctx.Project` to `XamlLanguageProject` (skip if null with warning)
  - parse the file via `XamlReader`
  - detect framework profile from root element via `XamlProfileDetector`
  - emit `SymbolDeclared(xaml-view)` for the root element if it has `x:Class`
  - emit `EdgeEmitted(xaml-view, csharp:T:..., "code-behind")` using `CanonicalKeys.ForType` from `harden-sdk-pre-xaml`
  - walk descendants, emit `SymbolDeclared(xaml-element)` for any element with `x:Name`/`Name`
  - emit `EdgeEmitted` for binds-path / handles-event / uses-resource / instantiates-type / merges / applies-style as encountered
  - emit `AnnotationAttached(flavor: "xaml-attached-property")` for attached properties
- [ ] 6.3 Resolve `Click="OnSave"` to `csharp:M:<class>.OnSave` via `CanonicalKeys.ForMethod` and the XAML view's `x:Class` value
- [ ] 6.4 Resolve `{StaticResource AccentBrush}` against `XamlLanguageProject.ResourceCache`; emit `uses-resource` edge if found, otherwise emit with unresolved target (still record the use)
- [ ] 6.5 Handle `ElementName` binding via the two-edge pattern documented in design decision 5: `binds-path` (path payload, no resolved target) + `binds-element` (resolved to the named element)
- [ ] 6.6 Populate `EdgeEmitted.Metadata` for `binds-path` using `PayloadKeys` constants from `harden-sdk-pre-xaml`

## 7. Carryover: dispatcher plumbing (5.3 / 6.1 / 6.2 from open-language-contract)

- [ ] 7.1 Update the indexer dispatcher so every dispatched document is routed with a populated `IndexContext.Project` (looking up the project that owns the file path)
- [ ] 7.2 Update `PluginHost` to discover `ILanguageProjectFactory` instances from registered plugins alongside the existing `ILanguageIndexer` discovery; run `DiscoverAsync` per scope at startup
- [ ] 7.3 Build a per-scope `Dictionary<string, ILanguageProject>` keyed by file path so the dispatcher can populate `IndexContext.Project` cheaply
- [ ] 7.4 Confirm the C# pathway (which doesn't use `IndexContext.Project` heavily) still works after the dispatcher change — should be transparent
- [ ] 7.5 Mark deferred tasks 5.3 / 6.1 / 6.2 in `2026-05-08-open-language-contract/tasks.md` as complete (carryover note)

## 8. Cross-language join smoke tests

- [ ] 8.1 Create `tests/fixtures/SampleWpf/` solution: one `.csproj`, three views (one with `x:Class`, one purely-XAML resource dictionary, one with a binding to a viewmodel), the codebehind partial classes, a viewmodel
- [ ] 8.2 In `IndexFixtureTests` (or new `XamlIndexFixtureTests`), index the fixture; assert:
  - `xaml-view` symbol exists for `Views/MainWindow.xaml`
  - a `code-behind` edge resolves to `csharp:T:SampleWpf.Views.MainWindow`
  - a `handles-event` edge for `Click="OnSave"` resolves to `csharp:M:SampleWpf.Views.MainWindow.OnSave`
  - a `binds-path` edge from the bound element carries `payload.path = "User.Name"` and `payload.mode = "two-way"`
  - the cross-language `find_references` on `csharp:T:SampleWpf.Views.MainWindow` returns the XAML view symbol
- [ ] 8.3 Add a "find me the codebehind for this view" test: `find_definition` on `xaml:view:Views/MainWindow.xaml` then `list_callees --kind code-behind` returns the codebehind type

## 9. Avalonia profile fixture

- [ ] 9.1 Create `tests/fixtures/SampleAvalonia/` solution covering Avalonia's `clr-namespace:` mapping and `ControlTheme` resource pattern
- [ ] 9.2 Index the fixture; assert profile detection picked `Avalonia`, the `IXamlDialect` strategy resolved `{Binding}` correctly, attached properties (`Grid.Row`) emitted under flavor `xaml-attached-property`
- [ ] 9.3 Cross-language join smoke test (same as 8.2 but for the Avalonia codebehind type)

## 10. Tool surface (parameter docs)

- [ ] 10.1 Update `find_definition`, `list_callers`, `list_callees`, `find_by_annotation`, `list_symbols_in_file`, `neighborhood`, `module_summary` parameter doc enumerations to include the new XAML kinds (`xaml-view`, `xaml-element`, `xaml-resource`, `xaml-style`, `xaml-template` for symbols; `code-behind`, `binds-path`, `binds-element`, `handles-event`, `uses-resource`, `instantiates-type`, `merges`, `applies-style` for edges; `xaml-attached-property` for annotation flavors)
- [ ] 10.2 No runtime changes required — soft registry already accepts the new kinds via the open-string contract

## 11. Validation and finishing

- [ ] 11.1 Run `openspec validate xaml-language-indexer --strict` and resolve any reported issues
- [ ] 11.2 Run `dotnet build` from repo root; resolve every compile error
- [ ] 11.3 Run `dotnet test` and resolve every test that broke
- [ ] 11.4 README: add a section showing a cross-language `find_references` example (XAML view → codebehind class)
- [ ] 11.5 Update CHANGELOG.md (or equivalent) with the new kind vocabulary

## 12. Open-question resolution (defer, but track)

- [ ] 12.1 Track the cross-project Generic.xaml cascade open question; capture the chosen approach in a follow-up proposal if it materialises before this change merges
- [ ] 12.2 Track the `xaml-template` split open question; revisit after first usage data
- [ ] 12.3 Track the position-info granularity open question (attribute vs. element); consistent choice across all event-bound edge kinds
