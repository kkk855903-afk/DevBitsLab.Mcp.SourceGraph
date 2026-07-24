using System.Text.Json;
using System.Text.Json.Nodes;
using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropFactPayloadCodecTests
{
    [Fact]
    public void ManagedImport_roundTripsRecursiveTypesAndRebindsEvidenceOwner()
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 999, reverseMetadata: true));

        var decoded = InteropFactPayloadCodec.DecodeManagedImport(
            json,
            ownerFileId: 42);

        decoded.Should().BeEquivalentTo(
            CreateManagedImport(ownerFileId: 42, reverseMetadata: false));
        decoded.ExactSpelling.Should().BeFalse();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("kind").GetString().Should().Be("managed_import");
        root.GetProperty("import_kind").GetString().Should().Be("dll_import");
        root.GetProperty("calling_convention").GetString().Should().Be("cdecl");
        root.GetProperty("return_type")
            .GetProperty("pointee_type")
            .GetProperty("category")
            .GetString()
            .Should()
            .Be("record");
        root.GetProperty("parameters")[1]
            .GetProperty("type")
            .GetProperty("element_type")
            .GetProperty("category")
            .GetString()
            .Should()
            .Be("signed_integer");
        json.Should().NotContain("producing_file_id");
        json.Should().NotContain("ProducingFileId");
    }

    [Fact]
    public void NativeExport_roundTripsArtifactAndProofFacts()
    {
        var json = InteropFactPayloadCodec.EncodeNativeExport(
            CreateNativeExport(ownerFileId: 301));

        var decoded = InteropFactPayloadCodec.DecodeNativeExport(
            json,
            ownerFileId: 77);

        decoded.Should().BeEquivalentTo(CreateNativeExport(ownerFileId: 77));
        decoded.IsBinaryVerified.Should().BeTrue();
        decoded.ModuleIdentitySource.Should().Be(NativeModuleIdentitySource.Binary);
        decoded.RetainedCallbacks.Should().ContainSingle();
        decoded.ExceptionEscape.Should().NotBeNull();
        decoded.ReturnAllocation!.AllocatorFamily
            .Should()
            .Be(InteropAllocatorFamily.CrtHeap);
        AllEvidence(decoded).Should().OnlyContain(evidence =>
            evidence.ProducingFileId == 77);
        json.Should().NotContain("producing_file_id");
    }

    [Fact]
    public void ManagedImport_roundTripsUnknownExactSpelling()
    {
        var import = CreateManagedImport(ownerFileId: 1) with
        {
            ExactSpelling = null,
        };

        var json = InteropFactPayloadCodec.EncodeManagedImport(import);
        var decoded = InteropFactPayloadCodec.DecodeManagedImport(json, 2);

        decoded.ExactSpelling.Should().BeNull();
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("exact_spelling").ValueKind
            .Should()
            .Be(JsonValueKind.Null);
    }

    [Fact]
    public void AbiRecord_roundTripsTargetFieldsLocationsAndNullableLayout()
    {
        var json = InteropFactPayloadCodec.EncodeAbiRecord(
            CreateAbiRecord(ownerFileId: 8));

        var decoded = InteropFactPayloadCodec.DecodeAbiRecord(
            json,
            ownerFileId: 91);

        decoded.Should().BeEquivalentTo(CreateAbiRecord(ownerFileId: 91));
        decoded.Fields[1].OffsetBytes.Should().BeNull();
        decoded.Target.Should().BeEquivalentTo(InteropTarget.WindowsX64Msvc);
        decoded.Fields.Select(field => field.Evidence.ProducingFileId)
            .Should()
            .OnlyContain(fileId => fileId == 91);
    }

    [Fact]
    public void Encoding_isDeterministicAndSortsEvidenceMetadata()
    {
        var first = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1, reverseMetadata: true));
        var second = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 500, reverseMetadata: false));

        first.Should().Be(second);
        first.Should().StartWith(
            "{\"version\":1,\"kind\":\"managed_import\","
            + "\"symbol_canonical_key\":");
        first.IndexOf("\"alpha\":\"first\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(first.IndexOf("\"zeta\":\"last\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Version", "2")]
    [InlineData("Kind", "\"native_export\"")]
    [InlineData("CallingConvention", "\"CDECL\"")]
    public void Decode_rejectsUnknownVersionKindAndEnum(
        string mutation,
        string replacement)
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1));
        json = mutation switch
        {
            "Version" => json.Replace(
                "\"version\":1",
                $"\"version\":{replacement}",
                StringComparison.Ordinal),
            "Kind" => json.Replace(
                "\"kind\":\"managed_import\"",
                $"\"kind\":{replacement}",
                StringComparison.Ordinal),
            "CallingConvention" => json.Replace(
                "\"calling_convention\":\"cdecl\"",
                $"\"calling_convention\":{replacement}",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        var act = () => InteropFactPayloadCodec.DecodeManagedImport(json, 1);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Decode_rejectsUnknownRootAndNestedProperties()
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1));
        var rootUnknown = json.Replace(
            "\"version\":1",
            "\"version\":1,\"unexpected\":true",
            StringComparison.Ordinal);
        var nestedUnknown = json.Replace(
            "\"canonical_name\":\"const Sample*\"",
            "\"canonical_name\":\"const Sample*\",\"unexpected\":true",
            StringComparison.Ordinal);

        var rootAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(rootUnknown, 1);
        var nestedAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(nestedUnknown, 1);

        rootAct.Should().Throw<InteropFactPayloadException>();
        nestedAct.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Decode_rejectsDuplicateRootAndNestedProperties()
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1));
        var rootDuplicate = json.Replace(
            "\"version\":1",
            "\"version\":1,\"version\":1",
            StringComparison.Ordinal);
        var nestedDuplicate = json.Replace(
            "\"canonical_name\":\"const Sample*\"",
            "\"canonical_name\":\"const Sample*\","
            + "\"canonical_name\":\"const Sample*\"",
            StringComparison.Ordinal);

        var rootAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(rootDuplicate, 1);
        var nestedAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(nestedDuplicate, 1);

        rootAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*duplicate*");
        nestedAct.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*duplicate*");
    }

    [Fact]
    public void Decode_rejectsMissingAndWronglyTypedProperties()
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1));
        var missing = Mutate(json, root => root.Remove("set_last_error"));
        var wrongType = Mutate(
            json,
            root => root["set_last_error"] = "false");

        var missingAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(missing, 1);
        var wrongTypeAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(wrongType, 1);

        missingAct.Should().Throw<InteropFactPayloadException>();
        wrongTypeAct.Should().Throw<InteropFactPayloadException>();
    }

    [Theory]
    [InlineData("pointer_depth", "33")]
    [InlineData("size_bytes", "-1")]
    [InlineData("size_bytes", "2147483648")]
    [InlineData("size_bytes", "1e999")]
    public void Decode_rejectsOutOfRangeAbiNumbers(
        string property,
        string replacement)
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1));
        var token = property == "pointer_depth"
            ? "\"pointer_depth\":1"
            : "\"size_bytes\":8";
        json = json.Replace(
            token,
            $"\"{property}\":{replacement}",
            StringComparison.Ordinal);

        var act = () => InteropFactPayloadCodec.DecodeManagedImport(json, 1);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Decode_rejectsInvalidLocationAndTargetDimensions()
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1));
        var invalidLocation = json.Replace(
            "\"start_line\":4",
            "\"start_line\":0",
            StringComparison.Ordinal);
        var invalidTarget = json.Replace(
            "\"pointer_size_bytes\":8",
            "\"pointer_size_bytes\":4",
            StringComparison.Ordinal);

        var locationAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(invalidLocation, 1);
        var targetAct = () =>
            InteropFactPayloadCodec.DecodeManagedImport(invalidTarget, 1);

        locationAct.Should().Throw<InteropFactPayloadException>();
        targetAct.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Decode_rejectsNonFiniteJsonNumber()
    {
        var json = InteropFactPayloadCodec.EncodeAbiRecord(
            CreateAbiRecord(ownerFileId: 1));
        json = json.Replace(
            "\"size_bytes\":16",
            "\"size_bytes\":NaN",
            StringComparison.Ordinal);

        var act = () => InteropFactPayloadCodec.DecodeAbiRecord(json, 1);

        act.Should().Throw<InteropFactPayloadException>();
    }

    [Fact]
    public void Decode_rejectsOversizedPayloadBeforeParsing()
    {
        var json = new string(
            'x',
            InteropFactPayloadCodec.MaximumPayloadBytes + 1);

        var act = () => InteropFactPayloadCodec.DecodeManagedImport(json, 1);

        act.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*byte limit*");
    }

    [Fact]
    public void Decode_rejectsOversizedCollections()
    {
        var json = InteropFactPayloadCodec.EncodeNativeExport(
            CreateNativeExport(ownerFileId: 1));
        var mutated = Mutate(
            json,
            root =>
            {
                var callbacks = new JsonArray();
                for (var index = 0; index < 4097; index++)
                {
                    callbacks.Add(null);
                }
                root["retained_callbacks"] = callbacks;
            });

        var act = () => InteropFactPayloadCodec.DecodeNativeExport(mutated, 1);

        act.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*item limit*");
    }

    [Fact]
    public void Encode_rejectsTypesBeyondRecursionLimit()
    {
        var type = ScalarType;
        for (var depth = 0; depth < 34; depth++)
        {
            type = new AbiTypeRef(
                $"level-{depth}*",
                AbiTypeCategory.Pointer,
                pointerDepth: 1,
                sizeBytes: 8,
                alignmentBytes: 8,
                pointeeType: type);
        }
        var import = CreateManagedImport(ownerFileId: 1) with
        {
            ReturnType = type,
        };

        var act = () => InteropFactPayloadCodec.EncodeManagedImport(import);

        act.Should().Throw<InteropFactPayloadException>()
            .WithMessage("*recursion limit*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Decode_requiresPositiveAnnotationOwner(long ownerFileId)
    {
        var json = InteropFactPayloadCodec.EncodeManagedImport(
            CreateManagedImport(ownerFileId: 1));

        var act = () =>
            InteropFactPayloadCodec.DecodeManagedImport(json, ownerFileId);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Decode_rejectsCrossFlavorPayload()
    {
        var json = InteropFactPayloadCodec.EncodeNativeExport(
            CreateNativeExport(ownerFileId: 1));

        var act = () => InteropFactPayloadCodec.DecodeManagedImport(json, 1);

        act.Should().Throw<InteropFactPayloadException>();
    }

    private static ManagedImport CreateManagedImport(
        long ownerFileId,
        bool reverseMetadata = false)
    {
        var pointer = new AbiTypeRef(
            "const Sample*",
            AbiTypeCategory.Pointer,
            pointerDepth: 1,
            sizeBytes: 8,
            alignmentBytes: 8,
            pointeeType: new AbiTypeRef("Sample", AbiTypeCategory.Record),
            isPointeeConst: true);
        var array = new AbiTypeRef(
            "int32[4]",
            AbiTypeCategory.Array,
            sizeBytes: 16,
            alignmentBytes: 4,
            fixedArrayLength: 4,
            elementType: ScalarType);

        return new ManagedImport(
            "csharp:M:Example.NativeMethods.Run",
            ManagedImportKind.DllImport,
            "medalgo.dll",
            "run",
            InteropCallingConvention.Cdecl,
            pointer,
            [
                new AbiParameter(
                    0,
                    "value",
                    pointer,
                    AbiParameterDirection.In,
                    Location("managed.cs", line: 8)),
                new AbiParameter(
                    1,
                    "items",
                    array,
                    AbiParameterDirection.InOut,
                    Location("managed.cs", line: 9)),
            ],
            CharacterSet: "utf-16",
            SetLastError: true,
            InteropTarget.WindowsX64Msvc,
            EvidenceFor(
                ownerFileId,
                "managed.cs",
                reverseMetadata,
                producer: "managed-interop"))
        {
            ExactSpelling = false,
        };
    }

    private static NativeExport CreateNativeExport(long ownerFileId)
    {
        var callback = new AbiTypeRef(
            "callback_t",
            AbiTypeCategory.FunctionPointer,
            pointerDepth: 1,
            sizeBytes: 8,
            alignmentBytes: 8);
        return new NativeExport(
            "cpp:function:run",
            "run",
            InteropCallingConvention.Cdecl,
            ScalarType,
            [
                new AbiParameter(
                    0,
                    "callback",
                    callback,
                    AbiParameterDirection.In,
                    Location("native.h", line: 11)),
            ],
            HasCLinkage: true,
            IsBinaryVerified: true,
            InteropTarget.WindowsX64Msvc,
            EvidenceFor(ownerFileId, "native.h", producer: "clang-interop"))
        {
            LibraryName = "medalgo.dll",
            ModuleIdentitySource = NativeModuleIdentitySource.Binary,
            RetainedCallbacks =
            [
                new NativeCallbackRetention(
                    0,
                    InteropTarget.WindowsX64Msvc,
                    EvidenceFor(
                        ownerFileId,
                        "native.cpp",
                        producer: "native-dataflow")),
            ],
            ExceptionEscape = new NativeExceptionEscape(
                InteropTarget.WindowsX64Msvc,
                EvidenceFor(
                    ownerFileId,
                    "native.cpp",
                    producer: "native-exception-flow")),
            ReturnAllocation = new NativeReturnAllocation(
                InteropAllocatorFamily.CrtHeap,
                InteropTarget.WindowsX64Msvc,
                EvidenceFor(
                    ownerFileId,
                    "native.cpp",
                    producer: "native-ownership")),
        };
    }

    private static AbiRecordLayout CreateAbiRecord(long ownerFileId) =>
        new(
            "record:Sample",
            AbiRecordKind.Sequential,
            SizeBytes: 16,
            AlignmentBytes: 4,
            Pack: 4,
            Fields:
            [
                new AbiFieldLayout(
                    0,
                    "value",
                    ScalarType,
                    OffsetBytes: 0,
                    SizeBytes: 4,
                    EvidenceFor(
                        ownerFileId,
                        "sample.h",
                        producer: "clang-record-layout")),
                new AbiFieldLayout(
                    1,
                    "opaque",
                    new AbiTypeRef("opaque", AbiTypeCategory.Opaque),
                    OffsetBytes: null,
                    SizeBytes: null,
                    EvidenceFor(
                        ownerFileId,
                        "sample.h",
                        producer: "clang-record-layout")),
            ],
            InteropTarget.WindowsX64Msvc,
            EvidenceFor(
                ownerFileId,
                "sample.h",
                producer: "clang-record-layout"));

    private static AbiTypeRef ScalarType { get; } =
        new(
            "int32",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static SourceLocation Location(string path, int line) =>
        new(path, line, 3, line, 12);

    private static Evidence EvidenceFor(
        long ownerFileId,
        string path,
        bool reverseMetadata = false,
        string producer = "interop-test")
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reverseMetadata)
        {
            metadata.Add("zeta", "last");
            metadata.Add("alpha", "first");
        }
        else
        {
            metadata.Add("alpha", "first");
            metadata.Add("zeta", "last");
        }
        return new Evidence(
            ownerFileId,
            Location(path, line: 4),
            EvidenceConfidence.Exact,
            producer,
            metadata);
    }

    private static IReadOnlyList<Evidence> AllEvidence(NativeExport export)
    {
        var evidence = new List<Evidence> { export.Evidence };
        evidence.AddRange(export.RetainedCallbacks.Select(item => item.Evidence));
        if (export.ExceptionEscape is not null)
        {
            evidence.Add(export.ExceptionEscape.Evidence);
        }
        if (export.ReturnAllocation is not null)
        {
            evidence.Add(export.ReturnAllocation.Evidence);
        }
        return evidence;
    }

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }
}
