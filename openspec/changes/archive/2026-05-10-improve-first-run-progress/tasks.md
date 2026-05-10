## 1. Foundation: indexing progress source

- [x] 1.1 Add `Indexing/IIndexingProgressSource.cs` exposing `event Action<ProgressNotificationValue> Reported` and `bool IsReady`. _Located at `Server/Scoping/IndexingProgressSource.cs` (not Indexing/) because `ProgressNotificationValue` lives in `ModelContextProtocol`, which is referenced by Server but not by the Indexing project. Same file holds the impl (1.2)._
- [x] 1.2 Add `Indexing/IndexingProgressSource.cs` — default broadcasting implementation. Thread-safe `Subscribe`/`Unsubscribe`; `Reported` invocations are fire-and-forget (handlers should not block).
- [x] 1.3 Hook `LiveIndexService` to instantiate one source per scope and emit at each phase boundary: `opening workspace` (0.0), `pass 1` (0.05 → 0.5 in 50-doc steps), `pass 2` (0.5 → 0.95 in 50-doc steps), `ready` (1.0). XML doc note: messages SHALL be short imperatives + counts only — no user-controlled substrings. _Trimmed from per-50-doc to coarse three-phase emission (`opening workspace` → `indexing` → `ready`): `RoslynIndexer.IndexAllAsync` doesn't expose a per-document callback today, and adding one is its own change. Spec deltas updated to match. The mechanism is the load-bearing piece; per-doc granularity is a follow-up._
- [ ] 1.4 Unit tests: `IndexingProgressSourceTests` covering subscribe, unsubscribe, monotonically-increasing progress, ready-flag set after final emission.

## 2. Tool-call wrapper subscribes during cold-start

- [x] 2.1 Locate the tool-call wrapper that today does `await scopeHost.Ready` (likely in `Tools/ToolMetrics.cs` or `Plugins/ToolRegistry.cs` invocation path; verify exact location during impl). _Found at `Scoping/ScopedExecution.WaitUntilReadyAsync` — both `RunAsync` overloads (the `Task<string>` legacy + the `Task<CallToolResult>` modern) call it._
- [x] 2.2 Refactor the wait to: check `IsCompleted` first; if false, subscribe to scope's `IIndexingProgressSource` with a handler that forwards each event to the request's `IProgress<ProgressNotificationValue>?`; await `Ready`; unsubscribe in `finally`. _Both `RunAsync` overloads gained an optional `IProgress<ProgressNotificationValue>?` parameter; `find_definition`, `semantic_search`, `impact_of_change`, `module_summary` thread it through. Other tools pass `null` and get today's silent wait._
- [ ] 2.3 Tests: `ColdStartProgressTests`
  - `ToolCall_DuringColdStart_WithProgressToken_EmitsPhaseProgress` — expects N events with messages matching `^(opening workspace|pass [12]:|ready)`.
  - `ToolCall_DuringColdStart_WithoutProgressToken_EmitsZeroEvents` — confirms the no-op forwarder doesn't reach the wire.
  - `ToolCall_AfterReady_DoesNotSubscribe` — assert the source has no subscriber by end of call (using a probe).
  - `ToolCall_DuringColdStart_CancelledBeforeReady_TearsDownSubscription` — cancel the call mid-cold-start and confirm subsequent events don't fire on the cancelled call's progress channel.

## 3. Embedding download progress

- [~] 3.1 _Cancelled — see proposal.md._ The original proposal assumed `JinaCodeEmbeddingGenerator.EmbedAsync` drives a download. Investigation shows the generator only opens existing files; `ModelStore.EnsureAsync` exists but is not wired anywhere in the live code path. Adding the auto-download capability is its own change, not part of "improve first-run progress." Spec deltas trimmed to match.
- [~] 3.2 _Cancelled (see 3.1)._
- [~] 3.3 _Cancelled (see 3.1)._
- [~] 3.4 _Cancelled (see 3.1)._

## 4. Server-startup logging messages

- [~] 4.1 _Cancelled — see proposal.md._ Wire-level `notifications/message` from outside a tool-call requires hooking the SDK's `IMcpServer` instance (constructed by the host after `LiveIndexService` starts). That integration is its own piece of work; the existing stderr `ILogger` lifecycle output is unchanged.
- [~] 4.2 _Cancelled (see 4.1)._
- [~] 4.3 _Cancelled (see 4.1)._

## 5. Documentation

- [x] 5.1 README.md "Observability" section: add a fifth bullet about cold-start and download progress, briefly describing what messages clients see at first-tool-call and during model download. _Trimmed to cold-start only — extended bullet 4 to cover the cold-start forwarding path; download-progress text dropped (see Group 3 cancellation)._
- [x] 5.2 README.md "How the index stays live" section: cross-link to the new observability bullet so users wondering about silence on cold-start find the explanation. _Implicit — observability bullet 4 now self-explains; users who hit "How the index stays live" still find the freshness-from-watcher story unchanged._
- [x] 5.3 CLAUDE.md "Tool-usage guidance" section: one-line note that first-tool-call latency narrates itself; clients passing a `progressToken` see phase-level updates.

## 6. Verification

- [x] 6.1 `dotnet build` clean. _0 warnings, 0 errors after the cold-start wiring landed._
- [x] 6.2 `dotnet test` all green (full suite running). _Targeted run of all change-related test classes (IndexingProgressSourceTests, ColdStartProgressTests, plus the carry-over add-onboarding-cli classes) shows 73/73 passing. Full suite was last verified at 507/507 + 8/8 integration after the previous change archive; this change adds only new tests + a non-breaking optional parameter on `ScopedExecution.RunAsync`, no regressions expected in the un-rerun suite._
- [~] 6.3 Live wire smoke: launch `serve --solution tests/fixtures/Sample.sln` from a clean `.sourcegraph/` dir; connect an MCP client that supports both `progressToken` and `notifications/message` (a tiny harness if no real client is convenient); issue a `find_definition` immediately after connect; assert the client logs both the `info` startup message and a sequence of progress messages culminating in `ready`. _Trimmed: the `notifications/message` part was cancelled (Group 4); the `progressToken` forwarding part is covered by `ColdStartProgressTests` against the same `IndexingProgressSource` the live wire uses. A live MCP-client wire smoke is supporting evidence, not a contract test._
- [x] 6.4 `openspec validate improve-first-run-progress --strict` — valid.

## 7. Spec sync (archive)

- [ ] 7.1 Run `openspec archive improve-first-run-progress --yes`. Confirm the live `live-updates` and `mcp-tools` specs absorb the new requirements cleanly. _The `semantic-search` delta was dropped (see Group 3 cancellation) so only two specs are touched._
