## ADDED Requirements

### Requirement: IToolRegistry.AddTool trigger overload
`IToolRegistry` SHALL expose two `AddTool` overloads:
- `AddTool(string toolName, string description, Delegate handler)` — the original 3-arg signature, retained unchanged from SDK 1.0.0 so plugins compiled against the earlier interface remain binary-compatible (plugins consume `IToolRegistry`, they don't implement it; adding methods to the interface is therefore safe for the plugin side).
- `AddTool(string toolName, string description, Delegate handler, string trigger)` — a new 4-arg overload added in SDK 1.1.0 that takes a required, non-empty trigger phrase.

When a tool is added via the 4-arg overload, the host SHALL append `Use when: <trigger>` as the final paragraph of the tool's effective description before registering the tool with the underlying MCP server. When a tool is added via the 3-arg overload, the description SHALL pass through unchanged.

#### Scenario: Plugin registers a tool with the trigger overload
- **WHEN** a plugin's `RegisterAsync` calls `registry.AddTool("find_handlers", "Find MediatR handlers for a request type.", handler, trigger: "\"who handles MediatR request X?\"")`
- **THEN** the host's `tools/list` response includes a tool whose description ends with the line `Use when: "who handles MediatR request X?"`

#### Scenario: Plugin registers a tool with the original 3-arg overload
- **WHEN** a plugin's `RegisterAsync` calls `registry.AddTool("find_handlers", "Find MediatR handlers.", handler)` (no trigger)
- **THEN** the host's `tools/list` response includes the tool whose description matches the supplied text verbatim, with no appended line

#### Scenario: Plugin compiled against SDK 1.0.0 stays binary-compatible
- **WHEN** a plugin DLL compiled against SDK 1.0.0 (which only knew the 3-arg `AddTool`) is loaded by the host running SDK 1.1.0
- **THEN** the plugin's calls to the 3-arg overload resolve at runtime to the unchanged interface method, the plugin loads without recompilation, and its tools register normally with no `Use when:` line appended

#### Scenario: Trigger overload rejects an empty trigger
- **WHEN** a plugin calls `registry.AddTool("x", "y", handler, trigger: "  ")` with whitespace-only trigger
- **THEN** the host throws `ArgumentException` (the 4-arg overload's contract is "trigger is required and non-empty"; plugins that don't have a trigger should call the 3-arg overload instead)
