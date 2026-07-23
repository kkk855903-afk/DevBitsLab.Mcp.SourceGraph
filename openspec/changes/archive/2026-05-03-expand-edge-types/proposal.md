## Why

The current edge model captures only `Calls`, `Inherits`, and `Implements` (class-level). That's enough for "who calls X?" but misses the questions agents actually ask in real reviews: *"who consumes `IUserRepository`?"*, *"is this method ever overridden?"*, *"who writes to `_state` versus reads it?"*, *"what types does this assembly throw?"* All of these are answerable from the same Roslyn syntax/semantic walk we already do — we just don't emit the edges. `EdgeKind.UsesType` already exists in the enum and is never written.

## What Changes

- Extends `EdgeKind` with `UsesType`, `OverridesMember`, `ImplementsMember`, `Instantiates`, `Throws`.
- Extends `ReferenceKind` with `Read` and `Write` for field/property accesses (distinguished by syntactic position: assignment LHS, `++`/`--`, `out`/`ref` argument).
- Pass 2 of `RoslynIndexer` learns to emit each new edge kind with bounded scope (only between *indexed* symbols — no edges to BCL types unless those are indexed).
- Existing tools (`list_callers`, `list_callees`, `neighborhood`, `impact_of_change`) gain an optional `kind` parameter to walk specific edge types.
- New tool `find_implementations(symbol)` returns concrete members satisfying an interface member.
- `find_references` results display the resolved kind (`def | ref | call | read | write | impl | inherit`).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `indexing`: pass 2 emits the new edge and reference kinds.
- `mcp-tools`: existing tools gain an optional `kind` filter; new `find_implementations` tool.

## Impact

- No schema change — `kind INTEGER` already accepts the new values.
- ~150 lines in `RoslynIndexer` for the new emitters and read/write detection.
- ~30 lines per affected tool to surface the new kinds.
- Edge table grows ~25× (mostly UsesType). Still small in absolute terms (<1 M edges on a typical solution).
