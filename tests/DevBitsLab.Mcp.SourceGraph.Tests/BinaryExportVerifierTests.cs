using System.Buffers.Binary;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class BinaryExportVerifierTests : IDisposable
{
    private readonly string _tempDir =
        Path.Join(Path.GetTempPath(), "sg-pe-export-tests-" + Guid.NewGuid().ToString("N"));

    public BinaryExportVerifierTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData(InteropArchitecture.X86, (ushort)0x014c)]
    [InlineData(InteropArchitecture.X64, (ushort)0x8664)]
    [InlineData(InteropArchitecture.Arm64, (ushort)0xaa64)]
    public async Task Complete_image_returns_names_aliases_ordinals_and_deduplicates(
        InteropArchitecture architecture,
        ushort expectedMachine)
    {
        var image = PeFixture.Build(
            architecture,
            functionCount: 3,
            ordinalBase: 7,
            [
                ("alias", (ushort)0),
                ("alpha", (ushort)0),
                ("alpha", (ushort)0),
                ("beta", (ushort)1),
            ]);
        var path = WriteImage(image);

        var result = await BinaryExportVerifier.VerifyAsync(path, Target(architecture));

        result.Status.Should().Be(BinaryExportVerificationStatus.Complete);
        result.IsComplete.Should().BeTrue();
        result.ImageArchitecture.Should().Be(architecture);
        result.Machine.Should().Be(expectedMachine);
        result.ModuleName.Should().Be("fixture.dll");
        result.Exports.Select(export => export.Ordinal).Should().Equal(7u, 8u, 9u);
        result.Exports[0].Names.Should().Equal("alias", "alpha");
        result.Exports[1].Names.Should().Equal("beta");
        result.Exports[2].Names.Should().BeEmpty();
        result.Exports.Should().OnlyContain(export => !export.IsForwarder);
    }

    [Fact]
    public async Task Complete_image_preserves_forwarder_evidence()
    {
        var image = PeFixture.Build(
            InteropArchitecture.X64,
            functionCount: 1,
            ordinalBase: 1,
            [("forwarded", (ushort)0)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(PeFixture.FunctionTableOffset, 4),
            PeFixture.ForwarderRva);
        PeFixture.WriteAscii(image, PeFixture.ForwarderOffset, "other.dll.actual");
        var path = WriteImage(image);

        var result = await BinaryExportVerifier.VerifyAsync(
            path,
            InteropTarget.WindowsX64Msvc);

        result.Status.Should().Be(BinaryExportVerificationStatus.Complete);
        var export = result.Exports.Should().ContainSingle().Subject;
        export.IsForwarder.Should().BeTrue();
        export.Forwarder.Should().Be("other.dll.actual");
    }

    [Fact]
    public async Task Image_for_another_machine_is_not_verified()
    {
        var path = WriteImage(PeFixture.Build(
            InteropArchitecture.X86,
            functionCount: 1,
            ordinalBase: 1,
            [("run", (ushort)0)]));

        var result = await BinaryExportVerifier.VerifyAsync(
            path,
            InteropTarget.WindowsX64Msvc);

        result.Status.Should().Be(BinaryExportVerificationStatus.TargetMismatch);
        result.IsComplete.Should().BeFalse();
        result.ImageArchitecture.Should().Be(InteropArchitecture.X86);
        result.Exports.Should().BeEmpty();
        result.ModuleName.Should().BeNull();
    }

    [Fact]
    public async Task Missing_artifact_is_unavailable_and_never_complete()
    {
        var path = Path.Join(_tempDir, "missing.dll");

        var result = await BinaryExportVerifier.VerifyAsync(
            path,
            InteropTarget.WindowsX64Msvc);

        result.Status.Should().Be(BinaryExportVerificationStatus.Unavailable);
        result.IsComplete.Should().BeFalse();
        result.Exports.Should().BeEmpty();
    }

    [Fact]
    public async Task Image_without_export_directory_is_complete_with_empty_universe()
    {
        var path = WriteImage(PeFixture.Build(
            InteropArchitecture.X64,
            functionCount: 0,
            ordinalBase: 1,
            [],
            includeExportDirectory: false));

        var result = await BinaryExportVerifier.VerifyAsync(
            path,
            InteropTarget.WindowsX64Msvc);

        result.Status.Should().Be(BinaryExportVerificationStatus.Complete);
        result.Exports.Should().BeEmpty();
        result.ModuleName.Should().BeNull();
    }

    [Fact]
    public async Task Relative_or_unresolved_paths_are_rejected_before_open()
    {
        Func<Task> relative = () => BinaryExportVerifier.VerifyAsync(
            "relative.dll",
            InteropTarget.WindowsX64Msvc);
        Func<Task> dotSegment = () => BinaryExportVerifier.VerifyAsync(
            Path.Join(_tempDir, ".", "artifact.dll"),
            InteropTarget.WindowsX64Msvc);

        await relative.Should().ThrowAsync<ArgumentException>();
        await dotSegment.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Truncated_and_malformed_headers_are_invalid()
    {
        var truncated = WriteImage([0x4d, 0x5a]);
        var badDos = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        badDos[0] = 0;
        var badPe = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        badPe[PeFixture.PeOffset] = 0;
        var badOptional = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        BinaryPrimitives.WriteUInt16LittleEndian(
            badOptional.AsSpan(PeFixture.OptionalHeaderOffset, 2),
            0xffff);

        foreach (var path in new[]
                 {
                     truncated,
                     WriteImage(badDos),
                     WriteImage(badPe),
                     WriteImage(badOptional),
                 })
        {
            var result = await BinaryExportVerifier.VerifyAsync(
                path,
                InteropTarget.WindowsX64Msvc);
            result.Status.Should().Be(BinaryExportVerificationStatus.Invalid);
            result.Exports.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Invalid_and_overflowing_rva_ranges_are_rejected()
    {
        var unmapped = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        PeFixture.WriteExportDataDirectory(unmapped, 0x3000, PeFixture.ExportDirectoryLength);

        var overflowing = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        PeFixture.WriteExportDataDirectory(
            overflowing,
            uint.MaxValue - 8,
            PeFixture.ExportDirectoryLength);

        foreach (var image in new[] { unmapped, overflowing })
        {
            var result = await BinaryExportVerifier.VerifyAsync(
                WriteImage(image),
                InteropTarget.WindowsX64Msvc);
            result.Status.Should().Be(BinaryExportVerificationStatus.Invalid);
            result.Exports.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Oversized_export_counts_are_rejected_before_table_allocation()
    {
        var tooManyFunctions = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            tooManyFunctions.AsSpan(PeFixture.ExportDirectoryOffset + 20, 4),
            uint.MaxValue);
        var tooManyNames = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            tooManyNames.AsSpan(PeFixture.ExportDirectoryOffset + 24, 4),
            uint.MaxValue);

        foreach (var image in new[] { tooManyFunctions, tooManyNames })
        {
            var result = await BinaryExportVerifier.VerifyAsync(
                WriteImage(image),
                InteropTarget.WindowsX64Msvc);
            result.Status.Should().Be(BinaryExportVerificationStatus.Invalid);
            result.Exports.Should().BeEmpty();
            result.Reason.Should().Contain("exceed");
        }
    }

    [Fact]
    public async Task Invalid_name_ordinal_and_unterminated_name_are_rejected()
    {
        var invalidOrdinal = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        BinaryPrimitives.WriteUInt16LittleEndian(
            invalidOrdinal.AsSpan(PeFixture.OrdinalTableOffset, 2),
            99);

        var unterminated = PeFixture.Build(
            InteropArchitecture.X64,
            1,
            1,
            [("run", (ushort)0)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            unterminated.AsSpan(PeFixture.NamePointerTableOffset, 4),
            PeFixture.UnterminatedNameRva);
        unterminated.AsSpan(PeFixture.UnterminatedNameOffset, 16).Fill((byte)'A');

        foreach (var image in new[] { invalidOrdinal, unterminated })
        {
            var result = await BinaryExportVerifier.VerifyAsync(
                WriteImage(image),
                InteropTarget.WindowsX64Msvc);
            result.Status.Should().Be(BinaryExportVerificationStatus.Invalid);
            result.Exports.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Cancellation_is_propagated_and_never_converted_to_a_status()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> act = () => BinaryExportVerifier.VerifyAsync(
            Path.Join(_tempDir, "not-opened.dll"),
            InteropTarget.WindowsX64Msvc,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private string WriteImage(byte[] image)
    {
        var path = Path.Join(_tempDir, Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, image);
        return path;
    }

    private static InteropTarget Target(InteropArchitecture architecture) =>
        architecture switch
        {
            InteropArchitecture.X86 => InteropTarget.WindowsX86Msvc,
            InteropArchitecture.X64 => InteropTarget.WindowsX64Msvc,
            InteropArchitecture.Arm64 => new(
                "win-arm64",
                InteropArchitecture.Arm64,
                InteropCompilerAbi.Msvc,
                pointerSizeBytes: 8,
                defaultPack: 8),
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
        };

    private static class PeFixture
    {
        public const int PeOffset = 0x80;
        public const int OptionalHeaderOffset = PeOffset + 24;
        public const int ExportDirectoryOffset = 0x200;
        public const int FunctionTableOffset = 0x240;
        public const int NamePointerTableOffset = 0x400;
        public const int OrdinalTableOffset = 0x500;
        public const int ForwarderOffset = 0x800;
        public const int UnterminatedNameOffset = 0x8f0;
        public const uint ForwarderRva = 0x1600;
        public const uint UnterminatedNameRva = 0x16f0;
        public const uint ExportDirectoryLength = 0x700;

        private const int FileLength = 0x1200;
        private const int HeaderLength = 0x200;
        private const uint SectionRva = 0x1000;
        private const uint SectionLength = 0x1000;
        private const uint FunctionTableRva = 0x1040;
        private const uint NamePointerTableRva = 0x1200;
        private const uint OrdinalTableRva = 0x1300;
        private const uint ModuleNameRva = 0x1400;
        private const int ModuleNameOffset = 0x600;
        private const int FirstExportNameOffset = 0x620;

        public static byte[] Build(
            InteropArchitecture architecture,
            int functionCount,
            uint ordinalBase,
            IReadOnlyList<(string Name, ushort FunctionIndex)> names,
            bool includeExportDirectory = true)
        {
            var image = new byte[FileLength];
            BinaryPrimitives.WriteUInt16LittleEndian(image, 0x5a4d);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(60, 4), PeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(PeOffset, 4),
                0x00004550);

            var machine = architecture switch
            {
                InteropArchitecture.X86 => (ushort)0x014c,
                InteropArchitecture.X64 => (ushort)0x8664,
                InteropArchitecture.Arm64 => (ushort)0xaa64,
                _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
            };
            var optionalHeaderSize =
                architecture == InteropArchitecture.X86 ? (ushort)0xe0 : (ushort)0xf0;
            BinaryPrimitives.WriteUInt16LittleEndian(
                image.AsSpan(PeOffset + 4, 2),
                machine);
            BinaryPrimitives.WriteUInt16LittleEndian(
                image.AsSpan(PeOffset + 6, 2),
                1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                image.AsSpan(PeOffset + 20, 2),
                optionalHeaderSize);

            var optionalMagic =
                architecture == InteropArchitecture.X86 ? (ushort)0x010b : (ushort)0x020b;
            BinaryPrimitives.WriteUInt16LittleEndian(
                image.AsSpan(OptionalHeaderOffset, 2),
                optionalMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(OptionalHeaderOffset + 60, 4),
                HeaderLength);
            var directoriesOffset =
                OptionalHeaderOffset + (architecture == InteropArchitecture.X86 ? 96 : 112);
            var directoryCountOffset =
                OptionalHeaderOffset + (architecture == InteropArchitecture.X86 ? 92 : 108);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(directoryCountOffset, 4),
                16);
            if (includeExportDirectory)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    image.AsSpan(directoriesOffset, 4),
                    SectionRva);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    image.AsSpan(directoriesOffset + 4, 4),
                    ExportDirectoryLength);
            }

            var sectionOffset = OptionalHeaderOffset + optionalHeaderSize;
            Encoding.ASCII.GetBytes(".edata").CopyTo(image.AsSpan(sectionOffset, 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(sectionOffset + 8, 4),
                SectionLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(sectionOffset + 12, 4),
                SectionRva);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(sectionOffset + 16, 4),
                SectionLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(sectionOffset + 20, 4),
                HeaderLength);

            if (!includeExportDirectory) return image;

            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(ExportDirectoryOffset + 12, 4),
                ModuleNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(ExportDirectoryOffset + 16, 4),
                ordinalBase);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(ExportDirectoryOffset + 20, 4),
                checked((uint)functionCount));
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(ExportDirectoryOffset + 24, 4),
                checked((uint)names.Count));
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(ExportDirectoryOffset + 28, 4),
                FunctionTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(ExportDirectoryOffset + 32, 4),
                NamePointerTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(ExportDirectoryOffset + 36, 4),
                OrdinalTableRva);
            WriteAscii(image, ModuleNameOffset, "fixture.dll");

            for (var i = 0; i < functionCount; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    image.AsSpan(FunctionTableOffset + (i * 4), 4),
                    checked((uint)(0x1800 + (i * 16))));
            }

            var nameOffset = FirstExportNameOffset;
            for (var i = 0; i < names.Count; i++)
            {
                var nameRva = checked(SectionRva + (uint)(nameOffset - HeaderLength));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    image.AsSpan(NamePointerTableOffset + (i * 4), 4),
                    nameRva);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    image.AsSpan(OrdinalTableOffset + (i * 2), 2),
                    names[i].FunctionIndex);
                WriteAscii(image, nameOffset, names[i].Name);
                nameOffset += Encoding.ASCII.GetByteCount(names[i].Name) + 1;
            }
            return image;
        }

        public static void WriteExportDataDirectory(byte[] image, uint rva, uint size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(OptionalHeaderOffset + 112, 4),
                rva);
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(OptionalHeaderOffset + 116, 4),
                size);
        }

        public static void WriteAscii(byte[] image, int offset, string value)
        {
            var written = Encoding.ASCII.GetBytes(value, image.AsSpan(offset));
            image[offset + written] = 0;
        }
    }
}
