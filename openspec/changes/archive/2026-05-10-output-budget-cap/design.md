## Context

Every list-shaped MCP tool in this server emits per-row data three times:
prose markdown row + `ResourceLinkBlock` JSON (one per row) + structured-content
array entry. The `ResourceLinkBlock` is the heaviest of the three at ~250–365
serialized bytes (multiple URI/Name/Title/Description/MimeType fields). The
prior `find_references` default of `limit=200` produced ~80K characters of
serialized output, comfortably above Claude Code's ~64K-character per-tool-call
ceiling. The host then truncates the result to disk and surfaces a generic
"exceeds maximum allowed tokens" error.

The codebase already documents the pattern of audience-restricted "X of N rows
omitted" notices in `Tools/Output/AudienceMetadata.cs` and the spec at
`openspec/specs/mcp-tools/spec.md` references "row truncation" — but no actual
size enforcement was wired up. The `limit` parameter alone is insufficient
because per-row size varies (long file paths, deep namespaces, dense XML
summaries) and the user can override `limit` to a larger value.

## Goals / Non-Goals

**Goals:**

- Stop Claude Code's host-side truncation from firing on routine list-shaped
  tool calls.
- Keep the prose / `ResourceLinkBlock` / structured-content trio internally
  consistent — the existing `StructuredContentInvariantTests` assert that prose
  row count equals structured array length, and that invariant must hold after
  trimming.
- Signal size-driven truncation distinctly from `limit`-driven truncation so
  the agent can react (re-query with smaller `limit` or refined filter) rather
  than silently miss data.

**Non-Goals:**

- Refactoring the per-row `ResourceLinkBlock` to dedupe shared URIs. The
  `find_references` Build helper already carries a TODO acknowledging the
  redundancy is wasteful but spec-mandated; the size cap absorbs the bloat
  without that surgery.
- Pagination / offset support for `find_references` and friends. Out of scope.
- Applying the cap to every list-shaped tool. Only the four highest-risk
  helpers are wired in this change; tools with low default limits and compact
  rows can be retrofitted later if needed.

## Decisions

**1. Estimate per-row cost with flat constants instead of measuring serialized size.**

We tier per-row cost into three constants (`CompactRowChars=500`,
`RichRowChars=1000`, `SnippetRowChars=1500`) chosen by inspection of each
tool's emitted fields. Alternative: serialize a sample row and use the actual
byte count.

Rationale: estimation is O(1) and correct in the common case; serialization
adds latency to every list-shaped call for a marginal accuracy gain. The
constants are deliberately generous (the 200-ref failure was at ~324 chars/row;
we use 500 for the same class) so the cap activates a touch earlier than
strictly necessary, preferring "trim" to "fail."

**2. Trim the items list at the call site, not in the Build helper.**

Each tool body computes `(kept, omitted) = OutputBudget.ChooseKeep(...)` after
fetching items and slices the list before rendering prose. The Build helper
takes the (already trimmed) list and an `omittedSize` parameter for the
metadata block.

Alternative: serialize the result, measure, and trim from the tail.

Rationale: trimming upstream keeps prose / links / structured perfectly in
lockstep, which `StructuredContentInvariantTests` already pin. A post-build
trim would have to reach into both `Content` (drop `ResourceLinkBlock`s) AND
the opaque `JsonElement` `StructuredContent` (drop array entries) AND the
prose markdown table (drop trailing rows) — three different surgeries with no
shared invariant. Upstream trim is one slice on a typed list.

**3. Communicate truncation via the existing audience-restricted `_meta:` block.**

When `omittedSize > 0`, the tool appends `("omitted_size", N.ToString())` to
the `extras` already passed to `AudienceMetadata.Build`. Clients that respect
the `audience: ["assistant"]` annotation hide the line from the human user;
the model sees it.

Alternative: emit a banner line in the user-visible prose ("128 of 200 rows
shown — re-query with smaller limit").

Rationale: the spec already documents `_meta:` as the channel for "X of N rows
omitted due to limit" notices. Reusing the established pattern avoids
introducing a second truncation-signalling channel and keeps human-facing
prose clean.

**4. Apply to four helpers now; retrofit the rest later.**

`find_references`, `list_members`, `list_symbols_in_file`, and `semantic_search`
are wired in this change — the first three because their default or absent
`limit` admits oversize queries, and the fourth because XML summaries make
rows unusually heavy. Tools with `limit=50` defaults and compact rows
(`list_callers`, `list_callees`, `find_implementations`, etc.) are unlikely
to overflow at default settings and can be retrofitted in a follow-up if
field reports surface failures.

## Risks / Trade-offs

**[Risk] Per-row cost constants drift from reality if tool output formats grow.**
→ Mitigation: `OutputBudgetTests` pin the worst-case-budget invariant
(`kept × perItemChars + baseChars ≤ budget`); future maintainers raising the
constants must keep the test green. The constants are commented inline with
the field set they cover.

**[Risk] An agent that doesn't read the audience-restricted metadata silently
misses rows when the cap fires.**
→ Mitigation: this matches the existing pattern documented in
`AudienceMetadata.cs` — clients that don't honour `audience: ["assistant"]`
already see the metadata as plain text in the prose, so `omitted_size=N` is
visible there too. Either way, the model gets the signal.

**[Risk] Lowered defaults (200 → 50 for `find_references`, 200 → 100 for
`list_members`) silently truncate for callers that previously relied on the
larger default.**
→ Mitigation: callers can pass an explicit `limit=200`. The size cap then
applies on top of `limit` and trims further only if rendering 200 rows would
exceed the budget — which is the same truncation the host would have applied,
but cleanly and with a metadata signal.

**[Trade-off] The four-helper scope leaves twelve other list-shaped tools
without the cap.**
→ Accepted: those tools have lower default limits and lighter rows, so
overflow is unlikely at defaults. Field reports drove the choice of which
four to fix; expanding scope speculatively would inflate the diff.
