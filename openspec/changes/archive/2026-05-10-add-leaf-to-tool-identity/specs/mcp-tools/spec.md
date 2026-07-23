## ADDED Requirements

### Requirement: Tool identity brand mark
The server SHALL stamp every built-in MCP tool's catalog identity with the green-leaf brand mark before that tool is advertised to clients via `tools/list`. Two `Tool` fields carry the mark:

- `Tool.Title` SHALL be set to `"🌿 " + Tool.Name` (the U+1F33F glyph followed by U+0020 followed by the tool's snake_case name as already populated on `Tool.Name`).
- `Tool.Description` SHALL be prepended with `"🌿 "` (U+1F33F U+0020) so the existing description text follows the brand mark on the same line. Idempotency: if `Tool.Description` already begins with `"🌿 "`, no second prefix SHALL be added.

The stamping SHALL apply only to built-in tools — those whose backing method's declaring type carries `[McpServerToolType]`. Plugin-registered tools (registered via `IToolRegistry.AddTool` in `Plugins/ToolRegistry.cs`) SHALL NOT receive the brand mark on either `Title` or `Description`. The brand mark on `Tool.Title` is independent of the existing brand mark on per-call response prose (`content[0].text`, governed by *Tool response brand mark*) and the brand mark on the `ServerInstructions` head string (governed by *Server-published usage instructions*); each surface is stamped independently and may be observed independently in `tools/list` (Title, Description) versus `tools/call` results (content prose) versus the `initialize` handshake (instructions).

The stamping SHALL be applied via a single chokepoint in the server's startup sequence (mirroring the existing `ToolDescriptionFormatter.ApplyTriggersFromAttributes` pass that mutates `ProtocolTool.Description` in place after `host.Build()`). The chokepoint SHALL respect `LeafFormatter.Suppressed` — when suppression is active, `Title` SHALL NOT be set (it remains null/unset, preserving the SDK's default behaviour) and `Description` SHALL NOT be prefixed.

#### Scenario: Built-in tool catalog entry carries Title and Description brand marks
- **WHEN** an MCP client calls `tools/list` against a freshly started `sourcegraph-mcp serve` process with no suppression flags
- **THEN** the response's `tools[]` entries for every built-in tool (e.g. `find_definition`, `search_symbols`, `find_references`) each have a populated `title` field equal to `"🌿 <name>"` (e.g. `"🌿 find_definition"`) and a `description` field whose first characters are `"🌿 "` followed by the tool's documented prose

#### Scenario: Plugin-registered tool catalog entry is not brand-marked
- **WHEN** a plugin-registered tool (registered through `IToolRegistry.AddTool`) appears in the `tools/list` response
- **THEN** that tool's `title` field is null/unset and its `description` does NOT start with `"🌿 "` — the plugin's authored identity ships verbatim

#### Scenario: Description prefix is idempotent
- **WHEN** a built-in tool's authored `Description` already begins with `"🌿 "` (e.g. due to manual pre-stamping in source)
- **THEN** the post-build pass leaves it unchanged — no second `"🌿 "` is stacked

#### Scenario: Title is independent of Name
- **WHEN** an MCP client invokes a tool by `Name` (e.g. `find_definition`)
- **THEN** invocation succeeds because `Name` is the wire-level identifier and is unaffected by this requirement; `Title` is a separate display-only field

#### Scenario: Pass is repeatable
- **WHEN** the post-build mutation pass runs more than once on the same registered tool collection (e.g. due to test harness rebuilding the host)
- **THEN** the resulting `Title` and `Description` are identical to a single-pass run — `Title` is `"🌿 " + Name` (not `"🌿 🌿 " + Name`), `Description` starts with exactly one `"🌿 "`

#### Scenario: New built-in tools added in future changes are automatically branded
- **WHEN** a future change registers a new method-based tool on a type carrying `[McpServerToolType]` (e.g. `find_data_bindings` from the in-flight `payload-tooling` change)
- **THEN** that tool's catalog entry receives the same `🌿 ` Title and Description treatment without any per-tool wiring — the chokepoint walks the registered set on every startup

#### Scenario: Existing trigger-append survives the leaf prefix
- **WHEN** a built-in tool's method carries a `[ToolTrigger("...")]` attribute (whose value is appended to `Description` by `ToolDescriptionFormatter.ApplyTriggersFromAttributes`)
- **THEN** the final `Description` shipped in `tools/list` is `"🌿 " + <original description> + "\n\nUse when: <trigger>"` — the leaf rides at the start, the trigger at the end, both passes coexist

## MODIFIED Requirements

### Requirement: Brand-mark suppression
The server SHALL accept `--no-leaf` as a CLI flag on `sourcegraph-mcp serve` and SHALL honour `SOURCEGRAPH_NO_LEAF` as an environment variable (truthy values: exact `1`, or `true` case-insensitive — same convention as `SOURCEGRAPH_NO_INSTRUCTIONS`). When either is set, the server SHALL omit the brand-mark prefix from every built-in tool's per-call response, SHALL omit the brand-mark prefix from the published `ServerInstructions` string, AND SHALL NOT stamp `Tool.Title` with `"🌿 " + Name` or prepend `"🌿 "` to `Tool.Description` for any built-in tool. The three suppression effects (per-call, instructions, and tool-identity) compose under the single `--no-leaf` / `SOURCEGRAPH_NO_LEAF` knob; there is no per-surface suppression. The `--no-leaf` and `--no-instructions` flags continue to compose independently: turning off one SHALL NOT turn off the other.

#### Scenario: Suppression via flag removes leaf from per-call, instructions, Title, and Description
- **WHEN** the server is started with `--no-leaf`
- **THEN** built-in tool responses contain no leading `🌿 ` on `content[0].text`, the published `ServerInstructions` string (if any) contains no leading `🌿 `, every built-in tool's `Tool.Title` is null/unset, and every built-in tool's `Tool.Description` does NOT start with `"🌿 "`

#### Scenario: Suppression via env var removes leaf from per-call, instructions, Title, and Description
- **WHEN** the server is started without `--no-leaf` but with `SOURCEGRAPH_NO_LEAF=1` in env
- **THEN** the same suppression pattern as the flag-based case applies: per-call prose unbranded, ServerInstructions head unbranded, Title null/unset, Description not branded

#### Scenario: Leaf suppression independent of instructions suppression
- **WHEN** the server is started with `--no-leaf` but WITHOUT `--no-instructions`
- **THEN** the `ServerInstructions` string is published with its cross-cutting guidance intact but no leading `🌿 ` prefix; every built-in tool's response prose has no leading `🌿 `; every built-in tool's `Title` is null/unset and `Description` is not `🌿 `-prefixed; no leaf appears anywhere in the catalog or per-call channels

#### Scenario: Suppression knobs compose
- **WHEN** the server is started with both `--no-leaf` and `--no-instructions`
- **THEN** the `initialize` response carries no `ServerInstructions` string at all, every built-in tool's response prose has no `🌿 ` prefix, every built-in tool's `Title` is null/unset, and every built-in tool's `Description` is not `🌿 `-prefixed

#### Scenario: Plugin tools unaffected by suppression
- **WHEN** any combination of suppression flags is set and a plugin-registered tool is invoked or appears in `tools/list`
- **THEN** the plugin's authored identity (Title, Description, response content) ships unchanged regardless of suppression — the suppression only governs surfaces the server itself stamps; plugin-authored text is never stamped to begin with
