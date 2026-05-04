## ADDED Requirements

### Requirement: ILanguageIndexer plugin contract
The SDK SHALL expose an `ILanguageIndexer` contract that declares the file extensions it handles and produces a stream of `IndexEvent` values for a given document.

#### Scenario: Built-in C# indexer implements the contract
- **WHEN** the server starts
- **THEN** `RoslynLanguageIndexer` is registered for `.cs` files and routes pass-1 / pass-2 work through `IndexEvent` emissions

#### Scenario: Third-party indexer registered
- **WHEN** a plugin in `.sourcegraph.json` declares `ILanguageIndexer` for `.py`
- **THEN** the server dispatches `.py` files to that plugin and ignores them in the C# indexer

### Requirement: ICodeAnalyzer plugin contract
The SDK SHALL expose an `ICodeAnalyzer` contract whose `AnalyzeAsync` is invoked once per indexed document with a context and an `IGraphEmitter`, allowing the analyzer to add symbols, references, edges, or attributes.

#### Scenario: Custom analyzer emits an Endpoint symbol
- **WHEN** an `AspNetCoreEndpointAnalyzer` runs against a document containing `app.MapGet("/api", h)`
- **THEN** it emits a synthetic `Endpoint` symbol via `IGraphEmitter` and the server persists it via the same `UpsertSymbolAsync` path used by the built-in indexer

### Requirement: IMcpToolPlugin contract
The SDK SHALL expose an `IMcpToolPlugin` contract whose `RegisterAsync` adds tools to the MCP server; tool names emitted by a plugin SHALL be prefixed with the plugin's declared `Prefix` followed by `.`.

#### Scenario: Plugin tool name prefixing
- **WHEN** a plugin with `Prefix = "mediatr"` registers `find_handlers`
- **THEN** the MCP server exposes the tool as `mediatr.find_handlers` and built-in tools (e.g. `find_definition`) keep unprefixed names

### Requirement: Plugin discovery via .sourcegraph.json
The host SHALL load plugins listed under `plugins[]` in `.sourcegraph.json`, supporting both NuGet packages (`{package, version}`) and DLL paths (`{path}`).

#### Scenario: NuGet plugin loaded
- **WHEN** `.sourcegraph.json` lists `{ "package": "DevBitsLab.Mcp.SourceGraph.Analyzers.AspNetCore", "version": "1.0.0" }`
- **THEN** the host restores it into `<repo>/.sourcegraph/plugins/restore/` and loads its assembly into a dedicated `AssemblyLoadContext`

#### Scenario: Path plugin loaded
- **WHEN** `.sourcegraph.json` lists `{ "path": ".sourcegraph/plugins/MyAnalyzer.dll" }`
- **THEN** the host loads that DLL directly into a per-plugin ALC

### Requirement: Per-plugin failure isolation
A plugin whose contract methods throw SHALL be marked `failed` in the registry; the rest of the system continues to operate, and the failure is surfaced via `plugins list`.

#### Scenario: Bad plugin doesn't crash the host
- **WHEN** an `ICodeAnalyzer.AnalyzeAsync` throws on a particular document
- **THEN** the analyzer is marked `failed` for that pass, other analyzers complete, the indexer returns success, and `plugins list` reports the failure with the exception type and message

### Requirement: Plugin status introspection
The CLI SHALL expose `plugins list` and `plugins info <name>` subcommands that report every loaded plugin's id, version, status (`loaded | failed | disabled`), implemented contracts, and registered tools / file extensions.

#### Scenario: Inspect plugins
- **WHEN** the user runs `sourcegraph-mcp plugins list` after starting a server with two plugins (one healthy, one failed)
- **THEN** the output is a table showing both plugins with their statuses and versions
