using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Core.Security;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeWorkerClientTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly NativeWorkerLaunchCommand _launch;

    public NativeWorkerClientTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-native-worker-client-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "medical.cpp");
        File.WriteAllText(_source, "extern \"C\" int medical_api();");
        _launch = new NativeWorkerLaunchCommand(
            Path.Combine(_root, OperatingSystem.IsWindows() ? "worker.exe" : "worker"),
            [Path.Combine(_root, "sourcegraph-mcp.dll")],
            _root);
    }

    [Fact]
    public async Task ExtractAsync_authorizesBeforeStartingFixedSanitizedProcess()
    {
        var request = Request();
        var response = SuccessfulResponse();
        var process = FakeProcess.Exited(
            NativeWorkerProtocol.AddFrame(
                NativeWorkerProtocol.EncodeResponse(response),
                NativeWorkerProtocol.MaximumResponseBytes));
        var launcher = new FakeLauncher(process);
        var trust = new FakeTrustPolicy(allowed: true);
        var client = Client(trust, launcher);

        var result = await client.ExtractAsync(_root, request);

        result.IsSuccess.Should().BeTrue();
        trust.Capabilities.Should().ContainSingle()
            .Which.Should().Be(ExecutionCapability.NativeParsing);
        launcher.StartCount.Should().Be(1);
        launcher.StartInfo.Should().NotBeNull();
        launcher.StartInfo!.UseShellExecute.Should().BeFalse();
        launcher.StartInfo.RedirectStandardInput.Should().BeTrue();
        launcher.StartInfo.RedirectStandardOutput.Should().BeTrue();
        launcher.StartInfo.RedirectStandardError.Should().BeTrue();
        launcher.StartInfo.FileName.Should().Be(_launch.FileName);
        launcher.StartInfo.WorkingDirectory.Should().Be(_root);
        launcher.StartInfo.ArgumentList.Should().Equal(
            _launch.PrefixArguments.Append(
                NativeWorkerEntrypoint.InvocationArgument));
        launcher.StartInfo.Environment.Keys.Should().NotContain(
            key => string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase));
        launcher.StartInfo.Environment.Keys.Should().NotContain(
            key => string.Equals(key, "HOME", StringComparison.OrdinalIgnoreCase));

        var writtenFrame = process.WrittenInput;
        var payload = NativeWorkerProtocol.RemoveSingleFrame(
            writtenFrame,
            NativeWorkerProtocol.MaximumRequestBytes);
        NativeWorkerProtocol.DecodeRequest(payload.Span)
            .Request.Should().BeEquivalentTo(request);
    }

    [Fact]
    public async Task ExtractAsync_roundTripsThroughRealWorkerProcess()
    {
        var serverDirectory = Path.GetDirectoryName(
            typeof(NativeWorkerClient).Assembly.Location)!;
        var serverAssembly = typeof(NativeWorkerClient).Assembly.Location;
        var executable = Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(),
            "..",
            "..",
            "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        File.Exists(executable).Should().BeTrue();
        var client = new NativeWorkerClient(
            new FakeTrustPolicy(allowed: true),
            new NativeWorkerClientOptions(TimeSpan.FromSeconds(10)),
            new NativeWorkerLaunchCommand(
                executable,
                [serverAssembly],
                serverDirectory),
            new SystemNativeWorkerProcessLauncher());

        var result = await client.ExtractAsync(_root, Request());

        if (!result.IsSuccess)
        {
            var standardError = result.Failure!.StandardError;
            var boundedStandardError = standardError is { Length: > 1000 }
                ? standardError[..1000]
                : standardError;
            result.Failure!.Code.Should().Be(
                "native-runtime-unavailable",
                boundedStandardError);
            result.Failure.ExitCode.Should().Be(3);
            return;
        }

        result.Extraction.Should().NotBeNull();
        if (result.Extraction!.IncludedFiles.Count == 0)
        {
            result.Extraction.Diagnostics.Should().Contain(
                diagnostic => diagnostic.Code == "CLANG0001");
        }
        else
        {
            result.Extraction.IncludedFiles.Should().Contain(_source);
        }
    }

    [Fact]
    public async Task ExtractAsync_doesNotStartProcessWhenTrustIsDenied()
    {
        var launcher = new FakeLauncher(
            FakeProcess.Exited(Array.Empty<byte>()));
        var trust = new FakeTrustPolicy(allowed: false);
        var client = Client(trust, launcher);

        var result = await client.ExtractAsync(_root, Request());

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be("trust-denied");
        launcher.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_evaluatesTrustBeforeRejectingMalformedRequest()
    {
        var launcher = new FakeLauncher(
            FakeProcess.Exited(Array.Empty<byte>()));
        var trust = new FakeTrustPolicy(allowed: true);
        var client = Client(trust, launcher);
        var request = Request() with
        {
            SourceFilePath = "relative.cpp",
        };

        var result = await client.ExtractAsync(_root, request);

        trust.Capabilities.Should().ContainSingle();
        result.Failure!.Code.Should().Be("invalid-request");
        launcher.StartCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExtractAsync_refusesUnmetRequiredIsolationBeforeLaunch(
        bool requireNetworkIsolation,
        bool requireReducedPrivilege)
    {
        var launcher = new FakeLauncher(
            FakeProcess.Exited(Array.Empty<byte>()));
        var trust = new FakeTrustPolicy(allowed: true);
        var options = new NativeWorkerClientOptions(
            TimeSpan.FromSeconds(1),
            new NativeWorkerIsolationRequirements(
                requireNetworkIsolation,
                requireReducedPrivilege));
        var client = new NativeWorkerClient(
            trust,
            options,
            _launch,
            launcher);

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.Code.Should().Be("isolation-unavailable");
        result.Isolation.NetworkIsolation.Should().BeFalse();
        result.Isolation.ReducedPrivilege.Should().BeFalse();
        launcher.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_timesOutAndKillsEntireProcessTree()
    {
        var process = FakeProcess.Running();
        var launcher = new FakeLauncher(process);
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            launcher,
            timeout: TimeSpan.FromMilliseconds(25));

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.Code.Should().Be("worker-timeout");
        process.KillCalls.Should().ContainSingle()
            .Which.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_timeoutAlsoBoundsAWorkerThatNeverReadsItsRequest()
    {
        var process = FakeProcess.RunningWithBlockedInput();
        var launcher = new FakeLauncher(process);
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            launcher,
            timeout: TimeSpan.FromMilliseconds(25));

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.Code.Should().Be("worker-timeout");
        process.KillCalls.Should().ContainSingle()
            .Which.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_cancellationKillsEntireProcessTreeAndPropagates()
    {
        var process = FakeProcess.Running();
        var launcher = new FakeLauncher(process);
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            launcher,
            timeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(25));

        var act = async () => await client.ExtractAsync(
            _root,
            Request(),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        process.KillCalls.Should().ContainSingle()
            .Which.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_rejectsMalformedWorkerOutput()
    {
        var process = FakeProcess.Exited(
            NativeWorkerProtocol.AddFrame(
                "{}"u8,
                NativeWorkerProtocol.MaximumResponseBytes));
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            new FakeLauncher(process));

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.Code.Should().Be("unsupported-response");
    }

    [Fact]
    public async Task ExtractAsync_rejectsOversizedWorkerOutputHeader()
    {
        var output = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            output,
            NativeWorkerProtocol.MaximumResponseBytes + 1);
        var process = FakeProcess.Exited(output);
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            new FakeLauncher(process));

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.Code.Should().Be("frame-too-large");
    }

    [Fact]
    public async Task ExtractAsync_rejectsSuccessfulPayloadWithFailingExitCode()
    {
        var process = FakeProcess.Exited(
            NativeWorkerProtocol.AddFrame(
                NativeWorkerProtocol.EncodeResponse(SuccessfulResponse()),
                NativeWorkerProtocol.MaximumResponseBytes),
            exitCode: 7);
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            new FakeLauncher(process));

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.Code.Should().Be("worker-exit-failed");
        result.Failure.ExitCode.Should().Be(7);
    }

    [Fact]
    public async Task ExtractAsync_rejectsFailurePayloadWithSuccessfulExitCode()
    {
        var response = new NativeWorkerResponseEnvelope(
            NativeWorkerProtocol.CurrentVersion,
            NativeWorkerProtocol.ResponseKind,
            Success: false,
            Result: null,
            new NativeWorkerFailure("worker-failed", "failed"),
            NativeWorkerIsolationCapabilities.Baseline);
        var process = FakeProcess.Exited(
            NativeWorkerProtocol.AddFrame(
                NativeWorkerProtocol.EncodeResponse(response),
                NativeWorkerProtocol.MaximumResponseBytes));
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            new FakeLauncher(process));

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.Code.Should().Be("worker-exit-mismatch");
        result.Failure.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_boundsAndDisclosesStandardError()
    {
        var error = Enumerable.Repeat((byte)'x',
            NativeWorkerProtocol.MaximumStandardErrorBytes + 100).ToArray();
        var process = FakeProcess.Exited(
            NativeWorkerProtocol.AddFrame(
                NativeWorkerProtocol.EncodeResponse(SuccessfulResponse()),
                NativeWorkerProtocol.MaximumResponseBytes),
            standardError: error,
            exitCode: 7);
        var client = Client(
            new FakeTrustPolicy(allowed: true),
            new FakeLauncher(process));

        var result = await client.ExtractAsync(_root, Request());

        result.Failure!.StandardErrorTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(result.Failure.StandardError!)
            .Should().Be(NativeWorkerProtocol.MaximumStandardErrorBytes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private NativeWorkerClient Client(
        FakeTrustPolicy trust,
        FakeLauncher launcher,
        TimeSpan? timeout = null) =>
        new(
            trust,
            new NativeWorkerClientOptions(
                timeout ?? TimeSpan.FromSeconds(1)),
            _launch,
            launcher);

    private ClangNativeExtractionRequest Request() =>
        new(
            _source,
            _root,
            ProducingFileId: 41,
            InteropTarget.WindowsX64Msvc,
            ["-x", "c++"],
            LibraryName: "medical.dll");

    private NativeWorkerResponseEnvelope SuccessfulResponse() =>
        new(
            NativeWorkerProtocol.CurrentVersion,
            NativeWorkerProtocol.ResponseKind,
            Success: true,
            new ClangNativeExtractionResult(
                Array.Empty<NativeFunctionFact>(),
                Array.Empty<NativeTypeDeclarationFact>(),
                Array.Empty<NativeExport>(),
                Array.Empty<AbiRecordLayout>(),
                Array.Empty<ClangExtractionDiagnostic>())
            {
                IncludedFiles = [_source],
            },
            Failure: null,
            NativeWorkerIsolationCapabilities.Baseline);

    private sealed class FakeTrustPolicy : IExecutionTrustPolicy
    {
        private readonly bool _allowed;

        public FakeTrustPolicy(bool allowed)
        {
            _allowed = allowed;
        }

        public List<ExecutionCapability> Capabilities { get; } = [];

        public ExecutionTrustDecision EvaluateRepositoryCapability(
            string repositoryRoot,
            ExecutionCapability capability)
        {
            Capabilities.Add(capability);
            return _allowed
                ? new ExecutionTrustDecision(true, ExecutionTrustReason.Allowed)
                : new ExecutionTrustDecision(
                    false,
                    ExecutionTrustReason.RepositoryNotTrusted);
        }

        public ExecutionTrustDecision EvaluatePathPluginCapability(
            string repositoryRoot,
            string entryAssemblyPath,
            ExecutionCapability capability,
            string? bundleRoot = null) =>
            throw new NotSupportedException();

        public ExecutionTrustDecision EvaluateNuGetPluginCapability(
            string repositoryRoot,
            string packageId,
            string exactVersion,
            ExecutionCapability capability) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLauncher : INativeWorkerProcessLauncher
    {
        private readonly FakeProcess _process;

        public FakeLauncher(FakeProcess process)
        {
            _process = process;
        }

        public int StartCount { get; private set; }
        public ProcessStartInfo? StartInfo { get; private set; }

        public INativeWorkerProcess? Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            StartInfo = startInfo;
            return _process;
        }
    }

    private sealed class FakeProcess : INativeWorkerProcess
    {
        private readonly Stream _input;
        private readonly MemoryStream _output;
        private readonly MemoryStream _error;
        private readonly TaskCompletionSource _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasExited;
        private int _exitCode;

        private FakeProcess(
            byte[] output,
            byte[] standardError,
            bool hasExited,
            int exitCode,
            Stream? input = null)
        {
            _input = input ?? new MemoryStream();
            _output = new MemoryStream(output, writable: false);
            _error = new MemoryStream(standardError, writable: false);
            _hasExited = hasExited;
            _exitCode = exitCode;
            if (hasExited)
            {
                _exit.SetResult();
            }
        }

        public static FakeProcess Exited(
            byte[] output,
            byte[]? standardError = null,
            int exitCode = 0) =>
            new(
                output,
                standardError ?? Array.Empty<byte>(),
                hasExited: true,
                exitCode);

        public static FakeProcess Running() =>
            new(
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                hasExited: false,
                exitCode: 0);

        public static FakeProcess RunningWithBlockedInput() =>
            new(
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                hasExited: false,
                exitCode: 0,
                new BlockingWriteStream());

        public Stream StandardInput => _input;
        public Stream StandardOutput => _output;
        public Stream StandardError => _error;
        public bool HasExited => _hasExited;
        public int ExitCode => _exitCode;
        public List<bool> KillCalls { get; } = [];
        public byte[] WrittenInput => ((MemoryStream)_input).ToArray();

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void Kill(bool entireProcessTree)
        {
            KillCalls.Add(entireProcessTree);
            _hasExited = true;
            _exitCode = -1;
            _exit.TrySetResult();
        }

        public void Dispose()
        {
            _input.Dispose();
            _output.Dispose();
            _error.Dispose();
        }
    }

    private sealed class BlockingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }
}
