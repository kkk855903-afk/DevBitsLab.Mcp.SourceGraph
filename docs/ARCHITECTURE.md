# Architecture

This document describes how 🌿 `DevBitsLab.Mcp.SourceGraph` is organised, how an
indexing run flows from disk to SQLite, and how MCP tool calls are dispatched
across scopes. It complements [README.md](../README.md) (user-facing) and
[CONTRIBUTING.md](../CONTRIBUTING.md) (workflow).

## Module layout

```
+-------------------------------+
|  Server (stdio MCP host)      |  CLI · ServerInstructions · Scoping/ · Tools/
|                               |  Plugins/ · Observability/ · LiveIndexService
+---------------+---------------+
                |
+---------------v---------------+
|  Watcher    |   Embeddings   |  File + git HEAD watcher; ONNX + sqlite-vec.
+---------------+---------------+
                |
+---------------v---------------+
|  Indexing                     |  Roslyn workspace, SymbolIndexer,
|                               |  AnalyzerPipeline, BlamePipeline.
+---------------+---------------+
                |
+---------------v---------------+
|  Storage                      |  IGraphStore, IScopeRegistry, FTS5 schema.
+---------------+---------------+
                |
+---------------v---------------+
|  Core                         |  Domain records: Scope, Symbol, Edge, etc.
+-------------------------------+

Sdk (netstandard2.0): IMcpToolPlugin, ICodeAnalyzer, ILanguageIndexer.
```

Dependencies flow strictly downward — `Core` knows nothing about Roslyn or
SQLite, `Storage` knows nothing about MCP, and so on. The plugin `Sdk` is a
sibling published artefact targeting `netstandard2.0` so plugin authors don't
need to track host TFM upgrades.

## Indexing pipeline

A scope's `IGraphStore` is populated by a one-shot or live indexing run:

1. **Workspace open** — `MSBuildLocator` resolves an SDK, then
   `MSBuildWorkspace` opens each `.sln`/`.slnx` declared in the scope's
   `ScopeProjectSet.Solutions`.
2. **SymbolIndexer** walks every `Document` and emits, per declaration:
   - `Symbol` rows (id, kind, FQN, span, signature, modifiers, accessibility,
     XML summary, test-framework hint).
   - `SymbolReference` rows for usages found in the file.
   - `Edge` rows for `calls`, `uses_type`, `overrides`, `implements_member`,
     `instantiates`, `throws`, `tests`.
   - `AttributeRecord` rows with the serialised constructor + named args.
3. **AnalyzerPipeline** runs Roslyn's `CompilationWithAnalyzers` per document
   under a 30-second timeout, and persists `DiagnosticRecord` rows.
4. **BlamePipeline** (gated by `--no-history`) runs `git blame --line-porcelain`
   over each symbol's span and caches the result against the file's content
   hash, so the next run reuses cached blame when the file hasn't changed.
5. **EmbeddingsHostedService** (gated by `--no-embeddings`) downloads the ONNX
   model on first run, tokenises symbol text with `FastBertTokenizer`, and
   writes vectors into `sqlite-vec`'s `vec0` table.

All writes go through the scope's `IGraphStore`, which wraps a single SQLite
connection per scope. FTS5 indexes for `name`, `fqn`, and `signature` are
maintained as triggers on the underlying tables.

## Scope router

`Scoping/ScopeRouter` sits in front of every MCP tool implementation. When a
tool call carries `scope = "<id>"`, the router resolves the scope's
`IGraphStore` from the registry and runs the query. When `scope = "*"`, it
fans out to every non-`isolated` scope in parallel and merges results.

The router is the only component that knows about `_meta.db`, the per-repo
SQLite registry that records each scope's status (`ok | degraded | indexing`)
and last-indexed timestamp.

## Live indexing

`LiveIndexService` is a `BackgroundService` that:

- subscribes to `SolutionWatcher` events (`*.cs` + `.git/HEAD`),
- coalesces edits within a 200 ms debounce window,
- re-runs the relevant pieces of the indexing pipeline against the affected
  documents,
- updates the symbol-id stability invariant (canonical hash → row id) so
  references from other files keep resolving across edits.

## MCP transport

`Program.cs` uses `Microsoft.Extensions.Hosting` to compose:

- the stdio MCP server (`Microsoft.ModelContextProtocol`);
- the scope router and per-scope graph stores;
- the live indexing service;
- optional embedding/history hosted services;
- the plugin host (`Plugins/PluginHost`) which loads each declared plugin into
  its own `AssemblyLoadContext`.

Every MCP tool implementation lives under `Tools/`. Each tool wraps its body
in `ToolMetrics.TrackAsync(...)`, which:

1. records latency, response length, error state into in-memory counters
   (`usage_stats`);
2. appends a JSONL line to `<root>/.sourcegraph/usage.jsonl`;
3. emits an `Activity` from
   `ActivitySource("DevBitsLab.Mcp.SourceGraph")` and a counter on
   `Meter("DevBitsLab.Mcp.SourceGraph")` so external observers (OpenTelemetry,
   `dotnet-counters`) can pick up the call.

## Plugin host

`Plugins/PluginHost` resolves each `PluginRef` from `.sourcegraph.json`:

- For `package`+`version` entries, it restores the NuGet package into the
  `.sourcegraph/plugins/<id>/` cache and loads the primary DLL.
- For `path` entries, it loads the DLL directly.

Each plugin runs inside its own `AssemblyLoadContext` configured to share
`DevBitsLab.Mcp.SourceGraph.Sdk` from the host context. This isolation lets a
plugin pin its own dependencies without breaking the host or other plugins.

## Versioning boundary

The `Sdk` package is the only **public binary contract**. The semver rules
described in [GOVERNANCE.md](../GOVERNANCE.md) apply to it specifically: new
methods on existing interfaces are additive (plugin authors implement, not
consume, the contract types), and breaking changes require a major bump.

The MCP wire protocol — tool names, argument shapes, return shapes — is also
considered part of the public contract once a tool is documented in
`README.md`. Removals and renames follow the deprecation policy.

## Where things live (cheat sheet)

| If you're touching... | Look at... |
|---|---|
| The set of MCP tools | `src/.../Server/Tools/` |
| Tool descriptions / handshake | `Server/ServerInstructions.cs`, `Server/Tools/ToolDescriptionFormatter.cs` |
| Symbol extraction | `Indexing/SymbolIndexer*` |
| Edge / reference extraction | `Indexing/ReferenceCollector*`, `Indexing/EdgeCollector*` |
| Diagnostic capture | `Indexing/AnalyzerPipeline*` |
| SQLite schema | `Storage/Schema.cs` |
| FTS5 / search | `Storage/SqliteGraphStore.cs` (`SearchSymbols`) |
| Semantic search | `Embeddings/*`, `Storage/SqliteEmbeddingsStore.cs` |
| Scope routing | `Server/Scoping/*` |
| Live updates | `Server/LiveIndexService.cs`, `Watcher/*` |
| Plugin loading | `Server/Plugins/PluginHost.cs` |
| CLI parsing | `Server/Cli/CommandLine.cs`, `Server/Program.cs` |
