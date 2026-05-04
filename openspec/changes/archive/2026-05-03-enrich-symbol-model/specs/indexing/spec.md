## ADDED Requirements

### Requirement: Symbol modifiers and accessibility recorded
The indexer SHALL capture every Roslyn modifier (`static`, `async`, `virtual`, `abstract`, `sealed`, `override`, `extern`, `readonly`, `partial`) and the `DeclaredAccessibility` of every indexed symbol, and SHALL persist both via `UpsertSymbolAsync`.

#### Scenario: Public async method
- **WHEN** an indexed C# file contains `public async Task DoAsync()`
- **THEN** the symbol's `accessibility` column is `Public` and `modifiers` is `"async"`

#### Scenario: Private readonly field
- **WHEN** an indexed C# file contains `private readonly string _x;`
- **THEN** `accessibility = Private` and `modifiers = "readonly"`

### Requirement: XML doc summary captured
The indexer SHALL parse the `<summary>` of each symbol's XML documentation comment (resolving `<inheritdoc/>` up the override chain when present) and SHALL store the parsed plain text on the symbol row.

#### Scenario: Documented method
- **WHEN** a method has `/// <summary>Publishes the feed.</summary>`
- **THEN** its `xml_summary` column equals `"Publishes the feed."`

#### Scenario: Inherited summary
- **WHEN** an override has `/// <inheritdoc/>` and its base method has a non-empty summary
- **THEN** the override's `xml_summary` equals the base's parsed summary

#### Scenario: No summary available
- **WHEN** a symbol has no XML doc, no inheritdoc, or inheritdoc points at an external assembly without XML docs
- **THEN** `xml_summary` is `NULL` (not the empty string)

### Requirement: Container hierarchy populated
The indexer SHALL set `symbols.container_id` to the row id of each symbol's containing symbol (`ContainingSymbol`) using a two-phase pass.

#### Scenario: Method inside a class
- **WHEN** `class Foo { void Bar() {} }` is indexed
- **THEN** `Bar.container_id` equals `Foo.id`

#### Scenario: Top-level type
- **WHEN** a class has no containing type (its container is a namespace)
- **THEN** the class row's `container_id` is the namespace's row id

#### Scenario: Symbol whose parent isn't indexed
- **WHEN** a symbol's containing symbol is filtered out by `IsIndexable` (e.g., a global namespace)
- **THEN** `container_id` is `NULL`
