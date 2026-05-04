## Context

`Edge(src, dst, kind)` and `SymbolReference(symbol_id, file_id, line, col, kind)` already accept arbitrary `INTEGER` values for `kind`, so the storage layer doesn't change. The work is concentrated in pass 2 of `RoslynIndexer`, which today only emits `Calls`, `Inherits`, `Implements`. The hard parts are:

1. **Scoping the explosion.** UsesType across an entire compilation generates millions of edges; we need to skip external types and trivial primitives.
2. **Attribution.** A `new T()` happens in *some* method; that method is the edge source. Same for `throw`. We already have `FindEnclosingMember` from pass 2.
3. **Read/Write detection.** Roslyn doesn't directly expose this; we infer from the syntax position.

## Goals / Non-Goals

**Goals:**
- The new edges are *useful and bounded*: queries like "consumers of `CancellationToken`" return tens of results, not the whole codebase.
- Read/Write is correct for the common cases (assignment LHS, `++`/`--`, `out`/`ref` parameter, compound assignment).
- Member-level Override and Implements work for properties, events, and methods (not just methods).
- Live updates correctly invalidate the new edges when their source file changes.

**Non-Goals:**
- Generic `UsesType` edges to types in external assemblies (`System.*`, third-party). They're noise. We only emit `UsesType` between *indexed* symbols.
- Static-flow precision for `Read`/`Write`. We don't run dataflow analysis; we use syntactic position. False positives on aliasing are accepted.
- `Throws` edges for exceptions that propagate (a caller of a throwing method). Only direct `throw` statements/expressions are edges. Transitive analysis is for `impact_of_change`.

## Decisions

**1. UsesType edge sources are members; targets are types.**
For a method `M` with signature `T M(U u, V v)`, we emit `UsesType(M → T)`, `UsesType(M → U)`, `UsesType(M → V)`. For body-locals like `var x = new W();` we additionally emit `UsesType(M → W)`. For class `C : B, IX`, the indexer emits the existing `Inherits(C → B)` and `Implements(C → IX)` and *also* a `UsesType` edge to each (yes, redundant — but cheap and lets `kind=uses_type` answer "all consumers of B" without extra cases).

**2. Read/Write detection from syntax.**
- LHS of `AssignmentExpressionSyntax`: `Write`.
- `++`, `--`, `+=`, `-=`, etc.: `Read` AND `Write` (two refs at the same position).
- Argument bound to `out` parameter: `Write`.
- Argument bound to `ref` parameter: `Read` AND `Write`.
- Everything else: `Read`.

**3. Member-level overrides via Roslyn properties.**
- `IMethodSymbol.OverriddenMethod` — emits `OverridesMember(M → base)`.
- `IPropertySymbol.OverriddenProperty` — same shape for properties.
- `IEventSymbol.OverriddenEvent` — same for events.

**4. Member-level implements via Roslyn helpers.**
For each member `M` of an indexed type `T`, walk `T.AllInterfaces` and call `T.FindImplementationForInterfaceMember(interfaceMember)`. When the result is `M`, emit `ImplementsMember(M → interfaceMember)`. Done once per type during pass 2.

**5. `Throws` edges from a single syntax pass.**
For each `ThrowStatementSyntax` / `ThrowExpressionSyntax`, resolve the thrown type via `model.GetTypeInfo` and emit `Throws(enclosing-member → type)`.

## Risks / Trade-offs

- **Edge table size.** UsesType + Read/Write split increases edge count. Mitigation: skip primitives, skip external types (only edges between *indexed* symbols), skip implicit Object base. On the Feeds solution we project ~80k edges total (vs. today's ~3k), well within SQLite's comfort zone.
- **Live-update reconcile cost.** Today `ClearFileOutgoingAsync` deletes a file's edges; with 5× more edges per file, the delete is 5× larger. Still <1 ms on the affected files.
- **`Read` vs `Write` ambiguity in expression trees and reflection.** Treated as `Read`. Documented limitation.
- **Compatibility.** `kind` integer values are extended, not changed. Existing queries with hard-coded `kind = 0` (Calls) keep working.
