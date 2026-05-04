## ADDED Requirements

### Requirement: .sourcegraph.json config file
The system SHALL recognise `.sourcegraph.json` at the repo root as the source of truth for scope configuration; absent file = single synthesised `default` scope.

#### Scenario: Documented schema
- **WHEN** a developer reads `CLAUDE.md` or `README.md`
- **THEN** the `.sourcegraph.json` schema is documented with the three scope-definition kinds (`solutions[]`, `projects[]`, `paths[]`), the `exclude[]` glob list, the `isolated` flag, and the `default_scope` field

#### Scenario: Schema validation
- **WHEN** the loader reads a malformed `.sourcegraph.json` (missing required fields, unknown keys, conflicting scope ids)
- **THEN** it fails fast on startup with a precise message naming the offending key, and the server exits with code `2`
