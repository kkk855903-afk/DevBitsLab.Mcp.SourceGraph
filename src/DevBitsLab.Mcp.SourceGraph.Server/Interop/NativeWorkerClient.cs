using System.Diagnostics;
using System.Reflection;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

/// <summary>Fixed process timeout and isolation requirements for native extraction.</summary>
public sealed record NativeWorkerClientOptions
{
    public NativeWorkerClientOptions(
        TimeSpan requestTimeout,
        NativeWorkerIsolationRequirements? isolationRequirements = null)
    {
        if (requestTimeout <= TimeSpan.Zero
            || requestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The native worker timeout must be between zero and ten minutes.");
        }

        RequestTimeout = requestTimeout;
        IsolationRequirements =
            isolationRequirements ?? new NativeWorkerIsolationRequirements();
    }

    public TimeSpan RequestTimeout { get; }
    public NativeWorkerIsolationRequirements IsolationRequirements { get; }

    public static NativeWorkerClientOptions Default { get; } =
        new(TimeSpan.FromSeconds(30));
}

internal sealed record NativeWorkerLaunchCommand(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string WorkingDirectory)
{
    public static NativeWorkerLaunchCommand Resolve()
    {
        var serverAssembly = typeof(NativeWorkerClient).Assembly.Location;
        var serverDirectory = Path.GetDirectoryName(serverAssembly)
            ?? throw new InvalidOperationException(
                "The native worker assembly directory could not be resolved.");
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || !Path.IsPathFullyQualified(processPath))
        {
            throw new InvalidOperationException(
                "The current executable path could not be resolved.");
        }

        var processName = Path.GetFileNameWithoutExtension(processPath);
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var workerAssembly =
                !string.IsNullOrWhiteSpace(entryAssembly)
                && string.Equals(
                    Path.GetFileName(entryAssembly),
                    Path.GetFileName(serverAssembly),
                    StringComparison.OrdinalIgnoreCase)
                    ? entryAssembly
                    : serverAssembly;
            return new NativeWorkerLaunchCommand(
                processPath,
                [workerAssembly],
                serverDirectory);
        }

        return new NativeWorkerLaunchCommand(
            processPath,
            Array.Empty<string>(),
            serverDirectory);
    }
}

internal interface INativeWorkerProcess : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

internal interface INativeWorkerProcessLauncher
{
    INativeWorkerProcess? Start(ProcessStartInfo startInfo);
}

internal sealed class SystemNativeWorkerProcessLauncher : INativeWorkerProcessLauncher
{
    public INativeWorkerProcess? Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        return process is null
            ? null
            : new SystemNativeWorkerProcess(process);
    }

    private sealed class SystemNativeWorkerProcess : INativeWorkerProcess
    {
        private readonly Process _process;

        public SystemNativeWorkerProcess(Process process)
        {
            _process = process;
        }

        public Stream StandardInput => _process.StandardInput.BaseStream;
        public Stream StandardOutput => _process.StandardOutput.BaseStream;
        public Stream StandardError => _process.StandardError.BaseStream;
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) =>
            _process.Kill(entireProcessTree);

        public void Dispose() => _process.Dispose();
    }
}

/// <summary>
/// Starts one short-lived native parser child per request. Trust is evaluated before path
/// validation, file access, serialization, or process creation. The child receives exactly one
/// framed request and must emit exactly one bounded framed response.
/// </summary>
public sealed class NativeWorkerClient
{
    private static readonly string[] _allowedEnvironmentVariables =
    [
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT_X86",
        "DOTNET_MULTILEVEL_LOOKUP",
        "SystemRoot",
        "WINDIR",
        "TEMP",
        "TMP",
        "TMPDIR",
    ];

    private readonly IExecutionTrustPolicy _trustPolicy;
    private readonly NativeWorkerClientOptions _options;
    private readonly NativeWorkerLaunchCommand _launch;
    private readonly INativeWorkerProcessLauncher _processLauncher;

    public NativeWorkerClient(
        IExecutionTrustPolicy trustPolicy,
        NativeWorkerClientOptions? options = null)
        : this(
            trustPolicy,
            options ?? NativeWorkerClientOptions.Default,
            NativeWorkerLaunchCommand.Resolve(),
            new SystemNativeWorkerProcessLauncher())
    {
    }

    internal NativeWorkerClient(
        IExecutionTrustPolicy trustPolicy,
        NativeWorkerClientOptions options,
        NativeWorkerLaunchCommand launch,
        INativeWorkerProcessLauncher processLauncher)
    {
        ArgumentNullException.ThrowIfNull(trustPolicy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(processLauncher);
        if (string.IsNullOrWhiteSpace(launch.FileName)
            || !Path.IsPathFullyQualified(launch.FileName)
            || string.IsNullOrWhiteSpace(launch.WorkingDirectory)
            || !Path.IsPathFullyQualified(launch.WorkingDirectory))
        {
            throw new ArgumentException(
                "The native worker launch command must use absolute paths.",
                nameof(launch));
        }

        _trustPolicy = trustPolicy;
        _options = options;
        _launch = launch;
        _processLauncher = processLauncher;
    }

    public async Task<NativeWorkerClientResult> ExtractAsync(
        string repositoryRoot,
        ClangNativeExtractionRequest request,
        CancellationToken cancellationToken = default) =>
        await ExtractCoreAsync(
                repositoryRoot,
                request,
                prepareProtectedInputs: true,
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<NativeWorkerClientResult> ExtractPreparedAsync(
        string repositoryRoot,
        ClangNativeExtractionRequest request,
        CancellationToken cancellationToken = default) =>
        await ExtractCoreAsync(
                repositoryRoot,
                request,
                prepareProtectedInputs: false,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<NativeWorkerClientResult> ExtractCoreAsync(
        string repositoryRoot,
        ClangNativeExtractionRequest request,
        bool prepareProtectedInputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExecutionTrustDecision trust;
        try
        {
            trust = await AuthorizeAsync(
                repositoryRoot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(
                "trust-evaluation-failed",
                $"Native parsing trust evaluation failed ({ex.GetType().Name}).");
        }
        if (!trust.IsAllowed)
        {
            return Failed(
                "trust-denied",
                $"Native parsing was denied ({trust.ReasonCode}).");
        }

        if (prepareProtectedInputs)
        {
            var protectedInputs =
                await ProtectedNativeInputPreparer.PrepareAsync(
                        repositoryRoot,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!protectedInputs.IsSuccess)
            {
                return Failed(
                    protectedInputs.FailureCode!,
                    protectedInputs.FailureMessage!);
            }
            request = protectedInputs.Request;
        }

        var isolationFailure = CheckIsolationRequirements();
        if (isolationFailure is not null)
        {
            return isolationFailure;
        }

        byte[] requestPayload;
        try
        {
            NativeWorkerProtocol.ValidateRepositoryAndRequestRoots(
                repositoryRoot,
                request);
            requestPayload = NativeWorkerProtocol.EncodeRequest(request);
        }
        catch (NativeWorkerProtocolException ex)
        {
            return Failed(ex.Code, ex.Message);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Failed(
                "invalid-request",
                $"The native worker request is invalid ({ex.GetType().Name}).");
        }

        using var workerTimeout = new CancellationTokenSource(
            _options.RequestTimeout);
        using var workerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                workerTimeout.Token);
        workerCancellation.Token.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo();
        INativeWorkerProcess? process;
        try
        {
            process = _processLauncher.Start(startInfo);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (workerTimeout.IsCancellationRequested)
            {
                return Failed(
                    "worker-timeout",
                    "The native worker exceeded its per-request timeout.");
            }
            return Failed(
                "process-start-failed",
                $"The native worker could not be started ({ex.GetType().Name}).");
        }
        if (workerCancellation.IsCancellationRequested)
        {
            if (process is not null)
            {
                using (process)
                {
                    await TerminateAsync(process).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Failed(
                "worker-timeout",
                "The native worker exceeded its per-request timeout.");
        }
        if (process is null)
        {
            return Failed(
                "process-start-failed",
                "The native worker process launcher returned no process.");
        }

        using (process)
        {
            Stream standardInputStream;
            Stream standardOutputStream;
            Stream standardErrorStream;
            try
            {
                standardInputStream = process.StandardInput;
                standardOutputStream = process.StandardOutput;
                standardErrorStream = process.StandardError;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or ObjectDisposedException
                    or NotSupportedException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                return Failed(
                    "worker-stream-open-failed",
                    $"The native worker streams could not be opened ({ex.GetType().Name}).");
            }

            var standardOutputTask = ReadBoundedAsync(
                standardOutputStream,
                NativeWorkerProtocol.MaximumResponseBytes + sizeof(int),
                CancellationToken.None);
            var standardErrorTask = ReadBoundedAsync(
                standardErrorStream,
                NativeWorkerProtocol.MaximumStandardErrorBytes,
                CancellationToken.None);

            try
            {
                await NativeWorkerProtocol.WriteFrameAsync(
                    standardInputStream,
                    requestPayload,
                    NativeWorkerProtocol.MaximumRequestBytes,
                    workerCancellation.Token).ConfigureAwait(false);
                await standardInputStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                await Task.WhenAll(
                    ObserveCaptureAsync(standardOutputTask),
                    ObserveCaptureAsync(standardErrorTask)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                var captures = await ObserveCapturesAsync(
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);
                return Failed(
                    "worker-timeout",
                    "The native worker exceeded its per-request timeout.",
                    standardError: captures.StandardError);
            }
            catch (Exception ex) when (
                ex is IOException
                    or ObjectDisposedException
                    or InvalidOperationException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                var captures = await ObserveCapturesAsync(
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);
                return Failed(
                    "request-write-failed",
                    $"The native worker request could not be written ({ex.GetType().Name}).",
                    standardError: captures.StandardError);
            }

            NativeWorkerClientResult? waitFailure;
            try
            {
                waitFailure = await WaitForExitAsync(
                    process,
                    workerCancellation.Token,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await Task.WhenAll(
                    ObserveCaptureAsync(standardOutputTask),
                    ObserveCaptureAsync(standardErrorTask)).ConfigureAwait(false);
                throw;
            }
            if (waitFailure is not null)
            {
                var captures = await ObserveCapturesAsync(
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);
                return waitFailure with
                {
                    Failure = waitFailure.Failure! with
                    {
                        StandardError = captures.StandardError.Text,
                        StandardErrorTruncated =
                            captures.StandardError.Truncated,
                    },
                };
            }

            var completedCaptures = await ObserveCapturesAsync(
                standardOutputTask,
                standardErrorTask).ConfigureAwait(false);
            var standardOutput = completedCaptures.StandardOutput;
            var standardError = completedCaptures.StandardError;
            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or ObjectDisposedException)
            {
                return Failed(
                    "worker-exit-code-unavailable",
                    $"The native worker exit code could not be read ({ex.GetType().Name}).",
                    standardError: standardError);
            }
            if (standardOutput.FailureCode is not null
                || standardError.FailureCode is not null)
            {
                return Failed(
                    standardOutput.FailureCode
                        ?? standardError.FailureCode!,
                    "A native worker redirected stream could not be read safely.",
                    exitCode,
                    standardError);
            }
            if (standardOutput.Truncated)
            {
                return Failed(
                    "worker-output-too-large",
                    "The native worker response exceeded its fixed byte limit.",
                    exitCode,
                    standardError);
            }

            NativeWorkerResponseEnvelope response;
            try
            {
                var payload = NativeWorkerProtocol.RemoveSingleFrame(
                    standardOutput.Bytes,
                    NativeWorkerProtocol.MaximumResponseBytes);
                response = NativeWorkerProtocol.DecodeResponse(payload.Span, request);
            }
            catch (NativeWorkerProtocolException ex)
            {
                return Failed(
                    ex.Code,
                    ex.Message,
                    exitCode,
                    standardError);
            }

            if (exitCode != 0 && response.Success)
            {
                return Failed(
                    "worker-exit-failed",
                    "The native worker returned a successful payload with a failing exit code.",
                    exitCode,
                    standardError);
            }
            if (exitCode == 0 && !response.Success)
            {
                return Failed(
                    "worker-exit-mismatch",
                    "The native worker returned a failure payload with a successful exit code.",
                    exitCode,
                    standardError);
            }
            if (!response.Success)
            {
                return new NativeWorkerClientResult(
                    null,
                    response.Failure! with
                    {
                        ExitCode = exitCode,
                        StandardError = standardError.Text,
                        StandardErrorTruncated = standardError.Truncated,
                    },
                    NativeWorkerIsolationCapabilities.Baseline);
            }

            return new NativeWorkerClientResult(
                response.Result,
                null,
                NativeWorkerIsolationCapabilities.Baseline);
        }
    }

    private ValueTask<ExecutionTrustDecision> AuthorizeAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _trustPolicy.EvaluateRepositoryCapability(
                repositoryRoot,
                ExecutionCapability.NativeParsing));
    }

    private NativeWorkerClientResult? CheckIsolationRequirements()
    {
        var requirements = _options.IsolationRequirements;
        var capabilities = NativeWorkerIsolationCapabilities.Baseline;
        if (requirements.RequireNetworkIsolation && !capabilities.NetworkIsolation)
        {
            return Failed(
                "isolation-unavailable",
                "The configured native worker requires network isolation, which is unavailable.");
        }
        if (requirements.RequireReducedPrivilege && !capabilities.ReducedPrivilege)
        {
            return Failed(
                "isolation-unavailable",
                "The configured native worker requires reduced privileges, which are unavailable.");
        }
        return null;
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _launch.FileName,
            WorkingDirectory = _launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in _launch.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add(NativeWorkerEntrypoint.InvocationArgument);

        startInfo.Environment.Clear();
        foreach (var name in _allowedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }
        return startInfo;
    }

    private static async Task<NativeWorkerClientResult?> WaitForExitAsync(
        INativeWorkerProcess process,
        CancellationToken workerCancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(
                workerCancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
            when (callerCancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            callerCancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            return Failed(
                "worker-timeout",
                "The native worker exceeded its per-request timeout.");
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or ObjectDisposedException
                or System.ComponentModel.Win32Exception)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            return Failed(
                "worker-wait-failed",
                $"The native worker could not be awaited ({ex.GetType().Name}).");
        }
    }

    private static async Task TerminateAsync(INativeWorkerProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                }
            }
            catch (Exception fallbackException) when (
                fallbackException is InvalidOperationException
                    or NotSupportedException
                    or System.ComponentModel.Win32Exception)
            {
                // Best effort: the process may have raced to exit.
            }
        }

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException
                or InvalidOperationException
                or ObjectDisposedException)
        {
            // The caller still receives the original timeout/cancellation outcome.
        }
    }

    private static async Task<(
        BoundedCapture StandardOutput,
        BoundedCapture StandardError)> ObserveCapturesAsync(
        Task<BoundedCapture> standardOutputTask,
        Task<BoundedCapture> standardErrorTask)
    {
        var output = ObserveCaptureAsync(standardOutputTask);
        var error = ObserveCaptureAsync(standardErrorTask);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return (
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }

    private static async Task<BoundedCapture> ReadBoundedAsync(
        Stream stream,
        int maximumRetainedBytes,
        CancellationToken cancellationToken)
    {
        using var retained = new MemoryStream(
            Math.Min(maximumRetainedBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maximumRetainedBytes - (int)retained.Length;
            if (remaining > 0)
            {
                retained.Write(buffer, 0, Math.Min(remaining, read));
            }
            if (read > remaining)
            {
                truncated = true;
            }
        }

        return new BoundedCapture(retained.ToArray(), truncated);
    }

    private static async Task<BoundedCapture> ObserveCaptureAsync(
        Task<BoundedCapture> captureTask)
    {
        var completed = await Task.WhenAny(
            captureTask,
            Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (completed != captureTask)
        {
            _ = captureTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return new BoundedCapture(
                "stream capture timed out"u8.ToArray(),
                Truncated: true,
                FailureCode: "worker-stream-timeout");
        }

        try
        {
            return await captureTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException
                or ObjectDisposedException
                or InvalidOperationException
                or NotSupportedException)
        {
            return new BoundedCapture(
                Encoding.UTF8.GetBytes(
                    $"stream capture failed ({ex.GetType().Name})"),
                Truncated: false,
                FailureCode: "worker-stream-read-failed");
        }
    }

    private static NativeWorkerClientResult Failed(
        string code,
        string message,
        int? exitCode = null,
        BoundedCapture? standardError = null) =>
        new(
            null,
            new NativeWorkerFailure(
                code,
                message,
                exitCode,
                standardError?.Text,
                standardError?.Truncated ?? false),
            NativeWorkerIsolationCapabilities.Baseline);

    private sealed record BoundedCapture(
        byte[] Bytes,
        bool Truncated,
        string? FailureCode = null)
    {
        public string? Text =>
            Bytes.Length == 0
                ? null
                : Encoding.UTF8.GetString(Bytes);
    }
}
