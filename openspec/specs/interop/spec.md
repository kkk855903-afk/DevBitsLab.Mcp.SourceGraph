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
