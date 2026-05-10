## ADDED Requirements

### Requirement: TypeScript / JavaScript file dispatch
The indexer SHALL register `TypeScriptLanguageIndexer` for the file extensions `.ts`, `.tsx`, `.js`, and `.jsx`. Each extension dispatches to the appropriate tree-sitter grammar (TypeScript / TSX / JavaScript). The indexer SHALL emit `IndexEvent`s for declarations, references, JSX usages, and the standard `FileScanned` sentinel.

#### Scenario: Plain TypeScript file produces declarations
- **WHEN** a `src/foo.ts` file declares `export function greet(name: string): string`
- **THEN** the indexer emits a `SymbolDeclared` for `greet` with `Kind = "method"` and canonical key `ts:M:src/foo.ts::greet`

#### Scenario: TSX file produces JSX-instantiation edges for PascalCase components
- **WHEN** a `src/page.tsx` file contains `<Button onClick={handler} disabled />`
- **THEN** the indexer emits an `EdgeEmitted` with `EdgeKindName = "instantiates"` whose target canonical key contains `Button`, and whose `Metadata` carries a `props` entry listing the prop names (`onClick`, `disabled`)

#### Scenario: HTML-cased JSX tag does not produce an edge
- **WHEN** the same file contains `<div className="foo" />`
- **THEN** the indexer SHALL NOT emit an `EdgeEmitted` whose target contains `div`; lower-cased JSX tags are filtered out as not referencing any user symbol

#### Scenario: JavaScript file uses the JavaScript grammar
- **WHEN** a `src/foo.js` file contains `function greet(name) { return name; }`
- **THEN** the indexer emits a `SymbolDeclared` whose canonical key starts with `js:M:` (matching the file extension's scheme)

#### Scenario: const distinguishes from let/var
- **WHEN** a file contains both `const API_BASE = "..."` and `let counter = 0;`
- **THEN** the indexer emits two `SymbolDeclared` events with `Kind = "constant"` and `Kind = "variable"` respectively

#### Scenario: Call expression produces a reference event
- **WHEN** a `src/foo.ts` file contains a call `greet("hello")`
- **THEN** the indexer emits a `ReferenceFound` whose `Kind = "call"` and whose target canonical key references `greet`

### Requirement: Default excludes for TypeScript / JavaScript scopes
The TypeScript indexer's `LanguageIndexerOptions.DefaultExcludes` SHALL include `**/node_modules/**`, `**/dist/**`, `**/.next/**`, `**/build/**`, `**/coverage/**`, `**/.cache/**`, `**/.parcel-cache/**`, `**/out/**`. The host applies these as floors — operator-supplied `exclude` patterns add to the list, never override it.

#### Scenario: Default excludes are accessible at runtime
- **WHEN** a caller reads `TypeScriptGrammarConfig.StandardExcludes`
- **THEN** the eight documented patterns are present, in the documented order

### Requirement: FileScanned sentinel emitted exactly once per indexed file
The indexer SHALL emit exactly one `IndexEvent.FileScanned` per `IndexAsync` call, regardless of whether the parse produced any other events. The sentinel carries the SHA-256 of the source bytes.

#### Scenario: Empty source still produces FileScanned
- **WHEN** the indexer is invoked on a zero-byte file
- **THEN** the resulting event list contains exactly one `FileScanned` and no other events

#### Scenario: Files above the size cap are skipped entirely
- **WHEN** a file's content exceeds `LanguageIndexerOptions.MaxFileSizeBytes` (default 10 MB)
- **THEN** the indexer returns an empty event list — no `FileScanned`, no symbols
