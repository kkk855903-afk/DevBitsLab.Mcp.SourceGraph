## Context

The MCP `Tool` object (advertised in `tools/list` responses) carries several user-visible identity fields:

| Field | Type | Role | Today |
|---|---|---|---|
| `Name` | `string` | snake_case identifier used for invocation | populated, e.g. `find_definition` |
| `Title` | `string?` | display label, separate from `Name` | **null on every tool** (verified via wire probe) |
| `Description` | `string?` | longer human-facing description | populated; the `ApplyTriggersFromAttributes` pass appends `Use when:` lines |
| `Icons` | `IList<Icon>` | optional rendered icons | empty on every tool |
| `Annotations.Title` | `string?` | older display label, sub-field | unused |

The current leaf chokepoint (`LeafFormatter`) operates on per-call response content (`content[0].text`) plus the `ServerInstructions` head string. Catalog metadata is left untouched. With the recent `tool-output-content-blocks` sweep flipping every tool to `UseStructuredContent = true`, MCP clients that prefer rendering `structuredContent` over prose `content` blocks effectively hide the per-call leaf — wire-level evidence (via `/tmp/leaf-probe.py`) confirms the leaf is correctly emitted on the wire but client UIs that bypass prose blocks won't show it.

The MCP SDK exposes a stable post-build mutation pattern at `Program.cs:349`:

```csharp
ToolDescriptionFormatter.ApplyTriggersFromAttributes(host.Services.GetServices<McpServerTool>());
```

This pattern walks every registered `McpServerTool`, accesses the writable `tool.ProtocolTool` (a `ModelContextProtocol.Protocol.Tool` instance), and mutates fields in place after the SDK has finished its own registration. The mutations are visible on every subsequent `tools/list` response — the SDK reads `ProtocolTool` live, not from a cached snapshot. This is the chokepoint for any "extend tool identity at startup" need; we ride it for the leaf, parallel to how the trigger pass rides it for `Use when:` lines.

The user goal — explored conversationally — is to make the brand mark visible to humans regardless of which content channel a client chooses to render. Per-tool identity (Title, Description) is the surface most universally rendered across MCP clients. Per-call content branding stays in place as belt-and-suspenders.

## Goals / Non-Goals

**Goals:**
- Every built-in tool's `ProtocolTool.Title` is set to `"🌿 " + ProtocolTool.Name` (snake_case form, matching `Name`).
- Every built-in tool's `ProtocolTool.Description` is prepended with `"🌿 "`.
- Both mutations apply uniformly: every method declared on a type carrying `[McpServerToolType]` receives the leaf, regardless of return type, registration path, or trigger presence.
- Plugin tools (registered via `IToolRegistry.AddTool`, declaring type does NOT carry `[McpServerToolType]`) are NOT branded.
- Suppression mirrors the existing `--no-leaf` / `SOURCEGRAPH_NO_LEAF` knob via `LeafFormatter.Suppressed`. No new flags.
- Single chokepoint — one helper, one call site, idempotent if it ever runs twice.
- The per-call response prose leaf (`LeafFormatter.BrandFirstText` chokepoint inside `ToolMetrics.Track*`) is unchanged.
- The `ServerInstructions` head leaf is unchanged.

**Non-Goals:**
- Setting `Tool.Icons` with a real icon (data URI / hosted SVG). The leaf stays a glyph-in-text per the conversation. The `Icons` field exists and is rw; a future change can populate it without refactoring this one.
- Branding `ProtocolTool.Annotations.Title` (older sub-field). Top-level `Title` is the modern home; we use that, not the legacy slot.
- Setting `Title` on plugin tools. Plugins keep their own voice; if a plugin author wants their tool branded, they can do it themselves.
- Decorating `ProtocolTool.Name`. `Name` is the wire-level identifier used for invocation — modifying it is invasive and breaks any client pinning the name.
- Per-tool customization (different glyph per tool, different format per tool). One server, one mark.
- Solving the underlying client-rendering question (whether the user's MCP client renders Title at all). We populate the field; clients render or don't.

## Decisions

### Decision 1 — Title format: `"🌿 " + Name` (snake_case, matches `Name`)

Picked over Pascal-Case (`🌿 Find Definition`), server-prefixed (`🌿 SourceGraph: find_definition`), and uniform-brand (`🌿 sourcegraph` on every tool's title).

**Rationale:**
- `Title` is the display label clients show alongside `Name`. Matching the snake_case form keeps the visual association tight: a user sees `🌿 find_definition` and knows it corresponds to the `find_definition` they invoke.
- Pascal-Case (`🌿 Find Definition`) is more "human" but introduces a translation step (`find_definition` → `Find Definition`) the user has to mentally undo to type the tool name. Marginal win for marginal cost.
- Server-prefixed (`🌿 SourceGraph: find_definition`) is verbose and the `.mcp.json` already shows `"name": "🌿 SourceGraph"` to the user — the server name is already visible in the client UI. Re-stating it on every tool's title is redundant.
- Uniform-brand (`🌿 sourcegraph` on every title) loses per-tool context. The whole point of `Title` is to give *this tool* a display label; making them all identical defeats the field.

The user explicitly chose this format in conversation: "🌿 find_definition." We commit.

### Decision 2 — Title set unconditionally (no fallback to existing Title)

The new pass writes `Title` whether or not it was previously set. Because the SDK's `WithToolsFromAssembly()` doesn't currently populate `Title` from any attribute (verified via wire probe — `Title` is null on every tool today), there is no existing value to preserve. We don't add a `[ToolTitle("...")]` attribute system or any per-tool Title override mechanism — that's out of scope. If a future change wants per-tool titles (custom display labels distinct from `🌿 + Name`), it can add an attribute and the pass can read it before applying the default.

For now: the rule is "every built-in tool's Title is `🌿 ` + its Name." Simple, predictable, idempotent.

### Decision 3 — Description prefix: `"🌿 "` at the start, idempotent

Mirror the existing `BrandFirstText` rule on the content channel: prepend `"🌿 "` to whatever the description currently is, unless it already starts with `"🌿 "` (idempotency cover for repeated runs of the pass).

The order vs. `ApplyTriggersFromAttributes` (the trigger-append pass) is irrelevant — the leaf goes at the start of `Description`, the trigger goes at the end. Both passes can run in either order with the same result. The proposal commits to running the leaf pass *after* the trigger pass for predictability and to mirror the read-then-write pattern, but nothing breaks if the order flips.

```
Before any pass: "Find the definition of a symbol..."
After trigger pass:  "Find the definition of a symbol...\n\nUse when: looking up..."
After leaf pass:     "🌿 Find the definition of a symbol...\n\nUse when: looking up..."
```

### Decision 4 — Plugin-skip via declaring-type filter

Plugin tools registered via `Plugins.ToolRegistry.AddTool(name, description, Delegate handler)` are NOT branded. The filter is structural: `tool.Metadata?.OfType<MethodInfo>().FirstOrDefault()?.DeclaringType?.GetCustomAttribute<McpServerToolTypeAttribute>() is not null` returns `true` only for built-in tool methods (which live on types decorated with `[McpServerToolType]`).

Plugin tools either lack `MethodInfo` in `Metadata` (delegate-based registration) or have a `MethodInfo` whose declaring type is the plugin assembly's class, which is NOT decorated with `[McpServerToolType]` (that attribute is reserved for the source-graph server's own `Tools/*.cs` types).

This is the same plugin-vs-built-in distinction the existing `ToolMetrics` chokepoint relies on (plugin tools bypass `ToolMetrics` because they register through a different SDK path). We replicate the boundary structurally.

**Alternative considered:** maintain a registry of "built-in tool names" and check membership. Rejected — duplication, drift risk, and the existing `[McpServerToolType]` attribute is already the authoritative marker.

### Decision 5 — Suppression: read `LeafFormatter.Suppressed` once at pass entry

When `LeafFormatter.Suppressed` is true, the new pass is a no-op:
- `Title` is left as the SDK default (typically null; the protocol allows null `Title` and clients fall back to `Name`).
- `Description` is left untouched (the trigger pass may have mutated it, but the leaf is not added).

We read `LeafFormatter.Suppressed` once at pass entry rather than per-tool because the static is set once at process start (in `Program.cs`) and never flipped during the host's lifetime. Same idiom as the existing `LeafFormatter.Brand` short-circuit.

The post-build pass runs at startup, after `LeafFormatter.Suppressed` is set. There is no race; the assignment happens earlier in `Program.cs` than the pass.

### Decision 6 — Don't touch `Tool.Icons` (per "no icon")

The `ModelContextProtocol.Protocol.Tool` type carries an `Icons : IList<Icon>` property (rw) and `Icon` exposes `Source` (URI), `MimeType`, `Sizes`, `Theme`. A real icon — say a green-leaf SVG via `data:image/svg+xml;base64,...` — would render as an honest icon in clients that support the field.

We don't populate it. Two reasons:
- Icon support is uneven across MCP clients. Many ignore the field entirely. Putting effort into an icon that may not render in the user's client is poor return on investment for a brand-recognition feature.
- The leaf-as-text-glyph framing matches the existing voice. Introducing a real icon channel parallel to text glyphs creates an inconsistency: some surfaces render an icon, others a glyph. Picking one channel keeps the voice unified.

If a future change wants to populate `Icons` (with the same opt-out via `LeafFormatter.Suppressed`), it's a clean follow-up — the chokepoint pass we're adding here already walks every tool and is the obvious site to extend.

### Decision 7 — Belt-and-suspenders: per-call leaf stays in place

The per-call response prose leaf (`LeafFormatter.BrandFirstText` inside `ToolMetrics.Track*`) is **not** removed by this change. Three rendering channels now carry the leaf:

| Channel | Where it shows | Rendered by clients that… |
|---|---|---|
| `Tool.Title` | tool selector label | …support the `Title` field (modern MCP) |
| `Tool.Description` | hover, expanded view | …surface tool descriptions (most clients) |
| `content[0].text` (head prefix) | response prose | …render prose `content` blocks |

A client may render any subset. Leaving all three in place means the leaf surfaces in *some* channel for any reasonable client. The token cost is minor: catalog-side leaves cost ~150–200 tokens once per session; per-call leaves cost ~1 token per call. Over a 30-call session that's ~30 + 150 = ~180 tokens for full coverage. Acceptable.

### Decision 8 — `move-leaf-to-footer` becomes optional

The drafted [move-leaf-to-footer](../move-leaf-to-footer/proposal.md) change argued for moving the per-call leaf from head to footer because (a) the head elbows content openings and (b) clients hiding prose mean head-prefix is invisible anyway. Argument (b) is the load-bearing one — and this proposal addresses it at a different layer (catalog metadata). Argument (a) is purely aesthetic.

This proposal supersedes `move-leaf-to-footer` *in motivation* but doesn't directly conflict with it. If the user later decides the head-prefix is aesthetically wrong, footer move is still on the table as a cosmetic follow-up. For now, recommend parking `move-leaf-to-footer`'s artifacts until that decision lands.

## Risks / Trade-offs

- **[Risk] `Title` may not be rendered by every MCP client.** The MCP spec says clients SHOULD use `Title` for display; not every client has caught up. → **Accepted.** That's why we also stamp `Description` (older, more universally rendered). Belt-and-suspenders covers minor client compatibility variance.

- **[Risk] Existing consumers of `Tool.Description` see a `🌿 ` prefix added.** If anything downstream parses `Description` as structured input, the prefix could break it. → **Accepted, low likelihood.** `Description` is documented as human-facing prose; treating it as parseable input was already a misuse. The existing `ApplyTriggersFromAttributes` pass already mutates `Description` (appending `Use when:` lines) without breaking anything we've seen.

- **[Risk] Plugin tools look "voiceless" relative to built-ins (built-ins have `🌿 ` Title and Description; plugins don't).** → **Accepted, same rationale as `add-leaf-brand-mark` Decision 4.** Plugins are third-party voices; the leaf is the first-party brand. The asymmetry honestly reflects authorship.

- **[Risk] `--no-leaf` users lose Title entirely (it stays null/Name-fallback) rather than getting a non-leafed Title.** → **Accepted.** Today every tool's Title is null; the SDK's tooling falls back to Name in clients that handle null Title. Suppressing the leaf means we don't populate Title — same effective result as today's baseline. If a future change wants a non-branded Title (e.g. `Find Definition` Pascal-Case without the leaf), it adds a separate `[ToolTitle("...")]` attribute and reads it as a non-leaf default.

- **[Risk] The post-build pass might run twice in test scenarios that build multiple hosts (`Microsoft.Extensions.Hosting.IHost.Build()` is idempotent but the SDK's tool registration could conceivably be re-run).** → **Mitigated by idempotency.** The `Title` write is `tool.ProtocolTool.Title = "🌿 " + tool.ProtocolTool.Name` — running twice is identical to running once. The `Description` prepend has an explicit `StartsWith("🌿 ")` short-circuit.

- **[Risk] Token cost (~150–200 tokens once per session in `tools/list`).** → **Accepted.** `tools/list` is fetched once per session by most clients. The cost is amortized across every tool call in that session. Negligible relative to typical session token budgets.

- **[Trade-off] Three rendering channels carrying the leaf is "redundant by design" but might feel noisy to a user inspecting wire output.** → **Accepted.** Brand recognition via redundancy is the point. Suppression via `--no-leaf` removes all three at once for users who prefer unbranded output.

## Migration Plan

1. **Land `ToolIdentityFormatter.cs` + suppression read.** New helper in `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/`. Public API: `public static void ApplyBrandMark(IEnumerable<McpServerTool> tools)`. Filters to built-ins (declaring type `[McpServerToolType]`), reads `LeafFormatter.Suppressed`, mutates `Title` and `Description`. Idempotency on Description via `StartsWith("🌿 ")`.

2. **Add unit tests `ToolIdentityFormatterTests.cs`** in `tests/DevBitsLab.Mcp.SourceGraph.Tests/`. Cover: title-and-description mutation, idempotency on description, suppressed pass-through, plugin-skip (using a stub tool whose declaring type lacks `[McpServerToolType]`), built-in detection, no-op when suppressed.

3. **Wire the pass into `Program.cs:349`** as a second call after `ApplyTriggersFromAttributes`. One line.

4. **Extend the wire-level invariant tests.** `LeafChokepointInvariantTests` (or a sibling class) gains an integration test that goes through `tools/list` and asserts every built-in `Tool` has `Title.StartsWith("🌿 ")` and `Description.StartsWith("🌿 ")`. Plugin-registered tools do NOT.

5. **Extend the suppression matrix.** Today `ServerInstructionsWiringTests` covers four cells: (no flags) / (`--no-leaf`) / (`--no-instructions`) / (both). Add Title and Description assertions to each cell. Title is `🌿 + Name` in cell 1, null/unset in cell 2, `🌿 + Name` in cell 3, null/unset in cell 4. Description analog.

6. **Documentation.** `README.md` and `CLAUDE.md` gain one paragraph each: per-tool identity branding, `--no-leaf` opt-out unchanged. The existing per-call leaf documentation stands.

7. **Park `openspec/changes/move-leaf-to-footer/`.** Either delete the directory (it was never applied) or annotate `proposal.md` with a one-line "superseded by `add-leaf-to-tool-identity` — visibility addressed at catalog layer." User decides.

8. **Validate and verify.** `openspec validate add-leaf-to-tool-identity --strict`. `dotnet build`. `dotnet test`. Manual smoke: run the wire probe (`/tmp/leaf-probe.py`) and confirm `Title` is populated and prefixed, and the `Description` survey shows `🌿 ` everywhere on built-ins.

**Rollback strategy:** Same as `add-leaf-brand-mark`. `SOURCEGRAPH_NO_LEAF=1` at the user's end disables every leaf surface (per-call, per-tool Title, per-tool Description, ServerInstructions). For deeper rollback, the change is contained to one new file (`ToolIdentityFormatter.cs`), one call site (`Program.cs:349+1`), and the test additions — clean revert.

## Open Questions

- **Should `Title` ride per-tool customization (a `[ToolTitle("...")]` attribute that overrides `🌿 + Name`)?** Out of scope for this change. Titles like `🌿 Find Definition` (Pascal-Case) or per-tool curated labels are real future asks but require an attribute system and per-tool curation. Not pursued now; can layer on top.

- **Should the per-call leaf eventually be dropped now that per-tool branding exists?** Open. If wire-level token cost becomes a concern in some future analysis, the per-call leaf is the most defensible chokepoint to remove (per-tool branding catches the same audience). Belt-and-suspenders is the conservative current call; can revisit.

- **Does this pass need to handle `Tool.Annotations.Title` for backward compatibility with older MCP clients that read the legacy field?** Probably not — the SDK version we're using (`ModelContextProtocol.Core 1.2.0`) writes top-level `Title` per the modern spec, and the C# SDK's own behavior implicitly handles whatever fallback older clients need. If we discover a client that reads only `Annotations.Title`, we can populate it with the same value in a follow-up.

- **Should the proposal name `Tool.Icons` as an explicit non-goal documented in the spec, or just leave it out?** Keeping it as a Decision (Decision 6) so the `--no-icon` decision is captured for future readers; not adding it to the spec because the spec describes what the system does, not what it deliberately doesn't do.
