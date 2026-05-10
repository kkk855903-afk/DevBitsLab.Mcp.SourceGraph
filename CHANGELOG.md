# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The `Tool` package (`DevBitsLab.Mcp.SourceGraph.Tool`) and the plugin `Sdk`
package (`DevBitsLab.Mcp.SourceGraph.Sdk`) are versioned independently — entries
below note which package the change applies to.

## [Unreleased]

### Added
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

[Unreleased]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.7.0...HEAD
[0.7.0]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.6.1...v0.7.0
[0.6.1]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/releases/tag/v0.6.0
