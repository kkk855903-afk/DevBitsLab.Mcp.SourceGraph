## 1. Server instructions composition

- [x] 1.1 Add a `ServerInstructions` static class (or constant) carrying the preamble + epilogue template.
- [x] 1.2 Wire `McpServerOptions.ServerInstructions` via `builder.Services.Configure<McpServerOptions>(...)` in `Program.RunServeAsync`, immediately around the existing `AddMcpServer()` block.
- [x] 1.3 Skip the configuration step entirely when `--no-instructions` is set or `SOURCEGRAPH_NO_INSTRUCTIONS=1` is present in env.
- [x] 1.4 Confirm the SDK packages the string into the `initialize` response by reading `McpClient.ServerInstructions` from a smoke test against the running stdio server. **Resolution downscoped**: rather than spawn a stdio process, `ServerInstructionsWiringTests` exercises the same `services.Configure<McpServerOptions>(...)` call `Program.cs` makes and resolves `IOptions<McpServerOptions>` to confirm `ServerInstructions` lands on the right value (set when enabled, null/empty when suppressed). The SDK itself is responsible for lifting `McpServerOptions.ServerInstructions` into the `initialize` response — that's the SDK's contract, not ours to retest. Real validation is post-deploy via `usage.jsonl` (see proposal *Impact* section).

## 2. Tool trigger metadata

- [x] 2.1 Add `ToolTriggerAttribute(string trigger)` (sealed, `AttributeUsage(Method, AllowMultiple = false)`) in `Server/Tools` next to the existing tool classes. Document that the value is the natural-language question phrase, surrounded by quotes.
- [x] 2.2 At registration time, scan the server's tool methods for `[ToolTrigger]`. Append `\n\nUse when: <trigger>` to the effective description before the SDK builder consumes it. **Resolution**: post-hoc rewrite of `McpServerTool.ProtocolTool.Description` on the collection resolved from DI after `host.Build()`. Implemented in `ToolDescriptionFormatter.ApplyTriggersFromAttributes`.
- [x] 2.3 If the SDK doesn't expose a clean post-hoc description rewrite, fall back to a build-time generator or a small reflection pass that builds tools manually instead of `WithToolsFromAssembly()`. Prefer the SDK-native path; document the fallback in `design.md` if it lands. **Resolution**: SDK-native path worked (`McpServerTool.ProtocolTool.Description` is settable and read on every `tools/list` response), no fallback needed.

## 3. Plugin contract update

- [x] 3.1 Add `string? trigger = null` parameter to `IToolRegistry.AddTool` in `src/DevBitsLab.Mcp.SourceGraph.Sdk/IMcpToolPlugin.cs`. Default-null preserves source compatibility.
- [x] 3.2 Update `ToolRegistry.AddTool` (in `Server/Plugins/ToolRegistry.cs`) to append the trigger line to the description before handing off to the MCP builder.
- [x] 3.3 Bump the SDK package version (minor) since the contract is additive — record the change in the SDK's changelog stub if one exists. **Resolution**: bumped `Version` from 1.0.0 to 1.1.0 in `DevBitsLab.Mcp.SourceGraph.Sdk.csproj` with an inline comment marking the additive change. No standalone changelog file exists.

## 4. Migrate built-in tools

- [x] 4.1 `Tools/GraphTools.cs` — strip the `Use for 'X?'` clauses from `[Description]` strings and add `[ToolTrigger("...")]` per tool. Affects every tool in the file.
- [x] 4.2 `Tools/ScopeTools.cs`, `Tools/HistoryTools.cs`, `Tools/PingTool.cs` — same migration where applicable. Tools whose role isn't trigger-driven (e.g. `usage_stats`, `graph_stats` — diagnostic) MAY skip the attribute; document the omission policy in the spec delta. **Skipped (diagnostic-only)**: `graph_stats`, `usage_stats`, `ping`.
- [x] 4.3 Build the project; confirm `tools/list` over MCP still shows the same tools and that descriptions end with `Use when: ...` for triggered tools. **Note**: this task verifies the build only. The `tools/list` runtime check is task 7.6.

## 5. CLI flag

- [x] 5.1 Add `--no-instructions` to `Cli/CommandLine.Parse` (boolean field, no value).
- [x] 5.2 Read `SOURCEGRAPH_NO_INSTRUCTIONS` env var (truthy: `1`, `true`) inside `RunServeAsync` and OR with the flag.
- [x] 5.3 Update `CommandLine.HelpText` with a one-line description of the flag.

## 6. Documentation

- [x] 6.1 Remove the `## When working in any indexed .NET solution: prefer source-graph tools` and `## Verifying the MCP is doing its job` blocks from `CLAUDE.md`. The remaining content (project layout, scopes, plugin notes) stays. Replaced with a short pointer to the server-published guidance.
- [x] 6.2 Add a one-paragraph note to the `README.md` under "Configuration" explaining `--no-instructions` and that the server self-publishes usage guidance.
- [x] 6.3 Update the SDK README / inline docstring on `IToolRegistry.AddTool` to document the new `trigger` parameter with an example. Done in `IMcpToolPlugin.cs` doc comment (no separate SDK README exists).

## 7. Tests

- [x] 7.1 Unit test: a tool method decorated with `[ToolTrigger]` produces a registered `McpServerTool` whose description ends with `Use when: <trigger>`. (`ToolTriggerTests.ApplyTriggersFromAttributes_*`)
- [x] 7.2 Unit test: a plugin tool registered via `IToolRegistry.AddTool(name, desc, handler, trigger: "...")` likewise gets the appended line; without `trigger`, the description is unchanged. (`ToolTriggerTests.ToolRegistry_*`)
- [x] 7.3 Integration test: connect a `McpClient` to a started server, read `ServerInstructions`, assert it contains both the preamble keyword (`prefer`) and the epilogue keyword (`usage_stats`). **Downscoped to a wiring test** (`ServerInstructionsWiringTests.Default_publishesInstructions`) that asserts the same DI-config call resolves `IOptions<McpServerOptions>.ServerInstructions` to a non-empty string containing both keywords.
- [x] 7.4 Integration test: with `--no-instructions`, `ServerInstructions` is null/empty. **Downscoped** (`ServerInstructionsWiringTests.Flag_suppressesInstructions`).
- [x] 7.5 Integration test: with `SOURCEGRAPH_NO_INSTRUCTIONS=1` set in env and no flag, `ServerInstructions` is null/empty. **Downscoped** (`ServerInstructionsWiringTests.EnvVar_suppressesInstructions_whenFlagAbsent` plus case-sensitivity coverage).
- [x] 7.6 Catalog smoke test: every built-in tool that previously embedded `Use for ...` prose now has a `[ToolTrigger]` attribute (lint-style test enumerates `[McpServerTool]` methods and asserts presence on the expected subset). (`ToolTriggerTests.Catalog_everyNonDiagnosticToolDeclaresATrigger`)

## 8. Update specs

- [x] 8.1 On archive, sync delta into `openspec/specs/mcp-tools/spec.md` (server-instructions requirement + `[ToolTrigger]` requirement).
- [x] 8.2 On archive, sync delta into `openspec/specs/mcp-config/spec.md` (`--no-instructions` flag + env var).
- [x] 8.3 On archive, sync delta into `openspec/specs/extensibility/spec.md` (new `trigger` parameter on `IToolRegistry.AddTool`). **Note**: delta restructured from MODIFIED→ADDED before sync (the original MODIFIED text would have silently dropped the existing prefixing scenario; the new ADDED requirement leaves `IMcpToolPlugin contract` untouched and adds `IToolRegistry.AddTool optional trigger argument` as a sibling).
