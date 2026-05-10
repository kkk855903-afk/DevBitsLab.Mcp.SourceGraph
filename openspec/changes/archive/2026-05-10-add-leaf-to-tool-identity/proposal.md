## Why

The `add-leaf-brand-mark` change (archived 2026-05-09) put a `🌿 ` prefix on every built-in tool's response prose so a reader has at-a-glance provenance — *this answer came from the live code graph*. After `tool-output-content-blocks` (archived same day) flipped every tool to `Task<CallToolResult>` with `UseStructuredContent = true`, MCP clients increasingly render `structuredContent` (typed tables, JSON viewers) and de-emphasize or hide the prose `content` blocks. Wire-level evidence confirms the leaf is still there — `content[0].text` of `find_definition` ships `🌿 10 hits for 'Calculator':...` exactly as designed — but in a typical user's MCP client UI the leaf may not be visible because the channel that carries it isn't the channel that gets rendered.

The leaf's job — brand recognition — needs a surface that survives this rendering shift. The `Tool` object in the MCP protocol has two text fields built for tool identity:

- **`Tool.Title`** — display label, separate from `Name` (the snake_case identifier used for invocation). Clients that support it render `Title` in tool selectors, dropdowns, and call labels.
- **`Tool.Description`** — already shown in tool catalogs, hover cards, and expanded call views.

Stamping the leaf on both fields puts the brand in the channels that virtually every MCP client renders — even minimal ones — at the cost of ~3 tokens per tool in the once-per-session `tools/list` payload. The per-call response prose leaf stays where it is (head prefix) as belt-and-suspenders for clients that *do* render content blocks. `ServerInstructions` is unchanged. Plugin tools remain unleafed (same first-party-voice reasoning as `add-leaf-brand-mark`).

A natural side effect: the in-flight [move-leaf-to-footer](../move-leaf-to-footer/proposal.md) proposal becomes optional. The visibility problem it tried to solve — making the leaf survive client-side prose suppression — is now addressed at a different layer (catalog metadata), which means the per-call head-vs-footer position is back to being a pure aesthetic question rather than a load-bearing visibility one.

## What Changes

- **Every built-in tool's `ProtocolTool.Title` is set to `"🌿 " + Tool.Name`** (e.g. `🌿 find_definition`, `🌿 search_symbols`). The snake_case form matches the existing `Name` field; clients that fall back to `Name` when `Title` is absent see the same identifier without the leaf. `Title` is purpose-built for display per the MCP spec.
- **Every built-in tool's `ProtocolTool.Description` is prepended with `"🌿 "`**. Idempotent: a description already starting with `🌿 ` is left alone. Order with the existing trigger-append pass (`ToolDescriptionFormatter.ApplyTriggersFromAttributes`) is irrelevant — leaf goes at the start, trigger goes at the end, regardless of which pass runs first.
- **Single chokepoint, mirroring the existing post-build pass.** A new `ToolIdentityFormatter` (or equivalent helper) extends the `Program.cs:349` call site:
  ```csharp
  ToolDescriptionFormatter.ApplyTriggersFromAttributes(tools);
  ToolIdentityFormatter.ApplyBrandMark(tools); // new
  ```
  The new helper walks the same `IEnumerable<McpServerTool>`, filters to built-in tool types (declaring type carries `[McpServerToolType]`), and mutates `ProtocolTool.Title` and `ProtocolTool.Description`.
- **Plugin tools are not branded.** The filter — declaring type carries `[McpServerToolType]` — naturally excludes plugin-registered tools. Same rationale as the existing chokepoint: the leaf is the first-party `sourcegraph` voice; plugin output is a third-party voice.
- **Suppression is unified with the existing `--no-leaf` knob.** When `LeafFormatter.Suppressed` is true, the new pass is a no-op: no `Title` is set (it stays the SDK default — typically null or `Name`), no `Description` prefix is added. Same flag/env-var contract as today; no new switches.
- **The per-call response prose leaf is unchanged.** `LeafFormatter.BrandFirstText` continues to prefix `content[0].text` with `🌿 ` for clients that render the prose channel. Belt-and-suspenders by design.
- **`ServerInstructions` head leaf is unchanged.** Same reasoning as `move-leaf-to-footer`'s Decision 5 — that's a session-level surface with its own role.
- **The `Icons` field on `Tool` is intentionally NOT used.** MCP supports it (the SDK's `Tool` has an `Icons : IList<Icon>` property), and `Icon.Source` accepts a `data:` URI for inline SVG/PNG. We choose text-glyph-in-Title-and-Description over a real icon because (a) text renders in every client; icon support is uneven, and (b) "leaf as glyph in branded text" matches the existing voice rather than introducing a parallel rendering channel. If a future change wants real icons, this proposal doesn't preclude it — `Tool.Icons` is rw and the same post-build pass could populate it.

## Capabilities

### Modified Capabilities

- `mcp-tools`: adds a per-tool brand mark on `Title` and `Description` (analogous to the existing per-response brand mark on prose content). Adds a new ADDED requirement *Tool identity brand mark* covering Title and Description stamping. Modifies the existing *Brand-mark suppression* requirement to extend its scope to the new identity surface (when suppressed, neither Title nor Description carries the leaf, mirroring how it already covers content prose and ServerInstructions).

## Impact

- **Code**: New file `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/ToolIdentityFormatter.cs` (~30–40 LOC), parallel to the existing `ToolDescriptionFormatter`. One additional call in `Program.cs` immediately after the existing `ApplyTriggersFromAttributes` line.
- **Tests**: Three additions — (a) `ToolIdentityFormatter` unit tests covering the title-and-description stamping, idempotency, plugin-skip, and suppression paths; (b) extension of the wire-level fixture used by `LeafChokepointInvariantTests` to confirm every built-in `[McpServerTool]` ships with `Title.StartsWith("🌿 ")` and `Description.StartsWith("🌿 ")` at `tools/list` time; (c) extension of `ServerInstructionsWiringTests` (or its sibling) to confirm `--no-leaf` removes the Title and Description prefix on every built-in tool.
- **Public API / wire format**: No new MCP fields. The change populates two existing `Tool` fields (`Title`, `Description`). `tools/list` shape is unchanged structurally — clients see populated fields where they previously saw default/null values. Existing consumers of `Description` see a `🌿 ` prefix added; the field is not a parsed identifier, so this is a presentation-only change.
- **Token cost**: ~3 tokens per tool × 22 built-in tools = ~66 tokens once per session in the `tools/list` payload. Plus whatever `Title` adds per tool (~3–4 tokens for `🌿 find_definition`-style strings, never previously populated, so net new). Total: ~150–200 once-per-session tokens. Negligible.
- **Documentation**: `README.md` and `CLAUDE.md` mention the per-tool branding alongside the existing per-response leaf — one paragraph each, parallel structure to the existing `--no-instructions` / `--no-leaf` documentation.
- **`move-leaf-to-footer` proposal**: superseded in spirit. The visibility argument that motivated the footer move is addressed by this proposal at the catalog layer. The aesthetic argument (head vs footer for content prose) remains valid as a future cosmetic change but no longer urgent. Park or close `openspec/changes/move-leaf-to-footer/`.

**Depends on**: nothing. Lands cleanly on top of `add-leaf-brand-mark`, `tool-output-content-blocks`, and `auto-register-tool-instructions` — all archived. The post-build pass at `Program.cs:349` provides the chokepoint we extend.

**Conflicts with**: nothing currently in flight. Active changes `payload-tooling` and `fix-stranded-reference-edges` neither register new tools nor touch `Program.cs`'s post-build mutation pass. If `payload-tooling` (which adds two new tools) lands first, those new tools automatically receive the brand mark since the pass walks the full `McpServerTool` collection.
