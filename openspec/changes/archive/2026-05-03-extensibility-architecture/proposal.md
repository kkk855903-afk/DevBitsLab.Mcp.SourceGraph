## Why

Today the indexer is a closed pipeline: only Roslyn, only C#, only the hard-coded edge / reference / tool surface. Every interesting question that needs codebase-specific knowledge — *"list all MediatR handlers"*, *"find every Avalonia `[StyledProperty]` declaration"*, *"which classes are `Singleton` per our DI conventions?"* — requires either a code change in this repo or a workaround. We want the system to be **extensible** along three independent axes so consumers can register their own knowledge without forking:

1. **Languages** — a third-party plugin teaches the indexer about TypeScript / Python / Go (the M10 future), or a niche DSL.
2. **Analyzers** — a Roslyn-style visitor adds custom symbols, edges, or attributes (e.g., a routing analyzer that emits `Endpoint` symbols from minimal-API maps).
3. **Tools** — a plugin registers additional MCP tools that expose its analyzer's output (e.g., `find_endpoints`, `list_handlers`).

This change defines a stable plugin contract for all three and a discovery mechanism (DLL drop or NuGet reference) so plugins can be developed and shipped separately.

## What Changes

- New capability `extensibility`.
- Three contracts:
  - `ILanguageIndexer` — file extension filter + parse + symbol/ref emit. Each plugin registers for one or more file extensions.
  - `ICodeAnalyzer` — invoked during pass 2 with a per-document context; emits additional `Symbol`, `Edge`, `AttributeRecord`, or `SymbolReference` rows.
  - `IMcpToolPlugin` — registers additional `[McpServerTool]`-attributed methods, scoped to a tool prefix to avoid collision (e.g. `mediatr.find_handlers`).
- Plugin discovery via:
  - `.sourcegraph.json` `plugins[]` array (recommended, version-pinned).
  - Directory drop at `<repo>/.sourcegraph/plugins/*.dll` (advanced).
  - Built-ins shipped in their own NuGet packages (e.g. `DevBitsLab.Mcp.SourceGraph.Analyzers.AspNetCore`).
- Plugin host: `AssemblyLoadContext`-isolated, per-plugin try/catch so a buggy plugin can't kill the host.
- New CLI command `plugins list/info` to introspect what's loaded.
- Plugin SDK package `DevBitsLab.Mcp.SourceGraph.Sdk` with the contracts and helpers.

## Capabilities

### New Capabilities

- `extensibility`: plugin contracts, discovery, lifecycle, and host isolation.

### Modified Capabilities

- `indexing`: pass 1 dispatches to the right `ILanguageIndexer` per file extension; pass 2 runs registered analyzers in parallel.
- `mcp-tools`: tool registry merges built-in and plugin-registered tools; plugin tool names are prefixed.
- `cli`: new `plugins` subcommand.

## Impact

- New SDK NuGet package `DevBitsLab.Mcp.SourceGraph.Sdk` versioned alongside the server.
- Plugin contract becomes a public API surface — needs a versioning policy (semver-major bumps may break plugins).
- `AssemblyLoadContext` isolation pulls in some startup cost (~50 ms per plugin); acceptable.
- ~400 lines of plugin-host code in the Server project.
- The built-in C# Roslyn indexer becomes the first registered `ILanguageIndexer`, dogfooding the contract.
