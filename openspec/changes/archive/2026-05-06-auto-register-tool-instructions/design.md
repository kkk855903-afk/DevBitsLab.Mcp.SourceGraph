## Context

The MCP `initialize` response carries an optional `instructions` string for cross-tool guidance. The C# SDK exposes it as `McpServerOptions.ServerInstructions`. We don't set it today, so per-repo `CLAUDE.md` is the only delivery mechanism for "prefer source-graph tools over `Grep`+`Read`". That doesn't generalise to other repos that install the server.

The existing tool descriptions already carry trigger phrases in prose: `find_definition` ends with `Use for 'where is X defined?'`, `search_symbols` with `Use this when you only have a fragment ('Calc', 'Greet', 'Async')`, etc. So "trigger metadata" isn't a new concept — it's a structural promotion of an existing convention.

## Goals / Non-Goals

**Goals:**

- Cross-tool guidance ("prefer the graph; verify with `usage_stats`") ships from the server, not from the host repo's `CLAUDE.md`.
- Per-tool trigger phrases stay co-located with their tool, structured as a first-class attribute rather than embedded in description prose.
- Plugin tools can declare triggers without touching the host or knowing about MCP internals.
- Users who don't want server-injected instructions can turn them off.

**Non-Goals:**

- A full markdown "question → tool" table inside `ServerInstructions`. Triggers travel with their tool in the catalog. The instructions string carries only the cross-cutting rule.
- Localisation of instructions or trigger text. English only, v1.
- Per-client tailoring. Same string for all clients; the server has no reliable signal about which client is connecting that's worth conditioning on.
- Hot reconfiguration of instructions. Server restart picks up changes. Same constraint as `.sourcegraph.json`.
- Auto-generating triggers from tool descriptions. Tools opt in by declaring `[ToolTrigger]` (or by passing `trigger:` for plugin registration); tools without one are not advertised by trigger.

## Decisions

**1. `ServerInstructions` = preamble + epilogue, no embedded table.**

Composition is a small static string built once at startup and assigned to `McpServerOptions.ServerInstructions` via `services.Configure<McpServerOptions>(...)`. Approximate content:

```
This MCP server exposes a code source graph for the connected .NET solution.
For symbol-level questions ("where is X defined?", "who calls X?", "what's
in this file?", etc.) prefer these tools before reaching for Grep+Read —
they answer in one structured call instead of dozens of file reads.

Each tool's description includes a "Use when:" line documenting the
question shape it answers.

Call `usage_stats` at the end of a turn to verify the graph was actually
queried; if counts didn't move you fell back to Grep+Read when a graph
tool would have been faster.
```

Rationale for excluding the table: tool descriptions already get sent to the model as part of the catalog. Including a duplicated table in instructions burns tokens for redundant data.

**2. `[ToolTrigger("...")]` is a separate attribute, not a property on `[McpServerTool]`.**

`McpServerToolAttribute` is owned by the SDK and not extensible. Introducing a sibling attribute keeps the change additive and lets us scan our own assembly for it without touching SDK behaviour. Trigger string is the natural-language question phrase, with surrounding quotes (e.g. `[ToolTrigger("\"where is X defined?\"")]`) so the appended description line reads naturally.

**3. Trigger appears as a final line on the tool's effective description.**

Format: `Use when: <trigger>`. Mirrors the existing prose convention (`Use for 'X'`) so the catalog reads consistently after migration. Append happens once at registration time; the SDK then sees the modified description and surfaces it in `tools/list` unchanged.

**4. Plugin contract gains an optional `trigger` argument.**

`IToolRegistry.AddTool(string toolName, string description, Delegate handler)` becomes `AddTool(string toolName, string description, Delegate handler, string? trigger = null)`. Default-null preserves existing behaviour. Same append semantics: when present, the host appends `Use when: <trigger>` to `description` before handing it to the SDK builder.

**5. Opt-out via flag + env var.**

CLI flag wins over env var when both are set. Three states: flag set → off; flag absent + env set → off; flag absent + env absent → on. The flag also blocks the env var so a user with `SOURCEGRAPH_NO_INSTRUCTIONS=1` in their shell can still re-enable per-invocation by *not* passing `--no-instructions` (the flag is presence-based, not boolean — env wins by default but the explicit absence-of-flag doesn't override env). Simpler: flag → off; otherwise check env. We'll go with the simpler form.

**6. Migrate existing tools' embedded "Use for X" prose to `[ToolTrigger]`.**

Mechanical change in `Tools/GraphTools.cs`, `Tools/ScopeTools.cs`, `Tools/HistoryTools.cs`, `Tools/PingTool.cs`. The `[Description]` keeps the *what-it-does* prose; the *when-to-use* trigger moves to the new attribute. Net result: descriptions get slightly shorter, the appended `Use when:` line restores roughly the same content. The rendered catalog stays comparable in tokens, but the structure is now machine-readable.

## Risks / Trade-offs

- **Some clients may not honour `instructions`.** Claude Code does. Cursor and Continue claim support; verify during implementation. Worst case: silently dropped, which is no worse than today.
- **Token cost is real but small.** ~80 tokens for instructions, plus ~15 per tool for the appended `Use when:` line. With prompt caching (5-min TTL) the cost is paid once per cache cycle, not per turn. For users who care, `--no-instructions` strips the 80-token preamble; the per-tool lines remain because they're part of the tool catalog.
- **Two ways to declare a trigger** during the migration window: prose-in-description and the new attribute. We accept the duplication in built-in tools transiently — phase 1 adds the attribute everywhere; phase 2 strips the prose. Both phases land in this change, no migration period for our own tools. Plugin tools can adopt at their own pace.
- **The `Use when:` line is not a hard API contract** — it's surfaced in tool descriptions, which the SDK already treats as free-form. If a future change wants to expose triggers as structured metadata over MCP (e.g., a `tools/listTriggers` extension), the `[ToolTrigger]` attribute is the natural source.
- **Validation is post-deploy.** We don't have a clean signal today on whether the `CLAUDE.md` block actually changed model behaviour. Real test: ship this, watch `usage.jsonl` across a few weeks, see if average per-session graph-tool invocations go up. Not blocking the change, but a project memory worth holding.
