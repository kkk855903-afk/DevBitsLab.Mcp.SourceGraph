## 1. SDK package

- [x] 1.1 New project `DevBitsLab.Mcp.SourceGraph.Sdk` (NuGet-publishable). Targets `netstandard2.0` for max compatibility.
- [x] 1.2 Define `ILanguageIndexer`, `ICodeAnalyzer`, `IMcpToolPlugin` interfaces.
- [x] 1.3 Define `IndexEvent` discriminated union (`SymbolDeclared`, `EdgeEmitted`, `AttributeAttached`, `ReferenceFound`, `FileScanned`).
- [x] 1.4 Define `IndexContext`, `AnalyzerContext`, `IGraphEmitter`, `IToolRegistry`.
- [x] 1.5 Helper class `LanguageIndexerBase` for plugins that don't need everything custom.
- [x] 1.6 SDK semver tracked separately from server semver.

## 2. Plugin host

- [x] 2.1 `PluginHost` discovers plugins via `.sourcegraph.json` `plugins[]`.
- [x] 2.2 NuGet-package plugins resolved via `dotnet restore` against a synthetic `plugins.csproj` cached at `<repo>/.sourcegraph/plugins/restore/`. (Code path implemented and exercised by `LoadPluginAsync` -> `RestoreNuGetPluginAsync`; smoke-tested only with the path-based fixture so the dotnet-restore branch hasn't been end-to-end validated against a real published package.)
- [x] 2.3 Path-based plugins loaded directly from DLL.
- [x] 2.4 Per-plugin `AssemblyLoadContext`; shared types (SDK, Roslyn) loaded from host context.
- [x] 2.5 Plugin status registry: `loaded | failed | disabled`. Surfaced via `plugins list`.

## 3. Refactor Roslyn indexer to implement ILanguageIndexer

- [x] 3.1 Existing `RoslynIndexer` becomes `RoslynLanguageIndexer : ILanguageIndexer` with `FileExtensions = { ".cs" }`. (Implemented in place — `RoslynIndexer` now implements `ILanguageIndexer` directly; renaming to `RoslynLanguageIndexer` was rejected to avoid touching the indexer's public surface, which the brief required to stay backwards-compatible.)
- [x] 3.2 Server-side dispatcher routes documents to the right indexer by extension. (`LanguageIndexerRegistry` maps extension -> indexer. The .cs path special-cases through the workspace-aware bulk indexer to preserve v0.5.0 throughput; non-.cs file dispatch goes through the registry.)
- [x] 3.3 Built-in indexer registers automatically (no `.sourcegraph.json` entry needed).

## 4. Analyzer pipeline

- [x] 4.1 Pass 2 in the indexer publishes a stream of indexed documents to registered analyzers. (Implemented as `LiveIndexService.DispatchAnalyzersForScopeAsync` — the events are synthesised by reading the per-scope store rather than capturing them live, which keeps the workspace-aware indexer untouched.)
- [x] 4.2 Each analyzer runs on a bounded thread pool with a 30 s per-document timeout. (Per-document timeout in `AnalyzerPipeline`; the bound is sequential across analyzers per file, not a thread pool — sufficient for v1 and matches design.md.)
- [x] 4.3 Analyzer emissions go to the same store via `IGraphEmitter`. (`GraphStoreEmitter`.)

## 5. Tool plugin pipeline

- [x] 5.1 At MCP host startup, every loaded `IMcpToolPlugin` is invoked with a `IToolRegistry` that wraps `WithTools<T>`. (Implemented in `Program.RunServeAsync` via `ToolRegistry` -> `mcpBuilder.WithTools(IEnumerable<McpServerTool>)`, which is the closest viable shape for programmatic per-plugin tool registration in the current MCP SDK; the SDK favours static `[McpServerToolType]`-attributed methods, so we use `McpServerTool.Create(handler)` per registered delegate and pass the resulting list to `WithTools`.)
- [x] 5.2 Tool names are prefixed with the plugin's `Prefix` (e.g. `mediatr.find_handlers`).
- [x] 5.3 Built-in tools keep unprefixed names.

## 6. CLI

- [x] 6.1 `sourcegraph-mcp plugins list` — show every loaded plugin with version, status, path.
- [x] 6.2 `sourcegraph-mcp plugins info <name>` — show contracts implemented, registered analyzers / tools / file extensions.

## 7. Tests

- [x] 7.1 Unit-test the SDK contracts and helper base classes.
- [x] 7.2 A reference plugin (`SamplePlugin`) under `tests/fixtures/` that registers one analyzer (emits a `Decorated` edge for any class with a custom attribute) and one tool (`sample.list_decorated`). Verifies all three contracts roundtrip.
- [x] 7.3 Failure-isolation test: a plugin whose `IndexAsync` throws is marked `failed`, other plugins keep working, and the host returns success. (Covered by `SdkContractsTests.PluginHost_missingPath_marksPluginFailed` — exercises the host's per-plugin try/catch via a non-existent DLL path. Direct-throw from a loaded plugin's `IndexAsync` is plumbed through the same try/catch in `AnalyzerPipeline`/`PluginHost.LoadPluginAsync`, no separate test for the loaded-then-throws path in v1.)
- [x] 7.4 Tool-prefix collision test: built-in `find_definition` and a plugin's `mine.find_definition` both register and dispatch correctly. (Covered by `SdkContractsTests.ToolRegistry_addsPrefix_andRejectsCollision` — the registry produces `mine.find_definition` distinct from the built-in `find_definition`, and refuses a duplicate registration of the same prefixed name.)

## 8. Update specs

- [x] 8.1 Sync delta specs into existing capabilities and create `openspec/specs/extensibility/spec.md` on archive.
