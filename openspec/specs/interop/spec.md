# Interop analysis

## Requirements

### Requirement: Managed imports normalize through Roslyn semantics

The managed interop adapter SHALL discover `DllImportAttribute` and
`LibraryImportAttribute` from resolved Roslyn attribute symbols, not from source-text matching.
It SHALL convert a declaration into the analyzer-neutral `ManagedImport` model for one explicit
`InteropTarget`, including library name, effective entry point, calling convention, character
encoding, `SetLastError`, return type, ordered parameters, direction, pointer depth, fixed-array
length, and occurrence evidence. Roslyn objects SHALL NOT cross into the rule engine.

#### Scenario: DllImport with explicit marshal facts

- **WHEN** a method has `DllImport("medalgo", EntryPoint = "run", CallingConvention = Cdecl, CharSet = Unicode)` and a parameter has `MarshalAs(LPUTF8Str)`
- **THEN** the adapter emits a `ManagedImport` whose target is explicit, calling convention is `Cdecl`, declaration character set is `utf-16`, that parameter encoding is `utf-8`, and evidence identifies the import attribute's real source range

#### Scenario: LibraryImport calling convention modifier

- **WHEN** a partial method has `LibraryImport` plus `UnmanagedCallConv(CallConvs = [CallConvStdcall, CallConvSuppressGCTransition])`
- **THEN** the ABI calling convention is `StdCall`; non-convention modifiers do not erase the convention fact

#### Scenario: Ordinary managed method

- **WHEN** a method has neither exactly one resolved `DllImportAttribute` nor exactly one resolved `LibraryImportAttribute`
- **THEN** the managed interop adapter returns no import fact and does not infer one from `extern`, method names, or string similarity

### Requirement: Unknown marshal facts remain unknown

The adapter SHALL preserve uncertainty instead of guessing platform-dependent layouts. Unknown
record sizes, unsupported custom marshalers, missing string encodings, invalid fixed lengths, and
overflowing size calculations SHALL remain unknown or opaque so downstream rules can emit an
inferred warning.

### Requirement: P/Invoke matching requires exact boundary identity

`InteropMatcher` SHALL require exact entry-point spelling, a target-aware module-name match, and
the same `InteropTarget`. On Windows only, module comparison is case-insensitive and a terminal
`.dll` suffix is optional. Source folders, project names, and similar spellings SHALL NOT create a
match. Every result SHALL retain the managed evidence plus evidence from the candidates that
determined its status.

#### Scenario: One exact Windows candidate

- **WHEN** managed module `MEDALGO` / entry point `run` is compared with a `medalgo.dll` export named exactly `run` for the same target
- **THEN** the result is `Matched`, identifies that native symbol, and explains the entry-point, module, and target facts

#### Scenario: Owning module is unavailable

- **WHEN** a same-name, same-target export has no proven native module identity
- **THEN** the result is `Unknown`, not matched by source-folder or project-name similarity

#### Scenario: Multiple exact candidates

- **WHEN** more than one export has the same normalized module, exact entry point, and target
- **THEN** the result is `Ambiguous`, carries all candidate evidence, and chooses no native symbol

### Requirement: Managed record layouts are target-specific

The Roslyn layout adapter SHALL convert a source struct into `AbiRecordLayout` for one explicit
`InteropTarget`. It SHALL honor `Sequential`/`Explicit`, `Pack`, declared `Size`, `FieldOffset`,
`MarshalAs(ByValArray)`, `MarshalAs(ByValTStr)`, fixed buffers, primitive widths, and field source
order. Sequential offsets SHALL use the target's default pack when no explicit pack is present.
Unknown nested sizes, invalid packs, and arithmetic overflow SHALL propagate as unknown rather
than guessed offsets. Sequential structs split across multiple partial declarations SHALL retain
their field facts but not claim a compiler-dependent cross-part offset order.

#### Scenario: Packed sequential struct

- **WHEN** a sequential managed struct uses `Pack = 1` and contains a byte followed by a 4-byte integer
- **THEN** the integer offset is `1`, the record alignment is `1`, and both field declarations carry source evidence

#### Scenario: Explicit union

- **WHEN** two fields in an explicit-layout struct both declare `FieldOffset(0)`
- **THEN** both normalized fields retain offset `0`; the adapter does not reorder or de-overlap them

#### Scenario: Auto layout

- **WHEN** a struct explicitly uses `LayoutKind.Auto`
- **THEN** no ABI layout fact is emitted

### Requirement: Phase 2 risk rules consume only proven normalized facts

The Phase 2 rule pack SHALL contain `Interop001` and `Interop003` through `Interop006`.
`Interop002` SHALL remain in the Phase 3 ABI-layout pack. Callback lifetime, native exception
escape, and memory ownership adapters SHALL normalize their results into Core domain facts before
rule evaluation; Roslyn, Clang, and other third-party objects SHALL NOT cross into these rules.
Every usage and native proof SHALL identify one explicit `InteropTarget` and carry occurrence
evidence. A missing fact, `Unknown` callback rooting, or `Unknown` allocator family SHALL remain
unknown and SHALL NOT produce a risk finding.

### Requirement: Interop004 requires retention and unrooted-call proof

`Interop004` SHALL emit a warning only when the native export is proven to retain a callback
parameter and a managed invocation of that same parameter position is proven not to establish a
GC root. The finding's managed symbol SHALL be the managed caller, not the import declaration.
The finding SHALL include declaration, retention, and call-site evidence, and its confidence SHALL
be the weakest included confidence.

#### Scenario: Retained callback passed without a GC root

- **WHEN** native data flow proves that callback parameter `0` is retained and managed data flow proves that caller `RegisterUnrootedCallback` passes parameter `0` without establishing a GC root for the same target
- **THEN** `Interop004` emits one warning attributed to `RegisterUnrootedCallback` with both proof locations

#### Scenario: One callback proof is absent

- **WHEN** retention is unknown, managed rooting is unknown, or the callback is proven rooted
- **THEN** `Interop004` emits no finding

### Requirement: Interop005 requires proven exception escape

`Interop005` SHALL emit an error only when native control flow proves that an exception can leave
the matched export across the C ABI for the same target. Declaration syntax, a throwable callee,
or an unknown catch path alone SHALL NOT be treated as escape proof. The finding SHALL include the
native escape evidence and boundary declaration evidence, and its confidence SHALL be the weakest
included confidence.

#### Scenario: Untranslated native throw reaches the export boundary

- **WHEN** native control flow proves that an exception can leave `risk_throws` without translation
- **THEN** `Interop005` emits one error for that import/export boundary

#### Scenario: Exception flow is unknown

- **WHEN** no native exception-escape fact exists for the target
- **THEN** `Interop005` emits no finding

### Requirement: Interop006 compares proven allocation and release families

`Interop006` SHALL emit a warning only when native return-flow analysis proves an allocator family
and managed use-flow analysis proves a different release family for that returned value on the
same target. The finding's managed symbol SHALL be the release caller. Equal families, unknown
families, facts for different targets, or a release not proven to consume the returned value SHALL
NOT produce a finding. The finding SHALL include allocation and release evidence plus boundary
declaration evidence, and its confidence SHALL be the weakest included confidence.

#### Scenario: CRT allocation released as COM task memory

- **WHEN** native return flow proves `risk_allocate` returns `CrtHeap` memory and managed caller `FreeWithWrongAllocator` is proven to release that value with `CoTaskMem`
- **THEN** `Interop006` emits one warning attributed to `FreeWithWrongAllocator`

#### Scenario: Ownership compatibility is not disproven

- **WHEN** the families are equal or either family is unknown
- **THEN** `Interop006` emits no finding
