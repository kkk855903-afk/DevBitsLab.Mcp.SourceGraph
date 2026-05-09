## MODIFIED Requirements

### Requirement: Caller and callee enumeration
The server SHALL expose `list_callers` and `list_callees` tools that walk `calls` edges by default, with an optional `kind` parameter that accepts a kebab-case edge kind name (`calls | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all`) or any future kind exposed by the active scope's plugins, to filter the edge kind walked. The XAML edge kinds (`code-behind`, `binds-path`, `binds-element`, `handles-event`, `uses-resource`, `instantiates-type`, `merges`, `applies-style`) are now part of the enumerable vocabulary on every scope that loads the XAML indexer.

#### Scenario: Find the codebehind of a XAML view
- **WHEN** the agent invokes `list_callees(symbol = "xaml:view:Views/MainWindow.xaml", kind = "code-behind")` against a scope that loaded the XAML indexer and indexed a WPF solution
- **THEN** the response lists the C# partial class symbol (`csharp:T:SampleWpf.Views.MainWindow`) as the resolved target

#### Scenario: List every binding to a viewmodel property (cross-language)
- **WHEN** the agent invokes `list_callers(symbol = "csharp:P:SampleWpf.ViewModels.MainViewModel.UserName", kind = "binds-path")`
- **THEN** the response lists every XAML element with a `binds-path` edge whose payload `path` resolves to `UserName` on the same target type, with each row's payload sub-line (per `harden-sdk-pre-xaml`) showing the `path`, `mode`, and `converter` values

### Requirement: find_by_annotation tool
The server SHALL expose a `find_by_annotation` tool that returns symbols matching an annotation name and optional flavor, argument substring, and symbol kind filter. The flavor enumeration accepted by the `flavor` parameter SHALL include `xaml-attached-property` (in addition to `csharp-attribute`) on every scope that loads the XAML indexer.

#### Scenario: Find every element with Grid.Row set
- **WHEN** the agent invokes `find_by_annotation(name = "Grid.Row", flavor = "xaml-attached-property")` against a scope that loaded the XAML indexer
- **THEN** the response lists every XAML element symbol carrying a `Grid.Row` attached property, with the value visible in the args column

#### Scenario: Cross-flavor query returns mixed results
- **WHEN** the agent invokes `find_by_annotation(name = "Background")` with no flavor specified, against a scope where the C# indexer emits `csharp-attribute` annotations and the XAML indexer emits `xaml-attached-property` annotations
- **THEN** any annotation with `name == "Background"` from either flavor appears in the response, each row tagged with its flavor

### Requirement: Symbol kind enumeration in tool parameters
The kind parameter on `list_symbols_in_file`, `find_definition`, and `module_summary` SHALL accept (in addition to the C# kinds documented by `open-language-contract`) the new XAML symbol kinds: `xaml-view`, `xaml-element`, `xaml-resource`, `xaml-style`, `xaml-template`. The expanded enumeration appears in the parameter doc on every scope that loads the XAML indexer.

#### Scenario: List every XAML view in a project
- **WHEN** the agent invokes `list_symbols_in_file(file = "Views/MainWindow.xaml")` against a scope that loaded the XAML indexer
- **THEN** the response includes the `xaml-view` symbol for the file root plus any `xaml-element`, `xaml-resource`, `xaml-style`, or `xaml-template` symbols declared inside

#### Scenario: Find definition of a XAML resource
- **WHEN** the agent invokes `find_definition(symbol = "xaml:resource:App.xaml#AccentBrush")`
- **THEN** the response includes the resource's declaration site (file path, line, column) plus every `uses-resource` edge that targets it (via the existing reference-listing behaviour)
