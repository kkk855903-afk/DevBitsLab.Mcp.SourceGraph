## Context

The implicit-scope annotation was a single line of formatting code in [ScopedExecution.cs:43-46](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs:43-46):

```csharp
var body = await onResolved(host).ConfigureAwait(false);
return resolution.IsImplicit
    ? $"_(scope: `{host.Scope.Id}`)_\n\n{body}"
    : body;
```

It fires when:

1. Exactly one scope host matched.
2. `ScopeResolution.IsImplicit` is true (the agent didn't pass `scope` explicitly; the router fell back to the configured `default_scope` or the lone registered scope).

Both conditions hold for the overwhelmingly common case: a single-solution `.sourcegraph.json` (or a `--solution` invocation) and an agent that doesn't bother specifying scope. So the annotation appears on virtually every response in single-solution setups.

The empirical UX cost: in the Claude VS Code extension's MCP panel, the response renders as

```
🌿 _(scope: `default`)_

<actual content>
```

The chokepoint leafs the *outermost* string — the implicit-scope annotation sits at line 1 — so the leaf appears glued to italic markdown chrome rather than to the real first line of the answer. The brand mark looks subordinated.

## Goals / Non-Goals

**Goals:**

- The implicit-scope branch returns the tool body unchanged. The leaf chokepoint then brands the body's actual first line of prose (e.g. `🌿 3 hits for 'Calculator':`, `🌿 Multiple symbols match…`).
- Preserve every other scope-tagging surface that's been deliberately wired:
  - `### scope: <id>` headers when the agent asked for multiple scopes via fan-out
  - Per-row `scope: <name>` / `scope: [a, b]` annotations in merged result sets
  - Resource-card scope tags in `Resources/GraphResources.cs` (different render surface, different audience)

**Non-Goals:**

- Removing the *signal value* of "which scope answered?" entirely. Agents can still introspect via `list_scopes`, the JSONL usage log, OTel `mcp.tool.scope` tag, or — for explicit multi-scope queries — the per-row tags that already exist.
- Adding a footer-style replacement annotation. Footer placement was discussed and is plausible (`{body}\n\n_— scope: \`default\`_`), but defers reverberation cost: every response grows by ~12 tokens just to communicate "yes, default scope answered, like usual." If footer signalling becomes important later, it's a separate change.
- Renaming or restructuring `ScopeResolution`. `IsImplicit` stays on the record; future consumers (telemetry, footer) might want it.

## Decisions

### Decision 1 — Drop the annotation in the implicit single-host branch only

The change touches one line. No expansion to multi-scope or explicit-scope paths.

**Rationale**: the multi-scope fan-out path (`### scope: <id>` per host) and per-row scope tags are *responses to explicit agent requests* — when an agent passes `scope = "*"`, structuring per-scope output is the answer's contract, not chrome. Only the implicit-default annotation is a "by the way, you ran against `default`" note that the agent didn't ask for.

### Decision 2 — Keep `ScopeResolution.IsImplicit` on the record

The field is part of a `public sealed record` in `Scoping/ScopeRouter.cs`. Removing it is a breaking change for any consumer (currently none in-tree, but the record is `public`). Since it's a small bool field and may host future telemetry or a footer renderer, leaving it costs nothing and preserves the option.

### Decision 3 — `.mcp.json` cleanup lands in the same commit

Two small `.mcp.json` edits ride alongside:

1. `"sourcegraph"` → `"SourceGraph"` — the Claude extension renders the config-key string as the chat heading after title-casing. PascalCase already-cased keys avoid sanitization surprises (an earlier experiment with `"🌿 SourceGraph"` showed the extension replaces emoji and whitespace with `_`, producing `[_SourceGraph__find_references]` headings).
2. `${workspaceFolder}/` placeholder removal — the extension launches the server with `cwd` = repo root, so relative paths work without depending on the `${workspaceFolder}` token expansion. Removing the placeholder simplifies the config and keeps it portable across clients that might not expand it.

These are config-hygiene tweaks that share the same UX-iteration motivation as the spec change; bundling them avoids a stale `.mcp.json` sitting in a separate commit with no visible reason.

## Risks / Trade-offs

- **[Risk] Agents that relied on the annotation for "which scope answered?"** → Mitigation: `list_scopes` is one tool call away and gives a definitive answer for the whole server. The OTel signal `mcp.tool.scope` and the JSONL usage log preserve the same information offline. No agent in-tree consumes the annotation programmatically; the only consumer was the human reading the chat, and the screenshot showed the annotation was visually subordinated anyway.

- **[Risk] Future telemetry or footer renderer wants `IsImplicit`** → Field stays on `ScopeResolution`. Trivial to wire a consumer later.

- **[Trade-off] Loses one weak signal in exchange for a stronger one (the leaf brand mark gets prime first-line real estate)**. Net UX positive in the rendering target we have evidence for (Claude VS Code extension); neutral elsewhere.

- **[Risk] `.mcp.json` rename breaks any client that hard-coded the lowercase `"sourcegraph"` key.** → Local repo `.mcp.json` only; no published config keyed off this. Personal/team `.mcp.json` files are user-managed and unaffected.

## Migration Plan

The implementation is a one-line code change plus a one-scenario spec edit. There is no rollout / rollback dance:

1. Land the code change in [ScopedExecution.cs](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs).
2. Land the spec scenario edit.
3. Land the `.mcp.json` cleanup.

**Rollback strategy**: revert the commit. Three lines of code, one scenario, two `.mcp.json` keys/placeholders.

## Open Questions

- **Should the annotation move to a footer instead?** Currently no — the cost (~12 tokens per response) outweighs the value. Revisit if/when an agent or workflow surfaces a need.
- **Should `Resources/GraphResources.cs` get the same treatment?** Probably not. Resource cards have different ergonomics and a different audience (manual inspection in resource panels), and the header tag there is purely informative without a brand-mark adjacency problem. Out of scope.
