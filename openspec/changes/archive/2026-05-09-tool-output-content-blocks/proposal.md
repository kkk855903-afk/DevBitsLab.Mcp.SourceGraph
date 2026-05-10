## Why

Every built-in MCP tool currently returns `Task<string>` — a single text blob the SDK auto-wraps into one `TextContentBlock`. That shape made sense when our tools first shipped, but it has become a ceiling on three distinct UX directions:

1. **The leaf brand-mark UX iteration showed that any per-response decoration competes with the client's own chrome.** We can't easily separate "the substantive answer" from "metadata/debug/scope info" — they're concatenated into one blob and rendered as one block. Multi-content lets us push debug/metadata into a separate content item with `audience: ["assistant"]` so it reaches the model but doesn't clutter the user's view.

2. **Agents that want to chain tool calls or post-process results have to re-parse our markdown.** They can't reach for typed JSON without first reverse-engineering our prose. The MCP 2025-06-18 spec adds `structuredContent` + `outputSchema` precisely for this — a tool ships both renderable prose AND the typed result object the agent (or a downstream tool) consumes programmatically.

3. **Our `Resources/GraphResources.cs` subsystem already exposes typed resource cards** under `graph://symbol/<id>` and `graph://file/<path>` URIs, but tools never *link* to those resources. Adding `resource_link` content items per result row gives clients with richer UI (Claude Code, Cursor) cards they can render adjacent to the prose; clients without those affordances see no regression because the prose still carries the file:line.

The SDK spike against ModelContextProtocol 1.2.0 confirmed that all three are achievable from `[McpServerTool]` attribute methods without dropping into `McpServerTool.Create(...)` registration: a tool can return `Task<CallToolResult>` (or `Task<IEnumerable<ContentBlock>>` for the simpler case), gets `IProgress<ProgressNotificationValue>` injected like `CancellationToken`, and `[McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(MyDto))]` wires up the schema declaration on `tools/list`.

## What Changes

- **Built-in tool method signatures evolve** from `Task<string>` to `Task<IReadOnlyList<ContentBlock>>` (most tools) or `Task<CallToolResult>` (tools that ship `structuredContent` or `isError` distinctly). Plugin tools keep their existing `Delegate`-based registration unchanged — the leaf-bypass and the simpler wire surface for plugin authors are preserved.
- **`ToolMetrics.TrackAsync<T>` and `TrackSync<T>` become generic** so the chokepoint can wrap whatever return type the tool body produces. The leaf brand mark is applied to the *first* `TextContentBlock` in the result list (not concatenated into a single string), so the brand sits adjacent to substantive content with no scope/metadata lines crowding it.
- **`structuredContent` ships alongside renderable prose** for every tool whose result is naturally typed: `find_definition`, `find_references`, `find_by_annotation`, `search_symbols`, `list_symbols_in_file`, `list_callers`, `list_callees`, `find_implementations`, `list_members`, `semantic_search`, `module_summary`, `impact_of_change`, `find_diagnostics`, `recent_changes`, `list_tests_for`, `who_authored`, `list_generated_files`, `list_scopes`. The output schemas are typed records (DTOs) — never anonymous types, since the SDK's source-gen `JsonContext` rejects them at runtime (we already paid this tuition once with the `initialize` vocabulary fix).
- **`resource_link` content items are emitted per result row** for tools whose rows are individual symbols or files. Each row carries its prose representation AND a `ResourceLinkBlock { Uri = "graph://symbol/<id>", … }` (or `graph://file/<path>` for file-rooted rows). Clients that render resource_links get expandable cards; clients that ignore them lose nothing.
- **Audience-restricted content blocks for metadata.** Per-call diagnostics that are useful to the agent but noise to the human (resolved scope, latency, cache hits, "X of N rows truncated due to limit") move into a `TextContentBlock { Annotations = { Audience = [Role.Assistant], Priority = 0.2 } }` so they reach the model without cluttering the chat. The leaf may also move into an audience-restricted block, eliminating the brand-mark-vs-chrome competition entirely — TBD per design.
- **Anonymous-type guard at the chokepoint.** `ToolMetrics.Track*` adds a defensive runtime check: if a `CallToolResult.StructuredContent` value is an anonymous type, fail loudly at request time (not in the wire serializer). Catches the same family of bug as the `initialize` regression we just fixed; saves a debugging session.
- **Existing `Format.Table` helper from `polish-tool-output-markdown` continues to be used** inside `TextContentBlock`s. Multi-content doesn't replace markdown formatting — it augments it.

## Capabilities

### New Capabilities
<!-- None — this change refines an existing capability's wire shape. -->

### Modified Capabilities

- `mcp-tools`: Adds three new requirements — `Multi-content tool responses`, `Structured content output`, `Resource-link content items`. Modifies the existing `Tool response brand mark` requirement so the leaf applies to the *first* `TextContentBlock` rather than the whole concatenated string. The per-tool requirements (`Definition lookup`, `Reference lookup`, etc.) get scenarios documenting which fields appear in their respective `structuredContent` payloads.

## Impact

- **Code (large)**: every method in `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/{GraphTools,HistoryTools,ScopeTools,PingTool}.cs` changes signature from `Task<string>` to `Task<IReadOnlyList<ContentBlock>>` or `Task<CallToolResult>`. New typed DTOs per tool live alongside `ServerVocabulary.cs` (or in a new `Tools/Output/` namespace). `ToolMetrics.TrackAsync<T>` becomes generic with a small adapter that brands the first `TextContentBlock` instead of prepending a string. `LeafFormatter.Brand` extends with a `BrandFirstText(IReadOnlyList<ContentBlock>)` overload.
- **Spec**: 3 ADDED requirements, 1 MODIFIED requirement (brand mark applies to first text block), and per-tool scenarios documenting `structuredContent` shapes.
- **Tests**: lots of new ones. `LeafChokepointInvariantTests` extends to cover both the legacy single-string and new content-list code paths. New `StructuredContentOutputTests` per tool. New `ResourceLinkEmissionTests`. `LeafFormatterTests` adds the `BrandFirstText` overload. Existing tests that asserted on string-shape responses migrate to inspect `CallToolResult.Content` / `.StructuredContent`.
- **Public API / dependencies**: No new NuGet dependencies. The MCP SDK already ships everything we need (verified by spike). Wire-level protocol unchanged — clients see richer payloads, but the JSON-RPC framing is identical.
- **Token cost**: per-call cost goes UP slightly when we emit `structuredContent` (the structured payload duplicates information already in the prose). Net per-session: agents that consume `structuredContent` directly stop running follow-up parser-style tool calls, which is a wash or net-negative across the session. Audience-restricted content blocks claw back display-side tokens by hiding scope/latency from the user-facing render.
- **Plugin compatibility**: `IToolRegistry.AddTool(name, description, handler, ...)` still accepts `Delegate` returning `string` / `Task<string>`. Plugin tools continue to ship single-text responses and continue to bypass the leaf chokepoint. The richer return types are an *option* for plugins that want them, not a requirement.
- **Backward compatibility**: this is a wire-shape evolution within the protocol. Older MCP clients that don't recognise `structuredContent` ignore it and render only `content`; clients that don't recognise `resource_link` ignore those items and render the surrounding text. No client breaks.
- **Documentation**: `README.md` gains a paragraph on structured output and resource_link consumption for downstream-tool authors. `CLAUDE.md` adds a one-liner so future Claude sessions know that `find_*` tools ship typed `structuredContent` they can consume directly.
