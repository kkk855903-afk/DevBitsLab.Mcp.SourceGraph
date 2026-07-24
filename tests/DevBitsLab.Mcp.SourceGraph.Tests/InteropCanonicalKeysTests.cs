using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropCanonicalKeysTests
{
    [Fact]
    public void Native_helpers_freezeLanguageKindPathAndQualifiedIdentity()
    {
        NativeCanonicalKeys.ForFunction(
                "c",
                @".\src\native\device.c",
                "device_open")
            .Should().Be("c:F:src/native/device.c::device_open");
        NativeCanonicalKeys.ForMethod(
                "src//native/algorithm.cpp",
                "medical::Algorithm::Run(int,const char*)")
            .Should().Be(
                "cpp:F:src/native/algorithm.cpp::medical::Algorithm::Run(int,const char*)");
        NativeCanonicalKeys.ForType(
                "cpp",
                "src/native/types.hpp",
                "medical::ScanResult")
            .Should().Be("cpp:T:src/native/types.hpp::medical::ScanResult");
        NativeCanonicalKeys.ForTypeAlias(
                "c",
                "include/device.h",
                "device_handle")
            .Should().Be("c:A:include/device.h::device_handle");
        NativeCanonicalKeys.ForExport(
                "c",
                "src/native/exports.cpp",
                "scan_run")
            .Should().Be("c:E:src/native/exports.cpp::scan_run");
    }

    [Fact]
    public void Native_helper_outputs_areAcceptedByCanonicalKeyValidator()
    {
        var keys = new[]
        {
            NativeCanonicalKeys.ForFunction("c", "device.c", "device_open"),
            NativeCanonicalKeys.ForMethod("algorithm.cpp", "Algorithm::Run(int)"),
            NativeCanonicalKeys.ForType("cpp", "types.hpp", "ScanResult"),
            NativeCanonicalKeys.ForTypeAlias("c", "types.h", "scan_result"),
            NativeCanonicalKeys.ForExport("c", "exports.cpp", "scan_run"),
        };

        keys.Should().OnlyContain(key => CanonicalKeyValidator.IsValid(key));
    }

    [Theory]
    [InlineData("rust", "src/a.rs", "run")]
    [InlineData("C", "src/a.c", "run")]
    [InlineData("c", "/absolute/a.c", "run")]
    [InlineData("c", "C:/absolute/a.c", "run")]
    [InlineData("c", "../outside/a.c", "run")]
    [InlineData("c", "src/a.c", " ")]
    [InlineData("c", "src/a.c", @"bad\name")]
    public void Native_helpers_rejectUnstableOrUnknownComponents(
        string scheme,
        string path,
        string name)
    {
        var act = () => NativeCanonicalKeys.ForFunction(scheme, path, name);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Proto_helpers_freezeDescriptorBasedIdentities()
    {
        ProtoCanonicalKeys.ForMessage(".medical.v1.ScanRequest")
            .Should().Be("proto:M:medical.v1.ScanRequest");
        ProtoCanonicalKeys.ForRpc(".medical.v1.Scanner", "StartScan")
            .Should().Be("proto:R:medical.v1.Scanner.StartScan");
        ProtoCanonicalKeys.ForField(
                ".medical.v1.ScanRequest",
                "patient_position")
            .Should().Be(
                "proto:F:medical.v1.ScanRequest.patient_position");
    }

    [Fact]
    public void Proto_helper_outputs_areAcceptedByCanonicalKeyValidator()
    {
        var keys = new[]
        {
            ProtoCanonicalKeys.ForMessage("medical.v1.ScanRequest"),
            ProtoCanonicalKeys.ForRpc("medical.v1.Scanner", "StartScan"),
            ProtoCanonicalKeys.ForField("medical.v1.ScanRequest", "patient_position"),
        };

        keys.Should().OnlyContain(key => CanonicalKeyValidator.IsValid(key));
    }

    [Theory]
    [InlineData("", "StartScan")]
    [InlineData(".", "StartScan")]
    [InlineData("medical..Scanner", "StartScan")]
    [InlineData("medical.v1.Scanner", "bad-name")]
    [InlineData("medical/v1/Scanner", "StartScan")]
    [InlineData("medical.v1.Scanner", "扫描")]
    public void Proto_rpc_rejectsInvalidDescriptorNames(
        string serviceFullName,
        string rpcName)
    {
        var act = () => ProtoCanonicalKeys.ForRpc(serviceFullName, rpcName);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void InteropVocabulary_constants_areKebabCaseAndStable()
    {
        var symbolKinds = new[]
        {
            SymbolKinds.Function,
            SymbolKinds.TypeAlias,
            SymbolKinds.NativeExport,
            SymbolKinds.Rpc,
            SymbolKinds.Message,
            SymbolKinds.ProtoField,
        };
        var edgeKinds = new[]
        {
            EdgeKinds.References,
            EdgeKinds.Reads,
            EdgeKinds.Writes,
            EdgeKinds.BindsTo,
            EdgeKinds.HandlesEvent,
            EdgeKinds.GrpcCalls,
            EdgeKinds.ImplementsRpc,
            EdgeKinds.RpcDispatchesTo,
            EdgeKinds.PInvokeMapsTo,
            EdgeKinds.StructMapsTo,
        };

        symbolKinds.Should().Equal(
            "function",
            "type-alias",
            "native-export",
            "rpc",
            "message",
            "proto-field");
        edgeKinds.Should().Equal(
            "references",
            "reads",
            "writes",
            "binds-to",
            "handles-event",
            "grpc-calls",
            "implements-rpc",
            "rpc-dispatches-to",
            "pinvoke-maps-to",
            "struct-maps-to");
        symbolKinds.Concat(edgeKinds)
            .Should().OnlyContain(kind => KebabCaseValidator.IsValid(kind));
    }
}
