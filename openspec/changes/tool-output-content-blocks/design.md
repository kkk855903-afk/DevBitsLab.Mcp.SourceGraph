## Context

Today every built-in MCP tool follows this shape:

```csharp
[McpServerTool] [Description("...")] [ToolTrigger("...")]
public static Task<string> FindDefinitionAsync(...) =>
    ToolMetrics.TrackAsync("find_definition", new { ... }, () =>
        ScopedExecution.RunAsync(router, scope, async host => {
            var sb = new StringBuilder();
            sb.AppendLine($"{hits.Count} hits for '{symbol}':");
            // ...
            return sb.ToString();
        }, ct));
```

The `Task<string>` return is auto-wrapped by the SDK into a single `TextContentBlock`. That means:

- **Every byte of every response is rendered to the user.** There's no way to ship debug/metadata invisibly to the model.
- **There's no typed result.** Agents that want to chain calls have to parse markdown.
- **There's no `resource_link` to our existing graph resources** at `graph://symbol/<id>`.
- **Progress notifications from slow tools are unreachable.**

The SDK spike (against ModelContextProtocol 1.2.0) confirmed every richer surface we want is supported by `[McpServerTool]` attribute methods natively — no need to drop into `McpServerTool.Create`. Specifically:

| Surface | Wire support | C# SDK API |
|---|---|---|
| Multi-content | `Task<IEnumerable<ContentBlock>>` or `Task<IReadOnlyList<ContentBlock>>` | Marshalled as-is |
| `CallToolResult` | `Task<CallToolResult>` | Marshalled as-is — full control of `Content`, `StructuredContent`, `IsError`, `Meta` |
| `outputSchema` | `[McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(MyDto))]` | Schema generated from type, validated against `"type":"object"` |
| `Annotations` (audience, priority) | `ContentBlock.Annotations = new Annotations { Audience = [Role.Assistant], Priority = 0.2 }` | Available on every block type |
| `ResourceLinkBlock` | `new ResourceLinkBlock { Uri, Name, Title, Description, MimeType, Size }` | No URI-scheme constraint |
| Progress | `IProgress<ProgressNotificationValue>` parameter, auto-injected | Silently no-ops when client didn't pass `progressToken` |

The sole gotcha — the same one that bit us with the `initialize` vocabulary regression — is that **`StructuredContent`, `Meta`, and any other field serialized through the SDK's source-gen `McpJsonUtilities.JsonContext` rejects anonymous types at runtime**. Every payload we put there must be a real DTO type or `JsonElement`/`JsonObject`.

## Goals / Non-Goals

**Goals:**

- Built-in tool responses become `IReadOnlyList<ContentBlock>` (or `CallToolResult` when richer fields are needed). The shape is the wire shape; we no longer rely on the SDK auto-wrapping.
- Every tool whose result is naturally typed ships `structuredContent` alongside renderable prose, with an `outputSchema` declared on the tool registration. Agents can `JSON.parse(result.structuredContent)` directly — no markdown parsing.
- Tools whose rows are individual symbols / files emit a `ResourceLinkBlock` per row, pointing at the existing `graph://symbol/<id>` (or new `graph://file/<path>`) URI. Clients with richer UI render expandable cards; clients without lose nothing.
- Per-call diagnostics that are useful to the model but noise to the human (resolved scope, latency, "X of N rows truncated") move into `audience: [Role.Assistant]` content blocks. The user's visible response shrinks; the model's view stays complete.
- The leaf brand-mark contract is preserved: `LeafFormatter.Brand` evolves to brand the *first* `TextContentBlock`'s text, not a concatenated string.
- Plugin-tool authors keep their existing single-string registration path. The richer return types are an option, not a requirement.
- A defensive runtime guard catches anonymous-type usage in `StructuredContent` / `Meta` at request time, not at wire serialization (where the failure mode is opaque).

**Non-Goals:**

- Removing `Task<string>` support entirely from the codebase. `PingTool.Ping()` and `ToolMetrics.TrackSync<T>` continue to handle the string case for trivial responses and for `usage_stats`/`graph_stats`.
- Forcing every tool to ship `structuredContent`. Some tools (`ping`, `graph_stats`) have results so terse that the structured surface adds no value.
- Touching `Resources/GraphResources.cs` semantics. We *link* to it; we don't change what it serves.
- Designing new graph URIs from scratch. We use what `GraphResources.cs` already exposes; if a row needs a URI that doesn't exist yet, we add a thin synthesiser, not a new resource handler.
- Implementing G (progress notifications) in this change. G is independent and ships as `report-progress-on-slow-tools`; including it here would bloat scope.

## Decisions

### Decision 1 — `IReadOnlyList<ContentBlock>` is the default; `CallToolResult` only when needed

Most tool methods return `Task<IReadOnlyList<ContentBlock>>`. The list contains:

- One leading `TextContentBlock` with the prose summary + body markdown (this is what the leaf chokepoint brands).
- Zero or more `ResourceLinkBlock`s, one per result row.
- Zero or one trailing `TextContentBlock` with `Annotations.Audience = [Role.Assistant]` carrying agent-only metadata.

When a tool needs `structuredContent` or wants to set `IsError` distinctly, it returns `Task<CallToolResult>` instead. Internally, `CallToolResult.Content` is the same list; `StructuredContent` is the typed DTO; `IsError` is the explicit error flag (replaces today's "the body string starts with 'error:'" convention).

**Rationale**: most call sites in the repo only need the content list; the few that need structured output are the ones we want explicit. Keeping the simpler return type for simple tools reduces the migration surface.

**Alternative considered**: every tool returns `CallToolResult`. Cleaner uniformity at the cost of every tool body constructing a result object even when it just wants prose. Rejected — verbosity per-call exceeds the value.

### Decision 2 — `ToolMetrics.TrackAsync<T>` becomes generic; brand-mark applies to first `TextContentBlock`

The chokepoint's signature evolves:

```csharp
// Before
public static async Task<string> TrackAsync(
    string toolName, object? args, Func<Task<string>> body);

// After (generic — the body's return type flows through)
public static async Task<T> TrackAsync<T>(
    string toolName, object? args, Func<Task<T>> body)
    where T : class;  // ContentBlock list, CallToolResult, or string
```

Inside, the chokepoint inspects the body's result and applies the leaf:

- For `string`: prepend `"🌿 "` (today's behavior — preserved for the few remaining string-returning tools and plugin tools).
- For `IReadOnlyList<ContentBlock>`: replace the *first* `TextContentBlock` with one whose text is `"🌿 " + original.Text`. Other content items unchanged. Skip if no `TextContentBlock` is in the list (rare — would mean a tool emitted only resource_links, which is suspicious).
- For `CallToolResult`: same as the list case, applied to `result.Content[0]` if it's a `TextContentBlock`.

`LeafFormatter` gets two overloads matching the new shapes; the existing `Brand(string)` is unchanged.

**Rationale**: keeps the leaf rule in one place, preserves the contract from `add-leaf-brand-mark`, scales to the new content shapes.

### Decision 3 — Typed DTOs for `structuredContent`, generated `outputSchema`

For every tool that ships `structuredContent`, we define a typed record (DTO) — never anonymous types — that mirrors the prose. The SDK uses `OutputSchemaType` to generate the JSON Schema for the tool's `outputSchema` field on `tools/list`.

Example for `find_definition`:

```csharp
public sealed record FindDefinitionResult(
    IReadOnlyList<FindDefinitionHit> Hits);

public sealed record FindDefinitionHit(
    string Fqn,
    string Kind,
    string FilePath,
    int Line,
    int Column,
    string? Signature,
    string? XmlSummary);

[McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindDefinitionResult))]
[Description("...")]
public static Task<CallToolResult> FindDefinitionAsync(...)
```

DTOs live in a new `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/Output/` namespace and are decorated with `[JsonSerializable]` against a per-project `JsonSerializerContext` so the SDK's source-gen path can write them without falling back to reflection.

**Rationale**: typed DTOs are self-documenting, IDE-discoverable, and avoid the anonymous-type landmine we already hit. Generated `outputSchema` keeps the wire-level schema in lockstep with the C# type — no manual schema authoring drift.

**Constraint per spike**: top-level `outputSchema` must be `"type":"object"`. Tools whose natural output is an array (most of them) wrap the array in an object: `FindDefinitionResult { Hits: [...] }`, not `IReadOnlyList<Hit>` directly.

### Decision 4 — `audience: [Role.Assistant]` for diagnostics, not for the leaf

The audience-restricted path was tempting for the leaf (the brand mark could become invisible-to-user). But:

- Removing the leaf from the user-visible response loses the "this came from sourcegraph" signal *for the human reading the chat*. We chose the leaf in the first place specifically because clients didn't reliably attribute server identity. Walking it back undermines that goal.
- The leaf doing nothing visible-to-user is hard to justify spec-wise. "Brand mark exists for the model's benefit, invisibly" is a peculiar contract.

So:

- **The leaf stays in the first `TextContentBlock`** (the user-visible one).
- **Audience-restricted blocks carry agent-only metadata**: resolved scope id, latency, cache-hit info, "X of N rows omitted due to limit" notices, edge-kind fallback warnings.

The result-content list shape becomes:

```
[
  TextContentBlock { Text = "🌿 4 references to **X** (class):\n…\n| Kind | Loc |\n| call | …" },
  ResourceLinkBlock { Uri = "graph://symbol/12345", … },
  ResourceLinkBlock { Uri = "graph://symbol/12348", … },
  TextContentBlock {
    Text = "_meta: scope=`default`, latency_ms=12, edge_kind=calls (default)_",
    Annotations = { Audience = [Role.Assistant], Priority = 0.2 }
  }
]
```

### Decision 5 — Resource URIs come from a single helper, decoupled from `Resources/GraphResources.cs`'s handlers

Tools don't enumerate resources programmatically (the SDK doesn't expose an in-tool API for that, per spike). Tools just construct opaque URIs.

A new `Resources/GraphResourceUris.cs` helper provides:

```csharp
public static class GraphResourceUris
{
    public static string Symbol(long id) => $"graph://symbol/{id}";
    public static string File(string path) => $"graph://file/{Uri.EscapeDataString(path)}";
}
```

Used by tools to construct `ResourceLinkBlock.Uri` values. Used by `Resources/GraphResources.cs` to validate inbound URIs against the same scheme. Single source of truth for the URI shape.

**Rationale**: tools and resource handlers must agree on URI structure. Centralising the helper means a future change to URI shape lands in one place.

### Decision 6 — Anonymous-type guard at the chokepoint

`ToolMetrics.TrackAsync<CallToolResult>` adds a defensive check: when `body()` returns a `CallToolResult`, before yielding it to the SDK, the chokepoint inspects `result.StructuredContent?.GetType()` and `result.Meta?.GetType()` for compiler-generated anonymous types (`Type.Name.StartsWith("<>f__AnonymousType")`). If found, the chokepoint throws `InvalidOperationException` with a message pointing to the offending tool name and field.

**Rationale**: the anonymous-type-vs-source-gen failure is opaque (the error fires deep in `JsonSerializer.Serialize` with a stack trace through SDK internals). Catching it at the chokepoint surfaces the bug at request time with a clear message, against the actual tool name. Saves a debugging session per occurrence. Same pattern we'd want anywhere user code feeds the SDK's source-gen path.

**Alternative considered**: a Roslyn analyzer that flags anonymous types on `CallToolResult.StructuredContent` assignment at compile time. Stronger guarantee, but more infrastructure. Defer to a follow-up if the runtime guard proves insufficient.

### Decision 7 — Plugin-tool registration unchanged; `IToolRegistry.AddTool` keeps `Delegate` signatures

Plugin tools register through `Plugins/ToolRegistry.AddTool(string toolName, string description, Delegate handler)`. The `Delegate` accepts handlers returning `string`, `Task<string>`, or — newly — `IReadOnlyList<ContentBlock>` / `Task<IReadOnlyList<ContentBlock>>` / `CallToolResult` / `Task<CallToolResult>`. The SDK's marshalling determines the wire shape.

**No changes to `ToolRegistry`** — the existing API accepts the richer return types without modification (the SDK's `McpServerTool.Create(handler)` already supports them per spike, finding 1).

Plugin authors who want structured output can return `CallToolResult` and set `[McpServerToolAnnotation]` properties as needed. Plugin authors who want simple text continue to return `string` / `Task<string>`. The leaf doesn't apply to plugin output (still — that's the brand-mark contract from `add-leaf-brand-mark`).

### Decision 8 — Migration order: helpers → vertical slice → sweep

1. **Land the helpers** (no production callers): `Format.AppendTable` is from `polish-tool-output-markdown` (already merged). Add `LeafFormatter.BrandFirstText`, generic `ToolMetrics.TrackAsync<T>`, anonymous-type guard, `GraphResourceUris`, the empty `Tools/Output/` DTO namespace, and a `JsonSerializerContext` for the DTOs. CI green.
2. **Convert one vertical slice** — `find_definition` is the natural candidate (high-traffic, naturally typed result, exercises every new surface: prose, structuredContent, resource_links, audience block). Land + scenario tests. **Pause for review** — proves the pattern.
3. **Sweep the remaining tools** in batches of 3–4 per commit. Each tool: define its DTO, attribute the method with `OutputSchemaType`, populate `CallToolResult` in the body. Co-located test for each.
4. **Migrate test infrastructure**: tests that currently inspect `string` results migrate to inspect `CallToolResult.Content` and `CallToolResult.StructuredContent`. The leaf-invariant test extends to cover both code paths during the migration; it shrinks back to one path once the sweep completes.
5. **Documentation pass**: README + CLAUDE.md notes about structured output and resource_link consumption.
6. **Final verification**: full real-MCP-client roundtrip against a converted tool, eyeball the chat rendering in the Claude extension.

## Risks / Trade-offs

- **[Risk] Anonymous-type sourcegen JsonContext failures during the sweep.** → Anonymous-type guard from Decision 6 catches them at request time. DTOs are mandatory per Decision 3.

- **[Risk] Test infrastructure churn.** Every test that asserts on `string` tool output becomes a multi-shape inspection. → Lots of small test edits during the sweep. The leaf change paid this tuition partly; this change pays it again at scale. Budget time.

- **[Risk] Older MCP clients ignore `structuredContent` / `resource_link` blocks** and only render text. → Accepted. The text content is self-sufficient. Newer clients gain richer rendering; older clients see no regression.

- **[Risk] `outputSchema` constraint (top-level must be `"type":"object"`).** → Mitigation: every tool's DTO is a wrapping record (`{ Hits: [...] }`, `{ References: [...] }`) — never a bare collection. Spec'd explicitly in the requirement scenarios.

- **[Risk] DTO drift vs. prose.** A tool's prose says "10 hits" but `structuredContent.Hits.Count = 9` because of a bug. → Mitigation: derive prose count from the same collection that populates `structuredContent`. New scenario tests assert `structuredContent.Items.Count == prose-row-count`.

- **[Risk] Token cost regression.** `structuredContent` duplicates information already in prose. → Accepted with caveat: agents that consume `structuredContent` programmatically usually skip parsing the prose, so per-call tokens go up but per-session tokens trend down or flat. We'll measure once a few tools are converted.

- **[Risk] Resource link URIs that don't resolve.** A tool emits `graph://symbol/12345` but the resource handler doesn't know symbol 12345. → Constraint: tools may only emit URIs for symbols whose IDs they just queried out of the graph. No speculative URIs. Test scenario asserts every emitted resource_link resolves successfully via `GraphResources.GetSymbol`.

- **[Risk] Plugin-author confusion** if they see built-in tools using `CallToolResult` and assume they must too. → README + CLAUDE.md notes clarify that plugins keep their simple `string` path; richer return types are optional. Can revisit if support tickets surface.

- **[Trade-off] Larger surface area to maintain.** Each tool gains a DTO definition, a schema annotation, a slightly more complex body. → Accepted. The capability gain is durable; the cost is per-tool one-time.

- **[Trade-off] Generic `TrackAsync<T>` is harder to read than the original concrete signature.** → Mitigation: clear inline comments at the chokepoint explain the type-check ladder. The benefit is one chokepoint covering every tool's return shape.

## Migration Plan

The migration plan is the task list. The big-rock structure:

1. Helpers (`LeafFormatter.BrandFirstText`, generic `TrackAsync<T>`, anonymous-type guard, `GraphResourceUris`, `Tools/Output/` DTOs + `JsonSerializerContext`).
2. Vertical slice: `find_definition`. Pause for review.
3. Sweep: `find_references`, `search_symbols`, `find_by_annotation`, `list_symbols_in_file`, `list_callers`, `list_callees`, `find_implementations`, `list_members`, `semantic_search`, `module_summary`, `impact_of_change`, `find_diagnostics`, `recent_changes`, `list_tests_for`, `who_authored`, `list_generated_files`, `list_scopes`. Group by similarity — the symbol-list tools are template work; semantic_search has its own DTO shape; diagnostics/changes have unique DTOs.
4. Audience-restricted metadata blocks across all converted tools.
5. README + CLAUDE.md updates.
6. Final verification + spec sync.

**Rollback strategy**: each per-tool conversion is independently revertable. Helpers and DTOs can sit unused after a revert without breaking the build. The chokepoint generic conversion is the riskiest piece — it's revertable but would require restoring the old single-string path; the per-tool reversion would unblock first.

## Open Questions

- **Should `who_authored` (single-string-returning today) get `structuredContent` even though its prose is one line?** Probably yes — the typed `{ author, sha, authored_at, blamed_lines }` record is small and useful for chained tool calls. Decide during the sweep.
- **Should `graph_stats` return `structuredContent` with the typed counts?** Yes — `{ files, symbols, references, edges }` is the canonical shape. Trivial DTO.
- **Should `usage_stats` return `structuredContent`?** Less obvious. Its existing markdown table is useful as-is; an agent that wants the typed snapshot can call `ToolMetrics.Snapshot()` indirectly via a new tool. Defer — do not include in this change.
- **Resource URI for FILES: `graph://file/<path>` — should we resolve this against the indexed `files` table, or just emit any path? Emitting any path lets us link to non-indexed files, but resolving requires a graph lookup.** Lean toward resolved-only (link to files we've actually seen) so the resource handler can produce a useful card. Decide during implementation.
- **Anonymous-type guard scope: `StructuredContent` and `Meta` only, or also nested fields inside?** Probably just the top-level fields — nested anonymous types within a typed DTO would already fail at compile time (records don't have anonymous-type properties). The risk is only at the boundary where user code hands an `object` to the SDK.
- **Should `IsError = true` be set on tool responses that today return `"No matches for 'X'."`?** No — "no matches" is a successful response, not an error. `IsError` reserved for actual failure paths (degraded scope, exception caught at the tool body, etc.).
