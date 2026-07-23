## 1. Core enums

- [x] 1.1 Extend `EdgeKind` with `UsesType`, `OverridesMember`, `ImplementsMember`, `Instantiates`, `Throws`.
- [x] 1.2 Extend `ReferenceKind` with `Read`, `Write`.

## 2. Indexer pass 2

- [x] 2.1 Add `EmitUsesTypeEdges(member, signatureSymbols)` — walks parameter types, return type, generic args, base list.
- [x] 2.2 Add `EmitInstantiates(node, enclosingMember, model)` — every `ObjectCreationExpressionSyntax` becomes an `Instantiates` edge plus a `Call` edge to the constructor.
- [x] 2.3 Add `EmitThrows(node, enclosingMember, model)` — every `ThrowStatementSyntax` / `ThrowExpressionSyntax`.
- [x] 2.4 Add `EmitOverrides(symbol)` — checks `OverriddenMethod` / `OverriddenProperty` / `OverriddenEvent`.
- [x] 2.5 Add `EmitMemberImplements(typeSymbol)` — walks `AllInterfaces` and `FindImplementationForInterfaceMember`.
- [x] 2.6 Refactor the ref/edge emitter to also emit `Read` / `Write` based on syntax position.

## 3. Storage

- [x] 3.1 Extend `IGraphStore.ListCallersAsync` / `ListCalleesAsync` to take optional `EdgeKind?` filter (default = `Calls` for back-compat).
- [x] 3.2 New `IGraphStore.ListImplementationsAsync(symbolId, limit)` returning members linked via `ImplementsMember`.
- [x] 3.3 New `IGraphStore.ListUsersOfTypeAsync(symbolId, limit)` for `UsesType` reverse lookup.

## 4. MCP tools

- [x] 4.1 Add optional `kind` parameter to `list_callers`, `list_callees`, `neighborhood`, `impact_of_change`. Accepts `calls | uses_type | overrides | implements | instantiates | throws | all`.
- [x] 4.2 New tool `find_implementations(symbol, includeAbstract = false)` returning every member that satisfies the named interface member.
- [x] 4.3 `find_references` results display the resolved `ReferenceKind` (`def | ref | call | read | write | impl | inherit`).

## 5. Tests

- [x] 5.1 Read/Write detection: fixtures with `_x = 1`, `_x++`, `Method(out _x)`, `Method(ref _x)`, plain read `var y = _x` (extended `tests/fixtures/Sample.Domain/Greeter.cs`).
- [x] 5.2 UsesType: a method that returns `IGreeter` and constructs `Greeter` produces `UsesType` edges to both (verified via SQL on the fixture).
- [ ] 5.3 OverridesMember on properties and events, not just methods. *(Sample fixture has no overrides; emitter exercised on real code via `OverriddenMethod/Property/Event`. No virtual base in the smoke fixture means this is a behaviour-only check, not a regression-suite case.)*
- [x] 5.4 ImplementsMember: `Greeter : IGreeter` with `Greet` emits `ImplementsMember(Greeter.Greet → IGreeter.Greet)`.
- [x] 5.5 Instantiates and Throws from a fixture method (`Calculator.MakeGreeter`, `Calculator.Divide`).
- [ ] 5.6 Live update: edit a fixture file to add a `throw new InvalidOperationException()`; assert the new `Throws` edge appears within the watcher debounce window. *(Live-update path is unchanged — pass 2 reruns on the changed file with the same emitters. Not added as an automated test because the repo currently has no test project.)*

## 6. Update specs

- [x] 6.1 Sync delta specs into `openspec/specs/{indexing, storage, mcp-tools}/spec.md` on archive.
