using System.Buffers.Binary;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Interop;

/// <summary>
/// Outcome of inspecting one configured PE artifact. Only <see cref="Complete"/> represents an
/// authoritative export universe.
/// </summary>
public enum BinaryExportVerificationStatus
{
    Complete,
    Unavailable,
    Invalid,
    TargetMismatch,
}

/// <summary>
/// One non-empty PE export-address-table slot. Multiple public names may alias the same ordinal;
/// <see cref="Names"/> is empty for an ordinal-only export.
/// </summary>
public sealed record BinaryExportEntry(
    uint Ordinal,
    uint AddressRva,
    IReadOnlyList<string> Names,
    bool IsForwarder,
    string? Forwarder);

/// <summary>
/// Structured result from <see cref="BinaryExportVerifier"/>. <see cref="Exports"/> is populated
/// only for <see cref="BinaryExportVerificationStatus.Complete"/>.
/// </summary>
public sealed record BinaryExportVerificationResult(
    BinaryExportVerificationStatus Status,
    InteropArchitecture? ImageArchitecture,
    ushort? Machine,
    string? ModuleName,
    IReadOnlyList<BinaryExportEntry> Exports,
    string Reason)
{
    public bool IsComplete => Status == BinaryExportVerificationStatus.Complete;
}

/// <summary>
/// Bounded, dependency-free reader for a configured Windows PE export table. The caller must
/// resolve and authorize physical containment before calling this component; relative or
/// dot-segment paths are rejected here, but this type intentionally does not make scope-policy
/// decisions.
/// </summary>
public static class BinaryExportVerifier
{
    private const ushort DosMagic = 0x5a4d;
    private const uint PeSignature = 0x00004550;
    private const ushort Pe32Magic = 0x010b;
    private const ushort Pe32PlusMagic = 0x020b;
    private const ushort MachineX86 = 0x014c;
    private const ushort MachineX64 = 0x8664;
    private const ushort MachineArm64 = 0xaa64;

    private const int CoffHeaderSize = 24;
    private const int SectionHeaderSize = 40;
    private const int ExportDirectorySize = 40;
    private const int MaxSections = 96;
    private const uint MaxExportDirectoryBytes = 16 * 1024 * 1024;
    private const uint MaxExportFunctions = 65_536;
    private const uint MaxExportNames = 65_536;
    private const int MaxExportNameBytes = 4_096;
    private const long MaxTotalStringBytes = 4 * 1024 * 1024;
    private const long MaxTotalReadBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Inspects one absolute, caller-authorized PE path. Cancellation is propagated. Missing,
    /// inaccessible, or transiently unreadable files return <c>Unavailable</c>; malformed bytes
    /// return <c>Invalid</c>; a valid image for another machine returns <c>TargetMismatch</c>.
    /// </summary>
    public static async Task<BinaryExportVerificationResult> VerifyAsync(
        string absolutePePath,
        InteropTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePePath);
        ArgumentNullException.ThrowIfNull(target);
        ValidateAbsoluteResolvedPath(absolutePePath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var stream = new FileStream(
                absolutePePath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
                    BufferSize = 1,
                });
            var reader = new BoundedFileReader(stream, MaxTotalReadBytes);
            var parser = new PeExportParser(reader, target);
            return await parser.ParseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidPeException ex)
        {
            return Failure(BinaryExportVerificationStatus.Invalid, ex.Message);
        }
        catch (FileNotFoundException)
        {
            return Failure(BinaryExportVerificationStatus.Unavailable, "PE artifact does not exist.");
        }
        catch (DirectoryNotFoundException)
        {
            return Failure(
                BinaryExportVerificationStatus.Unavailable,
                "PE artifact directory does not exist.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(BinaryExportVerificationStatus.Unavailable, "PE artifact cannot be read.");
        }
        catch (IOException)
        {
            return Failure(BinaryExportVerificationStatus.Unavailable, "PE artifact could not be read.");
        }
    }

    private static void ValidateAbsoluteResolvedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)
            || path.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "PE path must be an absolute, caller-resolved path without dot segments.",
                nameof(path));
        }
    }

    private static BinaryExportVerificationResult Failure(
        BinaryExportVerificationStatus status,
        string reason) =>
        new(status, null, null, null, [], reason);

    private sealed class PeExportParser(BoundedFileReader reader, InteropTarget target)
    {
        private readonly byte[] _stringBuffer = new byte[MaxExportNameBytes + 1];
        private IReadOnlyList<Section> _sections = [];
        private uint _sizeOfHeaders;
        private long _totalStringBytes;

        public async Task<BinaryExportVerificationResult> ParseAsync(
            CancellationToken cancellationToken)
        {
            var dosHeader = await reader.ReadBytesAsync(0, 64, cancellationToken)
                .ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt16LittleEndian(dosHeader) != DosMagic)
            {
                throw Invalid("DOS header signature is not MZ.");
            }

            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader.AsSpan(60, 4));
            if (peOffset < 64)
            {
                throw Invalid("DOS e_lfanew does not point past the DOS header.");
            }

            var coffHeader = await reader.ReadBytesAsync(peOffset, CoffHeaderSize, cancellationToken)
                .ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(coffHeader) != PeSignature)
            {
                throw Invalid("PE signature is missing.");
            }

            var machine = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader.AsSpan(4, 2));
            var numberOfSections =
                BinaryPrimitives.ReadUInt16LittleEndian(coffHeader.AsSpan(6, 2));
            var optionalHeaderSize =
                BinaryPrimitives.ReadUInt16LittleEndian(coffHeader.AsSpan(20, 2));
            if (numberOfSections is 0 or > MaxSections)
            {
                throw Invalid($"PE section count {numberOfSections} is outside the supported bound.");
            }
            if (optionalHeaderSize < 2)
            {
                throw Invalid("PE optional header is missing.");
            }

            var optionalHeaderOffset = Add(peOffset, CoffHeaderSize, "optional-header offset");
            reader.EnsureRange(optionalHeaderOffset, optionalHeaderSize);
            var magicBytes = await reader.ReadBytesAsync(
                    optionalHeaderOffset,
                    2,
                    cancellationToken)
                .ConfigureAwait(false);
            var optionalMagic = BinaryPrimitives.ReadUInt16LittleEndian(magicBytes);
            var (requiredOptionalBytes, numberOfDirectoriesOffset, dataDirectoriesOffset) =
                optionalMagic switch
                {
                    Pe32Magic => (104, 92, 96),
                    Pe32PlusMagic => (120, 108, 112),
                    _ => throw Invalid(
                        $"Unsupported PE optional-header magic 0x{optionalMagic:x4}."),
                };
            if (optionalHeaderSize < requiredOptionalBytes)
            {
                throw Invalid("PE optional header is truncated before the export data directory.");
            }

            var optionalHeader = await reader.ReadBytesAsync(
                    optionalHeaderOffset,
                    requiredOptionalBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            _sizeOfHeaders =
                BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(60, 4));
            var numberOfDirectories = BinaryPrimitives.ReadUInt32LittleEndian(
                optionalHeader.AsSpan(numberOfDirectoriesOffset, 4));
            var directoryCapacity =
                (optionalHeaderSize - dataDirectoriesOffset) / (2 * sizeof(uint));
            if (numberOfDirectories > directoryCapacity)
            {
                throw Invalid(
                    "PE optional header declares more data directories than it contains.");
            }
            var exportRva = BinaryPrimitives.ReadUInt32LittleEndian(
                optionalHeader.AsSpan(dataDirectoriesOffset, 4));
            var exportSize = BinaryPrimitives.ReadUInt32LittleEndian(
                optionalHeader.AsSpan(dataDirectoriesOffset + 4, 4));

            var sectionTableOffset = Add(
                optionalHeaderOffset,
                optionalHeaderSize,
                "section-table offset");
            var sectionTableSize = checked(numberOfSections * SectionHeaderSize);
            var sectionTableEnd = Add(
                sectionTableOffset,
                sectionTableSize,
                "section-table end");
            if (_sizeOfHeaders == 0
                || _sizeOfHeaders > reader.Length
                || sectionTableEnd > _sizeOfHeaders)
            {
                throw Invalid("PE SizeOfHeaders does not contain the complete header table.");
            }

            var sectionBytes = await reader.ReadBytesAsync(
                    sectionTableOffset,
                    sectionTableSize,
                    cancellationToken)
                .ConfigureAwait(false);
            _sections = ParseSections(sectionBytes);

            var imageArchitecture = ArchitectureForMachine(machine);
            ValidateMachineMagic(imageArchitecture, optionalMagic);
            if (imageArchitecture != target.Architecture)
            {
                return new BinaryExportVerificationResult(
                    BinaryExportVerificationStatus.TargetMismatch,
                    imageArchitecture,
                    machine,
                    null,
                    [],
                    $"PE machine 0x{machine:x4} does not match target architecture {target.Architecture}.");
            }

            if (numberOfDirectories == 0 || (exportRva == 0 && exportSize == 0))
            {
                if (numberOfDirectories != 0 && (exportRva == 0) != (exportSize == 0))
                {
                    throw Invalid("PE export-directory RVA and size must both be zero or non-zero.");
                }
                return Complete(imageArchitecture, machine, null, []);
            }
            if (exportRva == 0 || exportSize == 0)
            {
                throw Invalid("PE export-directory RVA and size must both be zero or non-zero.");
            }
            if (exportSize is < ExportDirectorySize or > MaxExportDirectoryBytes)
            {
                throw Invalid(
                    $"PE export-directory size {exportSize} is outside the supported bound.");
            }

            var exportEnd = AddRva(exportRva, exportSize, "export-directory range");
            var exportOffset = MapRvaRange(exportRva, checked((int)exportSize));
            var exportDirectory = await reader.ReadBytesAsync(
                    exportOffset,
                    ExportDirectorySize,
                    cancellationToken)
                .ConfigureAwait(false);

            var moduleNameRva =
                BinaryPrimitives.ReadUInt32LittleEndian(exportDirectory.AsSpan(12, 4));
            var ordinalBase =
                BinaryPrimitives.ReadUInt32LittleEndian(exportDirectory.AsSpan(16, 4));
            var numberOfFunctions =
                BinaryPrimitives.ReadUInt32LittleEndian(exportDirectory.AsSpan(20, 4));
            var numberOfNames =
                BinaryPrimitives.ReadUInt32LittleEndian(exportDirectory.AsSpan(24, 4));
            var functionsRva =
                BinaryPrimitives.ReadUInt32LittleEndian(exportDirectory.AsSpan(28, 4));
            var namesRva =
                BinaryPrimitives.ReadUInt32LittleEndian(exportDirectory.AsSpan(32, 4));
            var ordinalsRva =
                BinaryPrimitives.ReadUInt32LittleEndian(exportDirectory.AsSpan(36, 4));

            if (moduleNameRva == 0)
            {
                throw Invalid("PE export directory has no module-name RVA.");
            }
            var moduleName = await ReadAsciiStringAsync(
                    moduleNameRva,
                    exportRva,
                    exportEnd,
                    "module name",
                    cancellationToken)
                .ConfigureAwait(false);

            if (numberOfFunctions > MaxExportFunctions
                || numberOfNames > MaxExportNames)
            {
                throw Invalid(
                    $"PE export counts ({numberOfFunctions} functions, {numberOfNames} names) exceed the supported bound.");
            }
            if (numberOfFunctions == 0)
            {
                if (numberOfNames != 0)
                {
                    throw Invalid("PE export names exist without export-address-table entries.");
                }
                return Complete(imageArchitecture, machine, moduleName, []);
            }
            if (functionsRva == 0)
            {
                throw Invalid("PE export-address-table RVA is missing.");
            }

            var functionTableBytes = checked((int)numberOfFunctions * sizeof(uint));
            RequireDirectoryRange(functionsRva, functionTableBytes, exportRva, exportEnd);
            var functionTable = await reader.ReadBytesAsync(
                    MapRvaRange(functionsRva, functionTableBytes),
                    functionTableBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var functionRvas = new uint[numberOfFunctions];
            for (var i = 0; i < functionRvas.Length; i++)
            {
                functionRvas[i] =
                    BinaryPrimitives.ReadUInt32LittleEndian(functionTable.AsSpan(i * 4, 4));
            }

            var namesByFunction = new Dictionary<int, List<string>>();
            if (numberOfNames > 0)
            {
                if (namesRva == 0 || ordinalsRva == 0)
                {
                    throw Invalid("PE export name or ordinal table RVA is missing.");
                }
                await ReadNamesAsync(
                        namesRva,
                        ordinalsRva,
                        numberOfNames,
                        functionRvas,
                        namesByFunction,
                        exportRva,
                        exportEnd,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var entries = new List<BinaryExportEntry>(functionRvas.Length);
            for (var i = 0; i < functionRvas.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var addressRva = functionRvas[i];
                if (addressRva == 0) continue;

                var ordinalValue = (ulong)ordinalBase + (uint)i;
                if (ordinalValue > uint.MaxValue)
                {
                    throw Invalid("PE export ordinal overflows UInt32.");
                }

                var isForwarder = IsInRange(addressRva, exportRva, exportEnd);
                string? forwarder = null;
                if (isForwarder)
                {
                    forwarder = await ReadAsciiStringAsync(
                            addressRva,
                            exportRva,
                            exportEnd,
                            "forwarder",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    _ = MapRvaRange(addressRva, 1);
                }

                entries.Add(new BinaryExportEntry(
                    (uint)ordinalValue,
                    addressRva,
                    namesByFunction.TryGetValue(i, out var names)
                        ? names.ToArray()
                        : [],
                    isForwarder,
                    forwarder));
            }

            return Complete(imageArchitecture, machine, moduleName, entries);
        }

        private async Task ReadNamesAsync(
            uint namesRva,
            uint ordinalsRva,
            uint numberOfNames,
            IReadOnlyList<uint> functionRvas,
            Dictionary<int, List<string>> namesByFunction,
            uint exportRva,
            ulong exportEnd,
            CancellationToken cancellationToken)
        {
            var nameTableBytes = checked((int)numberOfNames * sizeof(uint));
            var ordinalTableBytes = checked((int)numberOfNames * sizeof(ushort));
            RequireDirectoryRange(namesRva, nameTableBytes, exportRva, exportEnd);
            RequireDirectoryRange(ordinalsRva, ordinalTableBytes, exportRva, exportEnd);
            var nameTable = await reader.ReadBytesAsync(
                    MapRvaRange(namesRva, nameTableBytes),
                    nameTableBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var ordinalTable = await reader.ReadBytesAsync(
                    MapRvaRange(ordinalsRva, ordinalTableBytes),
                    ordinalTableBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            var seen = new HashSet<(int FunctionIndex, string Name)>();
            string? previousName = null;
            for (var i = 0; i < numberOfNames; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nameRva =
                    BinaryPrimitives.ReadUInt32LittleEndian(nameTable.AsSpan(checked((int)i * 4), 4));
                var functionIndex = BinaryPrimitives.ReadUInt16LittleEndian(
                    ordinalTable.AsSpan(checked((int)i * 2), 2));
                if (functionIndex >= functionRvas.Count || functionRvas[functionIndex] == 0)
                {
                    throw Invalid("PE export-name ordinal does not identify a populated function.");
                }

                var name = await ReadAsciiStringAsync(
                        nameRva,
                        exportRva,
                        exportEnd,
                        "export name",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (previousName is not null
                    && string.CompareOrdinal(previousName, name) > 0)
                {
                    throw Invalid("PE export-name pointer table is not sorted.");
                }
                previousName = name;

                if (!seen.Add((functionIndex, name))) continue;
                if (!namesByFunction.TryGetValue(functionIndex, out var names))
                {
                    names = [];
                    namesByFunction.Add(functionIndex, names);
                }
                names.Add(name);
            }
        }

        private async Task<string> ReadAsciiStringAsync(
            uint rva,
            uint allowedStart,
            ulong allowedEnd,
            string label,
            CancellationToken cancellationToken)
        {
            if (!IsInRange(rva, allowedStart, allowedEnd))
            {
                throw Invalid($"PE {label} RVA is outside the export directory.");
            }

            var remainingInDirectory = allowedEnd - rva;
            var available = GetMappedByteCount(rva);
            var bytesToRead = (int)Math.Min(
                (ulong)_stringBuffer.Length,
                Math.Min(remainingInDirectory, available));
            if (bytesToRead <= 0)
            {
                throw Invalid($"PE {label} RVA is not backed by file data.");
            }

            await reader.ReadAsync(
                    MapRvaRange(rva, bytesToRead),
                    _stringBuffer.AsMemory(0, bytesToRead),
                    cancellationToken)
                .ConfigureAwait(false);
            var terminator = _stringBuffer.AsSpan(0, bytesToRead).IndexOf((byte)0);
            if (terminator <= 0)
            {
                throw Invalid(
                    terminator == 0
                        ? $"PE {label} is empty."
                        : $"PE {label} is not null-terminated within {MaxExportNameBytes} bytes.");
            }

            _totalStringBytes += terminator + 1L;
            if (_totalStringBytes > MaxTotalStringBytes)
            {
                throw Invalid("PE export strings exceed the total inspection budget.");
            }
            for (var i = 0; i < terminator; i++)
            {
                if (_stringBuffer[i] is < 0x20 or > 0x7e)
                {
                    throw Invalid($"PE {label} contains non-printable or non-ASCII bytes.");
                }
            }
            return Encoding.ASCII.GetString(_stringBuffer, 0, terminator);
        }

        private IReadOnlyList<Section> ParseSections(byte[] bytes)
        {
            var sections = new List<Section>(bytes.Length / SectionHeaderSize);
            for (var offset = 0; offset < bytes.Length; offset += SectionHeaderSize)
            {
                var header = bytes.AsSpan(offset, SectionHeaderSize);
                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
                var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
                var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
                var mappedSize = Math.Max(virtualSize, rawSize);

                if (mappedSize > 0
                    && ((ulong)virtualAddress + mappedSize > uint.MaxValue
                        || virtualAddress < _sizeOfHeaders))
                {
                    throw Invalid("PE section virtual range is invalid.");
                }
                if (rawSize > 0
                    && (rawPointer < _sizeOfHeaders
                        || (ulong)rawPointer + rawSize > (ulong)reader.Length))
                {
                    throw Invalid("PE section raw-data range is invalid.");
                }
                sections.Add(new Section(
                    virtualAddress,
                    mappedSize,
                    rawPointer,
                    rawSize));
            }

            var ordered = sections
                .Where(section => section.MappedSize > 0)
                .OrderBy(section => section.VirtualAddress)
                .ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                var previousEnd =
                    (ulong)ordered[i - 1].VirtualAddress + ordered[i - 1].MappedSize;
                if (ordered[i].VirtualAddress < previousEnd)
                {
                    throw Invalid("PE section virtual ranges overlap.");
                }
            }
            return sections;
        }

        private long MapRvaRange(uint rva, int byteCount)
        {
            if (byteCount < 0)
            {
                throw Invalid("Negative PE RVA byte count.");
            }
            var end = (ulong)rva + (uint)byteCount;
            if (end > (ulong)uint.MaxValue + 1)
            {
                throw Invalid("PE RVA range overflows UInt32.");
            }

            if (rva < _sizeOfHeaders && end <= _sizeOfHeaders)
            {
                reader.EnsureRange(rva, byteCount);
                return rva;
            }

            Section? match = null;
            foreach (var section in _sections)
            {
                if (rva < section.VirtualAddress) continue;
                var delta = (ulong)rva - section.VirtualAddress;
                if (delta + (uint)byteCount > section.MappedSize) continue;
                if (delta + (uint)byteCount > section.RawSize)
                {
                    throw Invalid("PE RVA range is not backed by section file data.");
                }
                if (match is not null)
                {
                    throw Invalid("PE RVA maps to more than one section.");
                }
                match = section;
            }
            if (match is null)
            {
                throw Invalid($"PE RVA 0x{rva:x8} does not map to file data.");
            }

            var fileOffset = (ulong)match.RawPointer + (rva - match.VirtualAddress);
            if (fileOffset > long.MaxValue)
            {
                throw Invalid("PE RVA file offset overflows Int64.");
            }
            reader.EnsureRange((long)fileOffset, byteCount);
            return (long)fileOffset;
        }

        private ulong GetMappedByteCount(uint rva)
        {
            if (rva < _sizeOfHeaders) return _sizeOfHeaders - rva;
            foreach (var section in _sections)
            {
                if (rva < section.VirtualAddress) continue;
                var delta = (ulong)rva - section.VirtualAddress;
                if (delta < section.MappedSize)
                {
                    return delta < section.RawSize ? section.RawSize - delta : 0;
                }
            }
            return 0;
        }

        private static void RequireDirectoryRange(
            uint rva,
            int byteCount,
            uint directoryStart,
            ulong directoryEnd)
        {
            var end = (ulong)rva + (uint)byteCount;
            if (rva < directoryStart || end > directoryEnd)
            {
                throw Invalid("PE export table points outside the export directory.");
            }
        }

        private static ulong AddRva(uint start, uint size, string label)
        {
            var end = (ulong)start + size;
            if (end > (ulong)uint.MaxValue + 1)
            {
                throw Invalid($"PE {label} overflows UInt32.");
            }
            return end;
        }

        private static bool IsInRange(uint value, uint start, ulong end) =>
            value >= start && (ulong)value < end;

        private static InteropArchitecture? ArchitectureForMachine(ushort machine) =>
            machine switch
            {
                MachineX86 => InteropArchitecture.X86,
                MachineX64 => InteropArchitecture.X64,
                MachineArm64 => InteropArchitecture.Arm64,
                _ => null,
            };

        private static void ValidateMachineMagic(
            InteropArchitecture? architecture,
            ushort optionalMagic)
        {
            if ((architecture == InteropArchitecture.X86 && optionalMagic != Pe32Magic)
                || (architecture is InteropArchitecture.X64 or InteropArchitecture.Arm64
                    && optionalMagic != Pe32PlusMagic))
            {
                throw Invalid("PE machine and optional-header format disagree.");
            }
        }

        private static BinaryExportVerificationResult Complete(
            InteropArchitecture? architecture,
            ushort machine,
            string? moduleName,
            IReadOnlyList<BinaryExportEntry> exports) =>
            new(
                BinaryExportVerificationStatus.Complete,
                architecture,
                machine,
                moduleName,
                exports,
                $"PE export table was completely verified ({exports.Count} populated ordinals).");
    }

    private sealed class BoundedFileReader
    {
        private readonly FileStream _stream;
        private readonly long _readBudget;
        private long _bytesRequested;

        public BoundedFileReader(FileStream stream, long readBudget)
        {
            _stream = stream;
            _readBudget = readBudget;
            Length = stream.Length;
        }

        public long Length { get; }

        public async ValueTask<byte[]> ReadBytesAsync(
            long offset,
            int byteCount,
            CancellationToken cancellationToken)
        {
            var bytes = new byte[byteCount];
            await ReadAsync(offset, bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }

        public async ValueTask ReadAsync(
            long offset,
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRange(offset, destination.Length);
            // The parser follows offsets declared by untrusted PE bytes. Account for every
            // request before issuing I/O so cyclic or oversized tables cannot amplify reads.
            if (_bytesRequested > _readBudget - destination.Length)
            {
                throw Invalid("PE inspection read budget exceeded.");
            }
            _bytesRequested += destination.Length;

            var read = 0;
            while (read < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Positional reads avoid mutable stream-position state while nested PE tables
                // are mapped, and make the requested offset explicit for range validation.
                var count = await RandomAccess.ReadAsync(
                        _stream.SafeFileHandle,
                        destination[read..],
                        offset + read,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    throw Invalid("PE file was truncated while being inspected.");
                }
                read += count;
            }
        }

        public void EnsureRange(long offset, int byteCount)
        {
            if (offset < 0
                || byteCount < 0
                || offset > Length
                || byteCount > Length - offset)
            {
                throw Invalid("PE file range is truncated or overflows the artifact.");
            }
        }
    }

    private sealed record Section(
        uint VirtualAddress,
        uint MappedSize,
        uint RawPointer,
        uint RawSize);

    private sealed class InvalidPeException(string message) : Exception(message);

    private static long Add(long left, long right, string label)
    {
        if (left < 0 || right < 0 || left > long.MaxValue - right)
        {
            throw Invalid($"PE {label} overflows Int64.");
        }
        return left + right;
    }

    private static InvalidPeException Invalid(string message) => new(message);
}
