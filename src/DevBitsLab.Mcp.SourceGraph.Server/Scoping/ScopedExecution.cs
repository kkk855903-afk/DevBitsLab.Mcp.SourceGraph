using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Helpers used by every scope-aware MCP tool. Centralises the resolution → fan-out → merge
/// pattern so each tool body stays focused on its own SQL shape.
/// </summary>
public static class ScopedExecution
{
    private const int MaxDiagnosticChars = 2_048;

    /// <summary>
    /// Resolve <paramref name="scope"/> via the router and short-circuit with an error string when
    /// the resolution fails. <paramref name="onResolved"/> is invoked once per matching scope and
    /// must return a per-scope rendering; the returned strings are concatenated into the response,
    /// each prefixed with a "scope: …" header when more than one host matched.
    /// </summary>
    public static async Task<string> RunAsync(
        ScopeRouter router,
        object? scope,
        Func<ScopeHost, Task<string>> onResolved,
        CancellationToken ct,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var resolution = router.Resolve(scope);
        if (resolution.IsError) return BoundDiagnostic(resolution.ErrorMessage!);

        // Skip degraded scopes silently when the user asked for a wildcard or an explicit list of
        // healthy scopes. When the user explicitly asks for a degraded scope, surface the message.
        var hosts = resolution.Hosts;
        if (hosts.Count == 0)
        {
            return "No scopes matched.";
        }

        // Common shortcut for single-host: no header dance, no merging. Implicit-scope
        // annotation removed — it landed on response line 1 and competed with the leaf brand
        // mark for visual real estate in chat clients (italic markdown chrome adjacent to the
        // glyph). Agents that want to know which scope answered can call list_scopes / inspect
        // graph_stats. ScopeResolution.IsImplicit is preserved on the record for future use
        // (e.g. footer placement, telemetry tag).
        if (hosts.Count == 1)
        {
            var host = hosts[0];
            await WaitUntilReadyAsync(host, ct, progress).ConfigureAwait(false);
            if (host.Status == "degraded")
            {
                return BoundDiagnostic(
                    $"scope `{host.Scope.Id}` is degraded: {host.StatusMessage ?? "(no message)"}");
            }
            var body = await onResolved(host).ConfigureAwait(false);
            return body.TrimEnd() + Environment.NewLine + Environment.NewLine
                + IndexFreshnessMetadata.BuildText(new[] { host });
        }

        // Multi-host: render each scope's response in turn, prefixed with the scope id. Tools that
        // want a deeper merge (find_definition, search_symbols) use the typed `MergeAsync` helpers
        // below instead.
        var sb = new StringBuilder();
        var results = await Task.WhenAll(hosts.Select(async h =>
        {
            await WaitUntilReadyAsync(h, ct, progress).ConfigureAwait(false);
            if (h.Status == "degraded")
            {
                return (
                    h.Scope.Id,
                    BoundDiagnostic($"scope is degraded: {h.StatusMessage ?? "(no message)"}"));
            }
            try
            {
                var body = await onResolved(h).ConfigureAwait(false);
                return (h.Scope.Id, body);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (h.Scope.Id, BoundDiagnostic($"scope query failed: {ex.Message}"));
            }
        })).ConfigureAwait(false);

        foreach (var (id, body) in results)
        {
            sb.AppendLine($"### scope: `{id}`");
            sb.AppendLine();
            sb.AppendLine(body.TrimEnd());
            sb.AppendLine();
            var metadataHost = hosts.First(h => h.Scope.Id == id);
            sb.AppendLine(IndexFreshnessMetadata.BuildText(new[] { metadataHost }));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sibling of the legacy string-returning <see cref="RunAsync(ScopeRouter, object?, Func{ScopeHost, Task{string}}, CancellationToken)"/>
    /// for tools that have evolved to <c>Task&lt;CallToolResult&gt;</c>. Resolves the scope and:
    /// <list type="bullet">
    ///   <item>On error / no hosts / degraded-with-explicit-target — returns a
    ///     <see cref="CallToolResult"/> whose first <see cref="TextContentBlock"/> carries the
    ///     diagnostic message (no <see cref="CallToolResult.IsError"/> flag — this matches the
    ///     existing-string overload's "diagnostic-as-prose" convention so behaviour is invariant
    ///     between the two paths).</item>
    ///   <item>Single resolved host — invokes <paramref name="onResolved"/> and returns its result
    ///     verbatim.</item>
    ///   <item>Multi-host fan-out — runs <paramref name="onResolved"/> per host in parallel, then
    ///     concatenates the resulting <see cref="CallToolResult.Content"/> lists with a
    ///     per-scope <c>### scope: `id`</c> header text block, mirroring the legacy overload's
    ///     prose merge. Tools whose output schema defines a multi-scope shape use the sibling
    ///     overload with a structured merge callback.</item>
    /// </list>
    /// </summary>
    public static Task<CallToolResult> RunAsync(
        ScopeRouter router,
        object? scope,
        Func<ScopeHost, Task<CallToolResult>> onResolved,
        CancellationToken ct,
        IProgress<ProgressNotificationValue>? progress = null) =>
        RunTypedAsync(
            router,
            scope,
            (host, _, _) => onResolved(host),
            mergeResults: null,
            ct,
            progress,
            maxHosts: null);

    /// <summary>
    /// Typed multi-scope execution for tools that publish a schema-valid merged
    /// <see cref="CallToolResult.StructuredContent"/> payload. The per-host callback receives its
    /// zero-based position and the total host count so callers can divide a call-wide row/size
    /// budget without resolving the router twice. On a multi-host call,
    /// <paramref name="mergeResults"/> receives every per-scope outcome, including
    /// degraded/query-error diagnostics; it must return a payload valid for the tool's declared
    /// output schema. Single-host results continue to pass through verbatim. When
    /// <paramref name="maxHosts"/> is set, an oversized selection is rejected before any
    /// per-scope callback runs so callers can guarantee a bounded response shape.
    /// </summary>
    public static Task<CallToolResult> RunAsync(
        ScopeRouter router,
        object? scope,
        Func<ScopeHost, int, int, Task<CallToolResult>> onResolved,
        Func<IReadOnlyList<ScopedCallToolResult>, CallToolResult> mergeResults,
        CancellationToken ct,
        IProgress<ProgressNotificationValue>? progress = null,
        int? maxHosts = null) =>
        RunTypedAsync(
            router,
            scope,
            onResolved,
            mergeResults,
            ct,
            progress,
            maxHosts);

    private static async Task<CallToolResult> RunTypedAsync(
        ScopeRouter router,
        object? scope,
        Func<ScopeHost, int, int, Task<CallToolResult>> onResolved,
        Func<IReadOnlyList<ScopedCallToolResult>, CallToolResult>? mergeResults,
        CancellationToken ct,
        IProgress<ProgressNotificationValue>? progress,
        int? maxHosts)
    {
        var resolution = router.Resolve(scope);
        if (resolution.IsError)
        {
            return DiagnosticResult.Error(BoundDiagnostic(resolution.ErrorMessage!));
        }

        var hosts = resolution.Hosts;
        if (hosts.Count == 0) return DiagnosticResult.Error("No scopes matched.");
        if (maxHosts is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxHosts),
                maxHosts,
                "The scope fan-out limit must be positive.");
        }
        if (maxHosts is { } hostLimit && hosts.Count > hostLimit)
        {
            return DiagnosticResult.Error(
                $"Scope selection matched {hosts.Count} scopes, exceeding this tool's "
                + $"maximum fan-out of {hostLimit}; provide a narrower scope list.");
        }

        if (hosts.Count == 1)
        {
            var host = hosts[0];
            await WaitUntilReadyAsync(host, ct, progress).ConfigureAwait(false);
            if (host.Status == "degraded")
            {
                return DiagnosticResult.Error(
                    BoundDiagnostic(
                        $"scope `{host.Scope.Id}` is degraded: {host.StatusMessage ?? "(no message)"}"));
            }
            try
            {
                var result = await onResolved(host, 0, 1).ConfigureAwait(false);
                return IndexFreshnessMetadata.Attach(result, new[] { host });
            }
            catch (Exception ex) when (CorruptionGuard.IsCorruptionError(ex) && CorruptionPolicy.Registry is not null)
            {
                // Reactive verification: run integrity_check, mark degraded if confirmed,
                // optionally fire autonomous rebuild. Original exception still rethrown so the
                // agent sees the failure once; subsequent calls hit the degraded short-circuit.
                await CorruptionGuard.VerifyAndMarkAsync(host, CorruptionPolicy.Registry,
                    ex, CorruptionPolicy.Logger, ct).ConfigureAwait(false);
                throw;
            }
        }

        // Multi-host fan-out: parallel-run, then concatenate Content lists with a per-scope header.
        // A schema-aware caller may additionally merge the per-scope structured payloads below.
        var perHost = await Task.WhenAll(hosts.Select(async (h, index) =>
        {
            await WaitUntilReadyAsync(h, ct, progress).ConfigureAwait(false);
            if (h.Status == "degraded")
            {
                return new ScopedCallToolResult(
                    h.Scope.Id,
                    h.Status,
                    h.StatusMessage,
                    DiagnosticResult.Error(BoundDiagnostic(
                        $"scope is degraded: {h.StatusMessage ?? "(no message)"}")));
            }
            try
            {
                var body = await onResolved(h, index, hosts.Count).ConfigureAwait(false);
                return new ScopedCallToolResult(h.Scope.Id, h.Status, h.StatusMessage, body);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (CorruptionGuard.IsCorruptionError(ex) && CorruptionPolicy.Registry is not null)
                {
                    // Don't bring down the whole multi-host fan-out on one corrupt scope —
                    // verify, mark degraded, and continue with the structured per-scope error.
                    await CorruptionGuard.VerifyAndMarkAsync(h, CorruptionPolicy.Registry,
                        ex, CorruptionPolicy.Logger, ct).ConfigureAwait(false);
                }
                return new ScopedCallToolResult(
                    h.Scope.Id,
                    h.Status,
                    h.StatusMessage,
                    DiagnosticResult.Error(BoundDiagnostic(
                        $"scope query failed: {ex.Message}")));
            }
        })).ConfigureAwait(false);

        var merged = new List<ContentBlock>();
        var anyError = false;
        foreach (var scoped in perHost)
        {
            merged.Add(new TextContentBlock { Text = $"### scope: `{scoped.ScopeId}`" });
            var body = scoped.Result;
            if (body.Content is { Count: > 0 } blocks)
            {
                foreach (var b in blocks) merged.Add(b);
            }
            // OR per-scope IsError flags so a single failed scope marks the merged response as
            // an error — keeps ToolMetrics' ok/err telemetry honest.
            if (body.IsError == true) anyError = true;
        }
        if (mergeResults is not null)
        {
            var schemaAwareResult = mergeResults(perHost);
            if (anyError && schemaAwareResult.IsError != true)
            {
                schemaAwareResult.IsError = true;
            }
            return IndexFreshnessMetadata.Attach(schemaAwareResult, hosts);
        }

        return IndexFreshnessMetadata.Attach(new CallToolResult
        {
            Content = merged,
            IsError = anyError ? true : null,
        }, hosts);
    }

    private static string BoundDiagnostic(string value) =>
        value.Length <= MaxDiagnosticChars
            ? value
            : value[..(MaxDiagnosticChars - 1)] + "…";

    // Diagnostic short-circuits route through DevBitsLab.Mcp.SourceGraph.Server.Tools.Output.DiagnosticResult.Build
    // so the same wire shape ships from every tool body — keeps the leaf chokepoint's branding
    // contract identical regardless of where the short-circuit originates.

    /// <summary>
    /// When a scope is mid-cold-index (registered with <c>status="indexing"</c>) the lazy-index-
    /// on-first-query semantics call for tools to wait until the scope settles into <c>"ok"</c> or
    /// <c>"degraded"</c> before answering rather than short-circuiting with an empty / no-scope
    /// diagnostic. <see cref="ScopeHost.Ready"/> completes once on the first transition out of
    /// <c>"indexing"</c>; the wait is bounded by the caller's <see cref="CancellationToken"/> so a
    /// stuck index can't hang a tool call indefinitely. No-op when the scope already settled.
    /// </summary>
    private static async Task WaitUntilReadyAsync(
        ScopeHost host,
        CancellationToken ct,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        // A scope that has already settled out of `"indexing"` doesn't need to be waited on —
        // even if the Ready TCS hasn't been signalled (test fixtures often build a host directly
        // with Status = "ok" and skip MarkReady). Checking Status first keeps such fixtures from
        // hanging forever; the Ready.IsCompleted check below is the production-path short-circuit
        // for hosts whose lifecycle is driven by LiveIndexService.
        if (host.Status != "indexing") return;
        if (host.Ready.IsCompleted) return;
        if (progress is null)
        {
            // No-op forwarder: the SDK injects a non-null IProgress for every tool call (including
            // the no-op for clients that didn't request progress). Tools that aren't yet
            // progress-aware pass `null` here — fall back to today's silent wait.
            await host.Ready.WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        // Cold-start forwarding: subscribe to the scope's progress source so each phase
        // emission is forwarded as a wire-level notifications/progress message tagged with the
        // request's progressToken. Unsubscribe in finally so a subsequent emission (e.g. from a
        // late MarkReady) doesn't fire on this call's IProgress after the wait completes.
        Action<ProgressNotificationValue> handler = progress.Report;
        host.ProgressSource.Reported += handler;
        try
        {
            await host.Ready.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            host.ProgressSource.Reported -= handler;
        }
    }

    /// <summary>
    /// Fan-out the typed <c>SymbolHit</c> query <paramref name="probe"/> across every resolved
    /// scope, then dedup the merged result by <c>canonical_key</c>. Each surviving hit is paired
    /// with the sorted list of scope ids it came from so the renderer can annotate
    /// <c>scope: [...]</c>.
    /// </summary>
    public static async Task<MergedHits<SymbolHit>> FanOutSymbolsAsync(
        IReadOnlyList<ScopeHost> hosts,
        Func<ScopeHost, Task<IReadOnlyList<SymbolHit>>> probe,
        CancellationToken ct)
    {
        var perScope = await Task.WhenAll(hosts.Select(async h =>
        {
            if (h.Status == "degraded")
            {
                return (h, (IReadOnlyList<SymbolHit>)Array.Empty<SymbolHit>());
            }
            var hits = await probe(h).ConfigureAwait(false);
            return (h, hits);
        })).ConfigureAwait(false);

        // Group by canonical_key when present; fall back to (Fqn, Kind, FilePath) for storage rows
        // that didn't carry the key (defensive — the migration path should always populate it).
        var byKey = new Dictionary<string, MergedHit<SymbolHit>>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        foreach (var (host, hits) in perScope)
        {
            foreach (var hit in hits)
            {
                var key = hit.CanonicalKey ?? $"!fqn:{hit.Fqn}|{hit.Kind}|{hit.FilePath}|{hit.StartLine}";
                if (byKey.TryGetValue(key, out var existing))
                {
                    existing.Scopes.Add(host.Scope.Id);
                }
                else
                {
                    var merged = new MergedHit<SymbolHit>(hit, new SortedSet<string>(StringComparer.Ordinal) { host.Scope.Id });
                    byKey[key] = merged;
                    orderedKeys.Add(key);
                }
            }
        }

        return new MergedHits<SymbolHit>(orderedKeys.Select(k => byKey[k]).ToList());
    }

    /// <summary>
    /// Format the trailing scope annotation rendered next to a row when multiple scopes are
    /// queried. Returns <c>"scope: foo"</c> for single-scope rows, <c>"scope: [bar, foo]"</c> for
    /// the merged case, or the empty string when only one host was queried.
    /// </summary>
    public static string ScopeAnnotation(IReadOnlyCollection<string> scopes, int totalHostsQueried)
    {
        if (totalHostsQueried <= 1) return "";
        if (scopes.Count == 1) return $" — scope: `{scopes.First()}`";
        return $" — scope: [{string.Join(", ", scopes.Order().Select(s => $"`{s}`"))}]";
    }
}

public sealed record MergedHit<T>(T Hit, SortedSet<string> Scopes);
public sealed record MergedHits<T>(IReadOnlyList<MergedHit<T>> Rows);

/// <summary>
/// One per-scope outcome supplied to a schema-aware typed fan-out merger.
/// </summary>
public sealed record ScopedCallToolResult(
    string ScopeId,
    string ScopeStatus,
    string? ScopeStatusMessage,
    CallToolResult Result);
