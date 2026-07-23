using System.Diagnostics;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Process-wide policy state for corruption recovery. Wires the env-var-gated autonomous-rebuild
/// flag plus a delegate the dispatch layer fires to kick off the actual rebuild on a corrupt
/// scope. Both fields are configured once at startup (in <c>Program.cs</c>); the dispatch path
/// reads them from <see cref="ScopedExecution"/> on every wrapped tool call.
///
/// Holding the rebuild delegate as a static field rather than threading <c>LiveIndexService</c>
/// through every <see cref="ScopedExecution"/> call site keeps the dispatch layer's signature
/// untouched — the corruption-recovery wiring is internal to <see cref="ScopedExecution"/> and
/// invisible to the 20+ existing tool bodies.
/// </summary>
public static class CorruptionPolicy
{
    /// <summary>
    /// True when <c>SOURCEGRAPH_AUTOREBUILD_CORRUPT_DBS</c> was set to a truthy value at startup.
    /// Read once in <c>Program.cs</c>; never flips during a process lifetime.
    /// </summary>
    public static bool AutoRebuildEnabled { get; set; }

    /// <summary>
    /// Fire-and-forget delegate invoked when corruption is confirmed AND
    /// <see cref="AutoRebuildEnabled"/> is true. The delegate runs the rebuild on a background
    /// task; the original tool call completes (with the corruption error) before the rebuild
    /// finishes. Default is a no-op (rebuild disabled or not configured).
    /// </summary>
    public static Func<string, CancellationToken, Task> RebuildDelegate { get; set; } =
        (_, _) => Task.CompletedTask;

    /// <summary>
    /// Shared scope registry handle used by <see cref="CorruptionGuard.VerifyAndMarkAsync"/> to
    /// persist the degraded row when corruption is confirmed. Set in <c>Program.cs</c> after the
    /// DI container is built; null when corruption recovery is not wired (e.g., one-shot CLI
    /// paths that don't have a registry).
    /// </summary>
    public static IScopeRegistry? Registry { get; set; }

    /// <summary>
    /// Logger used by <see cref="CorruptionGuard"/>. Set in <c>Program.cs</c>; defaults to a
    /// silent NullLogger so tests can exercise the guard without DI.
    /// </summary>
    public static ILogger Logger { get; set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Read the environment variable and return whether autonomous rebuild should be enabled.
    /// Truthy values: <c>"1"</c>, <c>"true"</c>, <c>"yes"</c> (case-insensitive). Everything
    /// else (including unset) is false.
    /// </summary>
    public static bool ParseEnvVar(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Reactive corruption verification for scope tool calls. When a wrapped tool body throws a
/// SQLite corruption error (code 11 / 26), this helper runs <see cref="IGraphStore.IntegrityCheckAsync"/>
/// and dispatches:
///
/// <list type="bullet">
///   <item><b>Integrity check returns "ok"</b> (false alarm — transient error): emit a
///     <c>corruption-suspected-but-clean</c> heal event and rethrow the original exception. The
///     scope is NOT marked degraded; the next call may succeed.</item>
///   <item><b>Integrity check returns non-"ok"</b> (corruption confirmed): emit
///     <c>corruption-detected</c>, mark the scope <c>degraded</c> with a discriminative
///     status_message hinting <c>repair_scope mode=rebuild</c>, optionally fire the autonomous
///     rebuild on a background task (if the env var is enabled), then rethrow.</item>
///   <item><b>Integrity check itself throws</b> (DB so broken even the check fails): emit
///     <c>corruption-detected</c> with <c>ok=false</c>, mark scope degraded with the verification
///     failure message, then rethrow.</item>
/// </list>
///
/// In all three branches the original exception still surfaces to the caller — the verification
/// adds wall-clock to the failed call but the structured `degraded` state is what subsequent
/// calls benefit from.
/// </summary>
internal static class CorruptionGuard
{
    /// <summary>
    /// Returns true when <paramref name="ex"/> indicates SQLite-level on-disk corruption (codes
    /// 11 = SQLITE_CORRUPT, 26 = SQLITE_NOTADB). Other SqliteExceptions propagate unchanged.
    /// </summary>
    public static bool IsCorruptionError(Exception ex)
    {
        if (ex is SqliteException sqlEx && sqlEx.SqliteErrorCode is 11 or 26) return true;
        if (ex is GraphStoreCorruptedException) return true;
        return false;
    }

    /// <summary>
    /// Run the verify-and-dispatch flow against a confirmed corruption error. Returns the
    /// status_message that was persisted (so the caller can include it in any rethrow / log).
    /// </summary>
    public static async Task<string> VerifyAndMarkAsync(
        ScopeHost host,
        IScopeRegistry registry,
        Exception originalException,
        ILogger logger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string integrityResult;
        try
        {
            integrityResult = await host.Store.IntegrityCheckAsync(ct).ConfigureAwait(false);
        }
        catch (Exception verifyEx) when (verifyEx is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(verifyEx, "Scope `{Id}`: integrity_check itself failed during corruption verification", host.Scope.Id);
            var msg = $"corruption detected: integrity_check itself failed: {verifyEx.Message}; call repair_scope mode=rebuild";
            HealLog.Append(kind: "corruption-detected", scope: host.Scope.Id, ok: false,
                ms: sw.Elapsed.TotalMilliseconds,
                details: $"integrity_check itself failed: {verifyEx.Message}");
            await MarkDegradedAsync(host, registry, msg, ct).ConfigureAwait(false);
            return msg;
        }

        sw.Stop();

        if (string.Equals(integrityResult, "ok", StringComparison.Ordinal))
        {
            // False alarm — DB is intact. Don't mark degraded; record the suspicion.
            HealLog.Append(kind: "corruption-suspected-but-clean", scope: host.Scope.Id, ok: true,
                ms: sw.Elapsed.TotalMilliseconds,
                details: "integrity_check passed; treating as transient");
            logger.LogWarning("Scope `{Id}`: SQLite corruption error suspected but integrity_check passed; treating as transient", host.Scope.Id);
            return $"corruption suspected but clean: {originalException.Message}";
        }

        // Confirmed corruption.
        var statusMessage = $"corruption detected: {integrityResult}; call repair_scope mode=rebuild";
        HealLog.Append(kind: "corruption-detected", scope: host.Scope.Id, ok: true,
            ms: sw.Elapsed.TotalMilliseconds,
            details: $"integrity_check failed: {integrityResult}");
        await MarkDegradedAsync(host, registry, statusMessage, ct).ConfigureAwait(false);

        // Fire-and-forget autonomous rebuild if the env var is enabled. The rebuild MUST outlive
        // the failed tool call: the originating MCP request is about to fail (the corrupt DB
        // can't serve it), and the request-scoped CancellationToken `ct` will complete when the
        // response goes back. Binding `Task.Run` or the inner await to `ct` would cancel the
        // rebuild prematurely. The RebuildDelegate (set in Program.cs) ignores its passed token
        // and uses LiveIndexService.HostStoppingToken internally — host shutdown is the only
        // legitimate cancellation reason for a fire-and-forget rebuild.
        if (CorruptionPolicy.AutoRebuildEnabled)
        {
            HealLog.Append(kind: "corrupt-db-rebuild-started", scope: host.Scope.Id, ok: true, ms: 0,
                details: "fire-and-forget rebuild kicked off");
            var scopeIdCapture = host.Scope.Id;
            _ = Task.Run(async () =>
            {
                var rebuildSw = Stopwatch.StartNew();
                try
                {
                    // The CancellationToken.None pass-through here is intentional — the delegate
                    // overrides it with the host-lifetime token via Program.cs wiring. Tests that
                    // configure their own RebuildDelegate may use the parameter directly.
                    await CorruptionPolicy.RebuildDelegate(scopeIdCapture, CancellationToken.None).ConfigureAwait(false);
                    rebuildSw.Stop();
                    HealLog.Append(kind: "corrupt-db-rebuilt", scope: scopeIdCapture, ok: true,
                        ms: rebuildSw.Elapsed.TotalMilliseconds,
                        details: "autonomous rebuild succeeded");
                }
                catch (OperationCanceledException)
                {
                    rebuildSw.Stop();
                    HealLog.Append(kind: "corrupt-db-rebuilt", scope: scopeIdCapture, ok: false,
                        ms: rebuildSw.Elapsed.TotalMilliseconds,
                        details: "rebuild cancelled by host shutdown");
                }
                catch (Exception ex)
                {
                    rebuildSw.Stop();
                    HealLog.Append(kind: "corrupt-db-rebuilt", scope: scopeIdCapture, ok: false,
                        ms: rebuildSw.Elapsed.TotalMilliseconds,
                        details: $"rebuild failed: {ex.Message}");
                }
            });
        }

        return statusMessage;
    }

    private static async Task MarkDegradedAsync(ScopeHost host, IScopeRegistry registry, string statusMessage, CancellationToken ct)
    {
        // In-process state always flips first — even if the registry write below throws (e.g.,
        // _meta.db is locked or itself corrupt), subsequent ScopedExecution calls against this
        // host will short-circuit on the in-memory `Status == "degraded"` check, so the user-
        // facing degraded behaviour holds. The registry persistence is the cross-restart story:
        // best-effort, log-and-continue. Letting an UpsertAsync exception escape would shadow
        // the original SQLite-corruption signal that called us here, so VerifyAndMarkAsync's
        // caller would see the wrong cause.
        host.Status = "degraded";
        host.StatusMessage = statusMessage;
        var row = new ScopeRow(
            Id: host.Scope.Id,
            Name: host.Scope.Name,
            Root: host.Scope.Root,
            ProjectSetJson: ScopeProjectSetSerialiser.Serialise(host.Scope.ProjectSet),
            Isolated: host.Scope.Isolated,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "degraded",
            StatusMessage: statusMessage);
        try
        {
            await registry.UpsertAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CorruptionPolicy.Logger.LogWarning(ex,
                "Scope `{Id}`: failed to persist degraded row to registry during corruption verification; in-process status is still flipped",
                host.Scope.Id);
        }
    }
}
