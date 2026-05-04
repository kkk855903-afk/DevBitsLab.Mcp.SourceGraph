## Why

In a modern .NET codebase the *interesting* facts about a symbol are encoded in attributes: ASP.NET routes (`[HttpGet("/api/users")]`), Avalonia properties (`[StyledProperty]`), MediatR handlers (`[GenerateHandler]`), authorisation (`[Authorize(Roles = "Admin")]`), obsolescence (`[Obsolete("Use Foo")]`), DI lifetimes (`[Singleton]`). The graph today has zero visibility into any of this — an agent asking "find every POST endpoint in this codebase" or "what's been deprecated this release?" has to fall back to grepping. Roslyn surfaces every attribute via `ISymbol.GetAttributes()` with full constructor arg / named arg fidelity; the data is right there.

## What Changes

- Adds an `attributes(symbol_id, name, full_name, args_json)` table with appropriate indexes on `(symbol_id)` and `(name)`.
- Indexer pass 1 captures `attribute.AttributeClass.Name`, the fully qualified name, and serialises the constructor and named arguments to JSON (preserving types).
- New tool `find_by_attribute(name, arg_value?, kind_filter?)` — "find every method tagged `[HttpGet]`", "find calls of `[Obsolete]` whose message mentions 'use Foo'".
- Existing tools surface attribute names in their output so the agent can spot `[Obsolete]` etc. without an extra call.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `indexing`: pass 1 captures every attribute on every indexed symbol.
- `storage`: new `attributes` table with FTS5 over `args_json` for argument-text searches.
- `mcp-tools`: new `find_by_attribute` tool; `find_definition`, `list_symbols_in_file`, `neighborhood` surface attached attributes.

## Impact

- Schema bump (depends on whether `enrich-symbol-model` lands first; if so, this is v5 → v6).
- ~50 lines in `RoslynIndexer` for attribute extraction + JSON serialisation.
- New 1-class storage adapter `AttributeStore` (or methods on `IGraphStore`).
- One new MCP tool.
- Index size grows roughly proportional to attribute density: in a typical ASP.NET project, +10-20% rows in `attributes` vs `symbols`. Small.
