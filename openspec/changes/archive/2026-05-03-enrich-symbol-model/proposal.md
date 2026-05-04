## Why

The current symbol row records `(name, fqn, kind, file_id, span, signature)` and nothing else. An agent asking "list public async methods of `IFeedRepository`" has to read every method's source to filter; "what does `PublishAsync` do?" has to fall back to `Read` because the XML doc summary is invisible to the graph; nested types and members can only be browsed flat because `container_id` is declared but never populated. Each of these forces the agent into a `Grep`+`Read` storm that the graph already has the data to satisfy in one query — Roslyn exposes all of it via `ISymbol`.

## What Changes

- Adds four columns to `symbols`: `modifiers TEXT`, `accessibility INTEGER`, `xml_summary TEXT`, plus populates the existing `container_id`.
- Indexer reads `IMethodSymbol.IsAsync`, `ISymbol.IsStatic`, `IsVirtual`, `IsAbstract`, `IsSealed`, `IsOverride`, `IsExtern`, `IsReadOnly`, `IsPartialDefinition`, `DeclaredAccessibility`, and `GetDocumentationCommentXml()` during pass 1 and writes the values via `UpsertSymbolAsync`.
- `xml_summary` is the parsed text of `<summary>` (and inheriteddoc resolution); the raw XML is dropped to keep the row compact. `xml_summary` is added to the FTS5 trigger surface so `search_symbols("retry")` finds anything that *describes itself* as doing retry.
- `find_definition`, `list_symbols_in_file`, `neighborhood`, `module_summary`, and the `graph://` resources include the new fields in their markdown output.
- New tool `list_members(container, include_inherited=false, accessibility?)` walks the `container_id` chain — replaces several common `list_symbols_in_file`+filter combinations.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `indexing`: pass 1 captures modifiers, accessibility, xml summary, and container_id from Roslyn's `ISymbol`.
- `storage`: schema gains four new columns plus an FTS5 trigger surface change.
- `mcp-tools`: existing tools surface the new fields; new `list_members` tool added.

## Impact

- Schema bump v4 → v5; existing graph DBs auto-rebuild on next start.
- ~80 lines in `RoslynIndexer`, ~30 lines in `SymbolMapping` (extract metadata), schema additions in `Schema.V1`.
- No CLI surface change; new fields are optional in tool output.
- FTS5 index size grows modestly (~+15-25% for the doc summary column on a typical .NET solution).
