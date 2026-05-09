# Contributing to 🌿 DevBitsLab.Mcp.SourceGraph

Thanks for your interest in contributing! This document covers everything you
need to get a change reviewed and merged.

## Code of Conduct

This project adopts the [Contributor Covenant](CODE_OF_CONDUCT.md).
By participating you agree to uphold it. Report unacceptable behaviour to
`jacques.bourque@gmail.com`.

## Quick start

```bash
git clone https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph.git
cd DevBitsLab.Mcp.SourceGraph
dotnet build
dotnet test
```

Requirements: **.NET 10 SDK** (pinned in [global.json](global.json)). The repo
restores everything else via NuGet on first build.

## How to propose a change

1. **Open an issue first** for anything larger than a typo or a one-line bug
   fix. Sketch the design briefly so we can agree on scope before code is
   written. For larger changes, an [OpenSpec proposal](openspec/) is welcome —
   see `openspec/AGENTS.md`.
2. **Fork and branch.** Branch from `main`. Use a descriptive name:
   `fix/scope-router-null-deref`, `feat/list-callers-include-tests`.
3. **Make focused commits.** Prefer small, logically-isolated commits with
   imperative-mood messages: `fix: stop watcher from re-indexing on .sourcegraph writes`.
4. **Add tests.** Every fix gets a regression test. Every feature gets at least
   one happy-path test plus the obvious failure mode. Tests live under
   [tests/DevBitsLab.Mcp.SourceGraph.Tests](tests/DevBitsLab.Mcp.SourceGraph.Tests/).
5. **Run `dotnet build` and `dotnet test` locally** before opening the PR.
   `TreatWarningsAsErrors=true` is on, so warnings fail the build.
6. **Open the PR** against `main`. Fill in the template. Link related issues.

## What lives where

| Path | Purpose |
|---|---|
| `src/DevBitsLab.Mcp.SourceGraph.Core/` | Domain types — Scope, Symbol, Edge records. No I/O. |
| `src/DevBitsLab.Mcp.SourceGraph.Storage/` | SQLite + FTS5 store, scope registry, config loader. |
| `src/DevBitsLab.Mcp.SourceGraph.Indexing/` | Roslyn workspace + symbol-index pipeline. |
| `src/DevBitsLab.Mcp.SourceGraph.Embeddings/` | ONNX + `sqlite-vec` semantic indexer (gated). |
| `src/DevBitsLab.Mcp.SourceGraph.Watcher/` | File + git HEAD watcher with debounce. |
| `src/DevBitsLab.Mcp.SourceGraph.Server/` | MCP stdio host, CLI, scope router, MCP tool implementations. |
| `src/DevBitsLab.Mcp.SourceGraph.Sdk/` | Public plugin SDK — `netstandard2.0`, kept binary-compatible. |
| `tests/DevBitsLab.Mcp.SourceGraph.Tests/` | xUnit + FluentAssertions test suite. |
| `tests/fixtures/` | Sample C# solutions used by integration tests. |
| `bench/DevBitsLab.Mcp.SourceGraph.Benchmarks/` | BenchmarkDotNet performance suite. |

The architecture overview lives in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Coding conventions

- **C# 13 / .NET 10**, `Nullable=enable`, `ImplicitUsings=enable`.
- **No suppressions of warnings** without a comment explaining why. Warnings
  fail the build.
- **Records over classes** for immutable data, sealed by default.
- **`async` methods end in `Async`**, accept and pass `CancellationToken`.
- **Logging via `ILogger<T>`**. No `Console.WriteLine` outside `Cli/`.
- **No new dependencies** without justification in the PR description. We pay
  every transitive dependency in startup time and supply-chain surface.
- **`InternalsVisibleTo`** is allowed for the test project. Avoid it across
  `src/` projects unless the alternative is significantly worse.
- **Public API in `Sdk/` is binary-compatible.** New methods are additive.
  Breaking changes require an SDK major bump and a changelog note.

## Testing conventions

- xUnit for the test framework, FluentAssertions for readability.
- Test classes mirror the file structure of `src/`.
- Integration tests that spin up a Roslyn workspace use the fixtures under
  `tests/fixtures/`.
- New MCP tools must be exercised by at least one end-to-end test that goes
  through `McpServerToolType` registration — see `ToolTriggerTests.cs` for the
  pattern.

## Adding a new MCP tool

1. Implement the tool in `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/`,
   following the existing `[McpServerTool]` shape and decorating with
   `[ToolTrigger("\"natural-language question?\"")]` (just the trigger
   phrase — the host appends the literal `Use when: ` prefix at
   registration). See `Tools/GraphTools.cs` for examples like
   `[ToolTrigger("\"where is X defined?\"")]`. The trigger is published
   at handshake as model-side guidance.
2. Wrap the body in `ToolMetrics.TrackAsync(...)` so calls flow through
   structured logging, the JSONL log, and OpenTelemetry signals.
3. Add a row to the tool reference table in [README.md](README.md).
4. Add at least one test under
   `tests/DevBitsLab.Mcp.SourceGraph.Tests/Tools/`.

## Performance work

Benchmark before / after with [BenchmarkDotNet](https://benchmarkdotnet.org):

```bash
dotnet run -c Release --project bench/DevBitsLab.Mcp.SourceGraph.Benchmarks -- --filter '*YourScenario*'
```

Paste the BDN summary table into the PR description for any change that claims
a perf win.

## Releasing

Releases are issued by maintainers via the `Release` GitHub Action
(`.github/workflows/publish-nuget.yml`). The workflow:

1. Bumps `<Version>` on the chosen csproj (`patch` / `minor` / `major` / `x.y.z`).
2. Tags `vX.Y.Z` and pushes.
3. Runs the full build + test suite. **A failing test blocks the release.**
4. Packs and pushes to NuGet.

Add an entry to [CHANGELOG.md](CHANGELOG.md) in the same PR as the change,
under the `## [Unreleased]` heading. The release commit promotes that section
to a new version heading.

## Filing security issues

Please follow [SECURITY.md](SECURITY.md). **Do not** open a public issue or PR
for a vulnerability.

## License

By contributing you agree that your contribution is licensed under the
[MIT License](LICENSE).
