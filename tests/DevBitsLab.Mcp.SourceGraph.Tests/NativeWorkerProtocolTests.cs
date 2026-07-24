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
                $"\"version\":{NativeWorkerProtocol.CurrentVersion}",
                $"\"unexpected\":true,\"version\":{NativeWorkerProtocol.CurrentVersion}",
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
                $"\"version\":{NativeWorkerProtocol.CurrentVersion}",
                $"\"version\":{NativeWorkerProtocol.CurrentVersion},"
                    + $"\"version\":{NativeWorkerProtocol.CurrentVersion}",
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

    [Fact]
    public void ResponseCodec_roundTrips_strict_direct_call_identity()
    {
        var caller = FunctionFact(
            "cpp:F:medical.cpp::caller()",
            "c:@F@caller#",
            "caller");
        var callee = FunctionFact(
            "cpp:F:medical.cpp::callee()",
            "c:@F@callee#",
            "callee");
        var call = new NativeCallFact(
            caller.GraphCanonicalKey,
            callee.DeclarationUsr,
            callee.GraphCanonicalKey,
            InteropTarget.WindowsX64Msvc,
            new Evidence(
                41,
                new SourceLocation(_source, 1, 1, 1, 2),
                EvidenceConfidence.Exact,
                "clang-native-call"));
        var extraction = EmptyExtraction() with
        {
            Functions = [caller, callee],
            Calls = [call],
        };
        var response = new NativeWorkerResponseEnvelope(
            NativeWorkerProtocol.CurrentVersion,
            NativeWorkerProtocol.ResponseKind,
            Success: true,
            extraction,
            Failure: null,
            NativeWorkerIsolationCapabilities.Baseline);

        var payload = NativeWorkerProtocol.EncodeWorkerResponse(
            response,
            Request());
        var decoded = NativeWorkerProtocol.DecodeResponse(
            payload,
            Request());

        decoded.Result!.Calls.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(call);
    }

    [Fact]
    public void ResponseCodec_roundTrips_exact_native_risk_facts()
    {
        var export = RiskExport();
        var extraction = EmptyExtraction() with
        {
            Exports = [export],
        };
        var response = new NativeWorkerResponseEnvelope(
            NativeWorkerProtocol.CurrentVersion,
            NativeWorkerProtocol.ResponseKind,
            Success: true,
            extraction,
            Failure: null,
            NativeWorkerIsolationCapabilities.Baseline);

        var payload = NativeWorkerProtocol.EncodeWorkerResponse(
            response,
            Request());
        var decoded = NativeWorkerProtocol.DecodeResponse(
            payload,
            Request());

        decoded.Result!.Exports.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(export);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("parameter-kind")]
    [InlineData("target")]
    [InlineData("allocator")]
    [InlineData("evidence")]
    [InlineData("duplicate-parameter-position")]
    public void ResponseCodec_rejects_malformed_native_risk_facts(
        string malformedKind)
    {
        var export = RiskExport();
        export = malformedKind switch
        {
            "position" => export with
            {
                RetainedCallbacks =
                [
                    export.RetainedCallbacks[0] with
                    {
                        ParameterPosition = 1,
                    },
                ],
            },
            "parameter-kind" => export with
            {
                Parameters =
                [
                    export.Parameters[0] with { Type = IntType() },
                ],
            },
            "target" => export with
            {
                ExceptionEscape = export.ExceptionEscape! with
                {
                    Target = InteropTarget.WindowsX86Msvc,
                },
            },
            "allocator" => export with
            {
                ReturnAllocation = export.ReturnAllocation! with
                {
                    AllocatorFamily = InteropAllocatorFamily.Unknown,
                },
            },
            "evidence" => export with
            {
                RetainedCallbacks =
                [
                    export.RetainedCallbacks[0] with
                    {
                        Evidence = export.RetainedCallbacks[0].Evidence
                            with
                            {
                                Confidence =
                                    EvidenceConfidence.Semantic,
                            },
                    },
                ],
            },
            "duplicate-parameter-position" => export with
            {
                Parameters =
                [
                    export.Parameters[0],
                    new AbiParameter(
                        0,
                        "value",
                        IntType(),
                        AbiParameterDirection.In,
                        new SourceLocation(
                            _source,
                            1,
                            1,
                            1,
                            2)),
                ],
            },
            _ => throw new InvalidOperationException(),
        };
        var response = new NativeWorkerResponseEnvelope(
            NativeWorkerProtocol.CurrentVersion,
            NativeWorkerProtocol.ResponseKind,
            Success: true,
            EmptyExtraction() with { Exports = [export] },
            Failure: null,
            NativeWorkerIsolationCapabilities.Baseline);

        var act = () => NativeWorkerProtocol.EncodeWorkerResponse(
            response,
            Request());

        act.Should().Throw<NativeWorkerProtocolException>()
            .Which.Code.Should().Be("invalid-response");
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

    private NativeExport RiskExport()
    {
        var callback = new AbiParameter(
            0,
            "callback",
            FunctionPointerType(),
            AbiParameterDirection.In,
            new SourceLocation(_source, 1, 1, 1, 2));
        return new NativeExport(
            "c:E:medical.cpp::risk",
            "risk",
            InteropCallingConvention.Cdecl,
            IntType(),
            [callback],
            HasCLinkage: true,
            IsBinaryVerified: false,
            InteropTarget.WindowsX64Msvc,
            Evidence(_source))
        {
            LibraryName = "medical.dll",
            ModuleIdentitySource =
                NativeModuleIdentitySource.Configuration,
            RetainedCallbacks =
            [
                new NativeCallbackRetention(
                    0,
                    InteropTarget.WindowsX64Msvc,
                    NativeRiskEvidence(
                        "clang-native-retention",
                        "parameterPosition",
                        "0")),
            ],
            ExceptionEscape = new NativeExceptionEscape(
                InteropTarget.WindowsX64Msvc,
                NativeRiskEvidence(
                    "clang-native-exception",
                    "escapeKind",
                    "direct-throw")),
            ReturnAllocation = new NativeReturnAllocation(
                InteropAllocatorFamily.CrtHeap,
                InteropTarget.WindowsX64Msvc,
                NativeRiskEvidence(
                    "clang-native-allocation",
                    "allocatorFamily",
                    "crt_heap",
                    ("allocator", "malloc"))),
        };
    }

    private Evidence NativeRiskEvidence(
        string producer,
        string factKey,
        string factValue,
        params (string Key, string Value)[] extraMetadata)
    {
        var metadata = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["target"] =
                InteropTarget.WindowsX64Msvc.RuntimeIdentifier,
            [factKey] = factValue,
        };
        foreach (var item in extraMetadata)
        {
            metadata.Add(item.Key, item.Value);
        }
        return new Evidence(
            ProducingFileId: 41,
            new SourceLocation(_source, 1, 1, 1, 2),
            EvidenceConfidence.Exact,
            producer,
            metadata);
    }

    private NativeFunctionFact FunctionFact(
        string key,
        string usr,
        string name) =>
        new(
            key,
            name,
            name,
            InteropCallingConvention.Cdecl,
            IntType(),
            [],
            HasCLinkage: false,
            IsExported: false,
            IsDefinition: true,
            Evidence(_source))
        {
            DeclarationUsr = usr,
            GraphCanonicalKey = key,
            Target = InteropTarget.WindowsX64Msvc,
        };

    private static AbiTypeRef IntType() =>
        new(
            "int",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static AbiTypeRef FunctionPointerType() =>
        new(
            "void (*)(int)",
            AbiTypeCategory.FunctionPointer,
            pointerDepth: 1,
            sizeBytes: 8,
            alignmentBytes: 8);
}
