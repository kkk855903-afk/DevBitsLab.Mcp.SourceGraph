## Context

`open-language-contract` added `payload TEXT NULL` to the `edges` table for
per-edge facts (binding paths, event names, prop names) — the channel
templated UI dialects need to attach data to edges that the
`(src, dst, kind)` triple cannot hold. `harden-sdk-pre-xaml` locks the
kebab-case payload key vocabulary in the SDK and surfaces non-null payloads
as an indented sub-line in `list_callers` / `list_callees` /
`neighborhood` markdown. `xaml-language-indexer` fills the column with
binding metadata (path, mode, converter, converter-parameter) on
`binds-path` edges and event metadata (event name) on `handles-event`
edges.

That covers the read path for an agent that already knows to walk
`binds-path` or `handles-event` edges. What it does NOT cover: an agent
asking "where does `User.Name` get bound TwoWay?" or "find every Click
handler that routes to `OnSave`". Those question shapes are recurrent and
named — `mode`, `converter`, `event`, `handler` are vocabulary the agent
knows from looking at XAML — and they deserve discoverable tools rather
than instructions to invoke a generic walk plus parse payload by eye.

This change adds two specialized MCP tools, `find_data_bindings` and
`find_event_handlers`, that walk the relevant edge kinds with payload-
aware filters. The tool surface stays narrow on purpose: this is the
named-shape query channel; the generic walk plus payload sub-line still
carries the long tail.

## Goals / Non-Goals

**Goals:**

- Discoverable tool names matching the agent's mental model
  (`find_data_bindings`, `find_event_handlers`) that surface in the
  MCP `tools/list` advertisement.
- Named parameter knobs (`mode`, `converter`, `event`, `handler`,
  `command`) that match the `PayloadKeys` vocabulary so the
  parameter-set documentation reads as a natural extension of the
  payload schema.
- Cross-language compatibility — the tools accept any indexer's
  emissions on `binds-path` / `handles-event`, so a TypeScript or
  Vue indexer that fills the same edge kinds works automatically.
- Soft response when the active scope hasn't loaded an indexer that
  emits the queried edge kind: zero results plus a one-line note,
  not an error.

**Non-Goals:**

- A generic `--payload-where path=User.Name` filter on the existing
  `list_callers` / `list_callees`. The escape hatch is real but
  not discoverable from the tool listing; defer until specialized
  tools demonstrably miss the use case.
- An `inspect_edge` tool that takes an edge id. Edge ids are not
  currently surfaced in any tool output; introducing them just to
  feed `inspect_edge` would inflate every other tool's row format
  for marginal value. Defer indefinitely.
- FTS over the `payload` JSON column. Path strings are short and
  exact-match-queried in practice; FTS adds index cost for no real
  query.
- Generated columns lifting hot payload keys (e.g. `event`) into typed
  columns. Premature; wait until profiling on the Avalonia/Uno
  fixtures shows pain.
- Tools for not-yet-emitted XAML edge kinds (`renders-template`,
  `applies-style`, `merges`). They are queryable today via
  `list_callers --kind <kebab>`; specialised tools would multiply
  before usage data shows the value.

## Decisions

### 1. Specialized tools over a generic --payload-where filter

**Choice:** Two new MCP tools (`find_data_bindings`, `find_event_handlers`)
with named parameters for the payload knobs each one queries. No
generic `--payload-where` flag added to `list_callers` / `list_callees`.

**Alternatives considered:**

- *Generic `--payload-where path=User.Name`.* Powerful but discoverable
  only from documentation; the agent doesn't reach for it from a
  `tools/list` listing. Reserve as a future escape hatch if specialised
  tools miss something.
- *One generic `find_edges` tool with kind + payload filters as
  arguments.* Too low-level; loses the "the agent recognises `mode`,
  `converter`, `handler` from XAML" affordance.

**Rationale:** The MCP contract advertises tools by name. Tools named
after the question shape ("find data bindings") are self-advertising;
tools named generically ("find edges with payload filter") are not.
Specialised tools beat generic flags for agent UX.

### 2. Lock parameter set to known XAML emit shape, accept TS may omit some

**Choice:** `find_data_bindings` parameters: `target`, `source`, `path`,
`mode`, `converter`, `scope`, `limit`. `find_event_handlers` parameters:
`handler`, `event`, `element`, `command`, `scope`, `limit`. Every
parameter is optional but at least one filter MUST be non-null (else
the tool returns the first `limit` results across the whole graph and
hints the agent to add a filter).

**Alternatives considered:**

- *Wait for TS/web-stack indexers before locking the param set.* The
  XAML emit shape is the immediate consumer; the param shape can
  widen additively later. TS will likely omit `mode` (no
  TwoWay-vs-OneWay distinction in JSX); the tool stays general by
  treating omissions as "no filter."
- *Cross-cut over every imaginable binding/event vocabulary.* Premature
  generalisation; every additional parameter is doc tax for no
  real consumer.

**Rationale:** The tools follow the indexer; the indexer emits what's
on disk. Lock to XAML's shape, widen when a second emitter shows up.

### 3. Don't FTS-index payload yet

**Choice:** Queries use `json_extract(payload, '$.path') = ?` over the
existing `kind_name = 'binds-path'` index. No FTS5 virtual table over
`payload`.

**Alternatives considered:**

- *FTS5 over a synthesised text column from payload keys.* Match the
  pattern used for attribute args in `attributes_fts` / `annotations_fts`.
  Justified there because attribute args are free-form prose; here, the
  values (`User.Name`, `OnSave`, `BoolToVisibility`) are short
  identifiers queried by exact match.

**Rationale:** Index cost without query benefit. The existing kind index
narrows the row set to the binding edges; `json_extract` on a few
hundred rows is fast.

### 4. Don't add generated columns yet

**Choice:** No generated columns lifting payload keys (e.g. `payload_path
TEXT GENERATED ALWAYS AS (json_extract(payload, '$.path'))`). Stay with
`json_extract` in the WHERE clause.

**Alternatives considered:**

- *Promote `path` and `event` to generated columns now.* These look
  like the most likely hot keys; doing it now avoids a schema bump
  later. But: a schema bump for an additive column is cheap (no
  data migration; the column is computed); promoting before profiling
  shows pain is YAGNI.

**Rationale:** Cheap to add later, fixed cost up front. Wait for
evidence.

### 5. Tools land AFTER xaml-language-indexer ships

**Choice:** This proposal cannot merge before `xaml-language-indexer`
ships. The tools have nothing to query without payload-emitting edges,
and the parameter-set decision in #2 needs to be informed by the actual
shape XAML emits in production (in case `mode` values come through as
`TwoWay` instead of the documented `two-way`, or `Converter` resolves
to a fully-qualified name vs. a short name).

**Alternatives considered:**

- *Land in parallel with XAML.* Saves a PR but couples two
  conceptually-distinct concerns; if XAML's emit shape needs
  adjustment after smoke testing, the tool's parameter set has to
  change in lockstep.
- *Land before XAML, with no test coverage.* Tools advertised in
  `tools/list` that always return empty are worse than tools that
  don't exist.

**Rationale:** Order of operations matters. Indexer first; tooling
second; the design is sketched here so the work can start as soon as
XAML lands.

### 6. Soft response for queries against unloaded vocabulary

**Choice:** When `find_data_bindings` is invoked against a scope whose
loaded indexers do not emit `binds-path` (e.g. a backend-only scope
without XAML), the tool returns an empty result list and includes a
one-line `note:` indicating the scope's `edge_kinds` vocabulary did
not include `binds-path`. Same for `find_event_handlers` against scopes
without `handles-event` emitters. The tool does NOT error.

**Alternatives considered:**

- *Hard error / refuse to register the tool in scopes without the
  vocabulary.* Tools advertised conditionally complicate the
  `tools/list` story (the same MCP server returns different tools
  per scope?); the `Capabilities.Experimental` vocabulary already
  tells the agent which kinds the scope can answer. Soft empty plus
  hint matches the existing `list_callers --kind not-a-real-kind`
  pattern documented in the open-language-contract specs.

**Rationale:** Consistency with existing patterns. The agent reads
the soft response, sees the missing vocabulary, and stops asking the
same query. No exceptional code path.

## Risks / Trade-offs

- **Parameter-set fit when TS arrives.** `mode` may not apply to JSX
  controlled components (no TwoWay/OneWay). → Mitigation: parameter is
  optional; TS-indexed scopes simply omit `mode` from their payload;
  the tool stays general. Document the cross-language semantics in
  the tool description.
- **Tool naming may need to evolve.** `find_data_bindings` is XAML-
  flavoured; if Vue's `v-model` or Svelte's two-way syntax gets indexed,
  is `find_data_bindings` still the right name, or do we want
  `find_bindings`? → Mitigation: `find_data_bindings` is general
  enough (data binding is the abstract pattern, not a XAML-specific
  one); revisit only if a downstream consumer surfaces real friction.
- **`json_extract` perf on large corpora.** A scope with 100k
  `binds-path` edges runs `json_extract` per row inside the WHERE
  clause. → Mitigation: SQLite's `json_extract` is fast for short
  values; profiling on Avalonia/Uno fixtures (introduced by
  `xaml-language-indexer`) covers the realistic worst case; promote
  hot keys to generated columns if profiling shows pain.
- **The `command` parameter is XAML-specific** and will be empty on
  scopes without `Command="{Binding ...}"` patterns. → Mitigation:
  documented in the tool description; the parameter is optional and
  has zero cost when unused.

## Migration Plan

1. **No SDK version bump.** Tool registrations live in the server.
2. **No schema bump.** `payload` column already exists (introduced by
   `open-language-contract`).
3. **Land in one PR** after `xaml-language-indexer` merges. The PR
   adds the tool registrations, the SQL helpers, and tests against the
   `SampleWpf` fixture introduced by `xaml-language-indexer`.
4. **CHANGELOG entry / README example** showing
   `find_data_bindings --target=User.Name --mode=TwoWay` against the
   sample fixture.

## Open Questions

- **Should `find_data_bindings` also return outgoing-side metadata**
  (the XAML element's containing view's `x:Class`)? Useful for "show
  me which views bind this property" but inflates output. Lean: off
  by default; expose via `--include-view` if real usage demands.
- **Hard-vs-soft error on missing vocabulary.** Decision 6 picks soft;
  open question is whether a `--strict-vocabulary` flag (mirroring
  the `vocabulary list --strict` pattern from `harden-sdk-pre-xaml`)
  earns its keep. Leans no until a real consumer asks.
- **Cross-language tool naming.** When TS bindings (`v-model`,
  controlled components) start emitting on `binds-path`, do we
  rename to `find_bindings`, or keep `find_data_bindings` as the
  pattern name? Decide when a second emitter is in flight.
