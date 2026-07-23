## Why

The `add-leaf-brand-mark` change just landed a `🌿 ` prefix on every built-in tool response. In production rendering inside the Claude VS Code extension's MCP panel, the leaf gets visually subordinated: the response's first line is already occupied by an italic `_(scope: \`default\`)_` annotation our own code emits when scope is implicit ([ScopedExecution.cs:45](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs:45)). The chokepoint then prepends the leaf to *that* line, so the chat shows `🌿 (scope: default)` in italic markdown chrome — the brand mark looks like flair on a metadata strip rather than a signal that the source-graph spoke. The agent (and the human reading along) has to scan past the italic line to find the substantive answer ("Multiple symbols match…", "3 hits for…").

The implicit-scope annotation was originally added to remind agents which scope answered when they didn't pass `scope` explicitly. In practice the signal is low-value: agents that need to know the scope can call `list_scopes` (one tool call, definitive answer) or read it from the JSONL usage log. Trading the annotation for a leaner first response line — where the leaf brand mark actually lands next to "real" prose — is a clean UX win.

## What Changes

- The single-host, implicit-scope branch in [ScopedExecution.cs:36-47](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs:36) no longer prepends `_(scope: \`<id>\`)_\n\n` to the tool body. The body flows through unchanged. The leaf chokepoint then brands the body's actual first line.
- `ScopeResolution.IsImplicit` stays on the record — the resolution state is still useful telemetry (and could host a footer-style annotation in a future change). Only the response-side annotation goes.
- Multi-scope explicit fan-out (`### scope: <id>` headers per scope) is **untouched** — that's a different code path with a different rationale (the agent explicitly asked for multiple scopes; structuring per-scope output is the answer's contract).
- Per-row `scope: <name>` annotations on merged results (the `find_definition`/`search_symbols` deduped output) are **untouched** for the same reason.
- The repo's `.mcp.json` registration key is renamed from `"sourcegraph"` to `"SourceGraph"` (PascalCase brand spelling) and the `${workspaceFolder}/` placeholders are dropped from the args (relative paths work fine when the extension's `cwd` is the repo root). This is small `.mcp.json` hygiene that lands in the same UX-iteration commit.

## Capabilities

### New Capabilities
<!-- None — this change refines an existing capability. -->

### Modified Capabilities

- `mcp-tools`: The "Default behaviour" scenario under *Optional scope parameter on every existing tool* currently asserts that "the response notes the implicit scope it queried." That clause becomes false after this change and needs updating; the rest of the requirement (default-scope routing) is unchanged.

## Impact

- **Code**: One method in [src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs:36) — the single-host branch's return statement collapses from a conditional formatter into a direct return. `IsImplicit` is no longer consumed by the formatter but stays on the `ScopeResolution` record.
- **Spec**: One scenario edit in `openspec/specs/mcp-tools/spec.md`. The requirement statement itself stays as-is.
- **Tests**: No test currently asserts on the `_(scope: …)_` annotation (verified by `grep -rn '_(scope:\|implicit' tests/`), so the change lands without test breakage. New coverage isn't strictly required — the absence of the annotation is a removal, and the surrounding behaviours (default-scope routing, per-row scope tags on multi-scope queries) are already covered.
- **Documentation**: No README/CLAUDE.md change. The annotation was internal rendering detail, not a documented contract.
- **Public API / dependencies**: None. Wire format unchanged except for the missing prefix line.
- **Config hygiene** (`.mcp.json`): the rename from `"sourcegraph"` to `"SourceGraph"` and the `${workspaceFolder}/` removal don't alter behaviour for any compliant client; they were tested under VS Code Insiders' MCP panel where the extension renders the config key as the response heading.
