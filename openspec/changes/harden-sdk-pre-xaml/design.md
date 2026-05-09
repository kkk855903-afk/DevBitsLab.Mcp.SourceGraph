## Context

`open-language-contract` reformed the SDK boundary in one PR but landed with
five tasks deferred (4.7 EXPLAIN QUERY PLAN, 8.8 / 8.9 stdio integration test +
no-instructions regression, 9.4 / 9.5 end-to-end smoke against the fixture
solutions). Each carries a small, isolated risk: a TEXT-kind index that the
planner doesn't actually pick up, an SDK-shaped `Capabilities.Experimental`
payload that gets serialised wrong by an MCP SDK upgrade, a multi-scope path
that works in process but breaks at the stdio boundary. Today there is one
in-tree consumer (the C# Roslyn indexer), zero external plugin authors, and
no second indexer to stress-test the surfaces. The next language family
(`xaml-language-indexer`) is the forcing function — it will read
`Capabilities.Experimental["sourcegraph.vocabulary"]`, populate the `payload`
column on `binds-path` edges, and depend on the `csharp:T:...` canonical-key
shape every cross-language plugin would otherwise reinvent.

This change closes the deferrals and locks the surfaces the next indexer
will sit on, so the XAML PR is purely indexer code. It is intentionally a
hardening change, not a feature: no new query shape reaches the agent, no
new symbol is indexed, no plugin gets loaded that wasn't loaded before. The
only externally visible surfaces are a `vocabulary list` CLI subcommand and
an additional sub-line in the markdown of three existing tools.

## Goals / Non-Goals

**Goals:**

- Cover deferred tasks 4.7 (EXPLAIN), 8.8 / 8.9 (stdio integration test +
  --no-instructions), 9.4 / 9.5 (end-to-end smoke) under one harness.
- Lock the kebab-case `payload` key vocabulary the next indexer will emit,
  so XAML doesn't ship and trigger a reindex over a renamed key six months
  later.
- Add a diagnostic CLI that surfaces the soft-registry vocabulary plus
  Levenshtein-near drift candidates, before drift becomes silent.
- Land the `CanonicalKeys.For{Type, Method, Field, Property}` SDK helpers
  every cross-language plugin needs to point an edge at a C# symbol.
- Light up the `payload` column visually in tool output so the moment any
  indexer fills it, the data is readable in markdown without a new tool.

**Non-Goals:**

- A full BenchmarkDotNet perf job for kind-filter queries. The cheap
  EXPLAIN assertion is the regression net; perf benchmarking stays
  deferred until a real regression appears.
- A strict vocabulary registry, manifest format, or rejection of
  undeclared kinds at load time. Soft registry remains the default; this
  change only adds the diagnostic surface that makes a future strict
  upgrade evidence-based.
- The XAML indexer itself.
- Any plugin distribution work (HTTP-driven NuGet restore, signature
  verification, trust-on-first-use).
- Backward compatibility with v0.7 SDK consumers — there are none.

## Decisions

### 1. Stdio integration test via the `ModelContextProtocol.Client` SDK

**Choice:** A new test project
`tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/` spawns
`dotnet run --project src/.../Server -- serve --solution
tests/fixtures/Sample.sln` as a child process and drives it through
`McpClient.CreateAsync(StdioClientTransport)` from the official C# SDK
(`ModelContextProtocol.Client`). Tests assert
`Capabilities.Experimental["sourcegraph.vocabulary"].edge_kinds` is a
sorted lowercase deduped array containing the SDK constants.

**Alternatives considered:**

- *Hand-rolled stdio + JSON-RPC.* Reinvents framing for no upside; misses
  the chance to exercise the same client surface real MCP consumers use.
- *In-process `IServiceCollection` test.* Already exists
  (`ServerInstructionsWiringTests`) and deliberately skips the SDK's
  `initialize` builder. Catches the wiring; misses serialization regressions.

**Rationale:** Going through the official client is the only way to catch
breakages caused by an MCP SDK upgrade renaming or restructuring
`Capabilities.Experimental`. `ModelContextProtocol 1.2.x` is pinned today
but the contract surface is what the next indexer will read; the test
should hit it.

### 2. EXPLAIN QUERY PLAN as a unit assertion, not a benchmark

**Choice:** A new test class runs `EXPLAIN QUERY PLAN <sql>` against the
four hot SQL strings (`ListCallers`, `ListCallees`, kind-filtered
`ListCalleesAsync`, recursive-CTE `ImpactOfChangeAsync`) and asserts the
returned plan strings contain `USING INDEX idx_edges_kind_name` or
`USING INDEX idx_edges_dst (dst, kind_name)`. No timings, no warmup, no
BenchmarkDotNet job.

**Alternatives considered:**

- *Full BenchmarkDotNet harness with kind-filter on/off perf comparison.*
  Right tool eventually, wrong moment. CI noise + warmup cost makes per-PR
  perf gating unsustainable; a one-shot baseline run before each release
  tag is the right cadence.
- *No assertion, trust the index.* The whole reason 4.7 was on the
  deferred list is that the schema move from INT to TEXT didn't
  ship with proof the planner picks the new index.

**Rationale:** A scan vs. index regression is binary and fast to assert.
A 5% p95 regression is not worth chasing in CI. Cover the binary case
cheaply; defer the analog case until evidence demands it.

### 3. `PayloadKeys` constants in the SDK before any indexer emits

**Choice:** Add `PayloadKeys` static class to the SDK with kebab-case
constants the next indexers (XAML first, web stack later) will populate
inside `EdgeEmitted.Metadata`: `Path`, `Mode`, `Converter`,
`ConverterParameter`, `Event`, `Handler`, `DataType`, `TargetType`,
`Key`, `BasedOn`, `ElementName`, `RelativeSource`, `FallbackValue`,
`StringFormat`, `UpdateSourceTrigger`. Constants are kebab-case strings
(`"converter-parameter"`, not `"converterParameter"`) consistent with
the rest of the wire vocabulary.

**Alternatives considered:**

- *Defer until XAML lands.* Locks the wrong key names if XAML emits
  `binding_path` and we later canonicalise on `path` — every indexed
  repo reindexes.
- *Per-language constants module (`XamlPayloadKeys`).* Premature
  splitting; most keys (`path`, `mode`, `event`) recur across templated
  UI dialects (XAML, JSX, Vue, Svelte). Single core class with
  per-language additions later is cheaper.
- *Free-form keys per plugin.* Re-introduces the soft-registry typo
  problem inside payload, where it's harder to surface than at the kind
  level (no FTS, no `vocabulary list`).

**Rationale:** The on-disk format is the one place reform is expensive
after the fact. Locking it now costs zero — there is no producer to
break — and is purely additive for plugin authors who haven't started
yet.

### 4. `CanonicalKeys.ForType / ForMethod / ForField / ForProperty` helpers

**Choice:** Add a static `CanonicalKeys` class to the SDK that constructs
`csharp:T:` / `csharp:M:` / `csharp:F:` / `csharp:P:` keys from
fully-qualified C# names. Handles generic-arity (``MyApp.Foo<T>`` →
`csharp:T:MyApp.Foo\`1`), nested types via `+`, generic methods,
parameter-list signatures.

**Alternatives considered:**

- *Each plugin re-derives doc-comment-id format.* Brittle: generic-arity
  formatting is non-trivial; nested types via `+` is easy to forget;
  every cross-language plugin would have its own subtly-wrong copy.
- *Expose the Roslyn `ISymbol.GetDocumentationCommentId()` directly.*
  Drags Roslyn into the SDK contract — exactly what `open-language-
  contract` worked to remove. The target plugin doesn't have an `ISymbol`
  to call into; it has a string.

**Rationale:** Cross-language joins reduce to string equality only if
both sides emit identical strings. The C# side runs through Roslyn; the
non-C# side reconstructs from a string. A single shared helper closes
the gap.

### 5. Always-render-payload in `list_callers` / `list_callees` /
       `neighborhood`

**Choice:** When an edge row carries a non-null `payload` JSON value,
render it as an indented sub-line under the edge row in markdown
output:

```
- MyApp.Views.Main#SaveBtn (xaml-element)
    binds-path → MyApp.ViewModels.MainVM.IsBusy (boolean)
    payload: { path: "IsBusy", mode: "two-way", converter: "BoolToVisibility" }
```

Wire `payload` through the storage read DTOs (today the read path
returns `SymbolHit`-shaped rows that don't surface payload; this change
plumbs it).

**Alternatives considered:**

- *New tool `inspect_edge` that surfaces payload on demand.* Requires
  exposing edge ids in other tool output (not the current shape); adds
  a round-trip; agent doesn't reach for it unprompted.
- *Generic `--include-payload` flag.* Discoverable only from docs; the
  default UX is empty unless the agent reads the help.

**Rationale:** When a non-C# indexer fills the column the data is
agent-readable immediately, with zero new tools. The C# indexer doesn't
emit payload today, so existing output is unchanged.

### 6. `vocabulary list` CLI subcommand, soft-by-default

**Choice:** A new `sourcegraph-mcp vocabulary list` subcommand modeled
on `plugins list` / `scopes list`. For each scope it prints the active
`edge_kinds` / `symbol_kinds` / `annotation_flavors` arrays (each entry
tagged by source — SDK constant vs. plugin id+version — and live
emission count from a `SELECT COUNT(*) FROM edges WHERE kind_name = ?`),
plus a "Drift candidates" section that flags Levenshtein-near pairs
(`bind-path` vs. `binds-path`) within the same scope. Default exit
code `0` (diagnostic only); a `--strict` flag exits non-zero on any
drift candidate so CI can wire it as a gate.

**Alternatives considered:**

- *No diagnostic surface; trust soft registry.* Drift becomes silent
  the moment a second indexer ships; impossible to spot without the
  output we already publish in `initialize`.
- *Strict registry instead.* Adds a manifest mechanism with no consumer
  to enforce against. The cheap-now move is the diagnostic; promote to
  strict when drift is observed.

**Rationale:** All-upside intermediate step. Makes a future strict
upgrade evidence-based ("we saw drift, ship strict") rather than
speculative.

## Risks / Trade-offs

- **Levenshtein drift detection produces false positives** (e.g. `calls`
  vs. `calls-virtual` are intentionally distinct, distance 8). →
  Mitigation: distance threshold ≤2, plus a per-scope allow list in
  `.sourcegraph.json` for known-distinct pairs once any are observed.
  Not implemented in v1; documented as an open question.
- **`PayloadKeys` reserves names that may not get used.** Reserving
  `relative-source` and `update-source-trigger` for XAML when the web
  stack may never emit them costs a few constants in the SDK and zero
  storage. Cheap.
- **Always-render-payload bloats output for edges with large dictionaries.**
  → Mitigation: render no more than 5 keys per edge; if more present,
  append `(N more)`. Pure formatting decision; revisit if real usage
  shows it's not enough.
- **Stdio integration test relies on `dotnet run` being on PATH in CI.**
  → Mitigation: same dependency the existing `dotnet test` job has;
  no incremental risk.
- **The drift CLI's `--strict` mode is the most visible side-channel for
  reviewing a plugin upgrade,** but no plugin upgrades exist today. →
  Mitigation: ship `--strict` as opt-in, document the use case, leave
  CI gate-wiring to a future change with real plugins.

## Migration Plan

1. **No SDK version bump required** — every addition is purely
   additive (`PayloadKeys` static class, `CanonicalKeys` static class,
   no contract surfaces touched). Patch-bump the SDK csproj.
2. **No schema bump.** The `payload` JSON column already exists on
   `edges` (introduced by `open-language-contract`); this change reads
   it.
3. **Land all six pieces in one PR** against `main`. Pieces are
   independent at the test level, but their value crystallises when
   they ship together as the "design before XAML" set.
4. **Add `tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/`** as a new
   csproj referenced from the slnx; CI runs it on Linux only (boundary
   bugs are platform-agnostic, no need to triple cost).
5. **CHANGELOG entry** (or equivalent SDK csproj XML doc note)
   documents the new `PayloadKeys` and `CanonicalKeys` types and the
   `vocabulary list` subcommand.

## Open Questions

- **Should `vocabulary list --strict` be wired into CI as a default
  gate, or stay opt-in?** The first plugin author hits it within their
  first emission; a CI gate catches drift before review. The
  counter-argument: a single-consumer scope has no producer of drift,
  and a default gate fails closed for no real reason. Lean opt-in for
  v1; revisit when the second consumer (XAML) ships.
- **How loud should the always-render-payload sub-line be in
  `module_summary`?** Module summary is already dense; payload sub-lines
  on every edge row could push it past readable. Lean: omit
  payload from `module_summary` rendering (or render only a count) and
  keep the sub-line on `list_callers` / `list_callees` / `neighborhood`.
- **Do we want a `--quiet` / `--json` mode on `vocabulary list`?** The
  drift-candidate scan is a natural consumer of CI output. Defer until
  someone asks; soft registry's whole point is not to gate.
