# Interop analysis

## Requirements

### Requirement: Managed imports normalize through Roslyn semantics

The managed interop adapter SHALL discover `DllImportAttribute` and
`LibraryImportAttribute` from resolved Roslyn attribute symbols, not from source-text matching.
It SHALL convert a declaration into the analyzer-neutral `ManagedImport` model for one explicit
`InteropTarget`, including library name, effective entry point, calling convention, character
encoding, `ExactSpelling`/runtime name-lookup policy, `SetLastError`, return type, ordered
parameters, direction, pointer depth, fixed-array length, and occurrence evidence. Defaults SHALL
be applied only when Roslyn and the explicit target runtime prove that default; unsupported,
conflicting, or target-dependent values SHALL remain unknown. Roslyn objects SHALL NOT cross into
the rule engine.

#### Scenario: DllImport with explicit marshal facts

- **WHEN** a method has `DllImport("medalgo", EntryPoint = "run", CallingConvention = Cdecl, CharSet = Unicode)` and a parameter has `MarshalAs(LPUTF8Str)`
- **THEN** the adapter emits a `ManagedImport` whose target is explicit, calling convention is `Cdecl`, declaration character set is `utf-16`, that parameter encoding is `utf-8`, and evidence identifies the import attribute's real source range

#### Scenario: LibraryImport calling convention modifier

- **WHEN** a partial method has `LibraryImport` plus `UnmanagedCallConv(CallConvs = [CallConvStdcall, CallConvSuppressGCTransition])`
- **THEN** the ABI calling convention is `StdCall`; non-convention modifiers do not erase the convention fact

#### Scenario: Ordinary managed method

- **WHEN** a method has neither exactly one resolved `DllImportAttribute` nor exactly one resolved `LibraryImportAttribute`
- **THEN** the managed interop adapter returns no import fact and does not infer one from `extern`, method names, or string similarity

### Requirement: Boundary types and parameter direction retain ABI meaning

Managed and native adapters SHALL normalize by-value scalar input, `ref`/`out`, explicit
`In`/`Out`, pointer and reference layers, pointee type, pointee `const` qualification, function
pointer, array, string, enum, and record identity without flattening distinct meanings into a
display string. A pointer's target-sized storage SHALL NOT be reported as the size of its pointee
or record. Record types SHALL carry a stable declaration identity; an incomplete or unresolved
record SHALL retain that identity with unknown layout facts rather than becoming a guessed scalar.
Strings SHALL use stable encodings such as `ansi`, `utf-8`, and `utf-16` only when the declaration,
marshal metadata, and target prove them. A native `char*`/`wchar_t*` spelling alone SHALL NOT prove
string ownership, termination, or direction.

`AbiParameterDirection` SHALL include `Unknown`. By-value language semantics and explicit
direction metadata MAY prove `In`, `Out`, or `InOut`; a mutable native pointer, contradictory
attributes, or missing annotations SHALL remain `Unknown`. Match and rule code SHALL NOT coerce
`Unknown` to `In`, nor report a direction mismatch unless both directions are proven.

#### Scenario: Mutable pointer direction is not guessed

- **WHEN** a native parameter is `Record* value` with no contract or data-flow proof and the managed parameter is explicitly `[In, Out] ref Record`
- **THEN** the native direction is `Unknown`; the adapter retains pointer depth and the `Record` pointee identity, and `Interop003` does not claim a proven direction mismatch

#### Scenario: Const pointee proves input without losing record identity

- **WHEN** a native parameter is `const Record* value` and `Record` resolves to a declaration for the selected target
- **THEN** it normalizes as input with one pointer layer, the `const` pointee fact, and the stable `Record` identity; pointer size and record size remain separate facts

### Requirement: Unknown marshal facts remain unknown

The adapter SHALL preserve uncertainty instead of guessing platform-dependent layouts. Unknown
record sizes, unsupported custom marshalers, missing string encodings, invalid fixed lengths, and
overflowing size calculations SHALL remain unknown or opaque so downstream rules can emit an
inferred compatibility warning without claiming a proven mismatch. An unrecognized, invalid, or
duplicated `MarshalAs` SHALL NOT fall back to the unmanaged default for the managed CLR type.
Fixed-array and inline-string size arithmetic SHALL be checked; zero/negative lengths and overflow
SHALL produce an opaque or unknown-sized fact with a stable reason, not an exception, wrapped
value, match, or compatibility claim.

#### Scenario: Unknown MarshalAs fails closed

- **WHEN** Roslyn resolves one `MarshalAs` attribute whose unmanaged enum value is unsupported
- **THEN** the parameter remains opaque with occurrence evidence and a stable reason; it is not normalized as though `MarshalAs` were absent

#### Scenario: Inline size overflows

- **WHEN** a fixed array or inline string's element width multiplied by its declared length exceeds the supported integer range
- **THEN** extraction completes with unknown size, the boundary remains queryable as partial/unknown compatibility, and no wrapped size or definitive size mismatch is emitted


### Requirement: P/Invoke matching requires exact boundary identity

`InteropMatcher` SHALL derive runtime export spellings only from the managed entry point,
`ExactSpelling`, proven character set, calling convention, and explicit target-runtime rules.
`ExactSpelling = true` permits only the declared spelling. On Windows, a proven non-exact lookup
policy MAY add the target-appropriate `A` or `W` spelling; an unknown character set SHALL NOT try
both and select whichever happens to exist. x86 decorated spellings such as `_name@N` SHALL be
viable only when the target/calling convention and checked parameter-byte calculation prove the
decoration, or when an authoritative module definition or binary export table names it. The
matcher SHALL NOT strip decoration or suffixes by similarity.

The matcher SHALL also require the same `InteropTarget` and proven native module ownership. On
Windows only, proven module names compare case-insensitively and a terminal `.dll` suffix is
optional. Source folders, project names, header names, `extern "C"`, and `dllexport` intent alone
SHALL NOT prove the owning module. A `.def` export or alias MAY prove a public export name and
module only when that file is proven to be a link input for the selected target artifact. A binary
export table is authoritative for that artifact; failed, unavailable, stale, or target-mismatched
binary verification makes artifact presence incomplete rather than proving absence.

Match status SHALL obey these evidence boundaries:

- `Matched` means exactly one runtime-legal export has proven target and module provenance, with
  its public name proven by a verified binary or authoritative build/link input.
- `Ambiguous` means at least two runtime-legal candidates are proven and no target may be chosen.
- `Unmatched` means a complete authoritative export universe was examined and proves that no legal
  module/name candidate exists.
- `Unknown` means target, lookup policy, module provenance, decoration, export verification, or
  candidate-universe completeness is insufficient to prove one of the other states.

Every result SHALL retain the managed evidence plus `.def`, binary, build-metadata, and source
evidence from the candidates that determined its status. A candidate without proven module
ownership SHALL never produce `Matched` and SHALL prevent a false unique match while it remains
viable.

#### Scenario: One exact Windows candidate

- **WHEN** managed module `MEDALGO` / entry point `run` is compared with a `medalgo.dll` export named exactly `run` for the same target
- **THEN** the result is `Matched`, identifies that native symbol, and explains the entry-point, module, and target facts

#### Scenario: Owning module is unavailable

- **WHEN** a same-name, same-target export has no proven native module identity
- **THEN** the result is `Unknown`, not matched by source-folder or project-name similarity

#### Scenario: Non-exact Windows lookup is ambiguous

- **WHEN** `ExactSpelling` is false, Unicode lookup is proven, and both runtime-legal exact and `W` exports remain viable with no proven runtime preference
- **THEN** the result is `Ambiguous`; the matcher does not choose one by enumeration order

#### Scenario: Def alias is tied to the selected artifact

- **WHEN** a scope-approved `.def` proven as a link input for `medalgo.dll` maps public export `run` to an internal decorated symbol for the same target
- **THEN** `run` is a viable public export with `.def` evidence; the internal decoration is not exposed as a second guessed entry point

#### Scenario: Source intent lacks artifact verification

- **WHEN** a header declares `extern "C" __declspec(dllexport) run` but neither module provenance nor a complete target artifact/link-input mapping is proven
- **THEN** the match is `Unknown`, not `Matched` or `Unmatched`

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

Phase 2 MAY extract target-specific record identity and known layout facts for normalization, but
it SHALL NOT compare managed/native field order, offsets, packing, nested layout, or overall
record compatibility. `Interop002`, `compare_struct`, and definitive struct-layout compatibility
remain Phase 3 behavior.

### Requirement: Production indexing publishes interop boundaries atomically

Cold, one-shot, and live production indexing SHALL run managed import extraction and configured
native export extraction; production behavior SHALL NOT depend on tests constructing domain DTOs
by hand. Extraction, matching, `pinvoke-maps-to` edges, and Phase 2 findings SHALL use one explicit
target and one complete privacy-approved native export snapshot.

Before reading bytes, parsing source or `.def` files, following includes, inspecting a binary, or
starting an approved compiler/tool process, the indexer SHALL enforce scope excludes, mandatory
privacy patterns, lexical repository containment, and resolved physical containment. Excluded,
privacy-sensitive, escaped, or untrusted inputs SHALL not be opened and SHALL make the relevant
candidate universe incomplete when they could affect a conclusion.

One managed file's import symbols, match outcomes, outgoing interop edges, findings, annotations,
hash, and occurrence evidence SHALL be replaced atomically, including a successful zero-fact
replacement. A non-cancellation extraction, matching, evidence-validation, or storage failure
SHALL retain that file's last successful interop facts and surface a file/project failure. A
confirmed managed-file deletion SHALL transactionally remove its owned imports, edges, findings,
and evidence. A native source, `.def`, binary, or project-control deletion SHALL first rebuild a
complete native snapshot and rematch affected managed imports; if rebuild is incomplete, prior
boundary facts remain and the scope is partial rather than publishing false `Unmatched` results.

Native include dependencies SHALL be tracked. A scope-approved header create/edit/delete SHALL
re-extract every known owning translation unit and fan out to all affected managed imports.
Conservative fanout across all scope-approved native translation units/imports is allowed when
the dependency set is complete; if complete fanout cannot be proven, the old boundary snapshot
SHALL be retained and incompleteness surfaced. Fanout paths SHALL be deterministic, deduplicated,
and rechecked against scope/privacy boundaries.

#### Scenario: Header edit refreshes a stored boundary

- **GIVEN** a stored import matches an export whose signature comes from a native header
- **WHEN** a scope-approved header edit changes its parameter type
- **THEN** every owning translation unit is re-extracted and the managed file's match, edge, and Phase 2 findings are atomically refreshed from the rebuilt snapshot

#### Scenario: Privacy rejection happens before parse

- **WHEN** an include, `.def`, or configured binary path is excluded, privacy-sensitive, or physically escapes the scope
- **THEN** the indexer does not open or parse it, reports the relevant extraction/match as incomplete, and does not replace prior facts with absence claims

### Requirement: Phase 2 risk rules consume only proven normalized facts

The Phase 2 rule pack SHALL contain `Interop001` and `Interop003` through `Interop006`.
`Interop002` SHALL remain in the Phase 3 ABI-layout pack. Callback lifetime, native exception
escape, and memory ownership adapters SHALL normalize their results into Core domain facts before
rule evaluation; Roslyn, Clang, and other third-party objects SHALL NOT cross into these rules.
Every usage and native proof SHALL identify one explicit `InteropTarget` and carry occurrence
evidence. Production rule evaluation SHALL run only for a `Matched` boundary. `Interop001` and
`Interop003` MAY emit an inferred warning that compatibility is unknown, but SHALL NOT turn an
unknown convention, direction, size, encoding, marshal shape, or pointee into a definitive
mismatch. Missing retention/escape/ownership facts, `Unknown` callback rooting, or `Unknown`
allocator family SHALL remain unknown and SHALL NOT produce `Interop004` through `Interop006`.

#### Scenario: Real extraction reaches matching and a Phase 2 rule

- **GIVEN** real Roslyn source declares a win-x86 `DllImport` for `medalgo`/`risk_call` with `StdCall`
- **AND** real native source plus authoritative artifact evidence proves the same module/export/target with `Cdecl`
- **WHEN** the production adapters extract both sides, the matcher selects the unique boundary, and the Phase 2 engine evaluates it
- **THEN** a `pinvoke-maps-to` edge and one `Interop001` finding are persisted with managed declaration, native declaration/artifact, target, and weakest-confidence evidence
- **AND** the acceptance test does not bypass either adapter by constructing `ManagedImport` or `NativeExport` directly

### Requirement: Interop edges and findings preserve boundary evidence

A `pinvoke-maps-to` edge SHALL be emitted only for `Matched`, from the real managed import symbol
to the real native export symbol. `Unknown`, `Unmatched`, and `Ambiguous` outcomes SHALL remain
queryable annotations/results and SHALL NOT synthesize a native target or edge. An interop finding
SHALL identify its rule, severity, explicit target, managed and native canonical keys when
applicable, and every occurrence needed to audit the claim. Parameter findings SHALL include both
parameter locations; `.def` aliases and binary verification SHALL retain their own provenance.
Finding and edge confidence SHALL be no stronger than the weakest evidence on which the result
depends.

Deleting or replacing either endpoint SHALL remove or recompute stale `pinvoke-maps-to` edges and
findings in the same successful refresh. Query rendering SHALL use stored evidence and SHALL NOT
reconstruct a stronger explanation from names.

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

### Requirement: Interop MCP tools are typed, bounded, and uncertainty-preserving

The server SHALL expose read-only `match_pinvoke` and `analyze_native_boundary` MCP tools with
named output DTOs and declared object-root `outputSchema`. Successful responses, including zero
rows, SHALL contain typed `structuredContent` as well as consistent prose.

`match_pinvoke` SHALL return typed per-scope match rows containing the managed symbol, explicit
target, status, optional native symbol, confidence, non-empty reasons, candidate count, and stored
evidence. `analyze_native_boundary` SHALL return the typed match plus the Phase 2 finding rows
(`Interop001`, `Interop003`–`Interop006`) and SHALL not expose `Interop002` or struct compatibility.
It SHALL evaluate findings only for `Matched`; other statuses return an empty findings collection
with their reasons intact.

Symbol selection SHALL fail closed: zero candidates is not-found, multiple managed imports or
native boundaries are an explicit ambiguous selection, and neither tool SHALL choose by store or
scope enumeration order. Multi-scope execution SHALL retain one typed scope block for every
selected scope, including empty, partial, and degraded scopes. A partial native/import snapshot
SHALL be disclosed and SHALL prevent an absence-only `Unmatched` conclusion.

The shared soft output budget SHALL cover the fully materialized response—prose, typed rows,
candidate details, and evidence—with the project target of 50K characters. Trimming SHALL be
deterministic and keep prose/structured rows consistent. The response SHALL disclose
`truncated`/`omitted_count` (including evidence omission) while retaining status, reasons, scope,
target, and total candidate/finding counts; truncation SHALL never convert `Ambiguous` to
`Matched`.

#### Scenario: Ambiguous match survives MCP rendering

- **GIVEN** one managed import has two proven runtime-legal native candidates
- **WHEN** `match_pinvoke` is called
- **THEN** prose and structured content both report `Ambiguous`, no native symbol is selected, and candidate evidence is returned up to the disclosed budget

#### Scenario: Partial scope cannot prove absence

- **GIVEN** native extraction failed for one scope-approved translation unit or artifact
- **WHEN** either interop tool queries an otherwise same-name import
- **THEN** that scope block is marked partial, the match is `Unknown` rather than absence-only `Unmatched`, and no boundary finding is emitted

#### Scenario: Bounded multi-scope response

- **GIVEN** multiple selected scopes contain matches, ambiguity, empty results, or degraded state
- **WHEN** an interop tool fans out
- **THEN** every selected scope retains a typed summary and the complete materialized response remains within the budget or explicitly reports deterministic omission
