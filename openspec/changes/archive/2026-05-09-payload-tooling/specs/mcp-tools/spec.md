## ADDED Requirements

### Requirement: find_data_bindings tool
The server SHALL expose a `find_data_bindings` MCP tool that returns rows from the `binds-path` edge kind, with optional filters on payload fields. Parameters: `target` (canonical key of bound symbol; matched against the edge's `dst`), `source` (canonical key of source UI element; matched against the edge's `src`), `path` (substring filter on `payload.path`), `mode` (exact match on `payload.mode`), `converter` (exact match on `payload.converter`), `scope` (scope id, comma-separated list, or `"*"`), `limit` (default 50). At least one filter parameter MUST be non-null; if all are null and no scope is restricted, the tool returns the first `limit` rows globally with a `note:` line asking the agent to add a filter.

The tool SHALL use the always-render-payload markdown shape (introduced by `harden-sdk-pre-xaml`): each row is `<source-element>  →  <target-or-unresolved>` followed by an indented `payload:` sub-line showing the relevant `path`, `mode`, `converter`, and `converter-parameter` keys.

#### Scenario: Find every TwoWay binding to a property
- **WHEN** the agent invokes `find_data_bindings(target = "csharp:P:SampleWpf.ViewModels.MainViewModel.UserName", mode = "two-way")`
- **THEN** the response lists every `binds-path` edge whose `dst` resolves to that C# property symbol AND whose `payload.mode` equals `"two-way"`; each row's payload sub-line shows the matched fields

#### Scenario: Find every binding using a specific converter
- **WHEN** the agent invokes `find_data_bindings(converter = "BoolToVisibility")`
- **THEN** the response lists every `binds-path` edge whose `payload.converter` equals `"BoolToVisibility"` (across every scope when no scope filter is specified, or restricted to the named scope)

#### Scenario: Path substring filter
- **WHEN** the agent invokes `find_data_bindings(path = "User.")`
- **THEN** the response lists every `binds-path` edge whose `payload.path` contains the substring `"User."` (e.g. `"User.Name"`, `"User.Email"`, `"User.Profile.Title"`)

#### Scenario: Soft empty when scope has no binds-path emitter
- **WHEN** the agent invokes `find_data_bindings(target = "csharp:P:Foo.Bar.Baz")` against a scope that has not loaded any indexer emitting the `binds-path` edge kind
- **THEN** the response is an empty result list with a single `note:` line indicating the scope's `edge_kinds` vocabulary does not include `binds-path` (the agent reads this and stops issuing the same query)

#### Scenario: All filters null returns hint
- **WHEN** the agent invokes `find_data_bindings()` with every filter null and no `scope` restriction
- **THEN** the response prepends a `note: provide at least one filter (target, source, path, mode, converter)` line and still returns up to `limit` rows so the agent can see what's available

### Requirement: find_event_handlers tool
The server SHALL expose a `find_event_handlers` MCP tool that returns rows from the `handles-event` edge kind, with optional filters on payload fields. Parameters: `handler` (canonical key of handler method; matched against the edge's `dst`), `event` (exact match on `payload.event`), `element` (canonical key of source UI element), `command` (exact match on `payload.command` for command-bound flavors where the indexer recorded a command name), `scope`, `limit` (default 50).

The tool SHALL render result rows as `<element>.<event-name>  →  <handler-or-command>` followed by the always-render-payload sub-line.

#### Scenario: Find every Click handler in a scope
- **WHEN** the agent invokes `find_event_handlers(event = "Click")` against a scope that loaded the XAML indexer
- **THEN** the response lists every `handles-event` edge whose `payload.event` equals `"Click"`, with each row showing the source XAML element and the resolved C# handler method

#### Scenario: Find every wiring to a specific handler
- **WHEN** the agent invokes `find_event_handlers(handler = "csharp:M:SampleWpf.Views.MainWindow.OnSave")`
- **THEN** the response lists every `handles-event` edge whose `dst` equals that C# method symbol

#### Scenario: Element + event combination
- **WHEN** the agent invokes `find_event_handlers(element = "xaml:element:Views/MainWindow.xaml#SaveBtn", event = "Click")`
- **THEN** the response lists the single edge wiring the named button's Click event to its handler

#### Scenario: Soft empty for command-only scope
- **WHEN** the agent invokes `find_event_handlers(command = "SaveCommand")` against a scope whose XAML indexer never emitted any `handles-event` edge with `payload.command` populated
- **THEN** the response is empty plus a `note:` line indicating no `handles-event` edges in the scope carried a `command` payload key

### Requirement: Tools self-advertise via tools/list
Both `find_data_bindings` and `find_event_handlers` SHALL appear in the MCP `tools/list` advertisement on every scope where the server runs, regardless of whether the scope's loaded indexers emit `binds-path` or `handles-event` edges. The tool description text MUST include a `Use when:` guidance line so an agent surfaces the tool on the right question shape.

#### Scenario: Tools advertised in a C#-only scope
- **WHEN** an MCP client invokes `tools/list` against a scope that loads only the built-in C# Roslyn indexer (no XAML indexer)
- **THEN** the returned tool list includes `find_data_bindings` and `find_event_handlers` with their `Use when:` descriptions; calling them returns the soft-empty behaviour documented above

#### Scenario: Tool descriptions document soft-empty behaviour
- **WHEN** an MCP client reads the `find_data_bindings` description from `tools/list`
- **THEN** the description names the edge kind it walks (`binds-path`), names the `PayloadKeys` constants its filters map to (`path`, `mode`, `converter`, `converter-parameter`), and documents the soft-empty response shape so an agent can predict the tool's behaviour without invoking it
