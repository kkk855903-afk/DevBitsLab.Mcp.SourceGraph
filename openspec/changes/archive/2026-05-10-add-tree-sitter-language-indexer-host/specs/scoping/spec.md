## ADDED Requirements

### Requirement: Scope `language` field
A scope entry in `.sourcegraph.json` MAY carry an optional `language` field whose value is a kebab-case string identifying the scope's primary language (e.g. `"typescript"`, `"python"`, `"go"`). The loader SHALL accept any kebab-case value and SHALL NOT enforce a closed list at this version. When present, `scopes info` and `scopes list` surface the value; when absent, both render `(unset)`.

#### Scenario: Loader accepts a kebab-case language
- **WHEN** a scope declares `"language": "typescript"`
- **THEN** `ScopeConfigLoader.Load` succeeds, the resulting `Scope` (or sister runtime config) carries the value, and `scopes info` renders `Language: typescript`

#### Scenario: Loader rejects a non-kebab-case language
- **WHEN** a scope declares `"language": "TypeScript"` or `"language": "type_script"` or `"language": ""`
- **THEN** the loader SHALL throw `ScopeConfigException` identifying the offending value and the scope name

#### Scenario: Loader accepts an unknown-but-kebab-case language
- **WHEN** a scope declares `"language": "phyton"` (a typo)
- **THEN** the loader SHALL succeed; the value is surfaced verbatim. Mis-routing is a soft-registry concern surfaced via diagnostics, not a load-time failure.

### Requirement: Scope `enrichment` field (forward-declared)
A scope entry MAY carry an optional `enrichment` object with a single nested `lsp` field. The `lsp` field SHALL declare a `command` (non-empty string) and an optional `args` (string array, defaulting to `[]`). The loader SHALL parse and validate the shape; the host SHALL surface the configuration via `scopes info` but SHALL NOT consume it at this version.

#### Scenario: Loader round-trips the enrichment block
- **WHEN** a scope declares `"enrichment": { "lsp": { "command": "typescript-language-server", "args": ["--stdio"] } }`
- **THEN** `Save(Load(...))` reproduces the same JSON, the `Scope` exposes the typed config, and `scopes info` renders the `Enrichment` section with `(no consumer at this version)` annotation

#### Scenario: Loader rejects an empty `command`
- **WHEN** a scope declares `"enrichment": { "lsp": { "command": "" } }` or omits `command` entirely
- **THEN** the loader SHALL throw `ScopeConfigException` identifying the offending scope and the missing/empty `command` field

#### Scenario: Loader rejects unknown enrichment keys at v1
- **WHEN** a scope declares `"enrichment": { "lsp": {...}, "embeddings": {...} }`
- **THEN** the loader SHALL throw `ScopeConfigException` reporting `embeddings` as an unknown enrichment key. Future enrichment kinds (embeddings, static analysis) are reserved-but-rejected at this SDK version, mirroring the canonical-key scheme posture; later changes may lift them.

#### Scenario: Inert enrichment annotated in `scopes info`
- **WHEN** a user sets `enrichment.lsp` and runs `scopes info <name>`
- **THEN** the output SHALL show the configured command and args, plus an explanatory annotation that no plugin claims this enrichment at the current version, so the operator does not assume the LSP is being launched
