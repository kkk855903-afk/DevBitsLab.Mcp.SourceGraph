## Why

The `open-language-contract` change added a `payload TEXT NULL` JSON column to
the `edges` table so per-edge facts (binding paths, event names, prop names,
converters) have a place to live. The `xaml-language-indexer` change fills it
with binding metadata. Generic walks (`list_callers --kind binds-path`)
technically expose those edges, but they don't expose payload content and the
question shape — "where does `User.Name` get bound `TwoWay`?", "find every
`Click` handler routing to `OnSave`" — has no discoverable home in a tool
listing. Specialized tools that name the common knobs (`mode`, `converter`,
`event`, `handler`) make the queries first-class for LLM agents and lift the
payload out of "general-purpose escape hatch" into "documented surface."

The companion change `harden-sdk-pre-xaml` lands the prerequisites — payload
key constants in the SDK, always-render-payload in existing tool markdown,
payload plumbed through the read path. This proposal sits on top of that
plumbing and adds the specialized query tools.

## What Changes

- **NEW MCP tool: `find_data_bindings`.** Walks `binds-path` edges with
  payload-aware filtering. Optional parameters (none required, but at least
  one must be non-null): `target` (bound viewmodel symbol), `source` (XAML
  element symbol), `path` (substring of `payload.path`), `mode` (exact match
  on `payload.mode`), `converter` (exact match on `payload.converter`),
  `scope`, `limit` (default 50). Returns markdown rows of the form
  `<source-element>  →  <target-or-unresolved>  [mode=…, converter=…, path=…]`.
- **NEW MCP tool: `find_event_handlers`.** Walks `handles-event` edges with
  payload-aware filtering. Optional parameters: `handler` (resolved C# method
  symbol), `event` (exact match on `payload.event`), `element` (XAML source
  element), `command` (exact match on `payload.command` for command-bound
  flavors), `scope`, `limit`. Returns markdown rows of the form
  `<element>.<event>  →  <handler-or-command>`.
- **No SDK changes.** The payload contract was set by `harden-sdk-pre-xaml`;
  this change consumes it.
- **No schema changes.** Queries use `json_extract` over the existing
  `kind_name` index. No new tables, columns, indexes, or generated columns.
- **`Use when:` lines** on both tools so the LLM agent surfaces them on the
  shape of question they answer (see design.md decision 1).

## Capabilities

### Modified Capabilities

- `mcp-tools`: registers two new tools (`find_data_bindings`,
  `find_event_handlers`). The existing tool set is unchanged.

## Impact

- **Code:** `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/GraphTools.cs` (two
  new `[McpServerTool]` methods + their `[ToolTrigger]` descriptions);
  `src/DevBitsLab.Mcp.SourceGraph.Storage/` (two new helpers on `IGraphStore`
  that issue payload-projecting queries).
- **Public contract:** Two new MCP tools advertised via `tools/list`. No
  existing tool changes shape.
- **Persistence:** None. The `payload` column already exists.
- **Telemetry:** Two new tool names appear in `usage_stats`, the JSONL log,
  and the OTel `Meter` instruments — same plumbing every tool uses.
- **CHANGELOG:** new tool registrations, README example.
- **Out of scope:** `inspect_edge` (deferred); generic `--payload-where`
  escape hatch (deferred); FTS over JSON; generated columns; tools for not-
  yet-emitted edge kinds (templates, styles, slots).

**Depends on:** `harden-sdk-pre-xaml` (payload plumbing through read path,
`PayloadKeys` constants, always-render-payload in tool markdown) AND
`xaml-language-indexer` (the indexer that actually emits `binds-path` and
`handles-event` edges with the expected payload shape). This proposal
cannot land before either prerequisite — without them the tools have
nothing to query and no shared key vocabulary to query against.
