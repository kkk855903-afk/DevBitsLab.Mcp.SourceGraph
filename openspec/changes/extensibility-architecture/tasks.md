## 1. SDK package

- [ ] 1.1 New project `DevBitsLab.Mcp.SourceGraph.Sdk` (NuGet-publishable). Targets `netstandard2.0` for max compatibility.
- [ ] 1.2 Define `ILanguageIndexer`, `ICodeAnalyzer`, `IMcpToolPlugin` interfaces.
- [ ] 1.3 Define `IndexEvent` discriminated union (`SymbolDeclared`, `EdgeEmitted`, `AttributeAttached`, `ReferenceFound`, `FileScanned`).
- [ ] 1.4 Define `IndexContext`, `AnalyzerContext`, `IGraphEmitter`, `IToolRegistry`.
- [ ] 1.5 Helper class `LanguageIndexerBase` for plugins that don't need everything custom.
- [ ] 1.6 SDK semver tracked separately from server semver.

## 2. Plugin host

- [ ] 2.1 `PluginHost` discovers plugins via `.sourcegraph.json` `plugins[]`.
- [ ] 2.2 NuGet-package plugins resolved via `dotnet restore` against a synthetic `plugins.csproj` cached at `<repo>/.sourcegraph/plugins/restore/`.
- [ ] 2.3 Path-based plugins loaded directly from DLL.
- [ ] 2.4 Per-plugin `AssemblyLoadContext`; shared types (SDK, Roslyn) loaded from host context.
- [ ] 2.5 Plugin status registry: `loaded | failed | disabled`. Surfaced via `plugins list`.

## 3. Refactor Roslyn indexer to implement ILanguageIndexer

- [ ] 3.1 Existing `RoslynIndexer` becomes `RoslynLanguageIndexer : ILanguageIndexer` with `FileExtensions = { ".cs" }`.
- [ ] 3.2 Server-side dispatcher routes documents to the right indexer by extension.
- [ ] 3.3 Built-in indexer registers automatically (no `.sourcegraph.json` entry needed).

## 4. Analyzer pipeline

- [ ] 4.1 Pass 2 in the indexer publishes a stream of indexed documents to registered analyzers.
- [ ] 4.2 Each analyzer runs on a bounded thread pool with a 30 s per-document timeout.
- [ ] 4.3 Analyzer emissions go to the same store via `IGraphEmitter`.

## 5. Tool plugin pipeline

- [ ] 5.1 At MCP host startup, every loaded `IMcpToolPlugin` is invoked with a `IToolRegistry` that wraps `WithTools<T>`.
- [ ] 5.2 Tool names are prefixed with the plugin's `Prefix` (e.g. `mediatr.find_handlers`).
- [ ] 5.3 Built-in tools keep unprefixed names.

## 6. CLI

- [ ] 6.1 `sourcegraph-mcp plugins list` — show every loaded plugin with version, status, path.
- [ ] 6.2 `sourcegraph-mcp plugins info <name>` — show contracts implemented, registered analyzers / tools / file extensions.

## 7. Tests

- [ ] 7.1 Unit-test the SDK contracts and helper base classes.
- [ ] 7.2 A reference plugin (`SamplePlugin`) under `tests/fixtures/` that registers one analyzer (emits a `Decorated` edge for any class with a custom attribute) and one tool (`sample.list_decorated`). Verifies all three contracts roundtrip.
- [ ] 7.3 Failure-isolation test: a plugin whose `IndexAsync` throws is marked `failed`, other plugins keep working, and the host returns success.
- [ ] 7.4 Tool-prefix collision test: built-in `find_definition` and a plugin's `mine.find_definition` both register and dispatch correctly.

## 8. Update specs

- [ ] 8.1 Sync delta specs into existing capabilities and create `openspec/specs/extensibility/spec.md` on archive.
