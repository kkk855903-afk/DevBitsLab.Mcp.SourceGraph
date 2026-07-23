## Context

`ISymbol.GetAttributes()` returns `ImmutableArray<AttributeData>`. Each `AttributeData` has `AttributeClass`, `ConstructorArguments` (`ImmutableArray<TypedConstant>`), and `NamedArguments` (`ImmutableArray<KeyValuePair<string, TypedConstant>>`). All of this is type-aware (we know `"/api"` is a string and `Roles = Admin | User` is an enum bitmask).

The design question is how to **encode arguments** so they're searchable enough for the common cases (route paths, type names in `[ServiceFilter(typeof(X))]`) without requiring the agent to query a JSON tree.

## Goals / Non-Goals

**Goals:**
- One round-trip per attribute, no second pass.
- Common queries answerable via SQL: "all `[HttpGet]`", "all `[HttpPost]` whose first arg starts with `/api/v2/`", "all `[Obsolete]` with non-empty message".
- Attribute *types* (the `AttributeClass`) reach back to the symbols table when the attribute itself is user-defined and indexed.

**Non-Goals:**
- Indexing every transitive attribute (the attribute's own attributes). Stops at depth 1.
- Indexing assembly-level and module-level attributes in v1. (Easy follow-up.)
- Resolving named-argument expressions like `Roles = nameof(SomeRole)` to their resolved values — we store the literal as Roslyn returned it.

## Decisions

**1. Args stored as JSON, with a stable schema.**
Each `attributes` row has `args_json` like:
```json
{
  "ctor": ["/api/users", 200],
  "named": { "Name": "users-list", "Order": 0 }
}
```
Keeps the storage flexible (no per-attribute table proliferation) while enabling structured queries via SQLite's `json_extract`. The shape is stable and documented.

**2. FTS5 over `args_json`.**
Trigram tokenisation already in use; a query like `find_by_attribute(name = "HttpGet", arg_value = "/api/v2")` becomes `WHERE name = ? AND args_fts MATCH ?`. Combines structural and textual filtering with no extra index.

**3. Attribute class linkage to `symbols` when present.**
When the attribute class is itself in our graph (a user-defined attribute), `attributes.attribute_symbol_id` carries the canonical-key id; otherwise `NULL`. Lets `find_references(symbol = "MyAuthAttribute")` return the symbols carrying that attribute.

**4. `find_by_attribute` accepts wildcard args via FTS.**
`find_by_attribute(name = "HttpGet", arg_value = "users")` matches `[HttpGet("/api/users")]`. Names are exact; values are FTS-matched. Power users can pass `name_pattern` for LIKE-style matching (e.g. all `*Authorize*`).

## Risks / Trade-offs

- **JSON storage is opaque to FTS without help.** Mitigated by maintaining a parallel virtualised FTS column over a flattened text representation of `args_json` (concatenation of values). Cheap.
- **Generic / open-type arguments** like `typeof(IEnumerable<>)` need careful display-string formatting. We use `SymbolDisplayFormat.FullyQualifiedFormat` and accept that round-tripping to source isn't perfect.
- **Code-as-attribute-arg** (`[X(MyConst)]`). Roslyn gives us the constant value; we store that. If the user wants the constant's *symbol* instead, they can `find_references(MyConst)`.
