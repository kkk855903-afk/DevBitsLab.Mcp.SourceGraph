# MedInteropChain fixture

Indexing-only, buildable fixture for MedInteropLens. It freezes the positive chain

`Button → Command → ViewModel → service → gRPC client → proto RPC → server override → P/Invoke → C export → C++ algorithm`

and keeps one isolated source pair for each `Interop001`–`Interop006` negative case.
The C# projects intentionally use small generated-API-shaped gRPC stubs so the default test
build does not require a network restore or a platform-specific WPF workload. The `.proto`,
C header, and C++ translation units remain the authoritative cross-language inputs.

`Expected/graph-contract.json` describes the contiguous eight-hop execution path separately from
the reverse `implements-rpc` audit edge. The contract test runs Roslyn, the XAML and protobuf
indexers, gRPC linking, native snapshot publication, interop analysis, and the execution-profile
trace against one store before accepting that golden file.

`NativeLibrary/medalgo.fixture` is inert deterministic content used only as the content-bound
binary input for that test's injected export-table verifier. It is not executable and is never
treated as a real PE by production code; the coordinator's snapshot, publication, matching, and
analysis stages remain the production implementations.

`Expected/interop-findings.json` describes the six intended findings; analyzers must attach their
own exact evidence locations rather than copying locations from the golden file.
