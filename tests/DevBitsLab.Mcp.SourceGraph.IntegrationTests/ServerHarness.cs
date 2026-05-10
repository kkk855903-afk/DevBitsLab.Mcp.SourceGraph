using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// Spawns the SourceGraph MCP server as a real child process and connects an
/// <see cref="McpClient"/> to it over stdio, returning the connected client and an
/// <see cref="IAsyncDisposable"/> handle that tears the process down on dispose. This is the
/// out-of-process equivalent of the in-process <c>ServerInstructionsWiringTests</c>: the wire
/// shape on the <c>initialize</c> response — including <c>Capabilities.Experimental</c> —
/// flows through the official MCP SDK exactly as it does for a real consumer (Claude Code,
/// Cursor, etc.), so a regression that affects only serialisation (e.g. an SDK upgrade
/// renaming a key, or System.Text.Json mishandling an anonymous type) is caught here where
/// the in-process tests would miss it.
///
/// <para>Lifetime contract:</para>
/// <list type="bullet">
///   <item>The child is launched via <c>dotnet run --no-build --no-launch-profile --project &lt;Server.csproj&gt; -- serve …</c>.
///         The IntegrationTests project's MSBuild graph lists the Server as a build-time dependency
///         (with <c>ReferenceOutputAssembly=false</c>), so by the time tests run the Server's
///         output is already on disk and the <c>--no-build</c> path is fast.</item>
///   <item>Every disposal path (graceful or aborted) waits up to <see cref="ProcessExitTimeout"/>
///         for the child to exit, then <c>Kill(entireProcessTree)</c>'s the dotnet host.
///         This keeps a stuck server from hanging CI: a deadlocked child fails the test fast.</item>
///   <item>Stderr lines from the child are captured into <see cref="StderrLines"/> via the
///         transport's <c>StandardErrorLines</c> callback, so tests can assert "the handshake
///         emitted no stderr".</item>
/// </list>
/// </summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    /// <summary>
    /// Hard cap on how long we wait for the spawned dotnet host to exit on dispose, and the
    /// matching cap on the MCP <c>initialize</c> handshake. A stuck child shouldn't hang the test
    /// run; if the graceful close-stdin path doesn't release the process within this window the
    /// harness escalates to <c>Kill(entireProcessTree=true)</c>.
    /// <para>
    /// Sized for the slowest supported runner: Windows agents pay a higher fixed cost on
    /// <c>dotnet run --no-build</c> startup plus per-scope SQLite open + vocabulary computation
    /// (the multi-scope fixture in particular dances close to the original 10s budget — the
    /// preceding test in the same class routinely reports ~10s on Windows). 30s gives Windows
    /// breathing room without making genuinely deadlocked tests slow to fail; macOS / Linux
    /// success-path runs are unaffected because they complete in 3–10s and yield well before
    /// the cap.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hard cap on how long we wait for the spawned <c>dotnet run --no-build … serve</c> child
    /// to complete the MCP <c>initialize</c> handshake. This is intentionally separate from
    /// <see cref="ProcessExitTimeout"/>: the cold spawn path covers process creation, .NET host
    /// boot, JIT of the server assembly, MSBuildLocator + Roslyn workspace warm-up, scope config
    /// load, and the initial JSON-RPC exchange. On a constrained CI runner (especially Windows,
    /// where the integration suite spawns multiple harnesses in parallel) that easily exceeds
    /// the 10s teardown budget, so we give the handshake a much wider window. A server that
    /// genuinely deadlocks during init still fails the test inside <see cref="HandshakeTimeout"/>
    /// — just not on the first slow CI scheduling slice.
    /// </summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);

    private readonly McpClient _client;
    private readonly ConcurrentQueue<string> _stderrLines;
    private bool _disposed;

    private ServerHarness(McpClient client, ConcurrentQueue<string> stderrLines)
    {
        _client = client;
        _stderrLines = stderrLines;
    }

    /// <summary>The connected MCP client. Already initialised — <c>ServerCapabilities</c> /
    /// <c>ServerInfo</c> / <c>ServerInstructions</c> are populated.</summary>
    public McpClient Client => _client;

    /// <summary>
    /// Snapshot of stderr lines the child has emitted so far. The MCP SDK's stdio transport
    /// invokes the <c>StandardErrorLines</c> callback on every stderr line; we accumulate them
    /// here. Tests call this after <see cref="StartAsync"/> returns to assert no stderr was
    /// produced during the handshake (the server logs only via <c>LogToStandardErrorThreshold =
    /// Trace</c> which is silent by default).
    /// </summary>
    public IReadOnlyCollection<string> StderrLines => _stderrLines.ToArray();

    /// <summary>
    /// Spawn the server and connect a client to it. <paramref name="serveArgs"/> is the rest
    /// of the command line after the implicit <c>serve</c> verb: e.g.
    /// <c>new[] { "--solution", "/abs/path/Sample.sln", "--no-history", "--no-embeddings" }</c>.
    /// We always force <c>--no-history</c> + <c>--no-embeddings</c> at the call site so the
    /// integration suite never touches git or downloads ONNX models in CI; this method takes
    /// the caller's flags as-is.
    ///
    /// <para>Cancellation: <paramref name="cancellationToken"/> cancels the
    /// <c>McpClient.CreateAsync</c> handshake. The harness uses an outer timeout of
    /// <see cref="HandshakeTimeout"/> derived from this token via
    /// <c>CancellationTokenSource.CreateLinkedTokenSource</c> so a server that never sends an
    /// <c>InitializeResult</c> doesn't hang the test runner.</para>
    /// </summary>
    public static async Task<ServerHarness> StartAsync(
        string[] serveArgs,
        IDictionary<string, string?>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var serverProject = LocateServerProject();
        var configuration = DetectBuildConfiguration();
        var stderrLines = new ConcurrentQueue<string>();

        // Wipe any pre-existing `.sourcegraph/` directory next to the solution before every
        // harness spawn. Without this, `_meta.db` persists across test runs and tests that
        // dispose the harness mid-cold-index leave a `Status='indexing'` registry row behind.
        // The next run's boot reconciliation legitimately marks that scope `degraded` and logs
        // a warning to stderr — which would break the `stderr should be empty` invariant some
        // tests check. Cleaning here keeps every spawn hermetic.
        TryClearSourcegraphDir(serveArgs);

        // Build the full argument list. `dotnet run` arguments come before the `--`, then the
        // server's CLI args after. `--no-launch-profile` skips the launchSettings.json lookup
        // that would otherwise log a warning to stderr on every spawn. `--no-build` keeps the
        // spawn fast — the IntegrationTests csproj already lists Server as a build-time dep so
        // the binaries exist by the time tests run. `--configuration <X>` is required because
        // `dotnet run --no-build` defaults to Debug regardless of how the test was built; CI
        // builds Release and the harness would otherwise look for Debug binaries that don't
        // exist. We mirror whichever configuration the test assembly itself was built in.
        var args = new List<string>
        {
            "run",
            "--project", serverProject,
            "--configuration", configuration,
            "--no-build",
            "--no-launch-profile",
            "--",
            "serve",
        };
        args.AddRange(serveArgs);

        // The transport owns the child process: it spawns it, wires its stdin/stdout to the
        // JSON-RPC channel, and disposes it when the McpClient is disposed. We just need to
        // hand it the spawn parameters. ShutdownTimeout limits how long the SDK waits for a
        // graceful close-stdin shutdown before forcing a kill on dispose.
        var transportOptions = new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = args,
            Name = "sourcegraph-mcp-integration-test",
            ShutdownTimeout = ProcessExitTimeout,
            StandardErrorLines = line =>
            {
                if (!string.IsNullOrEmpty(line)) stderrLines.Enqueue(line);
            },
        };
        if (environmentVariables is not null)
        {
            transportOptions.EnvironmentVariables = environmentVariables;
        }

        var transport = new StdioClientTransport(transportOptions, NullLoggerFactory.Instance);

        // Cap the handshake at HandshakeTimeout. A server that never replies to `initialize`
        // (deadlock, missing fixture, ungraceful crash) fails the test fast instead of hanging.
        // This is intentionally wider than ProcessExitTimeout — the cold spawn path
        // (process create + JIT + Roslyn warm-up + scope load) routinely exceeds the 10s
        // teardown budget under parallel CI load, particularly on Windows runners.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HandshakeTimeout);

        // McpClient.CreateAsync takes ownership of `transport` on success — disposing the
        // returned client also disposes the transport. On failure the ownership transfer never
        // happened, so we have to dispose the transport ourselves to avoid leaking the spawned
        // `dotnet run ... serve` child process. Track success via a non-null `client` reference
        // and use a finally branch (which runs uniformly on success, on the OCE → TimeoutException
        // rethrow, and on any other unexpected exception) to dispose the orphaned transport.
        McpClient? client = null;
        try
        {
            client = await McpClient
                .CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Internal handshake timeout fired (caller didn't cancel). Surface a clearer error so
            // the failing test points at the actual cause instead of a generic OCE.
            throw new TimeoutException(
                $"sourcegraph-mcp server did not complete the MCP `initialize` handshake within {HandshakeTimeout}. " +
                $"Captured stderr: {string.Join(Environment.NewLine, stderrLines)}");
        }
        finally
        {
            if (client is null)
            {
                await DisposeTransportSafelyAsync(transport).ConfigureAwait(false);
            }
        }

        return new ServerHarness(client, stderrLines);
    }

    /// <summary>
    /// Best-effort disposal for an <see cref="IClientTransport"/> we know was never handed off
    /// to an <see cref="McpClient"/>. Probes for both <see cref="IAsyncDisposable"/> and
    /// <see cref="IDisposable"/> so this works regardless of which interface the SDK exposes on
    /// the concrete transport. Swallows the realistic disposal-path exceptions so a failed
    /// dispose can't mask the original failure that triggered this cleanup.
    /// </summary>
    private static async ValueTask DisposeTransportSafelyAsync(IClientTransport transport)
    {
        try
        {
            switch (transport)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception ex) when (
            ex is IOException
            or ObjectDisposedException
            or OperationCanceledException
            or InvalidOperationException)
        {
            // Best-effort: don't let a failed transport disposal mask the original failure that
            // brought us into the cleanup path.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Disposing the client closes the transport, which closes the child's stdin and gives
        // it the configured ShutdownTimeout to exit gracefully before being killed. We don't
        // need to layer another timeout on top — the transport already enforces one.
        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException
            or ObjectDisposedException
            or OperationCanceledException
            or InvalidOperationException)
        {
            // Swallow the disposal-path exceptions the MCP SDK / stdio transport realistically
            // throws — broken pipe (IOException), already-disposed race
            // (ObjectDisposedException), graceful close-stdin timeout (OperationCanceledException),
            // and state-machine reentry (InvalidOperationException). Tests will already have
            // signalled failure via assertion if the disposal path was misbehaving; swallowing
            // these keeps a flaky teardown from masking the real test failure. Anything else
            // (e.g. a NullReferenceException indicating a bug in the harness itself) propagates
            // and surfaces.
        }
    }

    /// <summary>
    /// Detect the build configuration (<c>Debug</c> / <c>Release</c>) the test assembly itself
    /// was compiled in by looking at <see cref="AppContext.BaseDirectory"/> — paths under the
    /// MSBuild conventions land in <c>…/bin/Debug/&lt;tfm&gt;/</c> or <c>…/bin/Release/&lt;tfm&gt;/</c>.
    /// We mirror this configuration when invoking <c>dotnet run --no-build</c> for the server so
    /// the harness picks up the binaries that were just built (CI builds Release; local
    /// <c>dotnet test</c> defaults to Debug). Falls back to <c>Debug</c> if the path doesn't
    /// match either convention so unusual layouts don't crash the harness — the subsequent
    /// <c>dotnet run --no-build</c> will surface a useful error if the chosen configuration
    /// doesn't exist on disk.
    /// </summary>
    private static string DetectBuildConfiguration()
    {
        var baseDirectory = AppContext.BaseDirectory.Replace('\\', '/');
        if (baseDirectory.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }
        return "Debug";
    }

    /// <summary>
    /// Walk upward from <see cref="AppContext.BaseDirectory"/> to find the repo root and return
    /// the absolute path to <c>src/DevBitsLab.Mcp.SourceGraph.Server/DevBitsLab.Mcp.SourceGraph.Server.csproj</c>.
    /// Mirrors the fixture-discovery dance the in-process Tests project uses for
    /// <c>tests/fixtures/Sample.sln</c>.
    /// </summary>
    private static string LocateServerProject()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Join(
                d.FullName,
                "src",
                "DevBitsLab.Mcp.SourceGraph.Server",
                "DevBitsLab.Mcp.SourceGraph.Server.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "Could not locate src/DevBitsLab.Mcp.SourceGraph.Server/DevBitsLab.Mcp.SourceGraph.Server.csproj " +
            $"from {AppContext.BaseDirectory}. The IntegrationTests project must run from the repo it lives in.");
    }

    /// <summary>
    /// Best-effort clean of the <c>.sourcegraph/</c> directory next to the <c>--solution</c>
    /// argument the harness was invoked with, so prior-run state can't pollute the current
    /// spawn. Quietly skips when no <c>--solution</c> is in args, when the path doesn't exist,
    /// or when the directory can't be removed (a stuck file handle from a flaky prior teardown
    /// is recoverable on the next run; we'd rather proceed than fail the test setup).
    /// </summary>
    private static void TryClearSourcegraphDir(IReadOnlyList<string> serveArgs)
    {
        for (var i = 0; i < serveArgs.Count - 1; i++)
        {
            if (!string.Equals(serveArgs[i], "--solution", StringComparison.Ordinal)) continue;
            var solutionPath = serveArgs[i + 1];
            var dir = Path.GetDirectoryName(solutionPath);
            if (string.IsNullOrEmpty(dir)) return;
            var sourcegraphDir = Path.Join(dir, ".sourcegraph");
            if (!Directory.Exists(sourcegraphDir)) return;
            try { Directory.Delete(sourcegraphDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
            return;
        }
    }

    /// <summary>
    /// Walk upward from <see cref="AppContext.BaseDirectory"/> to find the absolute path of a
    /// fixture under <c>tests/fixtures/</c>. <paramref name="relativeFromFixtures"/> is the
    /// trailing path segment, e.g. <c>"Sample.sln"</c> or <c>"MultiScope"</c>. Throws if no
    /// matching path exists; the caller's `FileNotFoundException` / `DirectoryNotFoundException`
    /// surfaces with a concrete pointer to the missing fixture.
    /// </summary>
    public static string LocateFixture(string relativeFromFixtures)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Join(d.FullName, "tests", "fixtures", relativeFromFixtures);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            $"Could not locate tests/fixtures/{relativeFromFixtures} from {AppContext.BaseDirectory}.");
    }
}
