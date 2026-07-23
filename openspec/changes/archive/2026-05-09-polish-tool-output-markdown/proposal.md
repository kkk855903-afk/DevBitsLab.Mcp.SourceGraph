## Why

Most of our tools return list-shaped data (references, callers, members, diagnostics, recent changes) but render it as bulleted prose. Markdown tables exist for some surfaces — `usage_stats`, `list_scopes`, `list_generated_files` — and the visual chrome they get in IDE-class clients (VS Code, Cursor, Claude Code) is markedly stronger than prose: aligned columns, hover/copy on individual cells, alternating row backgrounds depending on the theme. Tabular data displayed as bullets gets none of that.

The leaf-brand-mark UX iteration showed that the bigger lever for "responses readers can scan" isn't ornament — it's giving the client more rendering primitives to work with. This change leans into that: convert the inherently tabular tools to tables, promote section headers where they exist, and tighten code-fencing on multi-line signatures. The scope is purely visual — no return type changes, no protocol-level shifts, no new dependencies. The companion changes (multi-content blocks, structuredContent + outputSchema, resource_link) are larger and depend on an SDK spike before design; this one ships independently and immediately.

## What Changes

- **Convert list-shaped tools to markdown tables when the row count is ≥ 2.** Single-result responses stay as prose so we don't pay table chrome for one-row data.
  - `find_references` — `| Kind | Location |` (and `| Scope |` when fan-out merged)
  - `find_by_annotation` — `| Symbol | Kind | Location |`
  - `search_symbols` — `| Symbol | Kind | Location |`
  - `list_callers` / `list_callees` — `| Symbol | Kind | Location |`
  - `find_implementations` — `| Symbol | Kind | Location |`
  - `list_members` — `| Member | Kind | Signature |`
  - `semantic_search` — `| Score | Symbol | Kind | Location |`
  - `find_diagnostics` — `| Severity | Code | Location | Message |`
  - `recent_changes` — `| When | Author | Symbol | Location |`
  - `list_tests_for` — `| Framework | Test | Location |`
  - `impact_of_change` — `| Depth | Symbol | Kind | Location |`
  - `module_summary` — `| In-deg | Symbol | Kind | Location |` (replaces the current `- in-deg N — fqn` bullets)
- **Stay as bulleted prose** (hierarchical content the table format doesn't accommodate):
  - `find_definition` (each hit has nested signature, summary, annotations, history)
  - `list_symbols_in_file` (each symbol has nested annotations + history)
  - `neighborhood` (already sectioned; the sections themselves get internal tables — see below)
- **Already tables** (no change): `usage_stats`, `list_scopes`, `list_generated_files`, `graph_stats`.
- **Promote section structure inside `neighborhood`.** The Inbound/Outbound sections currently render as `### Inbound (N)` followed by bullets. After: keep `### Inbound (N)` headers but render their contents as the same `| Symbol | Kind | Location |` table that `list_callers` / `list_callees` use — consistency between tools that walk the same edges.
- **First line stays prose.** Every tool body still leads with the substantive lead-in (`{n} hits for 'X':`, `Multiple symbols match …`, etc.) so the leaf chokepoint can prepend `🌿 ` cleanly. Tables and headers sit on subsequent lines.
- **No change to single-row responses.** A single-hit `find_references` or `search_symbols` keeps its current bulleted shape — prose for one row is more compact than a table header + one row.

## Capabilities

### New Capabilities
<!-- None — this change refines an existing capability's rendering, not its surface or semantics. -->

### Modified Capabilities

- `mcp-tools`: Adds rendering scenarios for tabular tools — clients see GFM tables instead of bulleted lists when row count is ≥ 2. The underlying tool semantics, parameters, and result data are unchanged. The existing tool-specific requirements stay; new scenarios are added under each affected tool's requirement (or under a new cross-cutting "Tabular rendering" requirement).

## Impact

- **Code**: Helper formatting changes scattered across `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/{GraphTools,HistoryTools,ScopeTools}.cs`. Each affected tool's body switches its row-emission loop from `sb.AppendLine($"- ...")` to a table-emit pattern. A small `Format.Table(...)` helper centralises the table header / separator construction so we don't repeat ourselves twelve times. No type or signature changes.
- **Spec**: Modifies `openspec/specs/mcp-tools/spec.md` — adds rendering scenarios on the affected tool requirements. Could optionally consolidate into a single new "Tabular rendering" requirement; design.md will pick the cleaner shape.
- **Tests**: Most existing tests don't pin tool output prose (verified during the leaf change's audit). The few that do — and any new ones we add for the tabular scenarios — assert on column-header substrings (`"| Kind | Location |"`) rather than exact whole-output strings. New positive tests cover: table renders for ≥ 2 rows, prose stays for single-row responses, table column counts match the documented scenarios.
- **Token cost**: Tables introduce ~15 tokens of overhead per response (header row + separator row). For ≥ 5 rows the per-row delta is comparable to prose; for 2–4 rows there's a small per-call cost (~5–15 tokens). Net per-session impact is negligible compared to the rendering quality gain in IDE-class clients. Clients that don't render markdown tables (raw stdio, simple terminals) still see legible pipe-delimited rows — graceful degradation.
- **Public API / dependencies**: None.
- **Documentation**: No README change — these are formatting refinements, not surface area.
- **Compatibility with later changes**: This change uses no protocol-level features beyond what we already use. The follow-on `tool-output-content-blocks` change (covering C + D + E from the design discussion) refactors return types; the tables and headers we land here travel forward into the new content-block surface unchanged — they're just markdown text that ends up inside `TextContent` items either way.
