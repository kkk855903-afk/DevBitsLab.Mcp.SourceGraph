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

    /// <summary>Path that holds the cached files for a specific model id.</summary>
    public string DirectoryFor(string modelId) =>
        Path.Join(_baseDir, SanitiseId(modelId));

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
            var dest = Path.Join(dir, entry.FileName);
            if (File.Exists(dest) && (entry.ExpectedSha256 is null || await VerifySha256Async(dest, entry.ExpectedSha256, ct).ConfigureAwait(false)))
            {
                _logger.LogDebug("Model file {File} already cached", entry.FileName);
                continue;
            }

            var url = $"https://huggingface.co/{modelId}/resolve/main/{entry.FileName}";
            _logger.LogInformation("Downloading {File} from {Url}", entry.FileName, url);

            var tmp = dest + ".tmp";
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var dst = File.Create(tmp))
                {
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }

                if (entry.ExpectedSha256 is not null && !await VerifySha256Async(tmp, entry.ExpectedSha256, ct).ConfigureAwait(false))
                {
                    File.Delete(tmp);
                    throw new ModelDownloadException(
                        $"SHA-256 mismatch for {entry.FileName} — refusing to use the partial download.");
                }

                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (HttpRequestException ex)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                throw new ModelDownloadException(
                    $"Could not download {entry.FileName} from {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                throw new ModelDownloadException($"Timed out downloading {entry.FileName}.");
            }
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

/// <summary>One file in a model's manifest. <see cref="ExpectedSha256"/> is optional but recommended.</summary>
public sealed record ModelFile(string FileName, string? ExpectedSha256 = null);

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}
