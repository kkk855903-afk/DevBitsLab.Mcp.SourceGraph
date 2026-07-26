using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class AbiStructCompatibilityEngineTests
{
    private readonly AbiStructCompatibilityEngine _engine = new();

    [Fact]
    public void FullyKnownEquivalentLayouts_areCompatibleWithPerCheckEvidence()
    {
        var managed = PacketLayout(AbiRecordKind.Sequential, native: false);
        var native = PacketLayout(AbiRecordKind.Native, native: true);

        var result = _engine.Compare(managed, native);

        result.Compatibility.Should().Be(InteropCompatibility.Compatible);
        result.Differences.Should().BeEmpty();
        result.Confidence.Should().Be(EvidenceConfidence.Semantic);
        result.Checks.Should().NotBeEmpty();
        result.Checks.Should().OnlyContain(check =>
            check.Compatibility == InteropCompatibility.Compatible
            && check.Evidence.Count > 0
            && !string.IsNullOrWhiteSpace(check.Reason));
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.FixedArrayLength);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.BooleanSize);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.PointerSize);
    }

    [Fact]
    public void CrossLanguageFieldNames_doNotAffectAbiCompatibility()
    {
        var managed = Layout(
            "managed:Names",
            AbiRecordKind.Sequential,
            16,
            8,
            8,
            [
                Field(0, "PatientAge", Int32(), 0, 4, native: false),
                Field(1, "Scale", Float64(), 8, 8, native: false),
            ],
            native: false);
        var native = Layout(
            "native:Names",
            AbiRecordKind.Native,
            16,
            8,
            8,
            [
                Field(0, "patient_age", Int32(), 0, 4, native: true),
                Field(1, "scale", Float64(), 8, 8, native: true),
            ],
            native: true);

        var result = _engine.Compare(managed, native);

        result.Compatibility.Should().Be(InteropCompatibility.Compatible);
        result.Checks.Count(check =>
                check.Aspect == AbiCompatibilityAspect.FieldOrder)
            .Should().Be(2);
    }

    [Fact]
    public void ProvenPackOffsetSizeArrayBooleanAndPointerDifferences_areErrors()
    {
        var managed = PacketLayout(AbiRecordKind.Sequential, native: false);
        var native = PacketLayout(
            AbiRecordKind.Native,
            native: true,
            pack: 1,
            size: 32,
            flagSize: 4,
            arrayLength: 4,
            pointerSize: 4,
            countOffset: 1);

        var result = _engine.Compare(managed, native);

        result.Compatibility.Should().Be(InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.Pack
            && check.Compatibility == InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.FieldOffset
            && check.Compatibility == InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.FieldSize
            && check.Compatibility == InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.FixedArrayLength
            && check.Compatibility == InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.BooleanSize
            && check.Compatibility == InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.PointerSize
            && check.Compatibility == InteropCompatibility.Error);
    }

    [Fact]
    public void FieldsReorderedAcrossOrdinalPositions_areErrors()
    {
        var int32 = Int32();
        var floating = new AbiTypeRef(
            "float",
            AbiTypeCategory.FloatingPoint,
            sizeBytes: 4,
            alignmentBytes: 4);
        var managed = Layout(
            "managed:Order",
            AbiRecordKind.Sequential,
            8,
            4,
            8,
            [
                Field(0, "First", int32, 0, 4, native: false),
                Field(1, "Second", floating, 4, 4, native: false),
            ],
            native: false);
        var native = Layout(
            "native:Order",
            AbiRecordKind.Native,
            8,
            4,
            8,
            [
                Field(0, "Second", floating, 0, 4, native: true),
                Field(1, "First", int32, 4, 4, native: true),
            ],
            native: true);

        var result = _engine.Compare(managed, native);

        result.Compatibility.Should().Be(InteropCompatibility.Error);
        result.Checks.Count(check =>
                check.Aspect == AbiCompatibilityAspect.FieldOrder)
            .Should().Be(2);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.FieldOrder
            && check.Compatibility == InteropCompatibility.Compatible);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.FieldCategory
            && check.Compatibility == InteropCompatibility.Error);
    }

    [Fact]
    public void UnknownDimensionsAndOpaqueTypes_areWarningsNotCompatibility()
    {
        var managed = Layout(
            "managed:Unknown",
            AbiRecordKind.Sequential,
            size: null,
            alignment: null,
            pack: null,
            [
                Field(
                    0,
                    "Value",
                    new AbiTypeRef("ManagedOpaque", AbiTypeCategory.Opaque),
                    offset: null,
                    size: null,
                    native: false),
            ],
            native: false);
        var native = Layout(
            "native:Unknown",
            AbiRecordKind.Native,
            size: null,
            alignment: null,
            pack: null,
            [
                Field(
                    0,
                    "Value",
                    new AbiTypeRef("NativeOpaque", AbiTypeCategory.Opaque),
                    offset: null,
                    size: null,
                    native: true),
            ],
            native: true);

        var result = _engine.Compare(managed, native);

        result.Compatibility.Should().Be(InteropCompatibility.Warning);
        result.Checks.Should().NotContain(check =>
            check.Compatibility == InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.RecordSize
            && check.Compatibility == InteropCompatibility.Warning
            && check.Confidence == EvidenceConfidence.Inferred);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.FieldCategory
            && check.Compatibility == InteropCompatibility.Warning);
    }

    [Fact]
    public void Missing_record_pack_uses_configured_target_default()
    {
        var managed = PacketLayout(
            AbiRecordKind.Sequential,
            native: false,
            pack: null);
        var native = PacketLayout(
            AbiRecordKind.Native,
            native: true,
            pack: null);

        var result = _engine.Compare(managed, native);

        result.Checks.Should().ContainSingle(check =>
            check.Aspect == AbiCompatibilityAspect.Pack
            && check.Compatibility == InteropCompatibility.Compatible
            && check.Reason.Contains("effective pack 8")
            && check.Reason.Contains("target default"));
    }

    [Fact]
    public void DifferentTargets_stopBeforeLayoutFactsAreCompared()
    {
        var managed = PacketLayout(AbiRecordKind.Sequential, native: false);
        var native = PacketLayout(
            AbiRecordKind.Native,
            native: true,
            target: InteropTarget.WindowsX86Msvc,
            pack: 1,
            size: 4);

        var result = _engine.Compare(managed, native);

        result.Compatibility.Should().Be(InteropCompatibility.Warning);
        result.Checks.Should().ContainSingle();
        result.Checks[0].Aspect.Should().Be(AbiCompatibilityAspect.Target);
        result.Checks[0].Compatibility.Should().Be(InteropCompatibility.Warning);
    }

    [Fact]
    public void ExactNestedIdentityMapping_comparesNestedLayout()
    {
        var (managed, native, mapping) = NestedPair(nativeChildOffset: 0);

        var result = _engine.Compare(managed, native, [mapping]);

        result.Compatibility.Should().Be(InteropCompatibility.Compatible);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.NestedRecordIdentity
            && check.Compatibility == InteropCompatibility.Compatible);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.NestedRecordLayout
            && check.Compatibility == InteropCompatibility.Compatible);
    }

    [Fact]
    public void NestedLayoutMismatch_propagatesAsError()
    {
        var (managed, native, mapping) = NestedPair(nativeChildOffset: 4);

        var result = _engine.Compare(managed, native, [mapping]);

        result.Compatibility.Should().Be(InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Path.EndsWith(".record.field[0]", StringComparison.Ordinal)
            && check.Aspect == AbiCompatibilityAspect.FieldOffset
            && check.Compatibility == InteropCompatibility.Error);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.NestedRecordLayout
            && check.Compatibility == InteropCompatibility.Error);
    }

    [Fact]
    public void MissingNestedIdentityMapping_isWarningAndConflictingMappingIsError()
    {
        var (managed, native, mapping) = NestedPair(nativeChildOffset: 0);

        var missing = _engine.Compare(managed, native);
        var conflicting = _engine.Compare(
            managed,
            native,
            [
                new AbiRecordIdentityMapping(
                    mapping.ManagedTypeCanonicalName,
                    "Native.ExpectedChild",
                    mapping.ManagedLayout,
                    mapping.NativeLayout),
            ]);

        missing.Compatibility.Should().Be(InteropCompatibility.Warning);
        missing.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.NestedRecordIdentity
            && check.Compatibility == InteropCompatibility.Warning);
        conflicting.Compatibility.Should().Be(InteropCompatibility.Error);
        conflicting.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.NestedRecordIdentity
            && check.Compatibility == InteropCompatibility.Error);
    }

    [Fact]
    public void NestedTargetMismatch_isWarningAndStopsThatNestedComparison()
    {
        var (managed, native, mapping) = NestedPair(nativeChildOffset: 0);
        var wrongTargetMapping = new AbiRecordIdentityMapping(
            mapping.ManagedTypeCanonicalName,
            mapping.NativeTypeCanonicalName,
            mapping.ManagedLayout with
            {
                Target = InteropTarget.WindowsX86Msvc,
            },
            mapping.NativeLayout);

        var result = _engine.Compare(
            managed,
            native,
            [wrongTargetMapping]);

        result.Compatibility.Should().Be(InteropCompatibility.Warning);
        result.Checks.Should().Contain(check =>
            check.Path.EndsWith(".record", StringComparison.Ordinal)
            && check.Aspect == AbiCompatibilityAspect.Target
            && check.Compatibility == InteropCompatibility.Warning);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.NestedRecordLayout
            && check.Compatibility == InteropCompatibility.Warning);
    }

    [Fact]
    public void InlineRecordCycle_isBoundedAndReportedAsWarning()
    {
        var managedType = new AbiTypeRef(
            "Managed.Node",
            AbiTypeCategory.Record,
            sizeBytes: 4,
            alignmentBytes: 4);
        var nativeType = new AbiTypeRef(
            "NativeNode",
            AbiTypeCategory.Record,
            sizeBytes: 4,
            alignmentBytes: 4);
        var managed = Layout(
            "managed:Node",
            AbiRecordKind.Sequential,
            4,
            4,
            8,
            [Field(0, "Next", managedType, 0, 4, native: false)],
            native: false);
        var native = Layout(
            "native:Node",
            AbiRecordKind.Native,
            4,
            4,
            8,
            [Field(0, "Next", nativeType, 0, 4, native: true)],
            native: true);
        var mapping = new AbiRecordIdentityMapping(
            managedType.CanonicalName,
            nativeType.CanonicalName,
            managed,
            native);

        var result = _engine.Compare(managed, native, [mapping]);

        result.Compatibility.Should().Be(InteropCompatibility.Warning);
        result.Checks.Should().ContainSingle(check =>
            check.Aspect == AbiCompatibilityAspect.Cycle
            && check.Compatibility == InteropCompatibility.Warning);
        result.Checks.Count.Should().BeLessThan(100);
    }

    [Fact]
    public void NestedRecordDepthLimit_isBoundedAndReportedAsWarning()
    {
        var chain = RecordChain(length: 35);

        var result = _engine.Compare(
            chain.Managed[0],
            chain.Native[0],
            chain.Mappings);

        result.Compatibility.Should().Be(InteropCompatibility.Warning);
        result.Checks.Should().Contain(check =>
            check.Aspect == AbiCompatibilityAspect.RecursionLimit
            && check.Compatibility == InteropCompatibility.Warning);
        result.Checks.Count.Should().BeLessThan(1000);
    }

    [Fact]
    public void OversizedFieldCollection_isRejectedBeforeEnumeration()
    {
        var type = Int32();
        var fields = Enumerable.Range(0, 4097)
            .Select(index => Field(
                index,
                $"F{index}",
                type,
                index * 4,
                4,
                native: false))
            .ToArray();
        var managed = Layout(
            "managed:Huge",
            AbiRecordKind.Sequential,
            16_388,
            4,
            8,
            fields,
            native: false);
        var native = Layout(
            "native:Huge",
            AbiRecordKind.Native,
            16_388,
            4,
            8,
            fields,
            native: true);

        var result = _engine.Compare(managed, native);

        result.Compatibility.Should().Be(InteropCompatibility.Warning);
        result.Checks.Should().ContainSingle(check =>
            check.Aspect == AbiCompatibilityAspect.CollectionLimit);
    }

    [Fact]
    public void CheckOrdering_isDeterministicWhenMappingInputOrderChanges()
    {
        var chain = RecordChain(length: 4);

        var forward = _engine.Compare(
            chain.Managed[0],
            chain.Native[0],
            chain.Mappings);
        var reverse = _engine.Compare(
            chain.Managed[0],
            chain.Native[0],
            chain.Mappings.Reverse().ToArray());

        reverse.Checks
            .Select(CheckIdentity)
            .Should()
            .Equal(forward.Checks.Select(CheckIdentity));
        reverse.Differences.Should().Equal(forward.Differences);
    }

    [Fact]
    public void Interop002Adapter_mapsOnlyNonCompatibleResults()
    {
        var adapter = new Interop002FindingAdapter();
        var compatible = _engine.Compare(
            PacketLayout(AbiRecordKind.Sequential, native: false),
            PacketLayout(AbiRecordKind.Native, native: true));
        var mismatch = _engine.Compare(
            PacketLayout(AbiRecordKind.Sequential, native: false),
            PacketLayout(
                AbiRecordKind.Native,
                native: true,
                pack: 1));
        var unknown = _engine.Compare(
            PacketLayout(AbiRecordKind.Sequential, native: false),
            PacketLayout(
                AbiRecordKind.Native,
                native: true,
                target: InteropTarget.WindowsX86Msvc));

        adapter.CreateFinding(compatible).Should().BeNull();
        var error = adapter.CreateFinding(mismatch);
        error.Should().NotBeNull();
        error!.RuleId.Should().Be("Interop002");
        error.Severity.Should().Be(InteropFindingSeverity.Error);
        error.ManagedSymbolCanonicalKey.Should().Be(
            mismatch.ManagedSymbolCanonicalKey);
        error.NativeSymbolCanonicalKey.Should().Be(
            mismatch.NativeSymbolCanonicalKey);
        error.Evidence.Should().NotBeEmpty();
        adapter.CreateFinding(unknown)!.Severity
            .Should().Be(InteropFindingSeverity.Warning);
    }

    private static string CheckIdentity(AbiCompatibilityCheck check) =>
        $"{check.Path}|{check.Aspect}|{check.Compatibility}|{check.Reason}";

    private static (
        AbiRecordLayout Managed,
        AbiRecordLayout Native,
        AbiRecordIdentityMapping Mapping) NestedPair(
        int nativeChildOffset)
    {
        var managedChild = Layout(
            "managed:Child",
            AbiRecordKind.Sequential,
            4,
            4,
            8,
            [Field(0, "Value", Int32(), 0, 4, native: false)],
            native: false);
        var nativeChild = Layout(
            "native:Child",
            AbiRecordKind.Native,
            4,
            4,
            8,
            [Field(0, "Value", Int32(), nativeChildOffset, 4, native: true)],
            native: true);
        var managedType = new AbiTypeRef(
            "Managed.Child",
            AbiTypeCategory.Record,
            sizeBytes: 4,
            alignmentBytes: 4);
        var nativeType = new AbiTypeRef(
            "NativeChild",
            AbiTypeCategory.Record,
            sizeBytes: 4,
            alignmentBytes: 4);
        var managed = Layout(
            "managed:Parent",
            AbiRecordKind.Sequential,
            4,
            4,
            8,
            [Field(0, "Child", managedType, 0, 4, native: false)],
            native: false);
        var native = Layout(
            "native:Parent",
            AbiRecordKind.Native,
            4,
            4,
            8,
            [Field(0, "Child", nativeType, 0, 4, native: true)],
            native: true);
        return (
            managed,
            native,
            new AbiRecordIdentityMapping(
                managedType.CanonicalName,
                nativeType.CanonicalName,
                managedChild,
                nativeChild));
    }

    private static (
        AbiRecordLayout[] Managed,
        AbiRecordLayout[] Native,
        AbiRecordIdentityMapping[] Mappings) RecordChain(
        int length)
    {
        var managed = new AbiRecordLayout[length];
        var native = new AbiRecordLayout[length];
        for (var index = length - 1; index >= 0; index--)
        {
            var isLeaf = index == length - 1;
            var managedType = isLeaf
                ? Int32()
                : new AbiTypeRef(
                    $"Managed.Level{index + 1}",
                    AbiTypeCategory.Record,
                    sizeBytes: 4,
                    alignmentBytes: 4);
            var nativeType = isLeaf
                ? Int32()
                : new AbiTypeRef(
                    $"NativeLevel{index + 1}",
                    AbiTypeCategory.Record,
                    sizeBytes: 4,
                    alignmentBytes: 4);
            managed[index] = Layout(
                $"managed:Level{index}",
                AbiRecordKind.Sequential,
                4,
                4,
                8,
                [Field(0, "Value", managedType, 0, 4, native: false)],
                native: false);
            native[index] = Layout(
                $"native:Level{index}",
                AbiRecordKind.Native,
                4,
                4,
                8,
                [Field(0, "Value", nativeType, 0, 4, native: true)],
                native: true);
        }

        var mappings = Enumerable.Range(1, length - 1)
            .Select(index => new AbiRecordIdentityMapping(
                $"Managed.Level{index}",
                $"NativeLevel{index}",
                managed[index],
                native[index]))
            .ToArray();
        return (managed, native, mappings);
    }

    private static AbiRecordLayout PacketLayout(
        AbiRecordKind kind,
        bool native,
        InteropTarget? target = null,
        int? pack = 8,
        int? size = 24,
        int flagSize = 1,
        int arrayLength = 3,
        int pointerSize = 8,
        int countOffset = 4)
    {
        var arrayElement = new AbiTypeRef(
            "uint16",
            AbiTypeCategory.UnsignedInteger,
            sizeBytes: 2,
            alignmentBytes: 2,
            isSigned: false);
        var array = new AbiTypeRef(
            "uint16[]",
            AbiTypeCategory.Array,
            sizeBytes: arrayLength * 2,
            alignmentBytes: 2,
            fixedArrayLength: arrayLength,
            elementType: arrayElement);
        var pointer = new AbiTypeRef(
            "void*",
            AbiTypeCategory.Pointer,
            pointerDepth: 1,
            sizeBytes: pointerSize,
            alignmentBytes: pointerSize);
        return Layout(
            native ? "native:Packet" : "managed:Packet",
            kind,
            size,
            8,
            pack,
            [
                Field(
                    0,
                    "Flag",
                    new AbiTypeRef(
                        "bool",
                        AbiTypeCategory.Boolean,
                        sizeBytes: flagSize,
                        alignmentBytes: flagSize,
                        isSigned: false),
                    0,
                    flagSize,
                    native),
                Field(1, "Count", Int32(), countOffset, 4, native),
                Field(2, "Values", array, 8, arrayLength * 2, native),
                Field(3, "Address", pointer, 16, pointerSize, native),
            ],
            native,
            target);
    }

    private static AbiRecordLayout Layout(
        string key,
        AbiRecordKind kind,
        int? size,
        int? alignment,
        int? pack,
        IReadOnlyList<AbiFieldLayout> fields,
        bool native,
        InteropTarget? target = null) =>
        new(
            key,
            kind,
            size,
            alignment,
            pack,
            fields,
            target ?? InteropTarget.WindowsX64Msvc,
            EvidenceAt(native, line: 1));

    private static AbiFieldLayout Field(
        int order,
        string name,
        AbiTypeRef type,
        int? offset,
        int? size,
        bool native) =>
        new(
            order,
            name,
            type,
            offset,
            size,
            EvidenceAt(native, line: order + 2));

    private static AbiTypeRef Int32() =>
        new(
            "int32",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static AbiTypeRef Float64() =>
        new(
            "double",
            AbiTypeCategory.FloatingPoint,
            sizeBytes: 8,
            alignmentBytes: 8);

    private static Evidence EvidenceAt(bool native, int line) =>
        new(
            native ? 2 : 1,
            new SourceLocation(
                native ? "Native.h" : "Managed.cs",
                line,
                1,
                line,
                20),
            native
                ? EvidenceConfidence.Exact
                : EvidenceConfidence.Semantic,
            native
                ? "clang-native-layout"
                : "roslyn-managed-layout");
}
