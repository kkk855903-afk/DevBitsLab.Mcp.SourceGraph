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
