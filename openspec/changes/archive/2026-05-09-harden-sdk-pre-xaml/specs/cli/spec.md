## ADDED Requirements

### Requirement: vocabulary subcommand
The CLI SHALL accept a `vocabulary` top-level subcommand that exposes diagnostic information about the soft-registry kind vocabulary. At v1 the only nested verb is `list`, which is also the default if the user invokes `vocabulary` with no nested verb. Future revisions may add `add` / `validate` / `import` if a strict registry is introduced.

#### Scenario: Run vocabulary list with default options
- **WHEN** `sourcegraph-mcp vocabulary list` is invoked from a repo root with at least one configured scope
- **THEN** the command exits `0`, prints one section per scope to stdout, and each section enumerates the scope's `edge_kinds`, `symbol_kinds`, and `annotation_flavors` arrays as observed in storage

#### Scenario: Vocabulary subcommand defaults to list
- **WHEN** `sourcegraph-mcp vocabulary` is invoked with no nested verb
- **THEN** the command behaves identically to `sourcegraph-mcp vocabulary list` (the only verb at v1 is the default)

#### Scenario: Unknown nested verb errors out
- **WHEN** `sourcegraph-mcp vocabulary register` (or any string other than `list`) is invoked
- **THEN** the command prints an unknown-subcommand error to stderr and exits `2`

### Requirement: vocabulary list output format
The `vocabulary list` subcommand SHALL print, for each scope known to the active `IScopeRegistry`, a header naming the scope id, then three labelled lists (`edge_kinds`, `symbol_kinds`, `annotation_flavors`). Each entry in each list SHALL be tagged with its source (`sdk` if the value matches a constant exposed by `EdgeKinds` / `SymbolKinds`; `plugin: <id>@<version>` if it matches a registered plugin's declared kinds; otherwise `unknown`) and a live emission count obtained by counting matching rows in the scope's storage (`COUNT(*) FROM edges WHERE kind_name = ?`, etc.).

#### Scenario: Single-language scope output
- **WHEN** `vocabulary list` runs against a scope whose only loaded indexer is the built-in C# Roslyn indexer with a freshly-indexed `Sample.sln`
- **THEN** every `edge_kinds` entry is tagged `[sdk, emitted: <N>]` and every `symbol_kinds` entry is tagged the same way; `annotation_flavors` is `csharp-attribute  [sdk, emitted: <N>]`

#### Scenario: Polyglot scope shows mixed sources
- **WHEN** `vocabulary list` runs against a scope that loads the built-in C# indexer and a hypothetical XAML indexer plugin with id `xaml-indexer@1.0.0`
- **THEN** SDK constants are tagged `[sdk]`, XAML-emitted kinds are tagged `[plugin: xaml-indexer@1.0.0]`, and live emission counts reflect the actual storage state for each

#### Scenario: Empty scope output
- **WHEN** `vocabulary list` runs against a scope whose storage is missing (cold scope, never indexed — the per-scope DB file does not exist on disk)
- **THEN** the section header for that scope is followed by a single `(no database at <path> — never indexed)` note, the `edge_kinds` / `symbol_kinds` / `annotation_flavors` lists are empty (no SDK fabricated `emitted: 0` rows), no error is produced, and the command continues to the next scope

### Requirement: vocabulary list drift detection
After printing the per-scope kind lists, the `vocabulary list` subcommand SHALL print a "Drift candidates" section that compares pairs of kinds within each scope using Levenshtein distance with threshold ≤2. Pairs that meet the threshold SHALL be listed in the form `<kind-a> ~ <kind-b>` so a maintainer can spot likely typos (`bind-path` vs `binds-path`).

#### Scenario: No drift detected
- **WHEN** every kind in every scope is at Levenshtein distance >2 from every other kind
- **THEN** the "Drift candidates" section header is followed by `(none)` and the command exits `0`

#### Scenario: Drift detected
- **WHEN** a scope's `edge_kinds` includes both `binds-path` and `bind-path` (Levenshtein distance 1)
- **THEN** the "Drift candidates" section lists `bind-path ~ binds-path` and the command still exits `0` by default

### Requirement: vocabulary list strict mode
The `vocabulary list` subcommand SHALL accept an optional `--strict` flag. When set, the command SHALL exit with code `2` if any drift candidate was reported (the full output is still printed first).

#### Scenario: Strict mode with no drift
- **WHEN** `vocabulary list --strict` is invoked against a scope with no drift candidates
- **THEN** the command exits `0`

#### Scenario: Strict mode with drift
- **WHEN** `vocabulary list --strict` is invoked against a scope where `bind-path` and `binds-path` both occur
- **THEN** the command prints the full output (including the drift candidate) and exits `2`

### Requirement: vocabulary list scope filter
The `vocabulary list` subcommand SHALL accept an optional `--scope <id>` flag that restricts the output to a single scope id; the default is to print every scope known to the registry.

#### Scenario: Filter to one scope
- **WHEN** `vocabulary list --scope backend` is invoked in a repo whose `.sourcegraph.json` declares scopes `backend`, `frontend`, and `vendor`
- **THEN** only the `backend` section is printed; `frontend` and `vendor` are not visited
