## MODIFIED Requirements

### Requirement: `.sourcegraph.json` scope shape
The documented `.sourcegraph.json` schema for a scope entry SHALL be extended with two optional fields: `language` (kebab-case string) and `enrichment` (object with one `lsp` sub-key carrying `command` + optional `args`). Both fields are absent by default; existing single-solution and multi-scope configs continue to load unchanged. The shipped schema documentation in the README SHALL include the new fields with one-line semantics.

#### Scenario: Pre-existing config loads unchanged
- **WHEN** a `.sourcegraph.json` file authored against the previous schema (no `language`, no `enrichment` on any scope) is loaded by the new host version
- **THEN** the loader produces the same `ScopeConfig` it produced before, with no additional warnings or errors

#### Scenario: New config with both fields populated
- **WHEN** a `.sourcegraph.json` declares
  ```jsonc
  { "scopes": [{ "name": "frontend", "paths": ["src/web/**/*.ts"],
                  "language": "typescript",
                  "enrichment": { "lsp": { "command": "typescript-language-server", "args": ["--stdio"] } } }] }
  ```
- **THEN** the loader succeeds, the `Scope` (or sister runtime record) exposes both values, and the README's documented shape matches what was loaded

### Requirement: CLI scaffolder writes the new fields when present
The `sourcegraph-mcp init-scopes` CLI subcommand SHALL not emit `language` or `enrichment` keys for the synthesised default config (those are operator-authored, not auto-discoverable). The `scopes add` subcommand SHALL accept optional `--language` and `--enrichment-lsp-command` flags and SHALL serialise them when supplied.

#### Scenario: `init-scopes` produces a minimal config
- **WHEN** the user runs `sourcegraph-mcp init-scopes` in a repo with a single .slnx and no `.sourcegraph.json`
- **THEN** the scaffolder writes a config containing only `name` + `solutions` for the default scope; no `language` or `enrichment` keys appear

#### Scenario: `scopes add` with optional language flag
- **WHEN** the user runs `sourcegraph-mcp scopes add frontend --paths "src/web/**/*.ts" --language typescript`
- **THEN** the resulting JSON entry has `language: "typescript"` serialised; `enrichment` is omitted entirely

#### Scenario: `scopes add` rejects an enrichment command without language context
- **WHEN** the user runs `sourcegraph-mcp scopes add frontend --paths "src/web/**/*.ts" --enrichment-lsp-command tsserver` (no `--language` flag)
- **THEN** the CLI MAY succeed (operators are trusted to know the consumer); a future change adding strict validation would reject. At v1 the field is informational, so the relaxed acceptance matches the loader's posture
