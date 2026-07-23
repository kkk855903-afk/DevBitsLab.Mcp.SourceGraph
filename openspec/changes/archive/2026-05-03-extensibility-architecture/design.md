## Context

The plugin model has to satisfy several constraints simultaneously: third-party authors should be able to ship analyzers without touching this repo; Roslyn's `MSBuildWorkspace` is heavy, so plugins should not need their own; storage must remain a single graph (plugins write into the same SQLite, not into per-plugin sidecars); failures in one plugin must never cascade. Refactoring the existing C# indexer behind a contract proves the contract is right.

## Goals / Non-Goals

**Goals:**
- A clear, narrow public SDK that plugins can target without taking on Roslyn directly.
- Hot-pluggability via assembly drop and explicit version pinning via NuGet.
- Robust failure isolation: a crashing plugin marks itself failed and the rest of the system runs.
- The built-in C# indexer is itself implemented as an `ILanguageIndexer` — proves the seam.

**Non-Goals:**
- Sandboxed (untrusted) plugins. We assume plugin authors are trusted (developer machine, opt-in).
- Cross-process plugins. In-process only in v1.
- Live plugin reload. Plugins load at server start; reload requires restart in v1.
- Configuration UI. Plugins read their own config from `.sourcegraph.json` keys they own.

## Decisions

**1. Three independent contracts, one SDK package.**
- `ILanguageIndexer` — `IReadOnlySet<string> FileExtensions`, `Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx)`. Returns events (symbols, refs, edges, attributes). Server fans out events to the store.
- `ICodeAnalyzer` — `string Name`, `Task AnalyzeAsync(AnalyzerContext ctx, IGraphEmitter emitter)`. Receives every document already indexed by a language indexer; emits additional rows.
- `IMcpToolPlugin` — `string Prefix`, `Task RegisterAsync(IToolRegistry registry)`. Plugin's tools end up named `<prefix>.<tool>` to avoid collisions.

**2. Plugin discovery via `.sourcegraph.json`.**
```json
{
  "plugins": [
    { "package": "DevBitsLab.Mcp.SourceGraph.Analyzers.AspNetCore", "version": "1.0.0" },
    { "path": ".sourcegraph/plugins/MyHouseAnalyzer.dll" }
  ]
}
```
NuGet refs are restored at server start (offline-first, then `dotnet restore`); paths are loaded directly.

**3. Per-plugin `AssemblyLoadContext` for isolation.**
Each plugin gets its own ALC. Common dependencies (the SDK contracts, `Microsoft.CodeAnalysis.*`) load from the host context to avoid duplication. Plugin-private deps load into the plugin's ALC.

**4. C# Roslyn indexer becomes the canonical built-in `ILanguageIndexer`.**
Refactor today's `RoslynIndexer` to implement `ILanguageIndexer` directly; the server's pluggable lookup `_languageIndexers[".cs"]` resolves to it. No behavioural change for users; proves the contract.

**5. Failure model.**
Per-plugin `try/catch` in the host. A throwing analyzer is marked `failed` for that index pass; the next pass retries (since file changes are independent). A throwing language indexer is marked `failed` permanently for the affected file; subsequent indexes log but skip. Per-plugin status is surfaced via `plugins list`.

**6. Tool name prefixing.**
Plugin tool names are mandatory-prefixed by the plugin's `Prefix`. The built-in tools (no plugin) keep their bare names. This keeps the agent surface predictable and prevents accidental shadowing.

**7. Programmatic tool registration via MCP SDK.**
The current `ModelContextProtocol` SDK favours static `[McpServerToolType]`/`[McpServerTool]`-attributed methods discovered via `WithToolsFromAssembly()`. A plugin loaded at runtime can't expose static-attributed methods on a type the host scanned at startup, so we use the SDK's `McpServerTool.Create(handler, options)` API per registered delegate and pass the resulting list to `IMcpServerBuilder.WithTools(IEnumerable<McpServerTool>)`. The plugin's `IMcpToolPlugin.RegisterAsync` calls `IToolRegistry.AddTool(name, description, Delegate)`; the host's `ToolRegistry` does the SDK call internally. End-state: built-in tools still come from `WithToolsFromAssembly`, plugin tools come from `WithTools(...)`, both end up in the same MCP catalog.

## Risks / Trade-offs

- **API stability burden.** A public SDK is a contract; we'll have to be conservative about breaking changes. Versioning rule: SDK gets its own semver, plugins declare a min version. Server refuses to load plugins targeting a higher SDK version than it ships.
- **Plugin authors writing buggy AnalyzerContext consumers.** Mitigated by per-document try/catch and a strict timeout (30 s default per analyzer per file).
- **Discovery complexity for end users.** Mitigated by built-in plugins shipped as separate NuGet packages with one-line install.
- **AssemblyLoadContext gotchas with Roslyn.** Roslyn types must be loaded from the host; we'll publish a `Microsoft.CodeAnalysis.*` shared list. Documented in the SDK readme.
- **Plugin tool prefixing might confuse model selection.** Mitigated by clear documentation (`mediatr.find_handlers` vs the built-in `find_definition`); the tool description explains the prefix.
