# Architecture

This document describes the implemented architecture of
`DevBitsLab.Mcp.SourceGraph` as extended for MedInteropLens. It complements the
user-facing [README](../README.md), the current implementation/status record in
[architecture.md](../architecture.md), and the residual-risk register in
[risk.md](../risk.md).

The repository is no longer the Phase-0 empty baseline. The current tool builds
one local, evidence-backed graph across C#, WPF/XAML, protobuf/gRPC, managed
interop, C ABI exports, and C++ functions.

## Design rules

The implementation follows five rules:

1. There is one MCP host and one graph store per scope, not a server/database per
   language.
2. Language adapters emit normalized canonical keys and facts. Roslyn symbols,
   Clang cursors, and protobuf descriptors do not cross the adapter boundary.
3. A logical edge is useful only with occurrence-level evidence.
4. Derived cross-language projections publish atomically and fail closed. An
   incomplete refresh cannot prove absence.
5. Discovery, indexing, live updates, secondary inputs, and deletion share the
   same mandatory privacy boundary.

The dependency direction is:

```text
Server / MCP tools
        ↓
query services, linkers, and rule engines
        ↓
Core + SDK normalized contracts
        ↓
Storage abstractions and SQLite
```

Analyzers depend on Core/SDK contracts and storage abstractions where they need
to publish facts. Storage has no MCP dependency, and MCP DTOs contain no parser
or compiler objects.

## Module layout

```text
+--------------------------------------------------------------------+
| Server                                                             |
| stdio MCP · scope router · LiveIndexService · bounded query tools   |
| gRPC linker/query · native coordinator/worker client · observability|
+-------------------------------+------------------------------------+
                                |
+-------------------------------v------------------------------------+
| Indexing              | Indexing.Xaml       | Indexing.Clang        |
| Roslyn + protobuf     | XAML semantics      | target-aware native   |
| managed interop/WPF   | resources/bindings  | facts and direct calls|
+-------------------------------+------------------------------------+
                                |
+-------------------------------v------------------------------------+
| Interop                                                           |
| P/Invoke matching · PE exports · ABI records · Interop001–006      |
+-------------------------------+------------------------------------+
                                |
+-------------------------------v------------------------------------+
| Storage                                                           |
| SQLite schema v14 · FTS5 · edge evidence · atomic projections      |
+-------------------------------+------------------------------------+
                                |
+-------------------------------v------------------------------------+
| Core + SDK                                                        |
| domain facts · privacy/trust · canonical keys · plugin contracts   |
+--------------------------------------------------------------------+

Watcher: registered source extensions + project files + git HEAD.
Embeddings: optional local ONNX + sqlite-vec.
TreeSitter/TypeScript: retained open-language adapters, not authoritative
for the MedInterop execution-completeness decision.
```

### Core

`Core` owns storage-neutral domain records, scope configuration, normalized
Interop/ABI facts, evidence, mandatory privacy rules, physical scope-path
resolution, and execution-trust contracts.

### SDK

`Sdk` is the public `netstandard2.0` plugin contract. It supplies
`ILanguageIndexer`, `IndexEvent`, open kebab-case symbol/edge kinds,
occurrence-level `EdgeEvidence`, and canonical-key helpers. Its current package
version is 2.5.0.

Supported authoritative canonical-key families are:

- `csharp:` for Roslyn symbols;
- `xaml:` for markup declarations;
- `proto:` for protobuf declarations;
- `c:` for C ABI exports;
- `cpp:` for C++ declarations.

### Indexing

`Indexing` owns the Roslyn workspace, C# declarations/references/edges,
diagnostic reconciliation, generated documents, command execution facts,
managed interop declarations/usages/layouts, and protobuf descriptor
compilation/projection.

The WPF risk analyzers live here because they require a complete Roslyn
compilation. They publish ordinary persisted diagnostics rather than introducing
a second rule database.

### XAML

`Indexing.Xaml` parses WPF, WinUI, UWP, Avalonia, and Uno dialects. For the
MedInterop WPF path it combines markup structure with a sanitized Roslyn
compilation to resolve `x:Class`, DataContext, Binding/Command properties,
events, and resources.

An unresolved Binding is an explicit outcome with a reason (`missing`,
`ambiguous`, `unsupported`, or `incomplete`). The indexer does not fabricate a
resolved property to make a path connect.

### Native and Interop

`Indexing.Clang` uses ClangSharp/libclang for target-aware function, struct,
union, enum, typedef, layout, export, direct-call, callback-retention,
exception-escape, and allocation facts. Production extraction runs in a child
process managed by `Server`, not inside the MCP process.

`Interop` consumes normalized managed/native facts. It owns matching, PE export
verification, ABI record comparison, and the initial `Interop001`–`Interop006`
rules.

### Storage

`Storage` owns the scope database and all mutation semantics. The current schema
version is 14. A scope database is a rebuildable derived index; `_meta.db` stores
the scope registry.

### Server

`Server` composes the stdio MCP transport, stores, live indexers, optional
embeddings/history, query tools, gRPC projection, and native coordination.
Domain tools return structured content and declare read-only/idempotent
semantics.

## The graph and its evidence

### Logical facts

`symbols` records stable canonical identity, kind, name/FQN, source file and
range, signature, and containment. `edges` contains one row per logical
`(source, target, relation)`.

The principal cross-domain relations are:

```text
binds-path
command-executes
grpc-calls
implements-rpc
rpc-dispatches-to
pinvoke-maps-to
struct-maps-to
```

They coexist with ordinary `calls`, `references`, `reads`, `writes`, `inherits`,
`implements`, and other source-graph relations.

### Occurrence evidence

Schema v13 split edge identity from proof. `edge_evidence` stores one or more
occurrences for a logical edge:

```text
producing file id
file path
1-based, end-exclusive source range
confidence: Exact | Semantic | Inferred
producer
occurrence payload
```

Multiple call sites between the same methods therefore remain visible. Deleting
or refreshing one producer removes only its evidence; the logical edge remains
while any other evidence survives.

### Atomic ownership

The write API has explicit ownership boundaries:

- `ReplaceFileFactsAsync` replaces one file's complete direct facts;
- producer/file and producer/scope APIs replace derived evidence precisely;
- multi-file derived replacement prevents mixed generations across a linker;
- native snapshot replacement publishes the complete native candidate;
- stale native symbols are deleted only after every managed boundary refreshes;
- gRPC baselines insert the first complete successful observation and are never
  overwritten by normal indexing.

All candidates are validated and endpoints resolved before old rows change.
Failure rolls the transaction back.

## Indexing pipelines

### Roslyn pipeline

A scope opens its selected solution with `MSBuildWorkspace`, applies the physical
privacy policy to the resulting solution, and indexes declarations in passes:

1. declarations and stable canonical identities;
2. annotations and generated-document facts;
3. references, calls, type/member relations, command execution, and managed
   interop usage;
4. compiler and WPF risk diagnostics.

Diagnostics are reconciled per successful project. Project-wide WPF facts cause
the small set of files that own current or previous WPF diagnostics to be
revisited, so adding a matching `-=` in a different file clears a stale
`WPFEVENT001` finding.

### XAML pipeline

The XAML project factory unions MSBuild resource items with a policy-pruned
directory discovery. It builds a project resource snapshot and supplies the
matching Roslyn compilation when semantic input is complete.

The language indexer publishes XAML symbols, ordinary markup relations,
resolved Binding/Command edges, and structured resolution annotations.
Incomplete project/resource input prevents a missing member/resource from
becoming a definitive finding.

### Protobuf pipeline

The protobuf indexer never parses `.proto` with an ad-hoc grammar. It:

1. validates source size, UTF-8, lexical and physical scope containment;
2. stages the approved import closure in a temporary directory;
3. invokes the pinned Grpc.Tools 2.82.0 `protoc` with descriptor source info;
4. parses the descriptor using Google.Protobuf;
5. projects messages, fields, services, RPCs, ranges, and strict versioned
   contract annotations.

The compiler has a 20-second timeout and bounded input/output/declaration
budgets.

### gRPC projection

`GrpcContractLinker` joins complete protobuf contracts with evidence-backed
Roslyn generated-code shapes. Names alone are insufficient: service container,
descriptor association, request/response types, streaming signature, and a
unique RPC must agree.

It atomically publishes:

- managed client → RPC as `grpc-calls`;
- server handler → RPC as `implements-rpc` for audit queries;
- RPC → server handler as `rpc-dispatches-to` for execution traversal.

During refresh, runtime state is `Partial`. A failed first run does not claim
retention; `RetainedLastGood` is true only if matching producer evidence exists
in storage.

### Native pipeline

`NativeInteropCoordinator` serializes the production flow for one scope:

```text
user trust decision
  → isolated worker extraction
  → dependency closure and two content-bound parses
  → optional target binary/export verification
  → atomic native snapshot
  → managed/native match and risk projection
  → proven orphan cleanup
```

Complete native snapshots publish function and type declarations into the same
symbol graph. An exact binary-verified P/Invoke boundary publishes
`struct-maps-to` only when the managed and native record identities are unique
at the same signature position; duplicate qualified native types remain
unmapped rather than being guessed.

The worker protocol rejects unknown members and limits request/response/stderr,
strings, collections, compiler arguments, and type depth. The client enforces a
bounded process timeout. The snapshot hashes every approved translation unit and
included file before publication and rejects content changes or inconsistent
re-parses.

An incomplete source or export universe leaves the last complete projection in
place and marks runtime state partial. Queries must disclose that retained state.

## Rule engines

### Interop

The rule engine consumes normalized, target-equivalent facts:

| Rule | Meaning |
|---|---|
| `Interop001` | calling-convention mismatch or unknown comparison |
| `Interop002` | managed/native record layout incompatibility |
| `Interop003` | count/direction/type/size/pointer/sign/encoding risk |
| `Interop004` | native retention plus proven unrooted managed callback |
| `Interop005` | proven native exception escape across a C ABI |
| `Interop006` | proven allocation/release family mismatch |

Unknown facts do not become a compatibility success.

### WPF

| Rule | Proof boundary |
|---|---|
| `XAMLBINDING001` | complete semantic context proves a missing member |
| `XAMLCOMMAND001` | complete context proves a missing/non-command member |
| `XAMLRESOURCE001` | complete resource snapshot proves a missing resource |
| `WPFEVENT001` | source static event + direct instance named handler + no exact removal in a complete, error-free compilation |
| `WPFTHREAD001` | direct BCL background inline callback accesses a statically known `DispatcherObject`; a direct Dispatcher marshal suppresses it |

Aliases, indirect callbacks, dynamic DataContexts, and unsupported forms remain
unknown rather than being guessed.

### gRPC

Contract checks report RPCs without evidence-backed implementations, uniquely
proven generated signature mismatches, field-number changes, and streaming-shape
changes. Change rules compare the current complete contract with the insert-only
first-success baseline. An incomplete refresh neither updates the baseline nor
creates speculative findings.

## Complete execution traversal

`trace_call_path(profile="execution")` uses an ordered stage machine to answer
the end-to-end question:

```text
XAML Button
  --binds-path--> ICommand property
  --command-executes--> ViewModel handler
  --calls--> managed service
  --grpc-calls--> protobuf RPC
  --rpc-dispatches-to--> managed server handler
  --calls--> P/Invoke declaration
  --pinvoke-maps-to--> C export
  --calls--> C++ algorithm
```

Every hop must have persisted evidence. The traversal is bounded by query length,
scope fan-out, depth, path count, expanded nodes, evidence per hop, total evidence,
cancellation, and the common output budget.

Only `calls` may repeat, and only inside the current managed-client,
managed-server, or native stage. Cross-domain stages cannot be skipped, reversed,
or repeated. With an exact canonical starting key, `to` may be omitted; terminal
discovery then returns native nodes reached after at least one post-P/Invoke call
that have no auditable outbound `calls` edge. Any traversal truncation makes an
empty terminal result non-authoritative.

At the start and end of traversal it compares:

- SQLite connection changes and external `data_version`;
- scope status;
- managed semantic-input completeness;
- gRPC runtime-state identity and completeness;
- native runtime-state identity, export-universe completeness, failures, retained
  state, and pending stale symbols.

If any generation changes while reading, existing paths may still be returned for
inspection, but `query-snapshot` makes the result partial and absence
non-authoritative.

## Privacy and trust

The mandatory privacy policy excludes:

```text
bin obj .vs Debug Release Images PatientData Database Logs
.git .sourcegraph node_modules
*.dcm *.jpg *.jpeg *.png
```

Configured scope excludes can only narrow this set. `ScopePathPolicy` evaluates
both lexical and resolved physical paths, follows existing symlink/junction/
reparse components, and fails closed when identity or containment cannot be
established.

The same policy is used by cold discovery, the Roslyn sanitizer, language
dispatch, XAML/protobuf secondary inputs, live watching, native include closure,
history, and deletion. Schema v12 deliberately rebuilt older indexes so content
captured before the medical policy could not survive an upgrade.

Native parsing additionally requires a user-owned trust document outside the
repository and executes in a worker. This does **not** make all project input
safe: MSBuild evaluation occurs before Roslyn can sanitize the solution, and
plugins loaded in an `AssemblyLoadContext` still execute with host permissions.
Only trusted solutions/plugins should be used until those surfaces also move to
an OS-restricted worker.

## MCP and query boundaries

`ScopeRouter` directs a tool to one scope or a bounded fan-out. Each scope owns
its store, indexer, readiness state, and gRPC/native runtime state.

Domain tools are structured, read-only, and idempotent. They disclose partial,
truncated, omitted, failure, and evidence counts. `query_graph` uses a read-only
SQLite connection and curated views. No query tool writes source or graph state.

Tool metrics keep counts, latency, error state, and response size. The persistent
`usage.jsonl` record intentionally excludes request values. OpenTelemetry
`ActivitySource`/`Meter` signals are local integration points.

Embedding generation is optional. Model download requires explicit permission
and is not needed for structural or cross-language queries.

## Validation

Tests are layered:

1. normalized facts, codecs, canonical keys, paths, trust, and rules;
2. storage transactions, producer isolation, deletion, baselines, read versions,
   and corruption recovery;
3. Roslyn/XAML/protoc/Clang/worker/PE/linker component and incremental tests;
4. the `MedInteropChain` contract, which runs the production graph pipeline in
   one store and proves the contiguous eight-hop path plus the separate
   `implements-rpc` audit edge.

The portable end-to-end contract injects native extraction/export verification
for deterministic execution. A separate fixture feeds the six negative cases
through real Roslyn and libclang facts and proves `Interop001`–`Interop006`.
Dedicated tests cover worker protocol, PE parsing, real WPF binding resolution,
and both WPF risk rules on a complete WindowsDesktop compilation. Release
validation additionally performs locked restore, Release build/test, structured
transitive vulnerability scanning, exact seven-package identity/version checks,
exact license/README/repository metadata checks, Portable PDB and Source Link
validation for six symbol packages, package installation, `--help`, bundled
`protoc`, a real native worker/libclang parse, and a stdio `tools/list` smoke.

## Versioning boundaries

The SDK package and documented MCP argument/result shapes are public contracts.
Canonical-key and relation identifiers are persistent data contracts. Additive
SDK changes require a minor version; breaking SDK or wire changes require the
normal major/deprecation policy.

The SQLite schema is a rebuildable implementation detail. Schema changes bump
`Schema.Version`; older databases are dropped and rebuilt rather than migrated
as authoritative user data. The gRPC baseline is the exception in semantics:
within a current v14 database it is intentionally historical and insert-only.

## Known architectural limits

- Static analysis does not prove arbitrary reflection, runtime DI, dynamic
  proxies, delegate aliases, function pointers, or every virtual dispatch.
- The execution profile follows control/call relations; arbitrary value/taint
  propagation from a parameter into hardware I/O side effects is not modeled.
- WPF rules deliberately favor narrow proofs over recall.
- ABI results are valid only for the configured OS, architecture, toolchain,
  compiler arguments, pack, and verified artifact.
- A native worker is a process/resource boundary, not a complete OS sandbox.
- MSBuild and plugin execution remain trusted-input surfaces.
- Local gRPC baselines do not replace release-version API governance.
- MedInteropLens is developer tooling, not medical-device or clinical
  certification.

## Where to look

| Concern | Primary code |
|---|---|
| Canonical keys and edge evidence | `Sdk/CanonicalKeys*`, `Sdk/*CanonicalKeys.cs`, `Sdk/EdgeEvidence.cs` |
| Privacy and physical paths | `Core/PrivacyPathPolicy.cs`, `Core/ScopePathPolicy.cs` |
| Roslyn and WPF diagnostics | `Indexing/RoslynIndexer.cs`, `Indexing/Wpf/` |
| XAML semantics/resources | `Indexing.Xaml/XamlSemanticResolver.cs`, `XamlLanguageIndexer.cs` |
| protobuf descriptors | `Indexing/Protobuf/` |
| Clang extraction | `Indexing.Clang/ClangNativeExtractor.cs` |
| Interop and ABI rules | `Interop/` |
| Atomic graph storage | `Storage/IGraphStore.cs`, `Storage/SqliteGraphStore*.cs` |
| gRPC projection/query | `Server/Grpc/` |
| native worker/snapshot | `Server/Interop/` |
| complete path query | `Server/Tools/TraceCallPathTools.cs` |
| live indexing | `Server/LiveIndexService.cs`, `Watcher/` |
| MCP domain tools | `Server/Tools/{Interop,Grpc,Wpf,TraceCallPath}Tools.cs` |
