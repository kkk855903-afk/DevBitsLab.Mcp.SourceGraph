## ADDED Requirements

### Requirement: Capture every attribute on indexed symbols
The indexer SHALL record every attribute (`ISymbol.GetAttributes()`) attached to an indexed symbol, including its short name, fully qualified name, constructor arguments, and named arguments.

#### Scenario: Method with a route attribute
- **WHEN** an indexed method is decorated `[HttpGet("/api/users")]`
- **THEN** an `attributes` row is written with `name = "HttpGet"`, `full_name = "Microsoft.AspNetCore.Mvc.HttpGetAttribute"`, `args_json` whose `ctor[0]` is the literal string `"/api/users"`, and `attribute_symbol_id` linking back to the user-defined attribute symbol if it's in the graph (else `NULL`)

#### Scenario: Multiple attributes
- **WHEN** a symbol has `[Authorize, Obsolete("Use Foo")]`
- **THEN** two `attributes` rows are written, in source order

### Requirement: Attribute reconciliation on file reindex
When a file is reindexed, the indexer SHALL delete every `attributes` row attached to that file's symbols before reinserting the new attribute set, in the same transaction as the symbol-set reconciliation.

#### Scenario: Attribute removed from source
- **WHEN** a file is edited to remove `[Obsolete]` from a method
- **THEN** after the live reindex, no `attributes` row remains for that method with `name = "Obsolete"`
