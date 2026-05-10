using System.Buffers;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// On-disk cache resolver for embedding model files. Lives at
/// <c>~/.cache/devbitslab.sourcegraph/models/&lt;id&gt;/</c> on Unix and
/// <c>%LOCALAPPDATA%/devbitslab.sourcegraph/models/&lt;id&gt;/</c> on Windows.
///
/// <para>
/// The downloader is best-effort and idempotent: if every required file already exists
/// (and SHA-256 matches when a manifest is provided), <see cref="EnsureAsync"/> is a
/// no-op. Otherwise it streams from Hugging Face into a <c>.tmp</c> sibling and renames
/// atomically once the SHA matches. Network failures surface as <see cref="ModelDownloadException"/>
/// and the caller (<see cref="EmbeddingsHostedService"/>) treats them as "embeddings disabled
/// for this session" rather than failing the whole indexer.
/// </para>
/// </summary>
public sealed class ModelStore
{
    private readonly string _baseDir;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public ModelStore(ILogger? logger = null, HttpClient? http = null, string? overrideBaseDir = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _baseDir = overrideBaseDir ?? DefaultCacheDir();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Path that holds the cached files for a specific model id.
    /// <para>
    /// <paramref name="modelId"/> reaches us from CLI flags and MCP tool arguments — both
    /// untrusted — and is fed into <see cref="RemoveAsync"/> / <see cref="RemoveAllAsync"/>'s
    /// recursive deletes. We defend against path-traversal in two layers:
    /// </para>
    /// <list type="number">
    ///   <item>Reject any input that contains <c>..</c> or single-dot segments outright (HF-style
    ///         ids never contain those — version numbers like <c>v1.5</c> are fine because the
    ///         check is for whole segments, not substrings).</item>
    ///   <item>After <see cref="SanitiseId"/>, resolve the joined path with
    ///         <see cref="Path.GetFullPath(string)"/> and require the result to be a strict
    ///         subdirectory of <see cref="_baseDir"/>. The strict-subdir check rejects empty
    ///         ids and bare-dot ids that would resolve to <see cref="_baseDir"/> itself
    ///         (deleting which would wipe the entire cache).</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="modelId"/> is empty, contains path-traversal segments, or
    /// resolves outside <see cref="_baseDir"/>.
    /// </exception>
    public string DirectoryFor(string modelId)
    {
        ValidateModelId(modelId);
        var sanitised = SanitiseId(modelId);
        var joined = Path.Join(_baseDir, sanitised);
        // Normalise both sides to absolute paths so the under-base check tolerates a relative
        // _baseDir (e.g. tests with overrideBaseDir = "./tmp/..." after a chdir).
        var resolved = Path.GetFullPath(joined);
        var baseResolved = Path.GetFullPath(_baseDir);
        var baseWithSep = baseResolved.EndsWith(Path.DirectorySeparatorChar)
            ? baseResolved
            : baseResolved + Path.DirectorySeparatorChar;
        // Strict subdirectory: the resolved path must START WITH `_baseDir<sep>`, not equal it.
        // A bare `.` would resolve to `_baseDir` itself, which `Directory.Delete(..., recursive)`
        // would happily wipe — that's still a privilege escalation against the cache root.
        //
        // Use case-insensitive comparison on platforms where the file system is
        // case-insensitive (Windows, macOS HFS+/APFS-default). Otherwise a drive-letter or
        // directory casing mismatch (e.g. `C:\Users\...` vs `c:\users\...`) would falsely
        // reject a valid path. Linux's case-sensitive default keeps OrdinalIgnoreCase from
        // weakening the security property because path resolution there is itself
        // case-sensitive — `_baseDir` and `resolved` will always have matching casing.
        var pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolved.StartsWith(baseWithSep, pathComparison))
        {
            throw new ArgumentException(
                $"modelId '{modelId}' resolves to '{resolved}', which is not a subdirectory of '{baseResolved}'.",
                nameof(modelId));
        }
        return resolved;
    }

    /// <summary>
    /// Layer-1 path-traversal defense: reject empty / whitespace-only ids and any id whose
    /// segments contain <c>.</c> or <c>..</c>. Run before <see cref="SanitiseId"/> because
    /// sanitisation collapses path separators and would otherwise hide a <c>../</c> segment as
    /// a benign-looking <c>.._</c> substring.
    /// </summary>
    private static void ValidateModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("modelId must not be empty.", nameof(modelId));
        }
        // Split on path separators and look for the first traversal segment. Using a single
        // LINQ pass instead of a foreach+if avoids CodeQL's `cs/missed-where-opportunity`
        // re-flag, and the resulting code reads as "find the bad segment, throw if any" — a
        // closer match to the actual contract.
        var traversalSegment = modelId.Split(new[] { '/', '\\' }, StringSplitOptions.None)
            .FirstOrDefault(segment => segment is "." or "..");
        if (traversalSegment is not null)
        {
            throw new ArgumentException(
                $"modelId '{modelId}' contains a path-traversal segment ('{traversalSegment}'); not allowed.",
                nameof(modelId));
        }
    }

    /// <summary>True when every file the model identity requires is present on disk.</summary>
    public bool IsCached(string modelId)
    {
        var dir = DirectoryFor(modelId);
        if (!Directory.Exists(dir)) return false;
        // We require the ONNX graph and the tokenizer.json at minimum; size verification
        // is left to the caller / the manifest check.
        return File.Exists(Path.Join(dir, "model.onnx"))
            && File.Exists(Path.Join(dir, "tokenizer.json"));
    }

    /// <summary>
    /// Returns the absolute path to a file within the model directory. Does not perform IO.
    /// Use <see cref="IsCached"/> to verify presence first.
    /// </summary>
    public string FilePath(string modelId, string fileName) =>
        Path.Join(DirectoryFor(modelId), fileName);

    /// <summary>
    /// Delete the per-model cache directory for <paramref name="modelId"/>. Tolerates a missing
    /// directory silently (returns 0). Returns the total bytes freed (sum of every file's length
    /// before deletion).
    /// </summary>
    public Task<long> RemoveAsync(string modelId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = DirectoryFor(modelId);
        if (!Directory.Exists(dir)) return Task.FromResult(0L);
        var freed = SumDirectorySize(dir);
        Directory.Delete(dir, recursive: true);
        _logger.LogInformation("Removed cached model {Model} ({Bytes} bytes)", modelId, freed);
        return Task.FromResult(freed);
    }

    /// <summary>
    /// Delete every per-model directory under <see cref="_baseDir"/>. Preserves
    /// <see cref="_baseDir"/> itself in case it's a shared cache root. Returns the list of removed
    /// paths and the total bytes freed.
    /// </summary>
    public Task<RemoveAllResult> RemoveAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_baseDir))
        {
            return Task.FromResult(new RemoveAllResult(Array.Empty<string>(), 0));
        }
        var removed = new List<string>();
        long freed = 0;
        foreach (var dir in Directory.EnumerateDirectories(_baseDir))
        {
            ct.ThrowIfCancellationRequested();
            freed += SumDirectorySize(dir);
            Directory.Delete(dir, recursive: true);
            removed.Add(dir);
        }
        _logger.LogInformation("Removed {Count} cached model directories ({Bytes} bytes)", removed.Count, freed);
        return Task.FromResult(new RemoveAllResult(removed, freed));
    }

    /// <summary>
    /// Compute the SHA-256 hex digest of the file at <c>FilePath(modelId, localName)</c>. Returns
    /// null when the file does not exist. The hex string is lower-case and matches the format used
    /// by <see cref="ModelFile.ExpectedSha256"/>.
    /// </summary>
    public async Task<string?> ComputeShaAsync(string modelId, string localName, CancellationToken ct = default)
    {
        var path = FilePath(modelId, localName);
        if (!File.Exists(path)) return null;
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static long SumDirectorySize(string dir)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* file disappeared mid-walk; skip */ }
        }
        return total;
    }

    /// <summary>
    /// Default cache root. Mirrors the convention `<see cref="DiskCachePath"/>` for the
    /// graph DB but lives under a tool-specific subfolder so swapping models doesn't churn
    /// the graph DB cache.
    /// </summary>
    public static string DefaultCacheDir()
    {
        // ~/.cache (Linux/macOS) or %LOCALAPPDATA% (Windows). XDG_CACHE_HOME wins when set,
        // matching the per-tool conventions already used by ResolvedDbPath.
        var root =
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".cache");
        return Path.Join(root, "devbitslab.sourcegraph", "models");
    }

    private static string SanitiseId(string modelId)
    {
        // jinaai/jina-embeddings-v2-base-code -> jinaai__jina-embeddings-v2-base-code
        // (one folder per model id; the "/" in HF identifiers is illegal as a path component
        // on Windows so we collapse to "__").
        return modelId.Replace('/', '_').Replace('\\', '_');
    }

    /// <summary>
    /// Idempotently fetch the files in <paramref name="manifest"/> from Hugging Face into the
    /// model's cache directory. Each file is written atomically (download to <c>.tmp</c>, verify
    /// SHA-256, rename) so a partial download never looks "cached".
    /// </summary>
    /// <exception cref="ModelDownloadException">
    /// Thrown when the network is unreachable or any file's hash mismatches.
    /// </exception>
    public async Task EnsureAsync(string modelId, IReadOnlyList<ModelFile> manifest, CancellationToken ct = default)
    {
        var dir = DirectoryFor(modelId);
        Directory.CreateDirectory(dir);

        foreach (var entry in manifest)
        {
            var localName = entry.ResolvedLocalName;
            var dest = Path.Join(dir, localName);
            if (File.Exists(dest) && (entry.ExpectedSha256 is null || await VerifySha256Async(dest, entry.ExpectedSha256, ct).ConfigureAwait(false)))
            {
                _logger.LogDebug("Model file {File} already cached", localName);
                continue;
            }

            var url = $"https://huggingface.co/{modelId}/resolve/main/{entry.RemotePath}";
            _logger.LogInformation("Downloading {File} from {Url}", localName, url);

            var tmp = dest + ".tmp";
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var dst = File.Create(tmp))
                {
                    await CopyWithProgressAsync(src, dst, localName, totalBytes, ct).ConfigureAwait(false);
                }

                if (entry.ExpectedSha256 is not null && !await VerifySha256Async(tmp, entry.ExpectedSha256, ct).ConfigureAwait(false))
                {
                    File.Delete(tmp);
                    throw new ModelDownloadException(
                        $"SHA-256 mismatch for {localName} — refusing to use the partial download.");
                }

                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (HttpRequestException ex)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                throw new ModelDownloadException(
                    $"Could not download {localName} from {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                throw new ModelDownloadException($"Timed out downloading {localName}.");
            }
        }
    }

    /// <summary>
    /// Streams <paramref name="src"/> to <paramref name="dst"/> in 64 KiB chunks, emitting an
    /// <see cref="LogLevel.Information"/> progress line at most once per second or every 5 MiB
    /// — whichever comes second. Used during model download so the connected MCP client (which
    /// observes the server's stderr) has visible progress instead of a silent stall.
    /// </summary>
    private async Task CopyWithProgressAsync(Stream src, Stream dst, string fileName, long? totalBytes, CancellationToken ct)
    {
        const int BufferBytes = 64 * 1024;
        const long BytesPerLog = 5L * 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long copied = 0;
            long bytesAtLastLog = 0;
            var lastLog = DateTime.UtcNow;
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, BufferBytes), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;

                var now = DateTime.UtcNow;
                if (copied - bytesAtLastLog >= BytesPerLog && (now - lastLog) >= TimeSpan.FromSeconds(1))
                {
                    LogProgress(fileName, copied, totalBytes);
                    bytesAtLastLog = copied;
                    lastLog = now;
                }
            }
            // Final tick so completion is logged even when the file is below the 5 MiB threshold.
            LogProgress(fileName, copied, totalBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void LogProgress(string fileName, long copied, long? total)
    {
        var copiedMb = copied / (1024d * 1024d);
        if (total is { } t && t > 0)
        {
            var totalMb = t / (1024d * 1024d);
            _logger.LogInformation("Downloading {File} {CopiedMb:F1}/{TotalMb:F1} MB", fileName, copiedMb, totalMb);
        }
        else
        {
            _logger.LogInformation("Downloading {File} {CopiedMb:F1} MB", fileName, copiedMb);
        }
    }

    private static async Task<bool> VerifySha256Async(string path, string expectedHex, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        var hex = Convert.ToHexStringLower(hash);
        return string.Equals(hex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Result of <see cref="ModelStore.RemoveAllAsync"/>: the list of deleted directories
/// and the total bytes freed.</summary>
public sealed record RemoveAllResult(IReadOnlyList<string> RemovedDirs, long FreedBytes);

/// <summary>
/// One file in a model's manifest.
/// <para>
/// <paramref name="RemotePath"/> is the source path on Hugging Face, joined onto
/// <c>https://huggingface.co/{modelId}/resolve/main/</c>. It MAY contain forward slashes
/// (e.g. <c>onnx/model.onnx</c>) when the upstream repo nests the file in a subdirectory.
/// </para>
/// <para>
/// <paramref name="LocalName"/> is the filename to write under the model cache directory.
/// Subdirectories on disk are intentionally not supported here — the cache is a flat layout
/// keyed by model id. When <paramref name="LocalName"/> is null, <see cref="ResolvedLocalName"/>
/// derives it from the trailing path segment of <paramref name="RemotePath"/>.
/// </para>
/// <para><paramref name="ExpectedSha256"/> is optional but recommended for pinned models.</para>
/// </summary>
public sealed record ModelFile(string RemotePath, string? LocalName = null, string? ExpectedSha256 = null)
{
    /// <summary>Filename used inside the cache directory; falls back to the trailing segment of <see cref="RemotePath"/>.</summary>
    public string ResolvedLocalName => LocalName ?? Path.GetFileName(RemotePath);
}

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}
