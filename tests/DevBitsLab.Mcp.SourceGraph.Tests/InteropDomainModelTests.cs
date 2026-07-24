using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropDomainModelTests
{
    [Fact]
    public void WindowsTargets_freezePointerWidthAndDefaultPack()
    {
        InteropTarget.WindowsX64Msvc.PointerSizeBytes.Should().Be(8);
        InteropTarget.WindowsX64Msvc.DefaultPack.Should().Be(8);
        InteropTarget.WindowsX86Msvc.PointerSizeBytes.Should().Be(4);
        InteropTarget.WindowsX86Msvc.CompilerAbi.Should().Be(InteropCompilerAbi.Msvc);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(16, 8)]
    [InlineData(8, 3)]
    public void InteropTarget_rejectsImpossibleAbiDimensions(int pointerSize, int pack)
    {
        var act = () => new InteropTarget(
            "invalid",
            InteropArchitecture.X64,
            InteropCompilerAbi.Msvc,
            pointerSize,
            pack);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UnknownTypeDimensions_remainNullInsteadOfBeingGuessed()
    {
        var opaque = new AbiTypeRef("custom-marshaler", AbiTypeCategory.Opaque);

        opaque.SizeBytes.Should().BeNull();
        opaque.AlignmentBytes.Should().BeNull();
        opaque.PointerDepth.Should().Be(0);
    }

    [Fact]
    public void IndirectedAndInlineTypes_keepPointeeElementAndConstIdentity()
    {
        var record = new AbiTypeRef("NativeInput", AbiTypeCategory.Record);
        var pointer = new AbiTypeRef(
            "const NativeInput*",
            AbiTypeCategory.Pointer,
            pointerDepth: 1,
            sizeBytes: 8,
            alignmentBytes: 8,
            pointeeType: record,
            isPointeeConst: true);
        var array = new AbiTypeRef(
            "int32[4]",
            AbiTypeCategory.Array,
            sizeBytes: 16,
            alignmentBytes: 4,
            fixedArrayLength: 4,
            elementType: new AbiTypeRef(
                "int32",
                AbiTypeCategory.SignedInteger,
                sizeBytes: 4,
                alignmentBytes: 4,
                isSigned: true));

        pointer.PointeeType.Should().Be(record);
        pointer.IsPointeeConst.Should().BeTrue();
        array.ElementType!.CanonicalName.Should().Be("int32");
    }

    [Fact]
    public void DomainFacts_keepTargetAndEvidenceProvenance()
    {
        var evidence = new Evidence(
            ProducingFileId: 7,
            Location: new SourceLocation("Interop/NativeMethods.cs", 8, 5, 12, 70),
            Confidence: EvidenceConfidence.Exact,
            Producer: "managed-interop");
        var int32 = new AbiTypeRef(
            "int32",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);
        var import = new ManagedImport(
            "csharp:M:Example.NativeMethods.Calculate(System.Int32)",
            ManagedImportKind.DllImport,
            "medalgo",
            "medalgo_calculate",
            InteropCallingConvention.Cdecl,
            int32,
            [
                new AbiParameter(
                    0,
                    "value",
                    int32,
                    AbiParameterDirection.In,
                    new SourceLocation("Interop/NativeMethods.cs", 12, 42, 12, 51)),
            ],
            CharacterSet: null,
            SetLastError: false,
            InteropTarget.WindowsX64Msvc,
            evidence);

        import.Target.RuntimeIdentifier.Should().Be("win-x64");
        import.Evidence.Location.FilePath.Should().Be("Interop/NativeMethods.cs");
        import.Parameters.Should().ContainSingle(parameter =>
            parameter.Direction == AbiParameterDirection.In);
    }
}
