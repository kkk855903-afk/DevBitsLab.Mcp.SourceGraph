# Roadmap

New proposals are drafted under `openspec/changes/<slug>/` and validated with
`openspec validate --all --strict`.

## Phase 5 — Open language hosts (in flight)

Generalising the per-language indexer slot beyond `.cs` (Roslyn) and `.xaml`
(custom parser) so the marginal cost of adding language N+1 collapses to
"bind a tree-sitter grammar + write a node-kind map".

| Change | Adds | Depends on |
|---|---|---|
| **`add-tree-sitter-language-indexer-host`** | New `Indexing.TreeSitter` project; abstract `TreeSitterLanguageIndexer<T>` base; SDK `INodeKindMapper` / `IModuleResolver` / `LanguageIndexerOptions` / `ITreeSitterGrammarConfig` contracts; scope-config `language` + `enrichment` fields; `scopes info` CLI; `TreeSitter.DotNet` runtime + 28 grammar binaries shipped transitively. No language emits rows yet. | — |
| **`add-typescript-language-indexer`** (MVP shipping) | First concrete language on the host: `.ts` / `.tsx` / `.js` / `.jsx` indexer using bundled grammars; PascalCase JSX `instantiates` edges with prop payload; default scope excludes (`node_modules`, `dist`, `.next`, …); lifts `js` / `ts` / `jsx` / `tsx` canonical-key schemes. **Cross-file ref resolution + LSP enrichment ship as follow-ups** (see below). | `add-tree-sitter-language-indexer-host` |

**Deferred follow-ups for the TypeScript stack** (factored out so the MVP can ship):

- `add-typescript-module-resolver` — hand-rolled cross-file resolver covering
  relative imports + tsconfig `paths` aliases + extension probing + re-export
  chase. Adds `tsconfig` field on scope-config.
- `add-typescript-lsp-enrichment` — LSP client wiring under
  `Indexing.TreeSitter/Lsp/`; spawns `typescript-language-server` post-pass when
  `enrichment.lsp.command` is set; merges results as `refs` rows tagged
  `enrichment_source = 'lsp'`. Carries the additive schema bump V11→V12 for the
  new column. `find_references` rendering gains a `(via lsp)` annotation.

After these land, future per-language changes (Python, Go, Rust, Ruby) reduce
to: subclass `TreeSitterLanguageIndexer<T>`, write the `INodeKindMapper`,
implement language-specific `IModuleResolver`, lift the relevant canonical-key
scheme.

## Shipped (archived)

The original phase-1-to-4 proposals plus two post-1.0 maturity changes
(`add-otel-signals`, `harden-release-pipeline`) all landed and were archived;
their delta specs are folded into the live `openspec/specs/` baselines.
Listed here so maintainers can trace history:

## Post-1.0 maturity (2026-05-07)

| Change | Adds | Capability |
|---|---|---|
| **`add-otel-signals`** | `ActivitySource("DevBitsLab.Mcp.SourceGraph")` + `Meter` with four named instruments emitted from every wrapped MCP tool call; coexists with the existing JSONL log + `usage_stats` tool. | `observability` |
| **`harden-release-pipeline`** | `dotnet test` gate before NuGet publish; cross-platform CI (Linux/macOS/Windows); CodeQL; Dependabot; deterministic builds + SourceLink; SDK packed alongside the Tool. | `distribution` |
| **`watch-scope-config`** | `ScopeConfigWatcher` (mtime polling), `ScopeRouter.Replace`/`Unregister` + `ScopeDiff` for diff-and-apply, `LiveIndexService` brings up/tears down/replaces scopes from `.sourcegraph.json` edits without restart. Plugin reloads explicitly out of scope. | `live-updates`, `scoping` |

## Phase 1 — Code-meaning enrichment

Foundational data the agent already needs. Cheap to ship, big agent-value lift.

| Change | Adds | Schema | Depends on |
|---|---|---|---|
| **`enrich-symbol-model`** | modifiers, accessibility, XML doc summary, populated `container_id` | +3 cols on `symbols`, FTS over `xml_summary` | — |
| **`index-attributes`** | every Roslyn attribute with full args, plus `find_by_attribute` tool | new `attributes` + `attributes_fts` tables | — |
| **`expand-edge-types`** | `UsesType`, `OverridesMember`, `ImplementsMember`, `Instantiates`, `Throws` edges; `Read`/`Write` ref kinds; `find_implementations` tool | none — extends existing enums | — |
| **`semantic-search`** | code-aware embeddings (Jina v2 base code, 768-dim), `sqlite-vec`, `semantic_search` tool | new `symbol_embeddings` virtual table + `embedding_meta` | — |

## Phase 2 — Scoping & isolation

The architectural shift from "one server = one solution" to "one server = N named scopes".

| Change | Adds | Depends on |
|---|---|---|
| **`add-scoping`** | `Scope` entity, `.sourcegraph.json`, per-scope DBs, `list_scopes`, scope-aware tools, one-shot migrator from single-DB layout | — (back-compat for single-solution users) |

## Phase 3 — Extensibility

Open the system so consumers can add languages, analyzers, and tools without forking.

| Change | Adds | Depends on |
|---|---|---|
| **`extensibility-architecture`** | `ILanguageIndexer`, `ICodeAnalyzer`, `IMcpToolPlugin` SDK; plugin discovery via `.sourcegraph.json`; per-plugin `AssemblyLoadContext` isolation; `plugins list/info` CLI | refactors the built-in C# Roslyn indexer to *implement* `ILanguageIndexer` (proves the contract) |

## Phase 4 — Free-data integrations

Information Roslyn and git already have, just not exposed.

| Change | Adds | Depends on |
|---|---|---|
| **`integrate-source-and-diagnostics`** | source-generated documents indexed (with `is_generated` flag); per-symbol Roslyn diagnostics; `find_diagnostics`, `list_generated_files` | — |
| **`integrate-tests-and-history`** | `Tests` edges from test methods to production code; per-symbol `git blame` cache; `list_tests_for`, `who_authored`, `recent_changes` | `index-attributes` (for test-framework attribute discrimination) |

## Suggested ordering

```
Phase 1 in any order ─┐
                      ├─→ Phase 4 (integrate-tests-and-history needs index-attributes)
Phase 2  ─────────────┤
                      ├─→ Phase 3 (extensibility refactor lands cleanly only after metadata is stable)
                      ▼
                  Phase 4 (rest)
```

In practice:

1. `enrich-symbol-model` — smallest, biggest UX win, no deps.
2. `index-attributes` — unlocks test detection later.
3. `expand-edge-types` — qualitative jump for graph queries, no schema work.
4. `add-scoping` — invisible to single-solution users; lands in parallel with Phase 1.
5. `semantic-search` — orthogonal to the others; can ship independently.
6. `extensibility-architecture` — wait until the metadata model stabilises so the SDK contract doesn't churn.
7. `integrate-source-and-diagnostics` — picks up generated MVVM / route attributes once Phase 1 is in.
8. `integrate-tests-and-history` — requires `index-attributes` for test-framework detection.

## Workflow

```bash
# review a proposal
openspec show enrich-symbol-model

# validate everything
openspec validate --all --strict

# implement a proposal (inside Claude Code)
/opsx:apply enrich-symbol-model

# fold a completed change into the live specs
/opsx:archive enrich-symbol-model
```

The seeded specs at `openspec/specs/{indexing, storage, live-updates, mcp-tools,
mcp-resources, cli, observability, distribution, mcp-config}/` describe today's
behaviour; each change above contains delta specs (`ADDED`, `MODIFIED`,
`REMOVED` requirements) that fold into those baselines on archive.
