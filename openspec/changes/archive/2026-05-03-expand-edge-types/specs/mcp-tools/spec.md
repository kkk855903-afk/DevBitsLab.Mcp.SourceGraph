## ADDED Requirements

### Requirement: find_implementations tool
The server SHALL expose a `find_implementations` tool that returns every member linked to a named interface member via `ImplementsMember` edges.

#### Scenario: Concrete implementations of an interface method
- **WHEN** the agent invokes `find_implementations(symbol = "IGreeter.Greet")` against a graph that has two implementing classes `Greeter` and `LoudGreeter`
- **THEN** the response lists both `Greeter.Greet` and `LoudGreeter.Greet` with their definition locations

## MODIFIED Requirements

### Requirement: Caller and callee enumeration
The server SHALL expose `list_callers` and `list_callees` tools that walk `Calls` edges by default, with an optional `kind` parameter that accepts `calls | uses_type | overrides | implements_member | instantiates | throws | all` to filter the edge kind walked.

#### Scenario: List callers (default = calls)
- **WHEN** the agent invokes `list_callers(symbol = "Calculator.Add")`
- **THEN** the response lists every symbol with an outgoing `EdgeKind.Calls` edge whose `dst` is the resolved id

#### Scenario: List consumers via uses_type
- **WHEN** the agent invokes `list_callers(symbol = "CancellationToken", kind = "uses_type")`
- **THEN** the response lists every symbol whose `UsesType` edge targets the resolved type id

### Requirement: Reference lookup
The server SHALL expose a `find_references` tool that returns every reference site for a symbol, surfacing the resolved `ReferenceKind` (`def`, `ref`, `call`, `read`, `write`, `impl`, `inherit`) per row.

#### Scenario: Distinguish reads and writes
- **WHEN** the agent invokes `find_references(symbol = "_state")` against a graph where `_state` is read in one place and written in two
- **THEN** the response includes one row with `kind = read` and two rows with `kind = write`, each with file:line
