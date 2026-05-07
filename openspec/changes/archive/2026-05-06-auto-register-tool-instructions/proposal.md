## Why

The repo's `CLAUDE.md` carries a "prefer source-graph tools before reaching for `Grep`+`Read`" block plus a "verify with `usage_stats`" closing note. That guidance only reaches a model when a user copies it into the host project's `CLAUDE.md`. Anyone installing `sourcegraph-mcp` in another repo gets the tools but not the guidance — a discoverability gap that almost certainly suppresses uptake.

The MCP spec has a built-in slot for exactly this: the `instructions` field on the `initialize` response. Claude Code (and other clients that honour the field) thread it into the system prompt. The C# SDK exposes it as `McpServerOptions.ServerInstructions`. Today the server doesn't set it.

A second observation: nearly every existing tool's `[Description]` already ends with a `Use for "X?"` clause carrying the trigger phrase. The convention exists; it just isn't structured. Promoting it to a `[ToolTrigger]` attribute makes the convention enforceable, lets plugins self-advertise without conventions, and keeps trigger phrases co-located with their tool (in the catalog) rather than duplicated into a table inside `instructions`.

## What Changes

- **Set `McpServerOptions.ServerInstructions`** at server startup. Content: a short preamble ("prefer source-graph tools over `Grep`+`Read` for symbol-level questions") plus an epilogue ("call `usage_stats` at end of turn to verify the graph was actually used"). ~80 tokens. The per-tool table is intentionally NOT embedded; triggers travel with their tool.
- **New `[ToolTrigger("...")]` attribute** on tool methods. At registration time the server appends `Use when: <trigger>` as a final line to the tool's effective description. The existing prose convention (`Use for 'X?'` embedded in `[Description]`) is migrated to the attribute on every built-in tool.
- **Plugin contract gains `trigger`**: `IToolRegistry.AddTool` gets an optional `string? trigger = null` parameter. Backwards-compatible — existing plugins keep compiling.
- **CLI opt-out**: `--no-instructions` flag (also `SOURCEGRAPH_NO_INSTRUCTIONS=1` env var) suppresses `ServerInstructions`. Default is on.
- **Strip the now-redundant CLAUDE.md sections**. The `## When working in any indexed .NET solution: prefer source-graph tools` and `## Verifying the MCP is doing its job` blocks come out of `CLAUDE.md` once the server ships them.

## Capabilities

### Modified Capabilities

- `mcp-tools`: server publishes `ServerInstructions`; tools may declare `[ToolTrigger]`; trigger text is appended to each tool's effective description.
- `mcp-config`: new `--no-instructions` flag and matching env var.
- `extensibility`: `IToolRegistry.AddTool` accepts an optional `trigger` argument that participates in the same description-append pipeline as `[ToolTrigger]`.

## Impact

- **Token cost**: ~80 tokens added once-per-session to the system prompt (preamble + epilogue). Cached by clients that use prompt caching. Tools that adopt `[ToolTrigger]` see ~15 tokens of "Use when: X" appended to their description in the catalog — but most of them already carry the same prose in `[Description]`, so the net delta after migration is roughly zero.
- **Behaviour change**: zero for clients that ignore `instructions`. For Claude Code / Cursor / Continue, the model receives the cross-cutting rule in the system prompt without any per-repo `CLAUDE.md` edit.
- **Plugin contract**: additive. Old plugins that don't pass `trigger` keep working; their tools just don't get a "Use when:" line appended.
- **Validation**: real success criterion is "`usage_stats` invocations go up across users after this lands". The signal lives in `<solution>/.sourcegraph/usage.jsonl` and can be inspected by users of the MCP server, no extra plumbing required.
