## MODIFIED Requirements

### Requirement: Self-applying schema migrations
`SqliteGraphStore` SHALL apply the bundled schema on connect; if the on-disk
version is below `Schema.Version`, all data tables and triggers SHALL be
dropped and recreated from the embedded SQL. The new `Schema.Version`
introduced by this change is `6` (bumped from `5`).

#### Scenario: Open a DB on an older schema
- **WHEN** the `schema_version` table reports a value less than
  `Schema.Version` (currently `6`)
- **THEN** `EnsureSchemaAsync` runs `Schema.DropAll`, applies `Schema.V1` and
  `Schema.V2` from scratch, inserts the new version row, and logs
  `"On-disk graph schema is vOLD; rebuilding to vNEW"`

#### Scenario: Open a v5 DB after the contract reform
- **WHEN** a server built against `Schema.Version = 6` opens a `.sourcegraph/scopes/<id>.db` whose `schema_version` row reports `5` (written by the previous server)
- **THEN** `EnsureSchemaAsync` drops every data table and recreates them from `Schema.V1` + `Schema.V2`; the watcher's next index pass populates them from source

## ADDED Requirements

### Requirement: Edge and symbol kinds stored as TEXT
The `edges.kind_name` column and the `symbols.kind_name` column SHALL be `TEXT NOT NULL` containing the kebab-case kind identifier (e.g. `"calls"`, `"renders-component"`, `"class"`, `"xaml-element"`). The legacy integer `kind` columns from prior schema versions SHALL NOT exist in `Schema.Version = 6`.

Indexes SHALL exist on `edges(kind_name)` and `symbols(kind_name)` to keep filter queries plan-efficient.

#### Scenario: Storing an edge with a string kind
- **WHEN** `BulkInsertEdgesAsync` is called with an edge whose kind is `"binds-path"`
- **THEN** the resulting row's `kind_name` column equals the literal text `"binds-path"`

#### Scenario: Querying edges by kind
- **WHEN** the host runs `SELECT ... FROM edges WHERE kind_name = 'calls'`
- **THEN** the SQL plan uses the `idx_edges_kind_name` index and returns matching rows

### Requirement: Edge payload column carries metadata
The `edges` table SHALL include a `payload TEXT NULL` column that stores the JSON serialization of an `EdgeEmitted.Metadata` dictionary when present, and `NULL` otherwise.

#### Scenario: Edge written without metadata
- **WHEN** an edge is emitted with `Metadata = null`
- **THEN** the resulting `edges` row has `payload IS NULL`

#### Scenario: Edge written with metadata
- **WHEN** an edge is emitted with `Metadata = { ["path"] = "User.Name" }`
- **THEN** the resulting `edges` row has `payload = '{"path":"User.Name"}'` (JSON object form), and `json_extract(payload, '$.path')` returns `'User.Name'`

### Requirement: Annotations table replaces attributes
The schema SHALL include an `annotations(id, symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)` table indexed on `(symbol_id)`, `(name)`, `(flavor)`, and `(attribute_symbol_id)`, plus an `annotations_fts` virtual table tokenising the synthesised `args_text` column. The legacy `attributes` table from prior schema versions SHALL NOT exist in `Schema.Version = 6`.

`flavor` SHALL be `TEXT NOT NULL`.

#### Scenario: Storing a C# attribute as an annotation
- **WHEN** the C# indexer emits `AnnotationAttached(name: "HttpGet", flavor: "csharp-attribute", ...)` and the host persists it
- **THEN** the resulting row in `annotations` has `flavor = 'csharp-attribute'`, `name = 'HttpGet'`, and the FTS table contains the args text for trigram matching

#### Scenario: Filter annotations by flavor
- **WHEN** the host runs `SELECT ... FROM annotations WHERE flavor = 'csharp-attribute' AND name = 'Authorize'`
- **THEN** the SQL plan uses the `idx_annotations_flavor` and `idx_annotations_name` indexes and returns matching rows

### Requirement: find_by_annotation query API
`IGraphStore` SHALL expose `FindByAnnotationAsync(name, flavor?, argSubstring?, kindFilter?, limit)` returning `SymbolHit` rows whose attached annotations match the criteria. When `flavor` is `null`, the query matches across all flavors. The legacy `FindByAttributeAsync` method SHALL NOT exist on `IGraphStore` after this change.

#### Scenario: Strict name match plus wildcard arg match
- **WHEN** `FindByAnnotationAsync("HttpGet", flavor: "csharp-attribute", argSubstring: "users", kindFilter: null, limit: 50)` is called against a graph with `[HttpGet("/api/users")]` on method `M`
- **THEN** `M` is in the result set

#### Scenario: Cross-flavor name match
- **WHEN** `FindByAnnotationAsync("Component", flavor: null, argSubstring: null, kindFilter: null, limit: 50)` is called against a future polyglot graph that has both a C# `[Component]` attribute and a TS `@Component` decorator with the same `name = "Component"`
- **THEN** both rows are returned, each carrying its `flavor` so the caller can distinguish them
