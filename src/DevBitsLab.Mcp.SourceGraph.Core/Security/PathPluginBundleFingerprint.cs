using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevBitsLab.Mcp.SourceGraph.Core.Security;

/// <summary>
/// Result of hashing a path-plugin bundle without loading any assembly.
/// </summary>
public sealed record PathPluginBundleFingerprintResult(
    bool IsSuccess,
    string? Fingerprint,
    ExecutionTrustReason Reason)
{
    public string ReasonCode => ExecutionTrustReasonCodes.For(Reason);
}

/// <summary>
/// Creates a deterministic identity for a path plugin's complete on-disk bundle.
/// </summary>
/// <remarks>
/// The hash covers the entry assembly's normalized relative path plus every file's normalized
/// relative path, length, and bytes in ordinal path order. Reparse points are rejected rather
/// than followed, so links cannot escape the bundle or form traversal cycles.
/// </remarks>
public static class PathPluginBundleFingerprint
{
    private static readonly byte[] _domainSeparator =
        "MedInteropLens.PathPluginBundle/v1"u8.ToArray();

    /// <summary>
    /// Hashes the directory containing <paramref name="entryAssemblyPath"/>, or an explicit
    /// <paramref name="bundleRoot"/> when supplied. This method is read-only and fail-closed:
    /// expected path and I/O failures are represented in the returned result.
    /// </summary>
    public static PathPluginBundleFingerprintResult Compute(
        string entryAssemblyPath,
        string? bundleRoot = null)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyPath)
            || (bundleRoot is not null && string.IsNullOrWhiteSpace(bundleRoot)))
        {
            return Failure(ExecutionTrustReason.InvalidRequest);
        }

        string entryPath;
        string rootPath;
        try
        {
            entryPath = Path.GetFullPath(entryAssemblyPath);
            rootPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(
                    bundleRoot
                    ?? Path.GetDirectoryName(entryPath)
                    ?? string.Empty));
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return Failure(ExecutionTrustReason.InvalidRequest);
        }

        if (!Directory.Exists(rootPath))
        {
            return Failure(ExecutionTrustReason.PathPluginBundleMissing);
        }
        if (!File.Exists(entryPath))
        {
            return Failure(ExecutionTrustReason.PathPluginEntryMissing);
        }
        if (!IsSameOrDescendant(rootPath, entryPath)
            || string.Equals(rootPath, entryPath, PathComparison))
        {
            return Failure(ExecutionTrustReason.PathPluginEntryOutsideBundle);
        }

        var manifestResult = CaptureBundleManifest(rootPath);
        if (manifestResult.Reason != ExecutionTrustReason.Allowed)
        {
            return Failure(manifestResult.Reason);
        }

        var manifest = manifestResult.Manifest!;
        var entryFile = manifest.Entries.SingleOrDefault(
            file => string.Equals(
                file.FullPath,
                entryPath,
                PathComparison)
                && !file.IsDirectory);
        if (entryFile is null)
        {
            return Failure(ExecutionTrustReason.PathPluginEntryMissing);
        }
        var entryRelativePath = entryFile.RelativePath;

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendFramedBytes(hash, _domainSeparator);
            AppendFramedString(hash, entryRelativePath);
            Span<byte> lengthBytes = stackalloc byte[sizeof(long)];

            foreach (var file in manifest.Entries.Where(entry => !entry.IsDirectory))
            {
                var beforeRead = CaptureSingleEntry(rootPath, file.FullPath);
                if (beforeRead.Reason != ExecutionTrustReason.Allowed
                    || beforeRead.Entry is null
                    || !HasSameManifestIdentity(file, beforeRead.Entry))
                {
                    return Failure(
                        beforeRead.Reason == ExecutionTrustReason.Allowed
                            ? ExecutionTrustReason.PathPluginFingerprintReadFailed
                            : beforeRead.Reason);
                }

                AppendFramedString(hash, file.RelativePath);
                using var stream = new FileStream(
                    file.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                var expectedLength = file.Length;
                if (stream.Length != expectedLength)
                {
                    return Failure(
                        ExecutionTrustReason.PathPluginFingerprintReadFailed);
                }
                BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, expectedLength);
                hash.AppendData(lengthBytes);

                var buffer = new byte[64 * 1024];
                long bytesRead = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    bytesRead += read;
                    if (bytesRead > expectedLength)
                    {
                        return Failure(
                            ExecutionTrustReason.PathPluginFingerprintReadFailed);
                    }
                }
                if (bytesRead != expectedLength)
                {
                    return Failure(
                        ExecutionTrustReason.PathPluginFingerprintReadFailed);
                }

                var afterRead = CaptureSingleEntry(rootPath, file.FullPath);
                if (afterRead.Reason != ExecutionTrustReason.Allowed
                    || afterRead.Entry is null
                    || !HasSameManifestIdentity(file, afterRead.Entry))
                {
                    return Failure(
                        afterRead.Reason == ExecutionTrustReason.Allowed
                            ? ExecutionTrustReason.PathPluginFingerprintReadFailed
                            : afterRead.Reason);
                }
            }

            var finalManifest = CaptureBundleManifest(rootPath);
            if (finalManifest.Reason != ExecutionTrustReason.Allowed
                || finalManifest.Manifest is null
                || !HasSameManifest(manifest, finalManifest.Manifest))
            {
                return Failure(
                    finalManifest.Reason == ExecutionTrustReason.Allowed
                        ? ExecutionTrustReason.PathPluginFingerprintReadFailed
                        : finalManifest.Reason);
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            return new PathPluginBundleFingerprintResult(
                true,
                $"sha256:{digest}",
                ExecutionTrustReason.Allowed);
        }
        catch (Exception ex) when (IsReadException(ex))
        {
            return Failure(ExecutionTrustReason.PathPluginFingerprintReadFailed);
        }
    }

    /// <summary>
    /// Security-internal identity check shared with the external trust-file boundary. Existing
    /// files must be ordinary, single-link files and, on Windows, have no alternate streams.
    /// Missing paths are left to the caller's normal missing-file handling.
    /// </summary>
    internal static bool HasStandaloneRegularFileIdentity(string path)
    {
        if (!File.Exists(path)) return true;
        try
        {
            var attributes = File.GetAttributes(path);
            return CaptureRegularFileMetadata(path, attributes).Reason
                == ExecutionTrustReason.Allowed;
        }
        catch (Exception ex) when (IsReadException(ex) || IsPathException(ex))
        {
            return false;
        }
    }

    private static BundleManifestResult CaptureBundleManifest(string rootPath)
    {
        if (ContainsReparsePoint(rootPath))
        {
            return new BundleManifestResult(
                null,
                ExecutionTrustReason.PathPluginBundleContainsReparsePoint);
        }

        var entries = new List<BundleEntry>();
        var directories = new Stack<string>();
        directories.Push(rootPath);
        try
        {
            while (directories.Count > 0)
            {
                var directory = directories.Pop();
                if (ContainsReparsePoint(directory))
                {
                    return new BundleManifestResult(
                        null,
                        ExecutionTrustReason.PathPluginBundleContainsReparsePoint);
                }
                var directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    return new BundleManifestResult(
                        null,
                        ExecutionTrustReason.PathPluginBundleContainsReparsePoint);
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return new BundleManifestResult(
                            null,
                            ExecutionTrustReason.PathPluginBundleContainsReparsePoint);
                    }
                    if (!TryNormalizeRelativePath(
                            Path.GetRelativePath(rootPath, entry),
                            out var relativePath))
                    {
                        return new BundleManifestResult(
                            null,
                            ExecutionTrustReason.PathPluginFingerprintReadFailed);
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        entries.Add(
                            new BundleEntry(
                                entry,
                                relativePath,
                                attributes,
                                IsDirectory: true,
                                Length: 0,
                                LastWriteStamp:
                                    Directory.GetLastWriteTimeUtc(entry).Ticks));
                        directories.Push(entry);
                        continue;
                    }

                    var metadata = CaptureRegularFileMetadata(entry, attributes);
                    if (metadata.Reason != ExecutionTrustReason.Allowed)
                    {
                        return new BundleManifestResult(null, metadata.Reason);
                    }
                    entries.Add(
                        new BundleEntry(
                            entry,
                            relativePath,
                            attributes,
                            IsDirectory: false,
                            metadata.Length,
                            metadata.LastWriteStamp));
                }
            }
        }
        catch (Exception ex) when (IsReadException(ex) || IsPathException(ex))
        {
            return new BundleManifestResult(
                null,
                ExecutionTrustReason.PathPluginFingerprintReadFailed);
        }

        var orderedEntries = entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (orderedEntries
            .Select(entry => entry.RelativePath)
            .Distinct(StringComparer.Ordinal)
            .Count() != orderedEntries.Length)
        {
            return new BundleManifestResult(
                null,
                ExecutionTrustReason.PathPluginFingerprintReadFailed);
        }
        return new BundleManifestResult(
            new BundleManifest(orderedEntries),
            ExecutionTrustReason.Allowed);
    }

    private static BundleEntryResult CaptureSingleEntry(
        string rootPath,
        string fullPath)
    {
        try
        {
            if (!IsSameOrDescendant(rootPath, fullPath)
                || string.Equals(rootPath, fullPath, PathComparison)
                || ContainsReparsePoint(fullPath))
            {
                return new BundleEntryResult(
                    null,
                    ExecutionTrustReason.PathPluginBundleContainsReparsePoint);
            }

            var attributes = File.GetAttributes(fullPath);
            if (!TryNormalizeRelativePath(
                    Path.GetRelativePath(rootPath, fullPath),
                    out var relativePath))
            {
                return new BundleEntryResult(
                    null,
                    ExecutionTrustReason.PathPluginFingerprintReadFailed);
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return new BundleEntryResult(
                    new BundleEntry(
                        fullPath,
                        relativePath,
                        attributes,
                        IsDirectory: true,
                        Length: 0,
                        LastWriteStamp:
                            Directory.GetLastWriteTimeUtc(fullPath).Ticks),
                    ExecutionTrustReason.Allowed);
            }

            var metadata = CaptureRegularFileMetadata(fullPath, attributes);
            return metadata.Reason == ExecutionTrustReason.Allowed
                ? new BundleEntryResult(
                    new BundleEntry(
                        fullPath,
                        relativePath,
                        attributes,
                        IsDirectory: false,
                        metadata.Length,
                        metadata.LastWriteStamp),
                    ExecutionTrustReason.Allowed)
                : new BundleEntryResult(null, metadata.Reason);
        }
        catch (Exception ex) when (IsReadException(ex) || IsPathException(ex))
        {
            return new BundleEntryResult(
                null,
                ExecutionTrustReason.PathPluginFingerprintReadFailed);
        }
    }

    private static RegularFileMetadataResult CaptureRegularFileMetadata(
        string path,
        FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory
                           | FileAttributes.ReparsePoint
                           | FileAttributes.Device)) != 0)
        {
            return new RegularFileMetadataResult(
                0,
                0,
                ExecutionTrustReason.PathPluginBundleContainsNonRegularFile);
        }

        if (OperatingSystem.IsWindows())
        {
            return CaptureWindowsRegularFileMetadata(path);
        }
        if (OperatingSystem.IsLinux()
            && RuntimeInformation.ProcessArchitecture
                is Architecture.X64 or Architecture.Arm64)
        {
            return CaptureLinuxRegularFileMetadata(path);
        }
        if (OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture
                is Architecture.X64 or Architecture.Arm64)
        {
            return CaptureMacRegularFileMetadata(path);
        }

        // Unknown Unix ABI: fail closed rather than opening a FIFO/device to discover whether it
        // is seekable (which itself can block indefinitely).
        return new RegularFileMetadataResult(
            0,
            0,
            ExecutionTrustReason.PathPluginBundleHasUnsupportedFileIdentity);
    }

    private static RegularFileMetadataResult CaptureLinuxRegularFileMetadata(
        string path)
    {
        try
        {
            LinuxStatProjection status;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    if (LinuxX64LStat(path, out var x64Status) != 0)
                    {
                        return MetadataReadFailure();
                    }
                    status = ProjectLinuxStat(x64Status);
                    break;
                case Architecture.Arm64:
                    if (LinuxArm64LStat(path, out var arm64Status) != 0)
                    {
                        return MetadataReadFailure();
                    }
                    status = ProjectLinuxStat(arm64Status);
                    break;
                default:
                    return UnsupportedFileIdentity();
            }

            if ((status.Mode & UnixFileTypeMask) != UnixRegularFile)
            {
                return NonRegularFile();
            }
            if (status.LinkCount != 1)
            {
                return UnsupportedFileIdentity();
            }
            return new RegularFileMetadataResult(
                status.Size,
                CombineUnixTimestamp(
                    status.ModifiedTime.Seconds,
                    status.ModifiedTime.Nanoseconds),
                ExecutionTrustReason.Allowed);
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                   or EntryPointNotFoundException
                                   or MarshalDirectiveException)
        {
            return UnsupportedFileIdentity();
        }
    }

    /// <summary>
    /// Decodes the two Linux LP64 <c>struct stat</c> layouts supported by the native
    /// identity check. Kept internal so tests can verify the ABI field offsets without
    /// executing Linux native code on the Windows test host.
    /// </summary>
    internal static (
        uint Mode,
        ulong LinkCount,
        long Size,
        long ModifiedSeconds,
        long ModifiedNanoseconds) DecodeLinuxStatForValidation(
            ReadOnlySpan<byte> nativeBytes,
            Architecture architecture)
    {
        var status = architecture switch
        {
            Architecture.X64 when nativeBytes.Length == LinuxX64StatSize =>
                ProjectLinuxStat(MemoryMarshal.Read<LinuxX64Stat>(nativeBytes)),
            Architecture.Arm64 when nativeBytes.Length == LinuxArm64StatSize =>
                ProjectLinuxStat(MemoryMarshal.Read<LinuxArm64Stat>(nativeBytes)),
            Architecture.X64 or Architecture.Arm64 =>
                throw new ArgumentException(
                    "The native stat buffer length does not match the selected ABI.",
                    nameof(nativeBytes)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(architecture),
                architecture,
                "The Linux stat ABI is not supported."),
        };

        return (
            status.Mode,
            status.LinkCount,
            status.Size,
            status.ModifiedTime.Seconds,
            status.ModifiedTime.Nanoseconds);
    }

    private static LinuxStatProjection ProjectLinuxStat(
        LinuxX64Stat status) =>
        new(
            status.Mode,
            status.LinkCount,
            status.Size,
            status.ModifiedTime);

    private static LinuxStatProjection ProjectLinuxStat(
        LinuxArm64Stat status) =>
        new(
            status.Mode,
            status.LinkCount,
            status.Size,
            status.ModifiedTime);

    private static RegularFileMetadataResult CaptureMacRegularFileMetadata(
        string path)
    {
        try
        {
            if (MacLStat(path, out var status) != 0)
            {
                return MetadataReadFailure();
            }
            if ((status.Mode & UnixFileTypeMask) != UnixRegularFile)
            {
                return NonRegularFile();
            }
            if (status.LinkCount != 1)
            {
                return UnsupportedFileIdentity();
            }
            return new RegularFileMetadataResult(
                status.Size,
                CombineUnixTimestamp(
                    status.ModifiedTime.Seconds,
                    status.ModifiedTime.Nanoseconds),
                ExecutionTrustReason.Allowed);
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                   or EntryPointNotFoundException
                                   or MarshalDirectiveException)
        {
            return UnsupportedFileIdentity();
        }
    }

    private static long CombineUnixTimestamp(long seconds, long nanoseconds) =>
        unchecked((seconds * 1_000_000_007L) + nanoseconds);

    private static RegularFileMetadataResult NonRegularFile() =>
        new(
            0,
            0,
            ExecutionTrustReason.PathPluginBundleContainsNonRegularFile);

    private static RegularFileMetadataResult UnsupportedFileIdentity() =>
        new(
            0,
            0,
            ExecutionTrustReason.PathPluginBundleHasUnsupportedFileIdentity);

    private static RegularFileMetadataResult MetadataReadFailure() =>
        new(
            0,
            0,
            ExecutionTrustReason.PathPluginFingerprintReadFailed);

    private static RegularFileMetadataResult CaptureWindowsRegularFileMetadata(
        string path)
    {
        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.RandomAccess);
            if (GetFileType(handle) != FileTypeDisk)
            {
                return new RegularFileMetadataResult(
                    0,
                    0,
                    ExecutionTrustReason.PathPluginBundleContainsNonRegularFile);
            }
            if (!GetFileInformationByHandle(handle, out var information))
            {
                return new RegularFileMetadataResult(
                    0,
                    0,
                    ExecutionTrustReason.PathPluginFingerprintReadFailed);
            }
            if (information.NumberOfLinks != 1
                || HasAlternateDataStream(path) != AlternateStreamResult.None)
            {
                return new RegularFileMetadataResult(
                    0,
                    0,
                    ExecutionTrustReason.PathPluginBundleHasUnsupportedFileIdentity);
            }

            var length =
                ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            var lastWriteStamp =
                ((long)information.LastWriteTimeHigh << 32)
                | information.LastWriteTimeLow;
            return new RegularFileMetadataResult(
                length,
                lastWriteStamp,
                ExecutionTrustReason.Allowed);
        }
        catch (Exception ex) when (IsReadException(ex) || IsPathException(ex))
        {
            return new RegularFileMetadataResult(
                0,
                0,
                ExecutionTrustReason.PathPluginFingerprintReadFailed);
        }
    }

    private static AlternateStreamResult HasAlternateDataStream(string path)
    {
        var findHandle = FindFirstStreamW(
            path,
            0,
            out var streamData,
            0);
        if (findHandle == InvalidFindHandle)
        {
            return AlternateStreamResult.InspectionFailed;
        }

        try
        {
            do
            {
                if (!string.Equals(
                        streamData.StreamName,
                        "::$DATA",
                        StringComparison.Ordinal))
                {
                    return AlternateStreamResult.Present;
                }
            }
            while (FindNextStreamW(findHandle, out streamData));

            return Marshal.GetLastPInvokeError() == ErrorHandleEof
                ? AlternateStreamResult.None
                : AlternateStreamResult.InspectionFailed;
        }
        finally
        {
            FindClose(findHandle);
        }
    }

    private static bool HasSameManifest(
        BundleManifest left,
        BundleManifest right) =>
        left.Entries.Count == right.Entries.Count
        && left.Entries
            .Zip(right.Entries)
            .All(pair => HasSameManifestIdentity(pair.First, pair.Second));

    private static bool HasSameManifestIdentity(
        BundleEntry left,
        BundleEntry right) =>
        string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal)
        && left.Attributes == right.Attributes
        && left.IsDirectory == right.IsDirectory
        && left.Length == right.Length
        && left.LastWriteStamp == right.LastWriteStamp;

    private static bool ContainsReparsePoint(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return true;

        var current = root;
        foreach (var segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (IsReadException(ex) || IsPathException(ex))
            {
                return true;
            }
        }
        return false;
    }

    private static void AppendFramedString(IncrementalHash hash, string value) =>
        AppendFramedBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendFramedBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, value.Length);
        hash.AppendData(lengthBytes);
        hash.AppendData(value);
    }

    private static bool TryNormalizeRelativePath(
        string path,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var normalizedSegments = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var normalizedSegment = segments[i].Normalize(NormalizationForm.FormC);
            if (!string.Equals(
                    normalizedSegment,
                    segments[i],
                    StringComparison.Ordinal))
            {
                return false;
            }

            // On Unix a backslash is a legal filename character, so escape it rather than
            // confusing it with the forward-slash separator used by the fingerprint format.
            normalizedSegments[i] = normalizedSegment
                .Replace("%", "%25", StringComparison.Ordinal)
                .Replace("\\", "%5C", StringComparison.Ordinal);
        }
        normalizedPath = string.Join('/', normalizedSegments);
        return normalizedPath.Length > 0;
    }

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        if (string.Equals(parent, candidate, PathComparison)) return true;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static bool IsReadException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    private static PathPluginBundleFingerprintResult Failure(
        ExecutionTrustReason reason) =>
        new(false, null, reason);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private const uint FileTypeDisk = 0x0001;
    private const int ErrorHandleEof = 38;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const int LinuxX64StatSize = 144;
    private const int LinuxArm64StatSize = 128;
    private static readonly IntPtr InvalidFindHandle = new(-1);

    [DllImport(
        "libc",
        EntryPoint = "lstat",
        SetLastError = true)]
    private static extern int LinuxX64LStat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out LinuxX64Stat status);

    [DllImport(
        "libc",
        EntryPoint = "lstat",
        SetLastError = true)]
    private static extern int LinuxArm64LStat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out LinuxArm64Stat status);

    [DllImport(
        "libc",
        EntryPoint = "lstat",
        SetLastError = true)]
    private static extern int MacLStat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out MacStat status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle fileHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation information);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FindFirstStreamW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(
        string fileName,
        int infoLevel,
        out Win32FindStreamData data,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FindNextStreamW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(
        IntPtr findHandle,
        out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    // glibc sysdeps/unix/sysv/linux/x86/bits/struct_stat.h, x86_64 LP64 ABI.
    [StructLayout(LayoutKind.Explicit, Size = LinuxX64StatSize)]
    private struct LinuxX64Stat
    {
        [FieldOffset(0)]
        public ulong Device;

        [FieldOffset(8)]
        public ulong Inode;

        [FieldOffset(16)]
        public ulong LinkCount;

        [FieldOffset(24)]
        public uint Mode;

        [FieldOffset(28)]
        public uint UserId;

        [FieldOffset(32)]
        public uint GroupId;

        [FieldOffset(36)]
        public int Padding;

        [FieldOffset(40)]
        public ulong RawDevice;

        [FieldOffset(48)]
        public long Size;

        [FieldOffset(56)]
        public long BlockSize;

        [FieldOffset(64)]
        public long Blocks;

        [FieldOffset(72)]
        public UnixTimespec AccessTime;

        [FieldOffset(88)]
        public UnixTimespec ModifiedTime;

        [FieldOffset(104)]
        public UnixTimespec ChangedTime;

        [FieldOffset(120)]
        public long Reserved0;

        [FieldOffset(128)]
        public long Reserved1;

        [FieldOffset(136)]
        public long Reserved2;
    }

    // glibc sysdeps/unix/sysv/linux/bits/struct_stat.h and bits/typesizes.h,
    // generic Linux LP64 ABI used by AArch64. Unlike x86_64, mode precedes a
    // 32-bit link count.
    [StructLayout(LayoutKind.Explicit, Size = LinuxArm64StatSize)]
    private struct LinuxArm64Stat
    {
        [FieldOffset(0)]
        public ulong Device;

        [FieldOffset(8)]
        public ulong Inode;

        [FieldOffset(16)]
        public uint Mode;

        [FieldOffset(20)]
        public uint LinkCount;

        [FieldOffset(24)]
        public uint UserId;

        [FieldOffset(28)]
        public uint GroupId;

        [FieldOffset(32)]
        public ulong RawDevice;

        [FieldOffset(40)]
        public ulong Padding1;

        [FieldOffset(48)]
        public long Size;

        [FieldOffset(56)]
        public int BlockSize;

        [FieldOffset(60)]
        public int Padding2;

        [FieldOffset(64)]
        public long Blocks;

        [FieldOffset(72)]
        public UnixTimespec AccessTime;

        [FieldOffset(88)]
        public UnixTimespec ModifiedTime;

        [FieldOffset(104)]
        public UnixTimespec ChangedTime;

        [FieldOffset(120)]
        public int Reserved0;

        [FieldOffset(124)]
        public int Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacStat
    {
        public int Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public int RawDevice;
        public UnixTimespec AccessTime;
        public UnixTimespec ModifiedTime;
        public UnixTimespec ChangedTime;
        public UnixTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long QuadSpare0;
        public long QuadSpare1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    private sealed record BundleManifest(IReadOnlyList<BundleEntry> Entries);

    private sealed record BundleEntry(
        string FullPath,
        string RelativePath,
        FileAttributes Attributes,
        bool IsDirectory,
        long Length,
        long LastWriteStamp);

    private sealed record BundleManifestResult(
        BundleManifest? Manifest,
        ExecutionTrustReason Reason);

    private sealed record BundleEntryResult(
        BundleEntry? Entry,
        ExecutionTrustReason Reason);

    private readonly record struct LinuxStatProjection(
        uint Mode,
        ulong LinkCount,
        long Size,
        UnixTimespec ModifiedTime);

    private sealed record RegularFileMetadataResult(
        long Length,
        long LastWriteStamp,
        ExecutionTrustReason Reason);

    private enum AlternateStreamResult
    {
        None,
        Present,
        InspectionFailed,
    }
}
