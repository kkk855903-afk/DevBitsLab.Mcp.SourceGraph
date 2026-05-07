# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The `Tool` package (`DevBitsLab.Mcp.SourceGraph.Tool`) and the plugin `Sdk`
package (`DevBitsLab.Mcp.SourceGraph.Sdk`) are versioned independently — entries
below note which package the change applies to.

## [Unreleased]

### Added
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
