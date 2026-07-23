## Why

The open-language-contract reform reshaped the SDK boundary, but it landed
with five tasks deferred to a follow-up: an out-of-process MCP `initialize`
integration test (8.8) and its `--no-instructions` regression (8.9), the
end-to-end smoke against `Sample.sln` and `MultiScope/` (9.4, 9.5), and an
EXPLAIN QUERY PLAN check pinning the new TEXT-kind index choices (4.7). The
window for piling small contract-shape decisions onto the same payload is
also still open: there is one in-tree consumer (the C# Roslyn indexer), no
external plugin authors, and the next language indexer (XAML) has not yet
emitted a single row.

This change closes those deferrals and locks the surfaces XAML will sit on,
so the XAML PR is purely indexer code rather than yet another
contract-and-indexer entanglement. Six small, additive, no-contract-breaking
pieces ride together as the "design before XAML" set: the test net, the
plan-stability assertion, the payload key vocabulary, the markdown rendering
that lights up payloads when an indexer fills them, the diagnostic CLI for
spotting drift, and the canonical-key construction helper every cross-language
plugin would otherwise reinvent.

## What Changes

- **NEW:** Out-of-process stdio MCP integration test project
  (`tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/`) using
  `ModelContextProtocol.Client` + `StdioClientTransport` to drive `initialize`
  against a freshly-spawned server. Asserts the
  `Capabilities.Experimental["sourcegraph.vocabulary"].edge_kinds` array is
  sorted, lowercase, deduped and contains the SDK constants. Companion test
  asserts the experimental key is absent under `--no-instructions`. The same
  harness covers an end-to-end smoke against `Sample.sln` and
  `MultiScope/`, retiring deferred tasks 8.8, 8.9, 9.4, and 9.5.
- **NEW:** Unit test that runs `EXPLAIN QUERY PLAN` against the four hot read
  paths (`ListCallers`, `ListCallees`, kind-filtered `ListCalleesAsync`,
  recursive-CTE `ImpactOfChangeAsync`) and asserts the planner uses
  `idx_edges_kind_name` or `idx_edges_dst (dst, kind_name)`. Pins index
  selection without committing to a BenchmarkDotNet job. Retires deferred
  task 4.7.
- **NEW:** `PayloadKeys` static class on the SDK declaring the kebab-case
  keys the next-generation indexers (XAML first, web stack later) will
  populate inside `EdgeEmitted.Metadata`: `path`, `mode`, `converter`,
  `converter-parameter`, `event`, `handler`, `data-type`, `target-type`,
  `key`, `based-on`, `element-name`, `relative-source`, `fallback-value`,
  `string-format`, `update-source-trigger`. Locks the wire format before
  any indexer emits, so a later canonicalisation isn't a reindex.
- **NEW:** `CanonicalKeys` static class on the SDK with `ForType(fqn)`,
  `ForMethod(fqn, signature)`, `ForField(typeFqn, name)`, and
  `ForProperty(typeFqn, name)` helpers that construct
  `csharp:T:` / `csharp:M:` / `csharp:F:` / `csharp:P:` keys from
  fully-qualified C# names — handles generic-arity (``MyApp.Foo<T>`` →
  `csharp:T:MyApp.Foo\`1`), nested types via `+`, and the reference-target
  side of a cross-language edge. Centralises the doc-comment-id derivation
  every cross-language plugin would otherwise rewrite.
- **NEW:** Always-render-payload in tool markdown. `list_callers`,
  `list_callees`, and `neighborhood` render a non-null `payload` JSON value
  as an indented sub-line under each edge row, mirroring the existing
  `annotations:` line pattern. The C# indexer doesn't emit payload today,
  so the change is a no-op for existing data — but it lights up the
  moment any indexer fills the column. Plumbing change: `payload` flows
  through the storage read path (`ListCallersAsync` / `ListCalleesAsync`)
  into the tool DTO.
- **NEW:** `sourcegraph-mcp vocabulary list` CLI subcommand modeled on the
  existing `plugins list` / `scopes list`. For each scope it prints the
  active `edge_kinds` / `symbol_kinds` / `annotation_flavors` arrays
  (each entry tagged by source — SDK constant vs. plugin id+version — and
  live emission count), plus a "Drift candidates" section that flags
  Levenshtein-near pairs (`bind-path` vs. `binds-path`) within the same
  scope. Default exit code `0` (diagnostic only); a `--strict` flag exits
  non-zero on any drift candidate so CI can wire it as a gate.

## Capabilities

### New Capabilities

- *(none — every piece extends an existing capability)*

### Modified Capabilities

- `extensibility`: SDK gains `PayloadKeys` constants and a `CanonicalKeys`
  helper class for cross-language plugins to construct C# canonical-key
  references without reinventing the doc-comment-id format.
- `mcp-tools`: `list_callers`, `list_callees`, and `neighborhood` always
  render a non-null `payload` JSON value as an indented sub-line under the
  edge row.
- `cli`: new `vocabulary list` subcommand with optional `--strict` flag.

## Impact

- **Code:** SDK (`PayloadKeys`, `CanonicalKeys`); Storage read DTOs to
  surface `payload`; Server tool renderers (`GraphTools`); CLI subcommand
  wiring; new test project under `tests/`.
- **Public contract:** SDK gains additive types only (no break). Tool
  output gains an optional sub-line that's only present when an indexer
  populates `EdgeEmitted.Metadata`. CLI gains a new subcommand, no
  existing ones touched.
- **Persistence:** No schema changes. The existing `payload TEXT NULL`
  column (introduced by open-language-contract) is the storage side; this
  change reads it.
- **CLI:** Strictly additive — `vocabulary list` joins `plugins list` /
  `scopes list` / `init-scopes` / `scopes add|remove`.
- **Out of scope:** The XAML indexer itself; full BenchmarkDotNet perf
  job; strict vocabulary registry; any plugin distribution work.

**Depends on:** none.
**Unblocks:** xaml-language-indexer.
