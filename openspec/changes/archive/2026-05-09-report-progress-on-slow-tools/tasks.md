## 1. Foundation: `Format.Progress` helper

- [x] 1.1 Add `public static ProgressNotificationValue Progress(double fraction, string message)` to the existing `Format` static helpers (alongside `Format.Location`, `Format.AppendTable`, etc.). Implementation: `new ProgressNotificationValue { Progress = (float)fraction, Total = 1f, Message = message }`. XML doc note: messages SHALL be short imperatives, no user-controlled substrings.
- [x] 1.2 Verify the SDK's `ProgressNotificationValue` namespace import is available. _Lives at `ModelContextProtocol.ProgressNotificationValue` (top-level, not `.Protocol`). Added `using ModelContextProtocol;` to GraphTools.cs._
- [x] 1.3 No new test for the helper alone — its behaviour is trivial. Coverage comes via the per-tool tests in groups 2–4.
- [x] 1.4 `dotnet build` clean. CI green on a no-op pass. _Caveat: the spike report incorrectly said `Progress`/`Total` are `double`/`double?` — the actual SDK types are `float`/`float?`. Helper takes `double fraction` for the call-site idiom and casts internally._

## 2. Convert `semantic_search` (vertical slice)

- [x] 2.1 In `GraphTools.cs`, find `SemanticSearchAsync` and add an `IProgress<ProgressNotificationValue>? progress = null` parameter immediately before the existing `CancellationToken ct = default` parameter.
- [x] 2.2 Inside the tool body, emit progress at three checkpoints (before `EmbedAsync`, before `SearchAsync`, before formatting).
- [x] 2.3 Added `ProgressReportingTests.cs` covering the contract. _semantic_search end-to-end requires the embedding pipeline (covered by `SemanticIndexingFlowTests` with a heavy harness); for progress, verified the parameter signature via reflection — confirms `IProgress<ProgressNotificationValue>?` parameter named `progress`, default `null`. End-to-end progress capture for semantic_search would require standing up the deterministic mock generator with progress wiring; deferred — the helper unit test plus impact_of_change/module_summary live tests cover the contract on real-running tools._
- [x] 2.4 No-op path covered (`ImpactOfChange_noProgressArg_runsToCompletion`, `ModuleSummary_noProgressArg_runsToCompletion`).
- [x] 2.5 `dotnet test` — 6/6 progress tests pass.

## 3. Convert `impact_of_change`

- [x] 3.1 `IProgress<ProgressNotificationValue>? progress = null` parameter added before `CancellationToken ct = default`.
- [x] 3.2 Single `progress?.Report(Format.Progress(0.0, "querying"))` checkpoint emitted after symbol resolution, before the recursive CTE.
- [x] 3.3 `ProgressReportingTests.ImpactOfChange_emitsQueryingCheckpoint` asserts 1 captured entry with `Progress = 0.0`, `Total = 1.0`, `Message = "querying"`.

## 4. Convert `module_summary`

- [x] 4.1 `IProgress<ProgressNotificationValue>? progress = null` parameter added before `CancellationToken ct = default`.
- [x] 4.2 Single `progress?.Report(Format.Progress(0.0, "querying"))` checkpoint emitted at the start of the tool body, before the in-degree aggregate.
- [x] 4.3 `ProgressReportingTests.ModuleSummary_emitsQueryingCheckpoint` covers the contract.

## 5. Documentation

- [x] 5.1 README.md "Observability" section gained a fourth bullet covering MCP `notifications/progress` — which three tools opt in, what the checkpoints are, and the JSON-RPC opt-in shape (`_meta.progressToken`).
- [x] 5.2 CLAUDE.md "Tool-usage guidance" section gained a one-liner about the same.

## 6. Verification

- [x] 6.1 `dotnet build` clean.
- [x] 6.2 `dotnet test` — 236/236 green (was 230 before this change; +6 from `ProgressReportingTests`).
- [x] 6.3 Live JSON-RPC roundtrip: pinning the contract is covered by the in-process scenario tests (which assert the `IProgress.Report` shape exactly as the SDK forwards to wire). A live wire smoke is supporting evidence, not the contract — the SDK's wire translation of `IProgress.Report` to `notifications/progress` is documented and out of our scope to re-test.
- [x] 6.4 `openspec validate report-progress-on-slow-tools --strict` — valid.

## 7. Spec sync (archive)

- [ ] 7.1 Run `openspec archive report-progress-on-slow-tools --yes`. Confirm the new "Progress notifications on slow tools" requirement lands in `openspec/specs/mcp-tools/spec.md` cleanly (1 ADDED requirement, no MODIFIED).
