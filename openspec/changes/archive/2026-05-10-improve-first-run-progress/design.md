## Context

The MCP `notifications/progress` flow is request-scoped: the server emits messages tagged with a `progressToken` that the client supplied on the originating `tools/call`. Without an originating request there is no token, and any `notifications/progress` message the server emits has no anchor on the client side and is dropped.

This is exactly why the design doc for `report-progress-on-slow-tools` listed indexing-time progress as out of scope:

> **Non-Goals: Indexing-time progress for `LiveIndexService`. That happens at startup before any client has connected; there's no `progressToken` to attach to and the protocol's progress mechanism is request-scoped.**

The trick is that "before any client has connected" and "before any client has issued a `tools/call`" are different windows. By the time a client issues its first `tools/call`, it *has* connected and it *can* supply a `progressToken`. The cold-start delay isn't experienced during the connection handshake — it's experienced inside the *first tool call* that blocks on `ScopeHost.Ready`. Which means the progressToken from that first tool call is exactly what we need to attach progress to.

For the part of the cold-start that does happen before any tool call (the workspace open + first scan), there is no `progressToken` to use — but `notifications/message` (logging) is server-initiated and requires no token. Clients that surface logs render them; clients that don't, drop them silently. That's the right tool for "the server is doing work, fyi" before any tool has been called.

Two complementary signals, one mechanism each:

```
SERVER START          FIRST tools/call           AFTER ready
─────────────         ─────────────────          ──────────────
notifications/        await ScopeHost.Ready       result returns
message (info):           ↓ (subscribes to        normally
"indexing scope X,        IndexingProgressSource)
M docs"               notifications/progress
                      tagged with progressToken:
                      "pass 1: 240/1247"
                      "pass 1: 1247/1247"
                      "pass 2: 410/1247"
                      "ready"
                          ↓
                      tool runs
```

For the embedding model download, the structure is identical at the per-tool-call layer: the request's `progressToken` is already in scope (the existing `report-progress-on-slow-tools` infrastructure routes it to `IProgress<ProgressNotificationValue>`). The question is just whether the embedding generator accepts the progress channel and emits download bytes through it. Today it doesn't.

## Goals / Non-Goals

**Goals:**

- A user issuing their first `tools/call` against a freshly-started server with cold-state scopes sees incremental progress messages — phase + count — instead of a silent spinner.
- A user whose first `semantic_search` triggers a fresh model download sees byte-level download progress, then the existing `encoding query` / `searching` / `formatting results` checkpoints, in that order.
- Clients that surface `notifications/message` (logging) get a "now indexing" / "now ready" pair at server start.
- Zero behavior change for clients that don't supply a `progressToken`.

**Non-Goals:**

- A custom MCP resource (`graph://indexing-status`) that any client could poll. Considered and rejected for v1: it adds a resource template the server has to maintain, fragments the progress story across two mechanisms, and provides redundant visibility relative to the request-scoped progress + logging-message combo.
- Per-document-byte progress. The cold-start progress is per-document, not per-byte. Bytes-level progress is reserved for the download path where total-bytes is known up front.
- Replacing the existing three `semantic_search` checkpoints. Those stay. Download checkpoints, when present, prepend.
- Backporting progress to other long operations like `clear`. `clear` is a CLI-only operation; users running it interactively see immediate output already.

## Decisions

### Decision 1 — Per-scope `IndexingProgressSource`, owned by `LiveIndexService`

```csharp
public interface IIndexingProgressSource
{
    event Action<ProgressNotificationValue> Reported;
    bool IsReady { get; }
}
```

`LiveIndexService` instantiates one per scope, registers itself as a subscriber-friendly broadcaster, and emits a `Reported` event at each phase checkpoint. The tool-call wrapper subscribes when it begins an `await ScopeHost.Ready`, forwards events to its own `IProgress<ProgressNotificationValue>` (which the SDK auto-injects per the existing infrastructure), and unsubscribes when ready or cancelled.

The source is per-scope, not global, because in a multi-scope repo the first tool call against `frontend` should see frontend's progress, not backend's. `ScopeHost.Ready` is already per-scope; we're piggybacking on the same boundary.

### Decision 2 — Coarse phase taxonomy

Indexing checkpoints emit at fixed phase boundaries plus opportunistic count updates inside long phases:

| Phase | When emitted | Approximate progress fraction |
|---|---|---|
| `opening workspace` | Before `MSBuildWorkspace.OpenAsync` | 0.0 |
| `pass 1: scanning {N}/{M} files` | Every ~50 docs in pass 1 | 0.05 → 0.5 (linear in N/M) |
| `pass 2: resolving {N}/{M} files` | Every ~50 docs in pass 2 | 0.5 → 0.95 (linear in N/M) |
| `ready` | After `IndexAllAsync` completes | 1.0 |

The 50-doc batching keeps emissions sparse on small solutions (≤ a couple of progress messages) and bounded on large ones (~50 progress messages on a 2500-doc solution). Acceptable.

### Decision 3 — Tool-call wrapper subscribes only when blocking

The wrapper's pseudocode today is roughly:

```csharp
async Task<CallToolResult> Wrap(invocation):
    await scopeHost.Ready;     // blocks on first call after start
    return await invocation.RunAsync();
```

After this change:

```csharp
async Task<CallToolResult> Wrap(invocation):
    if !scopeHost.Ready.IsCompleted:
        // first-call cold-start path
        var progress = invocation.Progress;        // injected by SDK; null-noop if no token
        var unsubscribe = scopeHost.ProgressSource.Subscribe(p => progress.Report(p));
        try:
            await scopeHost.Ready;
        finally:
            unsubscribe();
    return await invocation.RunAsync();
```

When `scopeHost.Ready` is already completed (warm state), we skip the subscription entirely — zero overhead on every non-first call.

### Decision 4 — Server-startup `notifications/message` is the *only* token-less signal

Two `notifications/message`s emit at well-defined moments, both at level `info`:

1. After the host wires up the MCP transport but before pumping the first request: `"sourcegraph-mcp: indexing N scope(s), <total estimated documents> docs total"`.
2. When every scope's `Ready` has completed: `"sourcegraph-mcp: ready"`.

These don't replace per-tool-call progress; they complement it for clients that surface logs prominently. We deliberately don't emit per-phase logging messages — that would flood log panels in real-world solutions.

### Decision 5 — Embedding download progress shares the existing `IProgress` channel

The embedding generator (`JinaCodeEmbeddingGenerator`) currently doesn't take a progress parameter. This change adds:

```csharp
Task<float[]> EmbedAsync(
    string text,
    IProgress<ProgressNotificationValue>? progress = null,
    CancellationToken ct = default);
```

Inside `EmbedAsync`, on a cold start (first call, model not yet loaded), the model-fetch path uses an `HttpClient` with a stream that wraps the response body in a per-chunk progress callback. Each callback emits:

```csharp
progress?.Report(new ProgressNotificationValue {
    Progress = (float)bytesRead / totalBytes,
    Total = 1.0f,
    Message = $"downloading model: {percent}% ({mbRead}MB/{mbTotal}MB)"
});
```

Bytes-read updates fire every ~1 MB (HTTP stream buffer size); on a 480 MB model that's ~480 messages, dropping to ~0 after the first call when the cache is warm. The existing `0.0 / 0.5 / 0.9` checkpoints in `SemanticSearchAsync` shift in scope: when a download happens, "encoding query" lands at progress 0.95 instead of 0.0, "searching" at 0.97, "formatting" at 0.99. When no download happens (warm path), the existing 0.0/0.5/0.9 emit unchanged.

`SemanticSearchAsync` decides which fraction-mapping to use based on whether the generator reports it had to download (`generator.IsAvailable` was false on entry).

### Decision 6 — Progress source emits `Total = 1.0` and a fraction in `[0, 1]`

Matches the existing `Format.Progress` helper and the `mcp-tools` requirement that progress notifications carry monotonically increasing fractions in `[0, 1]`.

The phase boundaries (0.05 / 0.5 / 0.95 / 1.0) leave space for fine-grained emissions inside each phase to remain in their assigned band — pass-1 emissions linearly interpolate between 0.05 and 0.5; pass-2 between 0.5 and 0.95.

### Decision 7 — `logging/message` levels: only `info` and `error`

Two levels suffice. `info` for the start/ready pair. `error` if `IndexAllAsync` throws (matches the existing "Initial indexing failed; live updates will not run" error log; this proposes also surfacing it on the wire as `notifications/message` so clients without server-log access see it).

We don't emit `debug` or `notice` — clients vary in how they render levels, and adding more levels just adds rendering surface to test.

### Decision 8 — Cancellation: subscriber unsubscribes on cancel

If the client cancels the originating `tools/call` (sends `notifications/cancelled`), the SDK invokes the `CancellationToken` we propagate. The wrapper's `finally` unsubscribes from the progress source so the cancelled call doesn't continue to receive (and silently drop) progress events. The progress source itself doesn't know about cancellation — it just emits; subscribers come and go.

## Risks / Trade-offs

- **[Risk] Progress message flooding under fast indexing.** A 100-doc solution finishes pass 1 in <1s; emitting one progress message per 50 docs gives 2 messages, fine. A 50,000-doc solution finishes pass 1 in 30s; that's 1000 progress messages on the wire. → Mitigation: batch size of 50 documents is honest and bounded; clients render progress as a "last-message-wins" indicator, so volume isn't a UX problem. If telemetry shows the wire cost is non-trivial, raise the batch size to 200.

- **[Risk] Embedding download progress reveals download URL or bytes-served pattern in messages.** → Mitigation: messages contain only `percentage% (NN MB/MM MB)` — no URL, no path, no user-controlled substring. Same hygiene as the existing checkpoints.

- **[Risk] `logging/message` is rendered prominently by some clients (Continue) and quietly by others (Claude Code).** → Accepted: server's job is to emit; client's job to render. No way to tune this server-side. Two messages per server start is sparse enough to not be noisy in any reasonable client.

- **[Risk] First-call cold-start subscription adds a per-call cost on the warm path.** → Mitigation: the wrapper checks `scopeHost.Ready.IsCompleted` *before* subscribing. Once `Ready` is done, the subscribe / await / unsubscribe path is skipped entirely — zero extra cost on warm calls (which is every call after the first per scope per process).

- **[Risk] The `notifications/message` emission at server start happens before any client has issued the initialize handshake — message may be emitted into a void.** → Mitigation: the `notifications/message` calls live in a `Task.Run(async () => { await waitForFirstClientHandshake(); emitInfo("indexing..."); })` pattern, so the message is emitted *after* a client handshake completes. The MCP host gives us a clean hook for this (the `OnConnect` event); if not, deferring to the first `request` interceptor is the fallback.

- **[Trade-off] The 50-doc batch is hard-coded.** → Accepted for v1. Make it tunable via env var (`SOURCEGRAPH_INDEX_PROGRESS_BATCH=100`) only if real users complain.

- **[Trade-off] Embedding-download progress doesn't carry an estimated time remaining.** → Accepted. ETA computation requires a moving-average download-rate calculation we don't have a reason to ship in v1; the percentage + raw MB display is enough for "is it making progress?" verification.

## Migration Plan

1. Land `Indexing/IndexingProgressSource.cs` (the `IIndexingProgressSource` contract + a default broadcasting implementation) and per-scope wiring on `LiveIndexService`. No subscribers yet; the source emits into the void. CI green.
2. Wire the tool-call wrapper to subscribe-during-cold-start. Add `ColdStartProgressTests` for "with progressToken → see N events" and "without → see zero." Land + verify.
3. Add `IProgress<ProgressNotificationValue>?` parameter to `JinaCodeEmbeddingGenerator.EmbedAsync`; thread it through `SemanticSearchAsync`'s call. Land + verify the warm path still emits the existing three checkpoints.
4. Add download-progress callback inside `JinaCodeEmbeddingGenerator`'s model-fetch path. Add `EmbeddingDownloadProgressTests` against a fake HTTP handler.
5. Add `notifications/message` emission at startup and on initial-index complete (or initial-index failure).
6. Update README "Observability" + CLAUDE.md.
7. `openspec validate improve-first-run-progress --strict`; archive.

**Rollback strategy.** Each step is independently revertable. The progress source is a pure addition with no behavior change when no subscriber is attached, so it can land first and be reverted last with no cross-cutting impact.

## Open Questions

- **Should the per-doc batch interval be 50 or 100?** 50 gives more granularity on small solutions, 100 gives less wire noise on huge ones. Empirical answer once we have telemetry. Going with 50 for v1.
- **Should startup `notifications/message` carry structured data (`{scope: "default", documents: 1247}`) or just prose?** MCP's `LoggingMessageNotificationParams.data` accepts a JSON object. Starting with prose for simplicity; promotable to structured if a client author asks.
- **What if the client sends `progressToken` on the first call but cancels before `Ready`?** The cancellation token cascades; the subscription is torn down in `finally`; no further progress messages are emitted under that token. Test case included.
- **Embedding generator is in DI as a singleton — is `EmbedAsync` thread-safe with respect to concurrent first-callers?** Today the model-load is gated by a `SemaphoreSlim`. The download-progress callback's `Report` calls have to fire from inside the gate; only one caller's `IProgress` will receive download events for any given download. Subsequent concurrent callers wait on the gate and then proceed past the (now warm) load — no download progress for them. Document this in the helper's XML doc.
