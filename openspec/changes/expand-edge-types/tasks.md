## 1. Core enums

- [ ] 1.1 Extend `EdgeKind` with `UsesType`, `OverridesMember`, `ImplementsMember`, `Instantiates`, `Throws`.
- [ ] 1.2 Extend `ReferenceKind` with `Read`, `Write`.

## 2. Indexer pass 2

- [ ] 2.1 Add `EmitUsesTypeEdges(member, signatureSymbols)` — walks parameter types, return type, generic args, base list.
- [ ] 2.2 Add `EmitInstantiates(node, enclosingMember, model)` — every `ObjectCreationExpressionSyntax` becomes an `Instantiates` edge plus a `Call` edge to the constructor.
- [ ] 2.3 Add `EmitThrows(node, enclosingMember, model)` — every `ThrowStatementSyntax` / `ThrowExpressionSyntax`.
- [ ] 2.4 Add `EmitOverrides(symbol)` — checks `OverriddenMethod` / `OverriddenProperty` / `OverriddenEvent`.
- [ ] 2.5 Add `EmitMemberImplements(typeSymbol)` — walks `AllInterfaces` and `FindImplementationForInterfaceMember`.
- [ ] 2.6 Refactor the ref/edge emitter to also emit `Read` / `Write` based on syntax position.

## 3. Storage

- [ ] 3.1 Extend `IGraphStore.ListCallersAsync` / `ListCalleesAsync` to take optional `EdgeKind?` filter (default = `Calls` for back-compat).
- [ ] 3.2 New `IGraphStore.ListImplementationsAsync(symbolId, limit)` returning members linked via `ImplementsMember`.
- [ ] 3.3 New `IGraphStore.ListUsersOfTypeAsync(symbolId, limit)` for `UsesType` reverse lookup.

## 4. MCP tools

- [ ] 4.1 Add optional `kind` parameter to `list_callers`, `list_callees`, `neighborhood`, `impact_of_change`. Accepts `calls | uses_type | overrides | implements | instantiates | throws | all`.
- [ ] 4.2 New tool `find_implementations(symbol, includeAbstract = false)` returning every member that satisfies the named interface member.
- [ ] 4.3 `find_references` results display the resolved `ReferenceKind` (`def | ref | call | read | write | impl | inherit`).

## 5. Tests

- [ ] 5.1 Read/Write detection: fixtures with `_x = 1`, `_x++`, `Method(out _x)`, `Method(ref _x)`, plain read `var y = _x`.
- [ ] 5.2 UsesType: a method `void M(IFoo f, List<Bar> b)` produces UsesType edges to `IFoo`, `List<>`, `Bar`.
- [ ] 5.3 OverridesMember on properties and events, not just methods.
- [ ] 5.4 ImplementsMember: a class `C : IGreeter` with `void Greet()` emits `ImplementsMember(C.Greet → IGreeter.Greet)`.
- [ ] 5.5 Instantiates and Throws from a fixture method.
- [ ] 5.6 Live update: edit a fixture file to add a `throw new InvalidOperationException()`; assert the new `Throws` edge appears within the watcher debounce window.

## 6. Update specs

- [ ] 6.1 Sync delta specs into `openspec/specs/{indexing, storage, mcp-tools}/spec.md` on archive.
