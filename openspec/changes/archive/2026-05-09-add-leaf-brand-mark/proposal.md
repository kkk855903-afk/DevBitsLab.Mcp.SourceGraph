## Why

When the source-graph server answers an agent question, its response is currently indistinguishable in the chat from a `Grep` + `Read` reply or any other MCP server's output. The agent (and the human reading along) has no fast visual signal that "this answer came from the live code graph." That blurs the credit line for the server's value proposition: the very thing the `usage_stats` directive in `ServerInstructions` exists to verify (was the graph actually queried?) is invisible until the agent runs that verification tool.

A small, consistent brand mark on every tool result solves the recognition problem in zero extra reading effort. A green leaf glyph (🌿) is a single token in typical BPE tokenizers, defaults to colored rendering everywhere, and visually branches — an apt metaphor for a graph tool. While we're touching every tool's lead-in line, we can also tighten verbose phrasing (`Found 3 match(es) for 'X':` → `🌿 3 hits for 'X':`) for token economy that compounds across a session.

## What Changes

- Every MCP tool response emitted by the server is prefixed with a `🌿 ` brand mark (single leaf glyph + space) on its first line.
- The `ServerInstructions` blurb published in the `initialize` response is also prefixed with the leaf, so the agent learns the `🌿 → sourcegraph` association from turn 0.
- Tool lead-in lines are tightened in the same pass for token economy:
  - `Found N match(es) for 'X':` → `N hits for 'X':`
  - `No definition found for 'X'.` → `No matches for 'X'.`
  - Equivalent trims across `find_definition`, `find_references`, `find_by_annotation`, `search_symbols`, `module_summary`, `impact_of_change`, `neighborhood`, and history/scope tools.
- The leaf is applied uniformly: success, empty results, and error paths all carry it. The leaf means "sourcegraph spoke," not "sourcegraph succeeded."
- A new opt-out knob mirrors the existing `--no-instructions` pattern: `--no-leaf` CLI flag and `SOURCEGRAPH_NO_LEAF=1` env var. When suppressed, neither the per-tool prefix nor the leaf on `ServerInstructions` is emitted.
- The `ping` tool keeps its current `pong @ <iso-time>` shape but also gains the leaf prefix — no exception, since the leaf is a server-wide voice signal, not a per-tool flourish.

## Capabilities

### New Capabilities
<!-- None — this change refines an existing capability rather than introducing a new one. -->

### Modified Capabilities
- `mcp-tools`: Adds a per-response brand-mark requirement and a leaf on the `ServerInstructions` string. Adds a suppression mechanism (`--no-leaf` / `SOURCEGRAPH_NO_LEAF`) and tightens the lead-in phrasing the existing tool-response requirements imply. No tool's semantics, schema, or returned data change — only the surface text framing.

## Impact

- **Code**: Every tool method in `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/*.cs` (`GraphTools`, `HistoryTools`, `ScopeTools`, `PingTool`) plus the response-formatting helpers they share. A thin `LeafFormatter` (or equivalent) centralises the prefix logic so the rule lives in one place. `ServerInstructions.Template` gains a leading `🌿 `. CLI parsing in `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/` adds the `--no-leaf` flag; `Program.cs` reads `SOURCEGRAPH_NO_LEAF`.
- **Spec**: One delta on `mcp-tools` adds the brand-mark requirement and updates the existing instructions requirement to reflect the leaf prefix.
- **Tests**: Existing tool tests that assert on response text need updating to either (a) start with `🌿 ` or (b) be retargeted to assert on the post-leaf substring. A new test fixture covers the `--no-leaf` / env-var suppression path. Snapshot/golden-file tests, if any, regenerate.
- **Public API / dependencies**: None. No new NuGet dependencies, no schema migrations, no breaking changes to MCP wire format — the leaf is just text content. `usage_stats`, `graph_stats`, and the `tools/list` schema are untouched.
- **Token cost (per session)**: Net negative. The leaf adds ~1 token per tool response; the lead-in tightening removes 4–5 tokens per response on the hot tools. Across a 30-call session that's a small but measurable win, and the brand visibility is the headline value.
- **Documentation**: `README.md` gains a note alongside the existing `--no-instructions` documentation describing the leaf and its opt-out. `CLAUDE.md` adds a one-liner so future Claude sessions know what `🌿` means at a glance.
