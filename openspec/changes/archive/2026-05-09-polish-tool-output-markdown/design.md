## Context

Today's tools emit list-shaped data as bulleted prose:

```
🌿 7 references to **Calculator.Add** (method):
- definition: Sample/Calculator.cs:14:5

7 references:
- ref at Sample/Other.cs:10:3
- call at Sample/Caller.cs:22:5
- read at Sample/Other.cs:8:1
- ...
```

In IDE-class clients (VS Code, Cursor, Claude Code), this renders as a single-column bulleted list. The visual chrome modern markdown clients give to *tables* — column alignment, hover, theme-styled cell borders — is unavailable. Same data presented as a table:

```
🌿 7 references to **Calculator.Add** (method):
definition: `Sample/Calculator.cs:14:5`

| Kind | Location |
|------|----------|
| ref  | Sample/Other.cs:10:3   |
| call | Sample/Caller.cs:22:5  |
| read | Sample/Other.cs:8:1    |
| ...  | ...                    |
```

This change is purely a rendering polish — no protocol-level changes, no return-type refactors, no new SDK features required. It lands fast and clears the way for later, larger output-protocol changes (C+D+E from the option discussion: multi-content blocks, structuredContent, resource_link) to inherit a cleaner per-tool baseline.

The pre-existing `usage_stats` / `list_scopes` / `list_generated_files` table renderers are the proof points — they've been in production since their respective changes shipped, and the GFM tables they emit are the format we want every list-shaped tool to use.

## Goals / Non-Goals

**Goals:**

- Every tool whose result is a list of homogeneous rows renders those rows as a markdown table when row count is ≥ 2.
- A single shared `Format.Table(...)` helper builds header + separator + row lines, used everywhere.
- The first line of every tool response stays as a substantive prose summary so the leaf chokepoint can prepend `🌿 ` without crowding markdown chrome (i.e. table headers don't land on response line 1).
- Tools whose output is hierarchical (each row has nested annotations, summary, history) — `find_definition`, `list_symbols_in_file` — stay bulleted; tables can't carry the nesting cleanly.
- Existing tables (`usage_stats`, `list_scopes`, `list_generated_files`) are unchanged.

**Non-Goals:**

- Switching `Task<string>` to a richer return type. That's the follow-on `tool-output-content-blocks` change.
- File:line strings as `[markdown links](file:///…)`. Considered (option B in our discussion); deliberately deferred so this change ships cleanly. Real file links could land alongside the content-block refactor.
- New tool semantics, fields, or filters.
- Reformatting `find_definition` or `list_symbols_in_file` away from bullets. Their data is hierarchical (per-row annotations, signature, summary, history rows) and tables don't accommodate that without ugly cell-wrapping.

## Decisions

### Decision 1 — Threshold: tables when row count ≥ 2

A markdown table costs roughly two rows of overhead (header + separator). At one row, prose is more compact:

```
- ref at Sample/Other.cs:10:3
```

vs.

```
| Kind | Location |
|------|----------|
| ref  | Sample/Other.cs:10:3 |
```

At two rows, the table is roughly even and starts winning visual chrome. At five rows the table is unambiguously better.

We use ≥ 2 as the cutoff so any "list-of-results" response with more than one row gets the table. Single-result responses retain their existing prose — and the existing single-result paths usually have richer per-row annotations (signature, summary) that are easier to read inline anyway.

### Decision 2 — Centralise table emission in a new `Format.Table` helper

Adding tables to twelve tools without a shared helper invites drift: each tool's table will subtly disagree with the next on column ordering, separator alignment, escape handling. A small static helper keeps the format canonical:

```csharp
public static void AppendTable(StringBuilder sb,
    IReadOnlyList<string> headers,
    IReadOnlyList<IReadOnlyList<string>> rows,
    IReadOnlyList<TableAlignment>? alignments = null);
```

Lives next to the existing `Format.*` helpers. Cells are pipe-escaped so paths/symbols containing `|` don't break the table. Alignment cues (left, right, centre) are optional and used by numeric columns (in-degree, depth, score).

### Decision 3 — Keep section headers as `### Heading`, not `## Heading`

The neighborhood tool already uses `### Inbound (N)` / `### Outbound (N)`. We considered promoting to `## ` for stronger visual chrome, but: (a) the response itself has no `# ` heading, so `## ` would be the *top* heading — which renders as a major page-section header in some clients and looks oversized in chat panels; (b) clients render `### ` perfectly well as a sub-header with weight; (c) we'd want `## ` available for any future single top-level heading per response.

Net: `### ` for sections, no top-level `# ` or `## `, prose stays the leaf-receiving first line.

### Decision 4 — Inline backticks for short code, fenced blocks reserved

Short identifiers and short signatures (`Calculator`, `public int Add(int a, int b)`) stay in inline backticks — they're already there, they're token-efficient, and they wrap naturally inside table cells. Multi-line signatures (rare in C#; common in TypeScript / Python plugin output) would benefit from triple-fence code blocks with language hints, but that's a per-call decision the renderer shouldn't make universally — leave the existing inline-backticks pattern as the default.

### Decision 5 — Column choices follow the data, not a uniform schema

Different tools naturally have different columns:

| Tool | Columns |
|---|---|
| `find_references` | `\| Kind \| Location \|` (+ `Scope` when fan-out merged) |
| `find_by_annotation` | `\| Symbol \| Kind \| Location \|` |
| `search_symbols` | `\| Symbol \| Kind \| Location \|` |
| `list_callers` / `list_callees` / `find_implementations` | `\| Symbol \| Kind \| Location \|` |
| `list_members` | `\| Member \| Kind \| Signature \|` |
| `semantic_search` | `\| Score \| Symbol \| Kind \| Location \|` |
| `find_diagnostics` | `\| Severity \| Code \| Location \| Message \|` |
| `recent_changes` | `\| When \| Author \| Symbol \| Location \|` |
| `list_tests_for` | `\| Framework \| Test \| Location \|` |
| `impact_of_change` | `\| Depth \| Symbol \| Kind \| Location \|` |
| `module_summary` | `\| In-deg \| Symbol \| Kind \| Location \|` |

Column ordering follows "most distinctive field first" so a reader scanning the table can skim the leftmost column and decide which row deserves attention.

### Decision 6 — Fan-out scope tags stay inline next to symbol names

When a fan-out merge produces rows from multiple scopes, the existing `— scope: \`<id>\`` annotation hangs off the symbol name (per [ScopedExecution.ScopeAnnotation](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs:132)). We preserve that — when a row is in a table, the scope tag rides inline with the `Symbol` cell:

```
| Symbol | Kind | Location |
|--------|------|----------|
| `Calculator.Add` — scope: `backend` | method | Sample/Calc.cs:14:5 |
```

Not the prettiest, but it preserves an existing per-row contract documented in the `mcp-tools` spec ("each row's markdown includes `scope: <name>`"). A future change could split scope into its own column when fan-out is active; out of scope here.

## Risks / Trade-offs

- **[Risk] Tables in non-markdown clients render as ugly pipe-delimited text.** → Acceptable. Most current MCP clients render markdown. Pure-text consumers (logs, raw stdio inspection) get readable pipe-delimited rows that any human can parse. No data is lost.

- **[Risk] Cells containing `|` characters break table parsing.** → Mitigation: `Format.Table` escapes `|` to `\|` in cell contents before emitting. Test coverage includes a row whose location/symbol contains a literal pipe.

- **[Risk] Wide cells (long signatures, deep file paths) cause horizontal scroll in tables but not in bullets.** → Acceptable. Modern chat UIs handle this with scroll/wrap as a UI concern; on the wire, the data is the same.

- **[Risk] Clients with character-cell terminals (rare for MCP) might prefer fixed-width formatting.** → Out of scope; markdown is our contract.

- **[Trade-off] +15 tokens per response (header + separator overhead).** → Negligible session-level. The lead-in tightening from `add-leaf-brand-mark` already gave us net-positive headroom. Each per-row delta is ~equal to prose for ≥ 5 rows; small loss for 2–4 rows. Acceptable for the rendering quality gain.

- **[Trade-off] Table columns are fixed per tool — can't easily add/drop columns based on per-call context.** → Accepted. Column choices are the per-tool contract; if a column has no data for a row (e.g. no scope when not multi-scope), the cell is empty.

## Migration Plan

1. Land `Format.Table(...)` helper alongside existing `Format.*` helpers — no callers yet.
2. Convert one representative tool (`find_references` is the highest-volume) and add its scenario tests. Land as a vertical slice — proves the pattern, gives reviewer a focal point.
3. Sweep the remaining tools in a follow-up batch, one or two per commit, each with a short scenario test confirming the column shape.
4. Update `openspec/specs/mcp-tools/spec.md` with a single new "Tabular rendering for list-shaped results" requirement (or one scenario per tool — design.md and the spec delta itself will pick the cleaner shape).

**Rollback strategy**: revert per tool. Each is independent of the others. The `Format.Table` helper has no other callers and could be deleted with the last reverted tool.

## Open Questions

- **One cross-cutting `Tabular rendering` requirement, or per-tool scenarios?** Spec delta drafts one scenario per affected tool initially — the requirement statement is the same shape for all of them, so a cross-cutting requirement may be cleaner. Decide during spec authoring.
- **Should `find_definition` get a *summary* table when the agent only needs the location list?** Could add an `output: "table"` / `output: "detailed"` parameter. Out of scope here; revisit if the bulleted format proves less useful in practice.
- **Should column alignment cues (`:---:`, `---:`) be applied to numeric columns?** `In-deg`, `Depth`, `Score`, `When (ago)` would benefit from right alignment. The helper supports it; the spec doesn't pin it. Probably yes, applied where the data is numeric.
