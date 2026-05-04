## ADDED Requirements

### Requirement: UsesType edges between indexed members and types
The indexer SHALL emit a `UsesType` edge from every indexed member symbol to every indexed type symbol that appears in its signature (parameter types, return type, generic arguments) and in its body's `new T()` and locally-declared types.

#### Scenario: Method that consumes a CancellationToken parameter
- **WHEN** an indexed method `void M(CancellationToken ct)` is processed and `CancellationToken` is itself indexed (or the agent has chosen to also index BCL types)
- **THEN** an edge `(M.id, CancellationToken.id, UsesType)` is written

#### Scenario: External / non-indexed type ignored
- **WHEN** the parameter type is not in the graph (e.g. an unindexed BCL type)
- **THEN** no edge is emitted for that type

### Requirement: Read vs Write reference kinds for field/property access
The indexer SHALL distinguish `Read` and `Write` reference kinds based on syntactic position (assignment LHS, `++`/`--`, `out`/`ref` argument).

#### Scenario: Plain read
- **WHEN** the source contains `var y = _x;`
- **THEN** the reference at `_x` is recorded with `kind = Read`

#### Scenario: Assignment LHS
- **WHEN** the source contains `_x = 1;`
- **THEN** the reference at `_x` is recorded with `kind = Write`

#### Scenario: Increment is read+write
- **WHEN** the source contains `_x++;`
- **THEN** two reference rows are written at the same position: one `Read`, one `Write`

#### Scenario: out parameter
- **WHEN** the source contains `Method(out _x)`
- **THEN** the reference at `_x` is `Write`

#### Scenario: ref parameter
- **WHEN** the source contains `Method(ref _x)`
- **THEN** two rows are written: one `Read`, one `Write`

### Requirement: Member-level Override edges
The indexer SHALL emit `OverridesMember` edges for methods, properties, and events whose `Overridden*` Roslyn property is set and points at an indexed symbol.

#### Scenario: Override of a virtual method
- **WHEN** `class B { public virtual void F() {} }` and `class D : B { public override void F() {} }` are both indexed
- **THEN** an edge `(D.F.id, B.F.id, OverridesMember)` is written

### Requirement: Member-level ImplementsMember edges
The indexer SHALL emit `ImplementsMember` edges from each implementing member to the interface member it satisfies, using `FindImplementationForInterfaceMember`.

#### Scenario: Class implements an interface method
- **WHEN** `interface IG { void Greet(); }` and `class G : IG { public void Greet() {} }` are both indexed
- **THEN** an edge `(G.Greet.id, IG.Greet.id, ImplementsMember)` is written

#### Scenario: Explicit interface implementation
- **WHEN** the implementing member is `void IG.Greet() {}` (explicit)
- **THEN** an `ImplementsMember` edge is still emitted with the explicit member as source

### Requirement: Instantiates edges from `new T()`
For every `ObjectCreationExpressionSyntax`, the indexer SHALL emit an `Instantiates` edge from the enclosing member to the constructed type (in addition to the existing `Call` edge to the constructor).

#### Scenario: Construct an indexed type
- **WHEN** a method body contains `new MyClass()` and `MyClass` is indexed
- **THEN** an edge `(method.id, MyClass.id, Instantiates)` is written alongside the existing constructor `Call` edge

### Requirement: Throws edges from `throw` syntax
For every `ThrowStatementSyntax` and `ThrowExpressionSyntax`, the indexer SHALL emit a `Throws` edge from the enclosing member to the thrown type, when the thrown type is indexed.

#### Scenario: Throw an indexed exception type
- **WHEN** an indexed method body contains `throw new MyDomainException();`
- **THEN** an edge `(method.id, MyDomainException.id, Throws)` is written
