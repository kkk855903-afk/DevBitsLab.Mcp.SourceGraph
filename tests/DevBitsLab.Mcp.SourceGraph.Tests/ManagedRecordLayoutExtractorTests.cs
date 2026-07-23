using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Interop;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ManagedRecordLayoutExtractorTests
{
    [Fact]
    public void SequentialPackOne_computesOffsetsInlineArraysAndInlineStrings()
    {
        const string source = """
            using System.Runtime.InteropServices;

            namespace Fixture;

            [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
            internal struct Packet
            {
                public byte Code;
                public int Value;

                [MarshalAs(
                    UnmanagedType.ByValArray,
                    SizeConst = 3,
                    ArraySubType = UnmanagedType.U2)]
                public ushort[] Values;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
                public string Name;
            }
            """;
        var type = CompileType(source, "Fixture.Packet");

        var layout = ManagedRecordLayoutExtractor.TryExtract(
            type,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 31);

        layout.Should().NotBeNull();
        layout!.Kind.Should().Be(AbiRecordKind.Sequential);
        layout.Pack.Should().Be(1);
        layout.AlignmentBytes.Should().Be(1);
        layout.SizeBytes.Should().Be(27);
        layout.Fields.Select(field => field.Name)
            .Should().Equal("Code", "Value", "Values", "Name");
        layout.Fields.Select(field => field.OffsetBytes)
            .Should().Equal(0, 1, 5, 11);
        layout.Fields[2].Type.FixedArrayLength.Should().Be(3);
        layout.Fields[2].SizeBytes.Should().Be(6);
        layout.Fields[3].Type.FixedArrayLength.Should().Be(8);
        layout.Fields[3].Type.StringEncoding.Should().Be("utf-16");
        layout.Fields[3].SizeBytes.Should().Be(16);
        layout.Fields.Should().OnlyContain(field =>
            field.Evidence.Location.FilePath == "Layout.cs"
            && field.Evidence.Location.StartLine > 0);
    }

    [Fact]
    public void ExplicitLayout_preservesOverlapsAndDeclaredSize()
    {
        const string source = """
            using System.Runtime.InteropServices;

            namespace Fixture;

            [StructLayout(LayoutKind.Explicit, Size = 8)]
            internal struct ValueUnion
            {
                [FieldOffset(0)]
                public int Integer;

                [FieldOffset(0)]
                public float Floating;

                [FieldOffset(4), MarshalAs(UnmanagedType.I1)]
                public bool Flag;
            }
            """;
        var type = CompileType(source, "Fixture.ValueUnion");

        var layout = ManagedRecordLayoutExtractor.TryExtract(
            type,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 32);

        layout.Should().NotBeNull();
        layout!.Kind.Should().Be(AbiRecordKind.Explicit);
        layout.SizeBytes.Should().Be(8);
        layout.Fields.Select(field => field.OffsetBytes)
            .Should().Equal(0, 0, 4);
        layout.Fields[2].SizeBytes.Should().Be(1);
    }

    [Fact]
    public void ImplicitSequentialLayout_usesExplicitTargetDefaults()
    {
        const string source = """
            namespace Fixture;
            internal struct Pair
            {
                public byte Tag;
                public int Value;
            }
            """;
        var type = CompileType(source, "Fixture.Pair");

        var layout = ManagedRecordLayoutExtractor.TryExtract(
            type,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 33);

        layout.Should().NotBeNull();
        layout!.Kind.Should().Be(AbiRecordKind.Sequential);
        layout.Pack.Should().Be(8);
        layout.Fields.Select(field => field.OffsetBytes)
            .Should().Equal(0, 4);
        layout.SizeBytes.Should().Be(8);
    }

    [Fact]
    public void AutoLayout_isNotPresentedAsAnAbiFact()
    {
        const string source = """
            using System.Runtime.InteropServices;
            namespace Fixture;

            [StructLayout(LayoutKind.Auto)]
            internal struct AutoPacket
            {
                public int Value;
            }
            """;
        var type = CompileType(source, "Fixture.AutoPacket");

        ManagedRecordLayoutExtractor.TryExtract(
                type,
                InteropTarget.WindowsX64Msvc,
                producingFileId: 34)
            .Should().BeNull();
    }

    [Fact]
    public void PartialSequentialStruct_doesNotGuessCrossPartFieldOrder()
    {
        const string source = """
            namespace Fixture;
            internal partial struct Split
            {
                public byte First;
            }

            internal partial struct Split
            {
                public int Second;
            }
            """;
        var type = CompileType(source, "Fixture.Split");

        var layout = ManagedRecordLayoutExtractor.TryExtract(
            type,
            InteropTarget.WindowsX64Msvc,
            producingFileId: 35);

        layout.Should().NotBeNull();
        layout!.SizeBytes.Should().BeNull();
        layout.AlignmentBytes.Should().BeNull();
        layout.Fields.Should().OnlyContain(field => field.OffsetBytes == null);
    }

    private static INamedTypeSymbol CompileType(
        string source,
        string metadataName)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: "Layout.cs");
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException(
                "The test host did not expose trusted platform assemblies.");
        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "ManagedLayoutFixture",
            [tree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));
        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        var result = compilation.GetTypeByMetadataName(metadataName);
        result.Should().NotBeNull();
        return result!;
    }
}
