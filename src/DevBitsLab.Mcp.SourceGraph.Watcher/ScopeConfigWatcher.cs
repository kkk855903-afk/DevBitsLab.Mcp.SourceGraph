using System.Diagnostics.CodeAnalysis;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Watcher;

/// <summary>
/// Watches <c>&lt;repoRoot&gt;/.sourcegraph.json</c> via mtime polling and emits
/// <see cref="ScopeConfigChange"/> values whenever the file's last-write time or presence
/// changes. The watcher is parse-tolerant: a malformed save logs at info level and emits
/// nothing, leaving the running scope set untouched until the next valid save. A file deletion
/// (or rename away from the repo root) emits a <see cref="ScopeConfigChange.Reverted"/> carrying
/// the synthesised default config.
///
/// <para>Why polling instead of <see cref="FileSystemWatcher"/>: macOS's FSEventStream-backed
/// implementation does not reliably deliver events for files at the root of the watched
/// directory (only subdirectory events fire). Polling gives us cross-platform reliability with
/// negligible cost — the default poll interval is 200ms (an mtime stat per tick), well below any
/// human edit cadence, and config files don't change fast enough that even sub-second latency
/// matters in practice.</para>
/// </summary>
public sealed class ScopeConfigWatcher : IAsyncDisposable
{
    private readonly string _repoRoot;
    private readonly IReadOnlyList<string> _discoveredSolutions;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<ScopeConfigWatcher> _logger;
    private readonly System.Threading.Channels.Channel<ScopeConfigChange> _changes =
        System.Threading.Channels.Channel.CreateUnbounded<ScopeConfigChange>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processor;

    public ScopeConfigWatcher(
        string repoRoot,
        IReadOnlyList<string>? discoveredSolutions = null,
        TimeSpan? debounce = null,
        ILogger<ScopeConfigWatcher>? logger = null)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _discoveredSolutions = discoveredSolutions ?? Array.Empty<string>();
        // The `debounce` parameter doubles as the poll interval — its meaning is "the smallest
        // window between observable change events." Default 200ms; tests use a smaller value to
        // keep them fast.
        _pollInterval = debounce ?? TimeSpan.FromMilliseconds(200);
        _logger = logger ?? NullLogger<ScopeConfigWatcher>.Instance;

        _processor = Task.Run(() => PollAsync(_cts.Token));
    }

    /// <summary>Returns an async stream of config changes. Stops when disposed.</summary>
    public IAsyncEnumerable<ScopeConfigChange> ReadAllAsync(CancellationToken ct = default) =>
        _changes.Reader.ReadAllAsync(ct);

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "PollAsync is a long-running poll loop that must never permanently die: anything escaping ScopeConfigLoader.Load (transient I/O, ACL flap, parse error, or any unforeseen exception type) silently disables live config reload until process restart. The broad final catch logs at error level so the failure is still visible while keeping the loop alive.")]
    private async Task PollAsync(CancellationToken ct)
    {
        var path = Path.Join(_repoRoot, ScopeConfigLoader.FileName);
        // Track last observed presence + mtime so we only emit on change. The very first iteration
        // fires unconditionally (sentinel `firstIteration` flag) so the diff-and-apply path
        // catches any change that landed between the server's startup-time
        // `ScopeConfigLoader.Load` and the watcher actually starting (the watcher boots after the
        // cold-index `WhenAll` settles, which can race with a config edit). The diff is a cheap
        // pure helper that returns "no-op" when the on-disk content matches what's already live,
        // so this initial emit is harmless when nothing changed and load-bearing when it did.
        bool lastExists = false;
        DateTime lastWriteUtc = DateTime.MinValue;
        bool firstIteration = true;

        // Wrap the entire loop in try/finally so `TryComplete` always fires, even when
        // `WriteAsync(..., ct)` throws `OperationCanceledException` mid-write during disposal.
        // Without this, `ReadAllAsync` consumers would hang waiting for completion that never
        // arrives, breaking the "stops when disposed" contract.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool exists;
                DateTime writeUtc;
                bool statSucceeded;
                try
                {
                    exists = File.Exists(path);
                    writeUtc = exists ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                    statSucceeded = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    // Filtered catch covers everything `File.Exists` and `File.GetLastWriteTimeUtc`
                    // realistically throw on a misbehaving filesystem. Treat any of these as
                    // "state unchanged for this tick" so the loop retries on the next tick rather
                    // than dying. Genuinely unforeseen exception types (NRE from a runtime bug,
                    // OOM, etc.) are intentionally left to propagate — the class-level
                    // reliability contract is about resilience to environmental flakiness, not
                    // masking real bugs.
                    _logger.LogDebug(ex, "Polling .sourcegraph.json failed; will retry on next tick");
                    exists = lastExists;
                    writeUtc = lastWriteUtc;
                    statSucceeded = false;
                }

                var presenceChanged = exists != lastExists;
                var mtimeChanged = exists && writeUtc != lastWriteUtc;

                // Gate the synthetic-init emit on `statSucceeded`: without this, a transient
                // probe failure at watcher startup would land us in the !exists branch with
                // firstIteration=true and emit a Reverted (synthesised default), tearing down
                // every live scope on a single ACL flap. The first *successful* stat is what
                // drives the synthetic init.
                if ((firstIteration && statSucceeded) || presenceChanged || mtimeChanged)
                {
                    if (statSucceeded) firstIteration = false;
                    // Note: we do NOT commit `lastExists` / `lastWriteUtc` here. Each branch below
                    // commits only on a successful (or "intentionally swallowed") outcome. If a
                    // transient I/O error escapes the load path, we leave the cursor as-is so the
                    // next tick still sees mtime/presence-changed and retries.
                    if (!exists)
                    {
                        var synthesised = ScopeConfigLoader.Synthesise(_repoRoot, _discoveredSolutions);
                        _logger.LogInformation(".sourcegraph.json is absent; reverting to synthesised default scope");
                        await _changes.Writer.WriteAsync(new ScopeConfigChange.Reverted(synthesised), ct).ConfigureAwait(false);
                        lastExists = false;
                        lastWriteUtc = DateTime.MinValue;
                    }
                    else
                    {
                        ScopeConfig parsed;
                        try
                        {
                            parsed = ScopeConfigLoader.Load(_repoRoot, _discoveredSolutions);
                            await _changes.Writer.WriteAsync(new ScopeConfigChange.Updated(parsed), ct).ConfigureAwait(false);
                            // Successful load + emit → commit cursor.
                            lastExists = true;
                            lastWriteUtc = writeUtc;
                        }
                        catch (ScopeConfigException ex)
                        {
                            // Parse error: the file as observed on disk is malformed. Commit the
                            // cursor so we don't re-fire this same warning on every subsequent
                            // tick — the user's next save will bump mtime and re-trigger the load.
                            _logger.LogInformation(ex, ".sourcegraph.json save was malformed; ignoring (current scopes still active)");
                            lastExists = true;
                            lastWriteUtc = writeUtc;
                        }
                        catch (Exception ex) when (ex is IOException
                            or UnauthorizedAccessException
                            or ArgumentException
                            or NotSupportedException
                            or System.Security.SecurityException
                            or System.Text.Json.JsonException
                            or System.InvalidOperationException)
                        {
                            // Filtered catch enumerating everything `ScopeConfigLoader.Load` can
                            // plausibly throw beyond `ScopeConfigException`: file API
                            // (IOException, UnauthorizedAccessException, SecurityException),
                            // bad path/argument shapes (ArgumentException, NotSupportedException),
                            // wrapper-or-shape JSON failures the loader doesn't catch
                            // (JsonException, InvalidOperationException). *Don't* commit the
                            // cursor — the next tick will see mtime-changed and retry. Logging
                            // at info because most cases here are transient (file locked
                            // mid-save, ACL flap). Genuinely unforeseen types (NRE, OOM,
                            // StackOverflow) are intentionally left to propagate so real bugs
                            // surface rather than getting silently swallowed.
                            _logger.LogInformation(ex, ".sourcegraph.json read failed; will retry on next poll");
                        }
                    }
                }

                try { await Task.Delay(_pollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* WriteAsync cancelled mid-disposal */ }
        finally
        {
            _changes.Writer.TryComplete();
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "DisposeAsync awaits the processor task during shutdown; the only practical signals are OperationCanceledException (cooperative shutdown) and the rare unhandled exception bubbling up from PollAsync. Either way the right move on dispose is to swallow and continue tearing down — propagating would mask the disposal contract.")]
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _processor.ConfigureAwait(false); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}

/// <summary>
/// One event from <see cref="ScopeConfigWatcher"/>. <see cref="Updated"/> means the watcher just
/// successfully parsed a present <c>.sourcegraph.json</c>; <see cref="Reverted"/> means the file is
/// absent and the synthesised single-scope default applies. Both carry the resolved
/// <see cref="ScopeConfig"/> so the diff-and-apply path can ignore the kind when it doesn't need to.
/// </summary>
public abstract record ScopeConfigChange(ScopeConfig Config)
{
    public sealed record Updated(ScopeConfig Config) : ScopeConfigChange(Config);
    public sealed record Reverted(ScopeConfig Config) : ScopeConfigChange(Config);
}
