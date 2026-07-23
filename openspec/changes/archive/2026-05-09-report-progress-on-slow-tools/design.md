## Context

The `notifications/progress` flow in MCP is a one-direction stream from server to client during the lifetime of a single tool call:

```
client                           server
  │                                │
  │ ─── tools/call (id=1,           │
  │      progressToken="abc")  ──→ │
  │                                │ … work begins …
  │ ←── notifications/progress     │
  │      { progressToken="abc",    │
  │        progress: 0.3 }         │
  │                                │ … more work …
  │ ←── notifications/progress     │
  │      { progressToken="abc",    │
  │        progress: 0.7 }         │
  │                                │ … work done …
  │ ←── tools/call response (id=1) │
  │                                │
```

The agent at the other end (Claude, the user) sees the progress messages render as a status indicator under the tool-call panel. When `tools/call` returns the final result, the indicator clears.

Two SDK-level facts shape the design:

1. **Auto-injection** — adding `IProgress<ProgressNotificationValue>` as a tool method parameter makes the SDK inject a forwarder that bridges `progress.Report(...)` calls into wire-level `notifications/progress` messages tagged with the request's `progressToken`. No additional registration plumbing.
2. **Silent no-op when no `progressToken`** — when the client didn't request progress, the injected reporter swallows `Report(...)` calls. Tools call `Report` unconditionally; the cost is negligible if no client is listening.

The mechanism is already in the protocol; we're not adding a feature, we're opting in.

## Goals / Non-Goals

**Goals:**

- `semantic_search`, `impact_of_change`, and `module_summary` accept `IProgress<ProgressNotificationValue>` and emit progress at coarse checkpoints. These three are the only tools today with multi-second tails worth narrating.
- A small `Format.Progress(progress, message)` helper centralises the `ProgressNotificationValue` construction so every checkpoint shares the same shape (and so `Total = 1.0` is set consistently).
- Tests that verify progress emission shape, monotonicity, and the no-op path.
- Documentation for clients on how to opt in (send `progressToken` in `tools/call` request).

**Non-Goals:**

- Adding progress reporting to fast tools (`find_definition`, `search_symbols`, etc.). Their median latency is sub-100ms; progress is just noise. Future tools opt in only if they prove slow under representative load.
- Indexing-time progress for `LiveIndexService`. That happens at startup before any client has connected; there's no `progressToken` to attach to and the protocol's progress mechanism is request-scoped. A separate hosted-service progress mechanism (server logs, custom MCP resource at `graph://indexing-status`, or polling-based) would be a different change.
- Streaming partial results inside a single tool call. The `notifications/progress` mechanism communicates *progress* (0..1 + a message), not partial *results*. Result streaming would require a different protocol surface (server-sent events on top of MCP transport, or repeated `notifications/message` items the client appends).

## Decisions

### Decision 1 — Three tools, no more, in this change

Scope is `semantic_search`, `impact_of_change`, `module_summary`. Other tools are explicitly out:

| Tool | Why excluded |
|---|---|
| `find_definition`, `find_references`, `search_symbols`, `find_by_annotation` | Median latency sub-100ms; no real wait to narrate. |
| `list_callers`, `list_callees`, `find_implementations`, `list_members` | Same — fast SQLite + FTS5 lookups. |
| `list_symbols_in_file`, `list_generated_files`, `list_scopes`, `graph_stats`, `usage_stats`, `ping` | Sub-millisecond effectively. |
| `find_diagnostics`, `recent_changes`, `list_tests_for`, `who_authored` | Storage-driven, fast. |
| `neighborhood` | Two SQLite calls; fast. |

Future changes can add the parameter to other tools when actual user-facing latency justifies it. Not opting them in now keeps this change scoped to its rationale.

### Decision 2 — Three checkpoints for `semantic_search`

```csharp
progress.Report(Format.Progress(0.0,  "encoding query"));     // before generator.EmbedAsync
// … encoder runs (cold start: ONNX model load is here)
progress.Report(Format.Progress(0.5,  "searching"));          // before EmbeddingsStore.SearchAsync
// … SQLite vec0 query
progress.Report(Format.Progress(0.9,  "formatting results")); // before the StringBuilder loop
// … return string
```

The 0.0 / 0.5 / 0.9 split reflects observed latency: cold-start encoding is by far the slowest checkpoint (3–5s), search is quick (~50ms), formatting is trivial. The values give a useful UX progress bar even though they're not measured live.

### Decision 3 — One checkpoint for `impact_of_change` and `module_summary`

Both tools are typically <100ms, but pathological inputs can push them into the 1–5s range. Adding the IProgress parameter without per-step instrumentation costs nothing today and gives future contributors a place to add fine-grained checkpoints if they prove valuable.

```csharp
progress.Report(Format.Progress(0.0, "querying"));
// … the recursive CTE / aggregate runs
// (final progress is implicit at the response boundary; no Report(1.0) needed because
//  the SDK terminates the progressToken when the tool result ships)
```

### Decision 4 — `Format.Progress(double, string)` returns `ProgressNotificationValue`

```csharp
public static ProgressNotificationValue Progress(double fraction, string message) =>
    new() { Progress = fraction, Total = 1.0, Message = message };
```

Centralising means every checkpoint sets `Total = 1.0` (so `progress / total` is the fraction), and every `Message` is a short imperative ("encoding query", "searching") not a status sentence. Clients that render the message verbatim get a consistent voice.

### Decision 5 — Parameter position: before `CancellationToken`

The SDK auto-injects both `IProgress<...>` and `CancellationToken`. Convention in C# (and in the rest of this codebase) puts `CancellationToken` last. Progress goes immediately before:

```csharp
public static Task<string> SemanticSearchAsync(
    ScopeRouter router,
    ICodeEmbeddingGenerator generator,
    [Description(...)] string query,
    [Description(...)] int k = 20,
    [Description(...)] string? kind = null,
    [Description(ScopeDescription)] string? scope = null,
    IProgress<ProgressNotificationValue>? progress = null,    // ← added here
    CancellationToken ct = default)
    => …
```

The parameter is declared nullable with a default of `null` — but the SDK injects a non-null no-op forwarder when no `progressToken` is set. Tools call `progress?.Report(...)` defensively; the `?` is redundant in practice (the SDK always supplies a non-null instance) but cheap insurance against future SDK behaviour changes.

### Decision 6 — Test pattern: capture-and-assert via fake `IProgress`

```csharp
var captured = new List<ProgressNotificationValue>();
var fake = new Progress<ProgressNotificationValue>(captured.Add);
// invoke tool body directly (or via ToolMetrics.TrackAsync) with `fake` as the progress
// assert: captured.Count == 3, Progress values monotonically increase, last Message == "formatting results"
```

The MCP SDK's `Progress` type isn't directly testable here because we don't have a wire connection — but the *contract* the tool body honours (calling `Report` at the right places with the right values) is independent of the wire side. Tests assert on the captured stream.

For the no-op path: pass a captured list of size 0 with `IProgress<...>` whose `Report` is a no-op; confirm the tool runs to completion identically. (Practically: omit the parameter and let the default kick in, since C# optional-parameter resolution defaults to `null`.)

## Risks / Trade-offs

- **[Risk] Progress messages with PII or sensitive paths.** The `Message` string is rendered to the user. If a future contributor includes a query string or file path in the message verbatim, a malicious query could echo to the chat. → Mitigation: keep messages to short imperatives ("encoding query", "searching") with no user-controlled substrings. Document this in the helper's XML docs.

- **[Risk] Progress flooding** — if a future contributor sticks `progress.Report(...)` inside a tight loop, every iteration becomes a wire message. → Mitigation: doc in `Format.Progress` says "emit at coarse checkpoints, not inside loops." Tests can also assert `captured.Count <= some bound` per tool.

- **[Risk] SDK injection surprise** — if a future SDK upgrade changes the auto-injection rules (e.g. requires `IProgress<ProgressNotificationValue>` to be `notnull`), our `?`-decorated parameter and `progress?.Report` calls would still work, but a `default = null` parameter might be rejected. → Mitigation: pin the SDK version we use; the spike confirmed 1.2.0 supports the pattern. Future SDK upgrades go through the usual change-test-PR loop.

- **[Trade-off] Three tools instrumented out of ~24 — feels uneven.** → Accepted. The mechanism is established; future opt-ins are a one-line parameter add per tool. The asymmetry reflects honest differences in tool latency, not arbitrary scoping.

- **[Trade-off] No automated way to prove the wire actually receives the notifications.** Our test asserts on the in-process `IProgress` interface. The SDK's wire-side translation (build a `notifications/progress` JSON-RPC message tagged with the request's `progressToken`) is the SDK's responsibility, not ours. → Accepted. We trust the SDK's behaviour as documented in the spike.

## Migration Plan

The change has no risky migration; it's purely additive:

1. Land `Format.Progress(...)` helper alongside the other `Format.*` helpers. No callers yet. CI green.
2. Add the `IProgress<ProgressNotificationValue>` parameter to `SemanticSearchAsync`, populate the three checkpoints, add the `ProgressReportingTests` for it. Land + verify.
3. Add the parameter to `ImpactOfChangeAsync` and `ModuleSummaryAsync`, populate the single "querying" checkpoint each, add scenario tests.
4. Update README + CLAUDE.md with the client-side opt-in note.
5. Run `openspec validate` + archive.

**Rollback strategy**: revert the per-tool commits independently. The helper has no other callers and can be removed with the last reverted tool. No infrastructure to dismantle.

## Open Questions

- **Should we also annotate progress messages with `audience: ["assistant"]`?** Progress notifications are per-call wire messages, not content blocks; the `audience` annotation is only on `ContentBlock`. Doesn't apply to progress.
- **Should `semantic_search` emit progress only on first-call (cold start) and skip on warm calls?** No mechanism to detect "cold call" cleanly from inside the tool; the encoder's `IsAvailable` flag goes true on first instantiation regardless. Emit unconditionally — clients that requested progress see all three checkpoints, which is fine even on a sub-second warm call.
- **Should `Total` be `null` when we don't know the absolute total** (e.g. for incremental indexing where the total file count varies)? Per spec, `Total` is optional. Setting `Total = 1.0` when our `Progress` is in `[0..1]` makes the fraction explicit. Set to `null` only if a future tool genuinely doesn't know its bounds.
