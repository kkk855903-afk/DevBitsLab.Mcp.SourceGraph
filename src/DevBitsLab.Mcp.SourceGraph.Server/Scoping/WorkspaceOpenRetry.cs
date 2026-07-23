using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Bounded-retry policy for the cold-index workspace-open path. Pulled out of
/// <see cref="LiveIndexService"/> as a standalone helper so the retry contract can be exercised
/// against fake open/index delegates with tiny backoffs in unit tests, without standing up the
/// hosted-service DI graph.
///
/// The production caller uses <see cref="DefaultBackoffs"/> (<c>[1s, 5s, 25s]</c>); tests pass
/// tiny backoffs so they don't burn 31 seconds proving the negative case.
/// </summary>
internal static class WorkspaceOpenRetry
{
    /// <summary>
    /// Default backoff schedule: <c>[1s, 5s, 25s]</c>. Drives 4 attempts total (1 initial + 3
    /// retries) — the schedule has one entry per delay BETWEEN attempts, and
    /// <see cref="RunAsync"/> computes <c>maxAttempts = backoffs.Count + 1</c>. Worst-case
    /// wall-clock = 1s + 5s + 25s = 31s before today's <c>degraded</c> outcome. Sized for the
    /// failure profile (dotnet restore racing, MSBuild SDK upgrade in flight, network-FS
    /// hiccup) — see <c>add-scope-repair-tools/design.md</c> Decision 1.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultBackoffs =
        new[] { TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(5000), TimeSpan.FromMilliseconds(25000) };

    /// <summary>
    /// Run <paramref name="openAsync"/> + <paramref name="indexAllAsync"/> with bounded retry.
    /// On success at attempt N>1 emits a heal event with <c>ok=true</c>; on final failure emits
    /// <c>ok=false</c> and rethrows the last exception.
    /// </summary>
    public static async Task<IndexResult> RunAsync(
        string scopeId,
        Func<CancellationToken, Task> openAsync,
        Func<CancellationToken, Task<IndexResult>> indexAllAsync,
        IReadOnlyList<TimeSpan> backoffs,
        ILogger logger,
        CancellationToken ct)
    {
        var maxAttempts = backoffs.Count + 1;
        var sw = Stopwatch.StartNew();
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await openAsync(ct).ConfigureAwait(false);
                var result = await indexAllAsync(ct).ConfigureAwait(false);
                if (attempt > 1)
                {
                    sw.Stop();
                    HealLog.Append(kind: "workspace-open-retried", scope: scopeId, ok: true,
                        ms: sw.Elapsed.TotalMilliseconds, details: $"succeeded on attempt {attempt}");
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < maxAttempts)
                {
                    var delay = backoffs[attempt - 1];
                    logger.LogWarning(
                        "Workspace open attempt {Attempt}/{Total} failed for scope `{Id}`: {Message}; retrying in {Delay}s",
                        attempt, maxAttempts, scopeId, ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        sw.Stop();
        HealLog.Append(kind: "workspace-open-retried", scope: scopeId, ok: false,
            ms: sw.Elapsed.TotalMilliseconds,
            details: $"all {maxAttempts} attempts failed: {lastException!.Message}");
        // Preserve the original throw site's stack trace. A bare `throw lastException` would
        // reset the stack to this line, which makes diagnosing the underlying workspace-open
        // failure (Roslyn, MSBuild, file-system) much harder for the operator looking at logs.
        ExceptionDispatchInfo.Capture(lastException).Throw();
        throw lastException; // unreachable; satisfies the C# definite-return checker.
    }
}
