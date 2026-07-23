## ADDED Requirements

### Requirement: PayloadKeys SDK constants
The SDK SHALL expose a `PayloadKeys` static class containing kebab-case `string` constants for the well-known keys plugins use inside `EdgeEmitted.Metadata` dictionaries: `Path`, `Mode`, `Converter`, `ConverterParameter`, `Event`, `Handler`, `DataType`, `TargetType`, `Key`, `BasedOn`, `ElementName`, `RelativeSource`, `FallbackValue`, `StringFormat`, `UpdateSourceTrigger`. Each constant SHALL hold a kebab-case string (e.g. `PayloadKeys.ConverterParameter == "converter-parameter"`). Plugins are NOT required to use these constants — `EdgeEmitted.Metadata` accepts any string keys — but SHOULD prefer them for cross-plugin payload interop.

#### Scenario: XAML indexer populates a binding payload via constants
- **WHEN** a XAML indexer emits a `binds-path` edge with metadata `{ [PayloadKeys.Path] = "User.Name", [PayloadKeys.Mode] = "two-way", [PayloadKeys.Converter] = "BoolToVisibility" }`
- **THEN** the persisted `payload` JSON value has the keys `"path"`, `"mode"`, and `"converter"` (verbatim from the constant values), and an MCP tool that surfaces payload renders those keys without translation

#### Scenario: All PayloadKeys values are kebab-case
- **WHEN** the SDK is loaded
- **THEN** every `string` constant exposed by `PayloadKeys` matches the kebab-case format `[a-z][a-z0-9]*(-[a-z0-9]+)*` (asserted by a startup test in the SDK test suite)

### Requirement: CanonicalKeys helpers for C# canonical-key construction
The SDK SHALL expose a `CanonicalKeys` static class with helpers that return canonical-key strings for C# language elements, so cross-language plugins do not reimplement Roslyn's `DocumentationCommentId` format. The class SHALL expose at minimum:

- `string ForType(string fullyQualifiedName)` — returns `csharp:T:<doc-comment-id-suffix>`, handling open generics (`MyApp.Foo<T>` → `MyApp.Foo\`1`), nested types via `+`, and `global::` prefix stripping
- `string ForMethod(string typeFullyQualifiedName, string methodName, IReadOnlyList<string>? parameterTypeFullyQualifiedNames = null)` — returns `csharp:M:<type-key-suffix>.<method-name>(<params>)`, with `<params>` rendered per Roslyn doc-comment-id rules (empty parens when null)
- `string ForField(string typeFullyQualifiedName, string fieldName)` — returns `csharp:F:<type-key-suffix>.<field-name>`
- `string ForProperty(string typeFullyQualifiedName, string propertyName)` — returns `csharp:P:<type-key-suffix>.<property-name>`

The keys produced by these helpers SHALL be byte-for-byte equal to those emitted by the built-in `RoslynLanguageIndexer` for the same C# symbol, so cross-language joins reduce to string equality on `symbols.canonical_key`.

#### Scenario: Cross-language plugin points an edge at a C# class
- **WHEN** a XAML indexer emits an edge `EdgeEmitted(src: "xaml:view:Views/Main.xaml", dst: CanonicalKeys.ForType("MyApp.Views.Main"), kind: "code-behind")`
- **THEN** `dst` equals `"csharp:T:MyApp.Views.Main"`, the same string the Roslyn indexer wrote when it emitted `SymbolDeclared` for the partial class, and the host's edge resolver finds both endpoints via `symbols.canonical_key`

#### Scenario: Open generic type
- **WHEN** the helper is called as `CanonicalKeys.ForType("System.Collections.Generic.List<T>")`
- **THEN** the returned key is `"csharp:T:System.Collections.Generic.List\`1"`

#### Scenario: Nested type
- **WHEN** the helper is called as `CanonicalKeys.ForType("MyApp.Outer+Inner")`
- **THEN** the returned key is `"csharp:T:MyApp.Outer.Inner"` (Roslyn doc-comment-id uses `.` separator, not `+`)

#### Scenario: Method with parameter list
- **WHEN** the helper is called as `CanonicalKeys.ForMethod("MyApp.Calculator", "Add", new[] { "System.Int32", "System.Int32" })`
- **THEN** the returned key is `"csharp:M:MyApp.Calculator.Add(System.Int32,System.Int32)"`

#### Scenario: Method with no parameter list provided
- **WHEN** the helper is called as `CanonicalKeys.ForMethod("MyApp.Foo", "Bar", parameterTypeFullyQualifiedNames: null)`
- **THEN** the returned key is `"csharp:M:MyApp.Foo.Bar"` (no parentheses; downstream resolver matches every overload)
