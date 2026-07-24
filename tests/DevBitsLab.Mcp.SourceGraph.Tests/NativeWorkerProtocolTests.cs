using System.Buffers.Binary;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeWorkerProtocolTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;

    public NativeWorkerProtocolTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-native-worker-protocol-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "medical.cpp");
        File.WriteAllText(
            _source,
            """
            extern "C" __declspec(dllexport) int medical_api(int value);
            """);
    }

    [Fact]
    public void RequestCodec_roundTripsStrictVersionedEnvelope()
    {
        var request = Request();

        var payload = NativeWorkerProtocol.EncodeRequest(request);
        var decoded = NativeWorkerProtocol.DecodeRequest(payload);

        decoded.Version.Should().Be(NativeWorkerProtocol.CurrentVersion);
        decoded.Kind.Should().Be(NativeWorkerProtocol.RequestKind);
        decoded.Request.Should().BeEquivalentTo(request);
    }

    [Fact]
    public void RequestCodec_rejectsUnknownJsonMembers()
    {
        var json = Encoding.UTF8.GetString(
            NativeWorkerProtocol.EncodeRequest(Request()));
        var malformed = Encoding.UTF8.GetBytes(
            json.Replace(
                "\"version\":1",
                "\"unexpected\":true,\"version\":1",
                StringComparison.Ordinal));

        var act = () => NativeWorkerProtocol.DecodeRequest(malformed);

        act.Should().Throw<NativeWorkerProtocolException>()
            .Which.Code.Should().Be("malformed-request");
    }

    [Fact]
    public void RequestCodec_rejectsDuplicateJsonMembers()
    {
        var json = Encoding.UTF8.GetString(
            NativeWorkerProtocol.EncodeRequest(Request()));
        var malformed = Encoding.UTF8.GetBytes(
            json.Replace(
                "\"version\":1",
                "\"version\":1,\"version\":1",
                StringComparison.Ordinal));

        var act = () => NativeWorkerProtocol.DecodeRequest(malformed);

        act.Should().Throw<NativeWorkerProtocolException>()
            .Which.Code.Should().Be("duplicate-json-property");
    }

    [Fact]
    public void FrameCodec_rejectsOversizedDeclaredLengthBeforeAllocation()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(
            header,
            NativeWorkerProtocol.MaximumResponseBytes + 1);

        var act = () => NativeWorkerProtocol.RemoveSingleFrame(
            header,
            NativeWorkerProtocol.MaximumResponseBytes);

        act.Should().Throw<NativeWorkerProtocolException>()
            .Which.Code.Should().Be("frame-too-large");
    }

    [Fact]
    public void RequestCodec_stopsSerializationAtFixedByteLimit()
    {
        var request = Request() with
        {
            CompilerArguments = Enumerable.Repeat(
                new string('a', 1024),
                2048).ToArray(),
        };

        var act = () => NativeWorkerProtocol.EncodeRequest(request);

        act.Should().Throw<NativeWorkerProtocolException>()
            .Which.Code.Should().Be("request-too-large");
    }

    [Fact]
    public void ResponseCodec_rejectsEvidenceOutsideApprovedIncludeGraph()
    {
        var outside = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"outside-{Guid.NewGuid():N}.cpp");
        File.WriteAllText(outside, "int outside();");
        try
        {
            var request = Request();
            var invalid = EmptyExtraction() with
            {
                Functions =
                [
                    new NativeFunctionFact(
                        "cpp:function:outside",
                        "outside",
                        "outside",
                        InteropCallingConvention.Cdecl,
                        IntType(),
                        Array.Empty<AbiParameter>(),
                        HasCLinkage: true,
                        IsExported: false,
                        IsDefinition: false,
                        Evidence(outside)),
                ],
            };
            var response = new NativeWorkerResponseEnvelope(
                NativeWorkerProtocol.CurrentVersion,
                NativeWorkerProtocol.ResponseKind,
                Success: true,
                invalid,
                Failure: null,
                NativeWorkerIsolationCapabilities.Baseline);
            var payload = NativeWorkerProtocol.EncodeResponse(response);

            var act = () => NativeWorkerProtocol.DecodeResponse(payload, request);

            act.Should().Throw<NativeWorkerProtocolException>()
                .Which.Code.Should().Be("invalid-response");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task WorkerEntrypoint_roundTripsOneFramedClangRequest()
    {
        var request = Request();
        var input = new MemoryStream(
            NativeWorkerProtocol.AddFrame(
                NativeWorkerProtocol.EncodeRequest(request),
                NativeWorkerProtocol.MaximumRequestBytes));
        var output = new MemoryStream();
        var error = new StringWriter();

        var exitCode = await NativeWorkerEntrypoint.RunAsync(
            input,
            output,
            error,
            _ => EmptyExtraction());

        var payload = NativeWorkerProtocol.RemoveSingleFrame(
            output.ToArray(),
            NativeWorkerProtocol.MaximumResponseBytes);
        var response = NativeWorkerProtocol.DecodeResponse(payload.Span, request);
        exitCode.Should().Be(0, response.Failure?.Message ?? error.ToString());
        error.ToString().Should().BeEmpty();
        response.Success.Should().BeTrue();
        response.Result.Should().NotBeNull();
        response.Result!.IncludedFiles.Should().Equal(_source);
    }

    [Fact]
    public async Task WorkerEntrypoint_returnsStructuredFailureForMalformedInput()
    {
        var input = new MemoryStream(
            NativeWorkerProtocol.AddFrame(
                "{}"u8,
                NativeWorkerProtocol.MaximumRequestBytes));
        var output = new MemoryStream();

        var exitCode = await NativeWorkerEntrypoint.RunAsync(
            input,
            output,
            TextWriter.Null);

        exitCode.Should().Be(2);
        var payload = NativeWorkerProtocol.RemoveSingleFrame(
            output.ToArray(),
            NativeWorkerProtocol.MaximumResponseBytes);
        var response = NativeWorkerProtocol.DecodeResponse(
            payload.Span,
            Request());
        response.Success.Should().BeFalse();
        response.Failure!.Code.Should().Be("unsupported-request");
    }

    [Fact]
    public async Task WorkerEntrypoint_rejectsInvalidExtractorOutputBeforeSerialization()
    {
        var outside = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"worker-outside-{Guid.NewGuid():N}.cpp");
        File.WriteAllText(outside, "int outside();");
        try
        {
            var invalid = EmptyExtraction() with
            {
                Functions =
                [
                    new NativeFunctionFact(
                        "cpp:function:outside",
                        "outside",
                        "outside",
                        InteropCallingConvention.Cdecl,
                        IntType(),
                        Array.Empty<AbiParameter>(),
                        HasCLinkage: true,
                        IsExported: false,
                        IsDefinition: false,
                        Evidence(outside)),
                ],
            };
            var input = new MemoryStream(
                NativeWorkerProtocol.AddFrame(
                    NativeWorkerProtocol.EncodeRequest(Request()),
                    NativeWorkerProtocol.MaximumRequestBytes));
            var output = new MemoryStream();

            var exitCode = await NativeWorkerEntrypoint.RunAsync(
                input,
                output,
                TextWriter.Null,
                _ => invalid);

            exitCode.Should().Be(3);
            var payload = NativeWorkerProtocol.RemoveSingleFrame(
                output.ToArray(),
                NativeWorkerProtocol.MaximumResponseBytes);
            var response = NativeWorkerProtocol.DecodeResponse(
                payload.Span,
                Request());
            response.Failure!.Code.Should().Be("invalid-extraction-result");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task WorkerEntrypoint_rejectsOversizedInputHeader()
    {
        var inputBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(
            inputBytes,
            NativeWorkerProtocol.MaximumRequestBytes + 1);
        var output = new MemoryStream();

        var exitCode = await NativeWorkerEntrypoint.RunAsync(
            new MemoryStream(inputBytes),
            output,
            TextWriter.Null);

        exitCode.Should().Be(2);
        var payload = NativeWorkerProtocol.RemoveSingleFrame(
            output.ToArray(),
            NativeWorkerProtocol.MaximumResponseBytes);
        var response = NativeWorkerProtocol.DecodeResponse(
            payload.Span,
            Request());
        response.Failure!.Code.Should().Be("frame-too-large");
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

    private ClangNativeExtractionRequest Request() =>
        new(
            _source,
            _root,
            ProducingFileId: 41,
            InteropTarget.WindowsX64Msvc,
            [
                "-x",
                "c++",
                "-std=c++17",
                "--target=x86_64-pc-windows-msvc",
                "-fms-extensions",
                "-D_WIN32=1",
            ],
            LibraryName: "medical.dll");

    private ClangNativeExtractionResult EmptyExtraction() =>
        new(
            Array.Empty<NativeFunctionFact>(),
            Array.Empty<NativeTypeDeclarationFact>(),
            Array.Empty<NativeExport>(),
            Array.Empty<AbiRecordLayout>(),
            Array.Empty<ClangExtractionDiagnostic>())
        {
            IncludedFiles = [_source],
        };

    private Evidence Evidence(string path) =>
        new(
            ProducingFileId: 41,
            new SourceLocation(path, 1, 1, 1, 2),
            EvidenceConfidence.Exact,
            "clang-native");

    private static AbiTypeRef IntType() =>
        new(
            "int",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);
}
