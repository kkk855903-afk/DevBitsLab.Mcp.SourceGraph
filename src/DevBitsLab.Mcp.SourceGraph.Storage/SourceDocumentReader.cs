using System.Text;
using System.Security.Cryptography;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

internal sealed record SourceDocumentSnapshot(
    string Path,
    string Content,
    long ByteLength,
    int LineCount,
    DateTimeOffset IndexedAt,
    byte[] Sha256);

internal static class SourceDocumentReader
{
    internal const int MaximumBytes = 4 * 1024 * 1024;

    internal static async Task<SourceDocumentSnapshot?> TryReadAsync(
        string path,
        DateTimeOffset indexedAt,
        CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            // A NUL is a reliable binary sentinel for the source formats we retain. UTF-16 is
            // the exception: its BOM makes alternating NUL bytes part of valid text, so let
            // StreamReader decode those files instead of silently losing searchable source.
            if (bytes.AsSpan().IndexOf((byte)0) >= 0 && !HasUtf16Bom(bytes)) return null;

            await using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var lineCount = content.Length == 0 ? 0 : 1 + content.Count(ch => ch == '\n');
            // Hash the original bytes, rather than the decoded string. Re-encoding would erase
            // distinctions such as the original encoding and BOM, so the digest would no longer
            // identify the exact file content captured by this snapshot.
            return new SourceDocumentSnapshot(
                path, content, bytes.LongLength, lineCount, indexedAt, SHA256.HashData(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool HasUtf16Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 2
        && ((bytes[0] == 0xFF && bytes[1] == 0xFE)
            || (bytes[0] == 0xFE && bytes[1] == 0xFF));
}
