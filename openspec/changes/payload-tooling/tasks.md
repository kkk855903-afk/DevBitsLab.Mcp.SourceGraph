## 1. SQL helpers on IGraphStore

- [ ] 1.1 Add `IGraphStore.FindDataBindingsAsync(string? targetCanonicalKey, string? sourceCanonicalKey, string? pathContains, string? modeExact, string? converterExact, IReadOnlyList<int>? scopeIds, int limit)` returning `IReadOnlyList<EdgeWithPayload>` shaped rows
- [ ] 1.2 Implement against SQLite using `WHERE kind_name = 'binds-path'` plus the optional payload filters via `json_extract`
- [ ] 1.3 Add `IGraphStore.FindEventHandlersAsync(string? handlerCanonicalKey, string? eventExact, string? elementCanonicalKey, string? commandExact, IReadOnlyList<int>? scopeIds, int limit)` returning the same row shape, walking `kind_name = 'handles-event'` edges
- [ ] 1.4 Round-trip test against an in-memory store seeded with both `binds-path` and `handles-event` edges with payloads

## 2. find_data_bindings tool registration

- [ ] 2.1 Add `[McpServerTool(Name = "find_data_bindings")]` method to `src/DevBitsLab.Mcp.SourceGraph.Server/Tools/GraphTools.cs`
- [ ] 2.2 Add a `[ToolTrigger]` description: `"Use when the agent needs to find or audit data bindings between XAML/UI elements and viewmodel properties — answers 'where does this property bind?', 'find every TwoWay binding', 'which views use this converter?'"`
- [ ] 2.3 Tool parameters: `target` (canonical key), `source` (canonical key), `path` (substring), `mode` (exact), `converter` (exact), `scope` (id, comma list, or `*`), `limit` (default 50)
- [ ] 2.4 Resolve `target` and `source` canonical keys via the existing symbol-resolution helper used by `find_definition`; pass through `pathContains` / `modeExact` / `converterExact` to the storage helper
- [ ] 2.5 Render result rows as markdown: `<source-element>  →  <target-or-unresolved>  [mode=…, converter=…, path=…]`; payload sub-line per `harden-sdk-pre-xaml` always-render rule
- [ ] 2.6 If at least one filter is null AND no scope is restricted AND no `target`/`source` resolves, prepend a `note: provide at least one filter (target, source, path, mode, converter)` line to the response
- [ ] 2.7 If the active scope's `edge_kinds` vocabulary does not include `binds-path`, return empty list with `note: scope <id> has no indexer that emits binds-path; load a XAML or web-stack indexer to populate this graph`

## 3. find_event_handlers tool registration

- [ ] 3.1 Add `[McpServerTool(Name = "find_event_handlers")]` method to `GraphTools.cs`
- [ ] 3.2 Add a `[ToolTrigger]` description: `"Use when the agent needs to find or audit event-to-handler wiring in XAML or component-based UI — answers 'find all Click handlers', 'where is OnSave wired up?', 'which buttons fire this command?'"`
- [ ] 3.3 Tool parameters: `handler` (canonical key), `event` (exact), `element` (canonical key), `command` (exact), `scope`, `limit`
- [ ] 3.4 Render result rows as markdown: `<element>.<event>  →  <handler-or-command>`; payload sub-line per always-render rule
- [ ] 3.5 Soft-empty behaviour identical to `find_data_bindings` when scope lacks `handles-event` emitter

## 4. Tests

- [ ] 4.1 Unit test (`tests/DevBitsLab.Mcp.SourceGraph.Tests/PayloadToolingTests.cs`) seeds a store with synthetic `binds-path` edges and verifies `FindDataBindingsAsync` filters work as documented
- [ ] 4.2 End-to-end test against the `SampleWpf` fixture introduced by `xaml-language-indexer`: invoke `find_data_bindings --target=SampleWpf.ViewModels.MainViewModel.UserName` and assert at least one row resolves with `payload.path = "UserName"` and `payload.mode = "two-way"`
- [ ] 4.3 End-to-end test against `SampleWpf` for `find_event_handlers --event=Click` returning the wired handlers
- [ ] 4.4 Soft-empty regression test: invoke `find_data_bindings` against a scope without a XAML indexer; assert empty list plus the documented `note:` line
- [ ] 4.5 Add a stdio integration test (using the harness from `harden-sdk-pre-xaml`) that drives `find_data_bindings` end-to-end through the MCP `tools/call` boundary against the `SampleWpf` fixture

## 5. Validation and finishing

- [ ] 5.1 Run `openspec validate payload-tooling --strict` and resolve any reported issues
- [ ] 5.2 Run `dotnet build`; resolve compile errors
- [ ] 5.3 Run `dotnet test`; resolve any test that broke
- [ ] 5.4 README example: a small block showing `find_data_bindings --target=User.Name --mode=TwoWay` rendered against the `SampleWpf` fixture
- [ ] 5.5 CHANGELOG entry: two new MCP tools, no SDK changes, no schema changes
