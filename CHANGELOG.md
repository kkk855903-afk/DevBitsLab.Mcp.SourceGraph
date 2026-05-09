# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The `Tool` package (`DevBitsLab.Mcp.SourceGraph.Tool`) and the plugin `Sdk`
package (`DevBitsLab.Mcp.SourceGraph.Sdk`) are versioned independently — entries
below note which package the change applies to.

## [Unreleased]

### Added
- 🌿 Green-leaf brand mark on every built-in MCP tool response so the agent
  (and reading human) can tell at a glance the answer came from sourcegraph
  vs. `Grep` + `Read`. Suppress with `--no-leaf` or `SOURCEGRAPH_NO_LEAF=1`.
  Also leafs the published `ServerInstructions` string. (`add-leaf-brand-mark`)
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
- `find_definition` now ships typed `structuredContent` (`FindDefinitionResult`
  with snake_case JSON properties) alongside renderable prose, plus one
  `resource_link` per hit pointing at `graph://symbol/<id>` for clients with
  richer UI, plus an audience-restricted (`Audience = [Assistant]`) metadata
  block carrying scope id and per-call latency. The remaining tools migrate
  in follow-up commits. (`tool-output-content-blocks` foundation + vertical
  slice; the bulk sweep across the other 17 tools is queued for a future
  `/opsx:apply` session.)
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

### Documentation
- New OpenSpec proposals drafted but not yet applied:
  - `tool-output-content-blocks` Groups 3-8 — bulk sweep of the remaining
    17 tools to the multi-content + structuredContent + resource_link
    protocol; foundation and `find_definition` vertical slice are already
    shipped (this PR).
  - `fix-stranded-reference-edges` — defensive recovery for the incremental
    indexer's "zombie file" state (file's outgoing references cleared in
    pass 1 but never repopulated; SHA-skip preserves the empty state
    indefinitely). Reproduced today against `HistoryTools.cs`. Fix proposal
    adds an `IGraphStore.HasOutgoingReferencesAsync` integrity check at the
    skip boundary plus a try/catch around pass 2's per-file walk.

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
