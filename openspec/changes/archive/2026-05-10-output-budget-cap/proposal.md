## Why

Claude Code (and similar MCP clients) cap each `tools/call` result at ~16K tokens / ~64K characters; oversize results trigger a host-side truncation that the agent reads as a cryptic error. Built-in list-shaped tools triplicate per-row data — markdown table row, `ResourceLinkBlock` JSON, structured-content array entry — so a single `find_references` query at the documented `limit=200` default routinely landed at ~80K characters and failed. Users hit this often enough that the failure showed up in field reports.

## What Changes

- Lower the default `limit` for `find_references` from 200 → 50 (matches `list_callers`, `list_callees`, `find_implementations`).
- Lower the default `limit` for `list_members` from 200 → 100.
- Introduce a soft serialized-size budget (~50K chars) applied per tool result. When the projected size of `prose + ResourceLinkBlocks + StructuredContent` would exceed the budget, the tool body trims its row list in lockstep so all three representations stay consistent.
- When trimming activates, the tool emits an `omitted_size=N` field in its existing audience-restricted `_meta:` block so the agent can detect truncation and re-query with a tighter filter.
- Wire the cap into the four highest-risk Build helpers: `find_references`, `list_members`, `list_symbols_in_file` (which has no `limit` parameter at all), and `semantic_search` (heavy rows from XML summaries).

## Capabilities

### New Capabilities

_(none — this change tightens existing tool-output behaviour rather than adding new tools.)_

### Modified Capabilities

- `mcp-tools`: adds a soft size budget requirement that every list-shaped built-in tool MUST honour, plus a metadata-key requirement (`omitted_size`) for signalling size-driven truncation distinct from `limit`-driven truncation.

## Impact

- Code: `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/GraphTools.cs` (default-limit changes + four Build-helper trims), new helper `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/Output/OutputBudget.cs`.
- Tests: new `tests/DevBitsLab.Mcp.SourceGraph.Tests/OutputBudgetTests.cs` pinning the `ChooseKeep` arithmetic; existing `StructuredContentInvariantTests` continue to pass because prose-row count and structured-array length remain in lockstep.
- API: lowered defaults are technically a behaviour change for callers that relied on the prior 200 — they continue to work but now return fewer rows unless they pass an explicit `limit=`. The `_meta:` block gains a new optional `omitted_size` key; clients that don't recognise it ignore it (audience-restricted).
- Dependencies: none.
