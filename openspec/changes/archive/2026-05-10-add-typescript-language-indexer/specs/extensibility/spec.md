## MODIFIED Requirements

### Requirement: Canonical-key URI convention
Every canonical key emitted by an `ILanguageIndexer` SHALL match the format `<scheme>:<rest>`, where `<scheme>` is one of the reserved-and-enforced schemes at this SDK version (`csharp`, `xaml`, `js`, `ts`, `jsx`, `tsx`). Schemes `vbnet`, `fsharp`, `razor`, `vue`, `svelte`, `python`, `go`, `rust` are documented as reserved-for-future-use but are NOT yet accepted by the host; emissions using those schemes SHALL be rejected.

`<rest>` SHALL be plugin-defined, but any path component embedded in `<rest>` SHALL be repo-relative (resolved against `Scope.Root`) and SHALL use forward slashes regardless of operating system.

#### Scenario: Built-in C# indexer emits a Roslyn-shaped key
- **WHEN** the Roslyn indexer emits a `SymbolDeclared` for `Sample.Domain.Calculator`
- **THEN** the `CanonicalKey` value is `"csharp:T:Sample.Domain.Calculator"` (Roslyn `DocumentationCommentId` prefixed with `csharp:`)

#### Scenario: XAML-style key passes validation
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "xaml:element:Views/Main.xaml#ConfirmBtn", ...)`
- **THEN** the host accepts the key (its scheme `xaml` is reserved-and-enforced)

#### Scenario: TypeScript scheme accepted
- **WHEN** the TypeScript indexer emits `SymbolDeclared(CanonicalKey: "ts:M:src/web/foo.ts::greet", ...)`
- **THEN** the host accepts the key; the scheme `ts` is reserved-and-enforced starting at this SDK version

#### Scenario: TSX scheme accepted
- **WHEN** the TypeScript indexer emits an instantiates edge with source key `"tsx:M:src/web/page.tsx::Page"` and target `"tsx:M:src/web/Button.tsx::Button"`
- **THEN** the host accepts both keys; the scheme `tsx` is reserved-and-enforced

#### Scenario: JavaScript / JSX schemes accepted
- **WHEN** the TypeScript indexer emits a key of the form `"js:M:src/foo.js::greet"` or `"jsx:M:src/page.jsx::Page"`
- **THEN** the host accepts both; schemes `js` and `jsx` are reserved-and-enforced

#### Scenario: Reserved-future scheme rejected
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "python:M:foo.bar", ...)` or `"vue:component:Header.vue"`
- **THEN** the host throws `ArgumentException` at emission time naming the unknown scheme, before the row reaches storage. Schemes `vbnet`, `fsharp`, `razor`, `vue`, `svelte`, `python`, `go`, `rust` remain reserved-for-future-use until their respective indexers ship and lift them.

#### Scenario: Backslash path in TS key rejected
- **WHEN** a plugin emits `SymbolDeclared(CanonicalKey: "ts:M:src\\foo.ts::greet", ...)`
- **THEN** the host throws `ArgumentException` identifying the backslash as an invalid separator

### Requirement: TypeScript canonical-key kind-prefix convention
TypeScript canonical keys SHALL follow the form `<scheme>:<kind-prefix>:<repo-relative-path>::<lexical-path>` where `<kind-prefix>` is one of the documented single-letter kind prefixes:

| Prefix | Kind family |
|--------|-------------|
| `T` | type, class, interface, enum, type-alias |
| `M` | function, method |
| `V` | const, let, var |
| `P` | property, field, enum-member |
| `N` | namespace, module |

The lexical path uses `::` for container nesting and `#` for type-property separation. The helper class `TypeScriptCanonicalKeys` builds keys in this shape; subclassed indexers SHOULD use the helper rather than constructing keys by hand so the format stays consistent.

#### Scenario: Function key shape
- **WHEN** the indexer emits `SymbolDeclared` for an exported function `greet` in `src/web/foo.ts`
- **THEN** `CanonicalKey == "ts:M:src/web/foo.ts::greet"`

#### Scenario: TSX file uses tsx scheme
- **WHEN** the indexer emits `SymbolDeclared` for a function `Page` in `src/web/page.tsx`
- **THEN** the canonical key starts with `tsx:M:` (matching the file's extension)

#### Scenario: JavaScript file uses js scheme
- **WHEN** the indexer emits `SymbolDeclared` for a function `greet` in `src/web/foo.js`
- **THEN** the canonical key starts with `js:M:`
