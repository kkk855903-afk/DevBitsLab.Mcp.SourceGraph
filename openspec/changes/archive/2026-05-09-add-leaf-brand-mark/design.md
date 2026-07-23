## Context

Today every tool method in `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/*.cs` returns a markdown string assembled by hand (e.g. `Found 3 match(es) for 'X':\n- ...`). All twenty-something built-in tools route their bodies through `ToolMetrics.TrackAsync` / `TrackSync` (in `Observability/ToolMetrics.cs`) — that wrapper is the only existing single chokepoint that sees every built-in tool's final string before it ships to the MCP client.

Plugin tools register through `Plugins/ToolRegistry.AddTool` and do **not** route through `ToolMetrics`. Their handler delegates are wrapped by `McpServerTool.Create` directly. This asymmetry matters for any cross-cutting concern that wants to touch tool output, including this one.

The `ServerInstructions` blurb is a separate static template (`ServerInstructions.Template`), published once in the `initialize` response, with an existing `--no-instructions` / `SOURCEGRAPH_NO_INSTRUCTIONS` suppression mechanism we should mirror.

The user intent: a small, cute, token-economic brand mark — 🌿 — visible on every response from this server. The design choices below are about *where* the mark gets stamped, *which* responses it stamps, and *how* the suppression knob fits without coupling concerns we want to keep separate (telemetry vs presentation).

## Goals / Non-Goals

**Goals:**
- A single `🌿 ` glyph + space prefixes the first line of every built-in tool response.
- The leaf appears on success, empty-result, and error-path responses alike. Meaning: "sourcegraph spoke."
- The leaf prefix is applied in exactly one place per call path — no `🌿` literals scattered through tool bodies.
- The `ServerInstructions` template is leafed too, so the agent learns `🌿 → sourcegraph` from the initialize handshake.
- Lead-in lines on hot tools (`find_definition`, `find_references`, `find_by_annotation`, `search_symbols`, `module_summary`, `impact_of_change`, `neighborhood`, history/scope tools) get tightened in the same pass for token economy.
- Suppression mirrors the existing `--no-instructions` ergonomics: `--no-leaf` flag and `SOURCEGRAPH_NO_LEAF=1` env var, both honoured.

**Non-Goals:**
- Decorating plugin tool output with the leaf. Plugins are third-party voices; the leaf is the source-graph first-party brand. Branding plugin output would lie about authorship.
- Adding a runtime icon/badge field to MCP `tools/list` or `initialize`. The protocol has no such field; this design uses only what the spec already exposes (response text).
- Per-tool customisation of the leaf (no per-tool "use a different glyph" hook). One server, one mark.
- Localisation. The leaf is a Unicode glyph; it has no language.
- Validation of how downstream clients render emoji. We pick a single-codepoint emoji that defaults to colored rendering; if a client strips emoji it strips emoji.

## Decisions

### Decision 1 — Glyph: 🌿 (U+1F33F, "herb")

Picked over alternatives 🍃 (leaf-fluttering), 🌱 (seedling), 🌳/🌲 (trees), and BMP candidates (☘, ❦, ❧).

**Rationale:**
- ~1 token in typical BPE tokenizers, defaults to colored rendering on every modern font (Apple, Google Noto, Twemoji, Segoe).
- Visually branches — a fitting metaphor for a graph tool. 🍃 is passive; 🌱 is "starter"; 🌳 reads as a heavyweight icon, not a mark.
- BMP options like ☘ U+2618 are 3 bytes vs 4, theoretically cheaper, but require a `U+FE0F` variation selector to render colored — and that selector costs an extra token in practice, erasing the saving while introducing a font-rendering trap on terminals that drop VS16.

### Decision 2 — Layout: inline prefix on the first line (Layout C)

Picked over a header-with-blank-line (Layout A), info-rich header (Layout B), and footer (Layout D) — all explored in conversation.

**Rationale:**
- Zero extra lines per response. The leaf becomes ornamentation on the existing first line: `🌿 3 hits for 'X':` rather than a header that costs a line break.
- Brand discoverable: every response leads with the mark. The pattern `🌿 → sourcegraph` settles in after a couple of calls.
- Token-economic: pairs naturally with lead-in tightening (Decision 5) since both touch the first line.
- Information-poor by choice. Layout B (`🌿 find_definition · backend · 3 hits`) carries useful context but adds tokens and makes the leaf an info-strip rather than a brand mark. The MCP client already shows the tool name in the chat; we don't need to repeat it.

### Decision 3 — Chokepoint: `ToolMetrics.TrackAsync/TrackSync`, via a thin presentation helper

Approach: introduce `Observability/../Presentation/LeafFormatter.cs` (or `Tools/LeafFormatter.cs`) — a static helper exposing a single method:

```csharp
public static string Brand(string toolResult)
    // Prepends "🌿 " unless suppressed; idempotent if a result already starts with the mark.
```

Inside `ToolMetrics.TrackAsync` and `TrackSync`, after `body()` completes successfully **and after** response-byte measurement (so telemetry continues to record the *unbranded* size — the leaf is presentation, not payload), we wrap the result:

```csharp
result = await body().ConfigureAwait(false);
return LeafFormatter.Brand(result);
```

Error paths: the existing `catch` rethrows. The MCP SDK marshals the exception into an error response; the SDK formats that envelope, not us. The leaf does **not** appear on thrown exceptions because we never see their final shipped text. (Tools that catch their own errors and return a string keep the leaf, since their string flows through the wrapper.) Documented as a known asymmetry; if it matters later, we add a leaf to thrown errors via an outer wrapper.

**Alternatives considered:**
- *Inline at every tool method body* — no central enforcement, easy to drift. Rejected.
- *Wrap each `McpServerTool` at registration time (decorator)* — cleanest for catching plugin tools too, but requires intercepting `McpServerTool.InvokeAsync`, which the SDK doesn't expose as a hook. Possible via reflection or wrapping the underlying handler delegate, but more code and more failure modes than the gain warrants. Rejected for now; revisit if plugin tools later need the leaf.
- *Embed in `ToolMetrics.Record` or `Telemetry`* — couples branding to telemetry concepts. The presentation helper sits next to `ToolMetrics` but is its own type, so each concern stays named.

### Decision 4 — Plugin tools: not leafed (for now)

Plugin tools register via `Plugins/ToolRegistry.AddTool` and bypass `ToolMetrics`. They receive **no** leaf prefix.

**Rationale:**
- Leaf = first-party `sourcegraph` voice. Plugins are third-party extensions; stamping their output with our mark misattributes authorship.
- No code change needed in `ToolRegistry` — the design naturally excludes plugins.
- If a plugin author wants to leaf their own output, nothing prevents them; that's their editorial choice.

If a future change wants to brand plugin output (e.g. with a different glyph or a `[plugin-prefix]` tag), it'd be a separate proposal.

### Decision 5 — Lead-in tightening, scoped to the leaf-touching commit

Every tool we modify to add the leaf prefix is a tool whose first line we're already touching. While there, tighten verbose phrasing:

| Today | After |
|---|---|
| `Found 3 match(es) for 'X':` | `🌿 3 hits for 'X':` |
| `No definition found for 'X'.` | `🌿 No matches for 'X'.` |
| `0 symbol(s) carry [Foo]:` (et al) | `🌿 No symbols carry [Foo].` |
| `{n} symbol(s) carry [Foo]:` | `🌿 {n} symbols carry [Foo]:` |

Aggregate measured saving: ~4–5 tokens per hot-tool call. Across a 30-call session, hundreds of tokens. The leaf adds back ~1 token. Net per-session win, plus brand visibility.

**Boundary:** if a tool's output is already terse (`pong @ <iso-time>`, `list_scopes` table headers, `usage_stats` table), we leaf it but don't rewrite. No drive-by refactors of well-formed output.

### Decision 6 — Suppression: mirror `--no-instructions`

Two paths, both honoured:
- CLI flag `--no-leaf` on `sourcegraph-mcp serve`
- Env var `SOURCEGRAPH_NO_LEAF=1` (truthy values: `1` exact-match, or `true` case-insensitive — same convention as `SOURCEGRAPH_NO_INSTRUCTIONS`)

Suppression turns off **both** the per-response prefix and the leaf on `ServerInstructions.Template`. No half-states.

A `LeafFormatter` static field is set once at startup (in `Program.cs`, alongside the existing instructions-suppression read). When suppressed, `Brand(s)` returns `s` unchanged — zero overhead path.

Why mirror, not extend? Two suppression knobs are simpler than a "strip all server flair" mega-flag, and they fail independently — turning off one doesn't accidentally turn off the other. And the user might genuinely want one but not the other (e.g. ship the instructions but suppress the leaf because their terminal renders emoji as monochrome boxes).

### Decision 7 — `ServerInstructions.Template` carries the leaf inline

Append `🌿 ` to the first character of `Template`, suppressed alongside the per-response prefix:

```csharp
public const string Template =
    """
    🌿 This MCP server exposes a live code source graph...
    """;
```

When `--no-leaf` is set we apply the same prefix-stripping suppression at publish time. Or we maintain two constants. The suppression-strip approach is simpler — one source of truth, runtime decides whether to ship the prefix.

### Decision 8 — Tests assert on post-leaf substring, not exact strings

Existing tests under `tests/` that assert `result.Should().Contain("Found ")` need to migrate to `Should().Contain("hits")` or similar (substring after the lead-in tightening). Tests that pin `result.Should().StartWith("Found ")` need to either:
- Move to `Should().StartWith("🌿 ")` (test the leaf invariant), or
- Move to `Should().Contain("hits")` (test the content, not the prefix).

Recommendation: introduce one test in `tests/.../ServerTests/LeafFormatterTests.cs` that pins the invariant — every response from a tracked built-in tool starts with `🌿 ` unless suppressed — and let other tests assert content semantics rather than exact starting strings. That separation keeps the leaf as one rule in one test, not a thousand string-match updates.

## Risks / Trade-offs

- **[Risk] Tests that pin exact response strings break en masse.** → Migration: enumerate them upfront via `grep -rn 'Found.*match' tests/`, decide per-test whether to update for the new wording or move to substring asserts. Treat this as task one, before touching any production code, so the change has a clean breakpoint.

- **[Risk] Some terminals / log piping render `🌿` as `?` or monospaced fallback.** → Mitigation: `--no-leaf` / `SOURCEGRAPH_NO_LEAF=1` is the escape hatch. Document it in `README.md` next to `--no-instructions`. If we hear from users that this is common we can lower the bar (e.g. honor `NO_COLOR` env).

- **[Risk] The wrapper applies the leaf *after* response-byte measurement, so `usage_stats` and OTel `mcp.tool.response_bytes` undercount by ~4 bytes (UTF-8 size of `🌿 `).** → Accepted. Telemetry tracks payload, branding is presentation. The discrepancy is uniform across tools, ~0.4% on a typical 1KB response, and aligns with the conceptual split. If users want byte-exact accounting, document the offset rather than recomputing.

- **[Risk] Plugin tools look "voiceless" relative to built-ins (built-ins are leafed, plugins aren't).** → Accepted. That asymmetry honestly reflects authorship. The asymmetry can be revisited if/when plugin authoring patterns mature.

- **[Risk] Errors thrown by tools (vs returned as error strings) skip the leaf because the SDK formats the error envelope.** → Accepted as a known asymmetry. Documented in design. Most user-facing error paths in this codebase return a string (`return $"No symbol found for '{symbol}'."`) and so are leafed. Genuine exceptions are rare and the SDK's envelope is recognisable already.

- **[Trade-off] Layout C buys "smallest footprint" at the cost of "no per-call info." Layout B (info-rich header) would carry tool name + scope + hit count for the cost of one extra line and ~5 tokens.** → Accepted. The MCP client already surfaces tool name; users who want per-call detail can call `usage_stats`. The leaf is a brand mark, not a status line.

- **[Trade-off] Two suppression knobs (`--no-instructions`, `--no-leaf`) instead of one umbrella `--no-flair`.** → Accepted. Independent failure modes, predictable composition, no surprise behavior.

## Migration Plan

1. **Enumerate test breakage upfront.** `grep -rn '"Found ' tests/` and similar — list every assertion that pins exact response text, and decide per-assertion whether it stays string-exact (and updates) or migrates to substring/invariant style.
2. **Land `LeafFormatter` + the `--no-leaf` knob first**, before touching call sites. CI green on a no-op pass (the helper exists, but `ToolMetrics` doesn't yet call it).
3. **Wire `ToolMetrics.TrackAsync`/`TrackSync` to call `LeafFormatter.Brand`.** All built-in tools start emitting the leaf in one commit. Run the test suite — assertions break exactly where the enumeration in step 1 said they would.
4. **Tighten lead-in lines tool by tool**, updating co-located tests in the same commit. Order by frequency of use (`find_definition`, `find_references`, `search_symbols` first).
5. **Leaf the `ServerInstructions.Template`** and add the suppression check at publish time (in the place that already handles `--no-instructions`).
6. **Document in `README.md` and `CLAUDE.md`** — one paragraph each: what the leaf means, how to suppress it.

**Rollback strategy:** Setting `SOURCEGRAPH_NO_LEAF=1` at the user's end disables the change without redeployment. If a deeper rollback is needed, the change is contained to ~1 helper, ~2 chokepoint lines in `ToolMetrics`, and the lead-in trims — a clean revert.

## Open Questions

- **Does `ping` truly belong in scope?** Currently `pong @ <iso-time>` — leafing it gives `🌿 pong @ <iso-time>`. The proposal says yes (uniform rule, no exceptions). Worth a sanity confirmation: anyone parsing `ping` output would break. Probably no one does. Confirm and proceed.
- **`usage_stats` table format — leaf the table header line, or the line above it?** The current first line is something like `usage stats since YYYY-MM-DD…` — easiest to leaf. But if we want the leaf-and-stats-line to *coexist* (so the leaf doesn't look like part of the table), maybe a tiny prefix line. Decide during step 4.
- **Should `LeafFormatter.Brand` strip a leading `🌿 ` from the input first** (for idempotency, in case a tool happens to emit one in its own body)? Cheap to add, defensive. Probably yes.
- **Worth memoizing `_isSuppressed` once at startup vs reading the env var on every call?** Yes — set the static field in `Program.cs` once, never re-read. Documented as the implementation note for step 2.
