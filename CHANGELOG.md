# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The `Tool` package (`DevBitsLab.Mcp.SourceGraph.Tool`) and the plugin `Sdk`
package (`DevBitsLab.Mcp.SourceGraph.Sdk`) are versioned independently — entries
below note which package the change applies to.

## [Unreleased]

### Changed
- **`ServerInstructions` slimmed; verbose body moved to `graph://help` resource.**
  The MCP `initialize` preamble dropped from ~22 lines to ~10: it keeps the leaf
  glyph, the prefer-graph rule, and the `usage_stats` reminder, plus a pointer
  to `graph://help`. Agents that want the tool-selection guide or the
  `describe_schema` / `query_graph` ad-hoc-query reference fetch the resource
  on demand instead of paying the upfront cost on every connection. Suppressed
  by the same `--no-instructions` / `SOURCEGRAPH_NO_INSTRUCTIONS=1` knobs as
  before. Leaf glyph unchanged on every surface.

### Added
- **`--no-tool-triggers` / `SOURCEGRAPH_NO_TOOL_TRIGGERS=1`.** Suppresses the
  `Use when: …` append on every tool description in `tools/list`. Useful when
  driving tool-selection guidance from your own `CLAUDE.md` instead. Gates at
  a single chokepoint inside `ToolDescriptionFormatter`, so the flag covers
  both built-in tools and plugin tools registered via `ToolRegistry`.
- **`request_len` field in `.sourcegraph/usage.jsonl`.** Sits next to the
  existing `response_len` so future tool-call cost analysis can compare the
  agent's input against the server's output. Recorded as the string length of
  the serialised `args` JSON (matching how `response_len` is measured — string
  `.Length`, not UTF-8 byte count). No client-visible behaviour change.

## [0.9.0] - 2026-07-24

MedInteropLens 0.9 extends the local source graph from managed-code navigation
to an evidence-backed WPF → gRPC → P/Invoke → C/C++ execution and compatibility
analysis surface. The tool package is `0.9.0`; the independently versioned
plugin SDK is `2.5.0`.

### Added

- **A validated UI-to-native execution state machine.** `trace_call` and
  `trace_call_path` accept `profile="execution"` and enforce the ordered stages
  `binds-path` → `command-executes` → managed `calls` → `grpc-calls` →
  `rpc-dispatches-to` → server `calls` → `pinvoke-maps-to` → native `calls`.
  Calls may repeat only inside their current managed/server/native stage;
  skipped, reversed, and repeated cross-domain transitions are rejected. With
  an exact canonical `from`, callers may omit `to` to discover every proven
  native leaf algorithm. The production fixture proves the minimal contiguous
  eight-hop route, with occurrence-level file/range/producer/confidence evidence.
- **Stable MedInteropLens MCP names.** Added compatibility entry points
  `search_code`, `find_symbol`, `trace_call`, and `impact_analysis`, plus the
  domain tools `trace_binding`, `trace_command`, `check_resources`,
  `trace_rpc`, `check_proto_contract`, `match_pinvoke`, `compare_struct`, and
  `analyze_native_boundary`. Domain queries and execution tracing advertise
  read-only/idempotent MCP hints.
- **WPF semantic projections.** The XAML/Roslyn pipeline now resolves data
  bindings, command properties, command execution, resources, and styles to
  canonical identities when the input proves a unique target. Query results
  preserve resolved/missing/ambiguous/incomplete/unsupported/unknown outcomes
  and exact occurrence evidence.
- **Two conservative WPF risk rules.** `WPFEVENT001` reports a source-defined
  static event retaining a direct named instance handler only when a complete,
  error-free compilation contains no exact matching `-=`. `WPFTHREAD001`
  reports `DispatcherObject` member access from inline callbacks directly
  scheduled by `Task.Run`, ThreadPool queue APIs, or an immediately started
  `Thread`, while recognizing direct Dispatcher marshaling. Lambdas/aliases or
  indirect shapes that cannot prove the event lifetime, and method-group or
  unknown thread-entry shapes, do not warn.
- **Protobuf and gRPC indexing/linking.** Source `.proto` contracts are compiled
  with bundled `protoc` assets and linked to generated managed clients and
  server overrides. The graph persists `grpc-calls`, managed→proto
  `implements-rpc`, and execution-direction proto→handler
  `rpc-dispatches-to`, with managed and protobuf evidence on derived links.
  `check_proto_contract` covers missing implementations, uniquely proven
  generated-signature mismatches, field-number changes, and streaming changes
  against a first complete successful baseline.
- **Target-aware native extraction and graph publication.** Explicit
  per-scope RID/compiler-ABI/pointer-size/pack configuration drives libclang
  translation-unit extraction for C/C++ functions, structs, unions, enums,
  typedefs, record layouts, direct calls, exports, callbacks, exception escape,
  allocation provenance, and optional binary-export verification. Stable Clang
  USRs resolve direct calls and type identities across translation units;
  content-bound snapshots prevent stale evidence from being published after
  source changes.
- **Managed/native matching and rule pack.** Roslyn extracts
  `DllImport`/`LibraryImport`, callback rooting, and return-release usage.
  Exact managed/native matching publishes `pinvoke-maps-to`; uniquely identified
  binary-verified record arguments also publish `struct-maps-to`. Boundary
  analysis reports proven `Interop001` calling-convention, `Interop003`
  parameter-type, `Interop004` callback-GC, `Interop005` native-exception, and
  `Interop006` allocator-mismatch risks. `compare_struct` performs
  target-specific, field-by-field ABI comparison and emits `Interop002`; nested
  records require explicit mappings and are never guessed by name.
- **RID-complete 0.9 packaging.** The outer .NET tool selects implementation
  packages for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, and
  `osx-arm64`, including native libclang and bundled protoc assets. The pinned
  LLVM 21 runtime set has no matching Intel-macOS ClangSharp payload, so
  `osx-x64` is not published. CI installs the packed tool, exercises `--help`,
  bundled `protoc`, and a real native worker/libclang parse on installed Ubuntu,
  Windows, and ARM64 macOS runtimes; Ubuntu also completes a stdio MCP
  `tools/list` handshake.

### Changed

- **Absence is now an explicit completeness claim.** Execution responses expose
  per-projection `status`, `authoritative`, `failure_count`, and
  `retained_last_good`, plus aggregate
  `execution_state.absence_authoritative`. Native-not-configured, partial or
  refreshing gRPC/native projections, retained last-good facts, bounded
  truncation, and graph/runtime-state changes during a read all make empty
  execution results non-authoritative. Existing evidence-backed paths remain
  visible.
- **Derived projections replace atomically and retain last-good evidence on
  incomplete input.** A failed native translation unit or incomplete gRPC
  refresh cannot erase the last complete snapshot or create speculative
  matches/findings. First incomplete observations correctly report no retained
  evidence; retention is claimed only when persisted producer evidence exists.
- **Semantic model download is offline by default.** `serve` and `index` never
  fetch the embedding model unless the operator runs `embeddings pull`, passes
  `--allow-model-download`, or sets
  `SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1`. A populated cache remains usable.

### Security

- **Mandatory local privacy boundary.** Build/output, repository metadata,
  medical-data/image, log/database, dependency, and internal graph paths are
  excluded before indexing. Scope excludes may narrow but never override the
  mandatory set; physical-path validation rejects links that escape the
  repository boundary.
- **Explicit trust for native parsing.** A scope's `interop` block configures
  inputs but does not authorize execution. `NativeParsing` requires an exact
  repository grant in the external user-owned `MedInteropLens/trust-v1.json`;
  missing, malformed, self-hosted, or unsupported trust stores fail closed.
- **Bounded native parser worker.** Each authorized translation unit is parsed
  in a short-lived child process with a sanitized environment, bounded framed
  protocol, 30-second default timeout, and process-tree termination. 0.9
  reports honestly that this baseline does not provide OS network isolation or
  reduced privileges; compiler-controlled file inputs and includes that cannot
  be proven inside the approved scope are rejected.

## [0.8.0] - 2026-05-10

### Fixed
- **Semantic search now actually works on a fresh checkout.** Three coordinated
  changes that together close the gap between "documented as available" and
  "actually loads":
  - `wire-model-autodownload` — `ModelStore.EnsureAsync` is now invoked at
    server startup so the embedding model is fetched from Hugging Face on
    first run (the downloader had been written in the v0.4 semantic-search
    change but never wired into `Program.cs`). Indexing runs concurrently
    with the download via a `ModelDownloadGate` singleton; the embed worker
    awaits the gate before probing `IsAvailable` (sticky), closing the race
    that previously disabled embeddings permanently for the session. New
    `--no-model-download` flag (and `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` env
    var) lets air-gapped operators run against a pre-populated cache without
    enabling the auto-fetch.
  - `migrate-to-ml-tokenizers` — replaced the BERT-only `FastBertTokenizer`
    with `Microsoft.ML.Tokenizers` 2.0 so the documented default model
    `jinaai/jina-embeddings-v2-base-code` (RoBERTa-tokenized BPE) actually
    loads. Previous library threw a `JsonException` on the upstream
    `tokenizer.json`, silently disabling semantic search even when the model
    file was on disk. New library handles both BPE (RoBERTa, Jina, BGE-M3)
    and WordPiece (BERT) from one API surface — dispatch keys off the
    `model.type` field of `tokenizer.json` at load time.
  - Pinned SHA-256 hashes for the default model's `model.onnx` and
    `tokenizer.json` so corrupted / tampered downloads fail loudly instead
    of poisoning the cache. Override-model paths (`--model <id>`) remain
    best-effort with no SHA verification.

### Added
- **Embedding cache management surface.** New CLI subcommand group
  `sourcegraph-mcp embeddings <status|pull|remove|verify>` and matching MCP
  tools `embeddings_status` / `embeddings_pull` / `embeddings_remove` /
  `embeddings_verify`. `status` reports the cache directory, model id +
  dimension, per-file presence/size/SHA, and free disk on the cache volume
  — first stop when the new `--no-model-download` warning fires. `remove`
  defaults to the active model; `--all` wipes every cached model directory;
  combining `--model X --all` is rejected. Mutating MCP tools carry MCP-spec
  `destructiveHint` / `idempotentHint` annotations via a new `[ToolAnnotation]`
  attribute + post-build walker so spec-aware hosts (Claude Code) prompt
  before invocation. (`add-embeddings-cli-and-tools`)
- **Cold-start progress visibility.** When a progress-aware tool
  (`find_definition`, `semantic_search`, `impact_of_change`, `module_summary`)
  is invoked with a `progressToken` against a scope whose initial indexing
  isn't finished, the server forwards per-scope phase progress (`opening
  workspace` → `indexing` → `ready`) as `notifications/progress` for the
  duration of the wait. The previously silent 10–60 s pause on cold-start
  now narrates itself in the chat panel. The mechanism is a per-scope
  `IIndexingProgressSource` exposed by `LiveIndexService` and a forwarding
  subscription inside `ScopedExecution.WaitUntilReadyAsync`. (`improve-first-run-progress`)
- **Onboarding CLI: `init`, `doctor`, `demo`.** Three new subcommands handle
  first-run setup end-to-end. `sourcegraph-mcp init` is interactive by default
  (or `--yes`-driven for CI); detects environment, picks MCP clients, and
  writes per-client config files with merge-by-name semantics — first-class
  support for Claude Code, **GitHub Copilot** (distinct `servers` / `type:
  "stdio"` schema in `.vscode/mcp.json`), Cursor, Continue, and Claude
  Desktop. Project-scoped writes are the default; user-scope writes require
  explicit `--user-<client>` opt-in (or `--claude-desktop` for Claude Desktop,
  which has no project equivalent). Existing client config files are merged
  into, never overwritten — only the `sourcegraph` server entry is touched,
  and `--force` is required to replace a differing existing entry. `doctor`
  runs a read-only environment diagnostic with `pass | warn | fail` exit
  codes (0 / 2 / 1) and a `--json` machine-readable mode. `demo` runs four
  canned operations (`ping`, `graph_stats`, `search_symbols`,
  `find_definition`) against the active scope and prints leaf-stamped
  markdown — the same shape an agent sees, providing the "ah, it works"
  confidence moment without an agent loop. Comment-aware degraded mode keeps
  hand-edited JSONC configs from being silently round-tripped through the
  parser. (`add-onboarding-cli`)
- **`partial` scope status with per-project + per-file failure isolation.** A
  single bad project in a multi-project solution no longer marks the whole
  scope `degraded`. The Roslyn indexer pre-flight-probes each project's
  `Compilation`, skipping ones that throw or return null. Pass 1B's per-document
  symbol walk is wrapped in try/catch with a `walkedFileIds` gate so
  reconcile / Pass 2 / Pass 3 preserve a failing file's prior store state
  rather than corrupting it with an incomplete walk. `IndexResult` gains
  `FailedProjects` / `FailedFiles`; `LiveIndexService` settles the scope to
  `partial` (at least one project produced symbols and something failed),
  `degraded` (every project failed or workspace open threw), or `ok`.
  `list_scopes` exposes `failed_projects` / `failed_files` arrays in both
  prose and structured output; `_meta.db` persists them as JSON columns so
  the failure detail survives server restarts. `scope = "*"` fan-out
  includes `partial` scopes alongside `ok`. (`add-per-project-failure-isolation`)

### Changed
- **Soft size budget on list-shaped tool results.** Built-in tools that emit
  per-row prose + `ResourceLinkBlock` + structured-content trios now trim
  upstream of emission when the projected serialized size would exceed
  Claude Code's ~64K-character per-`tools/call` ceiling. Wired into
  `find_references`, `list_members`, `list_symbols_in_file`, and
  `semantic_search` (the four highest-risk helpers). When the cap activates,
  the tool appends `omitted_size=N` to its existing audience-restricted
  `_meta:` block so the connected model can detect truncation and re-query.
  Also lowers two default `limit` values that routinely overran the cap:
  `find_references` 200 → 50 (matches `list_callers` / `list_callees` /
  `find_implementations`) and `list_members` 200 → 100. Callers needing more
  pass an explicit `limit=`. (`output-budget-cap`)

### Added
- **Generic tree-sitter language-indexer host.** New in-tree
  `DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter` assembly with an abstract
  `TreeSitterLanguageIndexer<TGrammarConfig>` base that future per-language
  plugins (TypeScript, Python, Go, Rust, …) subclass. SDK 2.1.0 → 2.2.0 adds
  `INodeKindMapper`, `IModuleResolver`, `LanguageIndexerOptions`, and
  `ITreeSitterGrammarConfig` — the four contracts a tree-sitter-backed plugin
  sits on. `TreeSitter.DotNet 1.3.0` is brought in transitively, shipping
  `libtree-sitter` plus 28+ language grammar binaries across every target RID
  (no first-party native packaging in this repo). Scope-config gains optional
  `language` (kebab-case identifier) and `enrichment` (forward-declared,
  carries one nested `lsp: { command, args }` block) fields. New
  `sourcegraph-mcp scopes info <name> [--json]` CLI subcommand surfaces
  identity / project set / language / enrichment for a single scope. No
  schema changes — the host emits no rows of its own; concrete languages do.
  (`add-tree-sitter-language-indexer-host`)
- **Built-in TypeScript / JavaScript / TSX / JSX indexer.** New in-tree
  `DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript` assembly registered for
  `.ts` / `.tsx` / `.js` / `.jsx`. Per-extension grammar dispatch (TypeScript
  / TSX / JavaScript). Emits the eight TypeScript symbol kinds documented in
  the design (function / class / interface / type-alias / enum / method /
  field / variable / constant / namespace), call-expression and type-identifier
  references, and JSX `instantiates` edges for PascalCase components carrying
  a JSON-encoded `props` payload. Edge sources prefer the nearest enclosing
  named declaration (function / class / const) and fall back to a synthesised
  file-namespace symbol so `GraphStoreEmitter` can resolve every JSX edge at
  flush time. Default scope excludes for `node_modules` / `dist` / `.next` /
  `build` / `coverage` / `.cache` / `.parcel-cache` / `out` keep a fresh
  install from indexing dependency trees. Lifts the canonical-key schemes
  `js` / `ts` / `jsx` / `tsx` from reserved-rejected to reserved-accepted in
  the SDK validator. New `BuiltInIndexers.RegisterAll(...)` helper centralises
  the in-tree indexer set across the `serve` and `index` CLI paths.
  Cross-file ref resolution (tsconfig `paths`, re-export chase) and LSP
  enrichment via `typescript-language-server` are deferred to follow-up
  changes. (`add-typescript-language-indexer`)
- **Live `.sourcegraph.json` reload — no restart required.** New
  `ScopeConfigWatcher` (mtime-polled at 200ms) plus `ScopeDiff` +
  `ScopeRouter.Replace`/`Unregister` primitives feed
  `LiveIndexService.OnConfigChangedAsync`, which routes each save through the
  four delta kinds: **add** (new scope passes through the existing
  `PrepareScopeAsync` → `RunInitialIndexAsync` → `StartWatcher` chain),
  **remove** (host disposed, registry row dropped, on-disk per-scope DB
  preserved as a re-add cache), **modify** (atomic-swap via
  `ScopeRouter.Replace` with a 5s deferred-disposal grace window so in-flight
  tool calls against the old host complete cleanly), and **default-scope**
  flip (router metadata only, no scope is reindexed). Malformed saves are
  parse-tolerant: the watcher logs at info and emits nothing, leaving the
  running scope set untouched until the next valid save. File deletion (or
  rename away from the repo root) reverts to the synthesised default. Plugin
  changes (`plugins[]`) are explicitly NOT live-reloadable: a save that
  touches the array logs a warning and otherwise applies any concurrent
  scope deltas. The `--solution` CLI override disables the watcher (the JSON
  is bypassed at startup, so the live path follows suit). Watcher uses mtime
  polling rather than `FileSystemWatcher` for cross-platform reliability —
  macOS's FSEventStream backend doesn't deliver events for files at the
  watched directory's root, and 200ms latency is below any human's edit
  cadence. `SOURCEGRAPH_SCOPE_REPLACE_GRACE_MS` env var lets test harnesses
  shrink the grace window. (`watch-scope-config`)

- **Two new MCP tools for payload-aware edge walks: `find_data_bindings`
  and `find_event_handlers`.** Specialised tool surface over the
  `binds-path` and `handles-event` edge kinds, with named parameter knobs
  matching the SDK `PayloadKeys` constants — `path` (substring) / `mode`
  (exact) / `converter` (exact) plus optional `target` / `source`
  canonical-key narrowing for `find_data_bindings`; `event` / `command`
  plus optional `handler` / `element` for `find_event_handlers`. Soft-empty
  `note:` line when the active scope's loaded indexers don't emit the
  queried edge kind (mirrors the lenient `list_callers --kind=…` pattern).
  Both tools ship typed `structuredContent` (`FindDataBindingsResult` /
  `FindEventHandlersResult`) alongside the always-render-payload markdown.
  No SDK changes, no schema changes — `payload` column was already present
  from `open-language-contract`. (`payload-tooling`)
- **Built-in XAML indexer.** New in-tree
  `DevBitsLab.Mcp.SourceGraph.Indexing.Xaml` assembly registered for `.xaml`
  files. Indexes WPF / WinUI 3 / UWP / Avalonia / Uno from a single indexer
  with framework-profile auto-detection. Emits five symbol kinds
  (`xaml-view`, `xaml-element`, `xaml-resource`, `xaml-style`,
  `xaml-template`), eight cross-language edge kinds (`code-behind`,
  `binds-path`, `binds-element`, `handles-event`, `uses-resource`,
  `instantiates-type`, `merges`, `applies-style`), and one annotation
  flavor (`xaml-attached-property`). Cross-language joins to the C#
  Roslyn graph go through string equality on `symbols.canonical_key`
  via the `CanonicalKeys` helpers (e.g. `x:Class="MyApp.Views.Main"` →
  `csharp:T:MyApp.Views.Main`). Per-project resource cascade cache built
  once at scope startup from `App.xaml`'s `Application.Resources`,
  `MergedDictionaries`, and `Themes/Generic.xaml`.
  (`xaml-language-indexer`)
- **Per-scope `ILanguageProjectFactory` discovery.** `PluginHost` now
  activates `ILanguageProjectFactory` instances from registered plugins
  alongside `ILanguageIndexer` ones; new `LanguageProjectFactoryRegistry`
  feeds a per-scope `ScopeHost.ProjectByFilePath` map populated at scope
  startup. The new `LanguageIndexerDispatcher` walks every non-`.cs` file
  whose extension has a registered indexer and routes it through that
  indexer with `IndexContext.Project` populated. The existing C# bulk
  pathway is unchanged; the deferred 5.3 / 6.1 / 6.2 plumbing from
  `open-language-contract` lands here as the carryover. (`xaml-language-indexer`)
- 🌿 Green-leaf brand mark on every built-in MCP tool response so the agent
  (and reading human) can tell at a glance the answer came from sourcegraph
  vs. `Grep` + `Read`. Suppress with `--no-leaf` or `SOURCEGRAPH_NO_LEAF=1`.
  Also leafs the published `ServerInstructions` string. (`add-leaf-brand-mark`)
- 🌿 Per-tool brand mark on every built-in MCP tool's catalog identity in
  `tools/list`: `Tool.Title` is set to `🌿 <name>` (e.g. `🌿 find_definition`)
  and `Tool.Description` is `🌿 `-prefixed. Surfaces the brand in MCP clients
  that render tool selectors / hover cards / structured detail rather than
  per-call prose, where the existing `add-leaf-brand-mark` head prefix can be
  hidden. Plugin-registered tools are skipped (first-party voice only).
  Suppression covered by the same `--no-leaf` / `SOURCEGRAPH_NO_LEAF=1` knob.
  (`add-leaf-to-tool-identity`)
- Markdown tables for list-shaped tool results when the row count is ≥ 2:
  `find_references`, `find_by_annotation`, `search_symbols`, `list_callers`,
  `list_callees`, `find_implementations`, `list_members`, `semantic_search`,
  `find_diagnostics`, `recent_changes`, `list_tests_for`, `impact_of_change`,
  `module_summary`, plus the inbound/outbound sections of `neighborhood`.
  Single-result responses keep their existing bulleted form. Hierarchical
  tools (`find_definition`, `list_symbols_in_file`) stay bulleted because
  per-row nesting (xml summary, annotations, history) doesn't fit a table
  cleanly. (`polish-tool-output-markdown`)
- MCP `notifications/progress` on three slow tools — `semantic_search`
  (cold-start ONNX model load), `impact_of_change`, `module_summary`. Clients
  opt in by sending a `progressToken` on the originating `tools/call` request;
  no-op otherwise. (`report-progress-on-slow-tools`)
- All 20 built-in MCP tools now ship typed `structuredContent` alongside
  renderable prose, with `outputSchema` declared on `tools/list`. Each tool
  emits one `resource_link` per result row pointing at the corresponding
  `graph://symbol/<id>`, `graph://file/<path>`, or `graph://namespace/<name>`
  resource, plus a trailing audience-restricted (`Audience = [Assistant]`,
  `Priority = 0.2`) metadata block carrying scope id, latency, and per-tool
  row counts. Field names use snake_case on the wire, with
  `[JsonPropertyName]` overrides on every multi-word DTO field so the
  exporter-derived `outputSchema` and the source-gen-derived
  `structuredContent` payload converge on the same casing. Older clients that
  ignore `structuredContent` / `resource_link` see a complete prose answer;
  clients that respect `audience` annotations filter the metadata block out
  of the user view. (`tool-output-content-blocks`)
- **Sdk 2.1.0** — `PayloadKeys` static class with kebab-case constants
  for the well-known keys plugins put in `EdgeEmitted.Metadata` (`path`,
  `mode`, `converter`, `converter-parameter`, `event`, `handler`,
  `data-type`, `target-type`, `key`, `based-on`, `element-name`,
  `relative-source`, `fallback-value`, `string-format`,
  `update-source-trigger`). Locks the wire vocabulary before any
  cross-language indexer emits.
- **Sdk 2.1.0** — `CanonicalKeys` helpers (`ForType`, `ForMethod`,
  `ForField`, `ForProperty`) constructing doc-comment-id-shaped C#
  canonical keys (`csharp:T:` / `csharp:M:` / `csharp:F:` / `csharp:P:`).
  Cross-language plugins reuse these instead of reimplementing Roslyn's
  format; tested for byte-equality against
  `ISymbol.GetDocumentationCommentId()`.
- Out-of-process stdio MCP integration test project
  (`tests/DevBitsLab.Mcp.SourceGraph.IntegrationTests/`) using
  `ModelContextProtocol.Client` + `StdioClientTransport`; locks the
  `Capabilities.Experimental["sourcegraph.vocabulary"]` contract on
  every `initialize` against a freshly-spawned server.
- `QueryPlanTests` — `EXPLAIN QUERY PLAN` regression that asserts the
  four hot edge-walking SQL paths use `idx_edges_kind_name` /
  `idx_edges_dst` (or the PK auto-index) and never fall back to
  `SCAN edges`. Pins index selection across schema tweaks.
- `list_callers`, `list_callees`, and `neighborhood` now render a
  non-null `payload` JSON value as an indented sub-line under each
  edge row (capped at 5 keys with `(N more)` overflow). No-op for
  current data; lights up the moment any indexer fills the column.
- `sourcegraph-mcp vocabulary list` CLI subcommand — per-scope
  diagnostic over the soft-registry kind vocabulary with source
  attribution (`sdk` / `plugin: <id>@<version>` / `unknown`) and live
  emission counts; Levenshtein-≤2 drift detection inside each scope's
  kind list; optional `--strict` flag for CI gating.
- Multi-OS CI workflow (`ci.yml`) running build + test on
  `ubuntu-latest`, `macos-latest`, and `windows-latest` for every push and PR.
- CodeQL static analysis (`codeql.yml`) on push, PR, and a weekly schedule.
- Dependabot configuration for NuGet packages (grouped by family) and
  GitHub Actions.
- Test gate added to the release workflow — `dotnet test` now blocks NuGet
  publishes from a failing build.
- Deterministic build settings (`Deterministic`, `EmbedUntrackedSources`) and
  `Microsoft.SourceLink.GitHub` wiring in `Directory.Build.props`.
- `SECURITY.md` with private vulnerability-disclosure channels and supported
  versions table.
- `CONTRIBUTING.md` covering coding conventions, test conventions, the MCP-tool
  authoring checklist, and the release flow.
- `CODE_OF_CONDUCT.md` (Contributor Covenant v2.1).
- `MAINTAINERS.md` and `GOVERNANCE.md`.
- Issue and pull-request templates under `.github/`.
- JSON Schema for `.sourcegraph.json` (`schema/sourcegraph.schema.json`) so
  editors validate scope/plugin configuration.
- OpenTelemetry instrumentation: `ActivitySource("DevBitsLab.Mcp.SourceGraph")`
  spans and `Meter("DevBitsLab.Mcp.SourceGraph")` counters/histograms emitted
  from every wrapped MCP tool call. Disabled at zero cost when no listener is
  attached.
- BenchmarkDotNet project (`bench/DevBitsLab.Mcp.SourceGraph.Benchmarks`) with
  baseline scenarios for indexing throughput and graph-query latency.
- `docs/ARCHITECTURE.md` describing module boundaries, the indexing pipeline,
  and the scope-router data flow.
- README sections covering the platform support matrix and configurable
  resource limits.

### Changed
- Tool response lead-in lines tightened for token economy:
  `Found N match(es) for 'X':` → `🌿 N hits for 'X':`,
  `No definition found for 'X'.` → `🌿 No matches for 'X'.`,
  collective `(s)` plurals dropped (`5 symbol(s) carry [Foo]:` →
  `🌿 5 symbols carry [Foo]:`). Net-positive across a typical session even
  after the leaf glyph is added. (Lands with `add-leaf-brand-mark`.)
- Single-host implicit-default scope responses no longer prefix with an
  italic `_(scope: \`default\`)_` line. Removing it gives the brand mark
  prime first-line real estate, adjacent to substantive content rather
  than chrome. Agents that need to know which scope answered can still
  call `list_scopes`, read the `mcp.tool.scope` OTel tag, or inspect the
  per-call `usage.jsonl` log entry. Multi-scope explicit fan-out is
  unchanged (per-scope `### scope: <id>` headers still appear).
  (`drop-implicit-scope-annotation`)
- Indexer now wraps the `Ping` tool through `ToolMetrics.TrackSync`. It
  was bypassing the chokepoint entirely — no leaf, no telemetry. Same
  `pong @ <iso-time>` payload, just with the standard observability
  surfaces around it.

### Fixed
- `Capabilities.Experimental["sourcegraph.vocabulary"]` no longer crashes
  the `initialize` handler under MCP SDK 1.2.0's source-generated JSON
  context. The payload was an anonymous type rejected by
  `McpJsonUtilities+JsonContext`; replaced with a `JsonObject` graph
  that the SDK's context handles natively. Wire shape unchanged.
  (`fix-initialize-vocabulary-serialization` — landed independently from
  main's `harden-sdk-pre-xaml` change, which carries the same fix.)
- **Self-heal stranded reference edges.** Pass 1's "unchanged file"
  SHA-skip path now requires that a symbol-bearing file has at least one
  outgoing pass-2 artifact in the store (a ref row, or an outgoing edge
  from a symbol declared in the file) before skipping pass 2; files
  whose refs and edges were cleared but never repopulated (transient
  compile gap, exception in the per-file walk) get re-walked
  automatically on the next index. Pass 2's per-file body is wrapped in
  a try/catch so one file's walk failure no longer aborts the whole
  loop, and a post-failure clear inside the catch drops any partial
  refs-only commit so the next index detects the zombie state. New
  `IGraphStore.HasOutgoingReferencesAsync` storage method (default
  `true`; `SqliteGraphStore` overrides with an indexed `refs` OR
  `edges JOIN symbols.file_id` EXISTS probe). Recovery emits an
  info-level log line per affected file.
  (`fix-stranded-reference-edges`)

## [0.7.0] - 2026-05-06

### Added
- Server publishes tool-usage instructions in the MCP `initialize` response;
  individual tools self-declare `[ToolTrigger]` strings.

### Changed
- `IToolRegistry` gains a binary-compatible 4-arg `AddTool` overload
  (`Sdk` 1.0.0 → 1.1.0). Plugins compiled against 1.0.0 keep working.

## [0.6.1] - 2026-04-29

### Fixed
- `DiagnosticsAndGeneratorTests` now build the source-generator fixture before
  running.

## [0.6.0] - 2026-04-23

Initial public release covering Roslyn-backed indexing, FTS5 name search,
optional ONNX semantic search, multi-solution scopes, the live file/git
watcher, and the plugin SDK.

[Unreleased]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.9.0...HEAD
[0.9.0]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.8.0...v0.9.0
[0.8.0]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.6.1...v0.7.0
[0.6.1]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/releases/tag/v0.6.0
