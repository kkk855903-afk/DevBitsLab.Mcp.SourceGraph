using DevBitsLab.Mcp.SourceGraph.Embeddings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Single call site for the embedding-cache configuration verbs (status / pull / remove / verify)
/// shared between the <c>sourcegraph-mcp embeddings</c> CLI subcommand group and the matching
/// <c>embeddings_*</c> MCP tools. Wraps <see cref="ModelStore"/> with manifest-aware reporting and
/// a few invariants the surface layers shouldn't have to repeat.
///
/// <para>
/// Why a dedicated service: the CLI router (no DI scope, one-shot) and the MCP tool methods
/// (resolved through DI on the long-running <c>serve</c> path) need identical behaviour. Putting
/// the logic here means there's one definition of "what does <c>status</c> return," not two
/// drifting copies.
/// </para>
/// </summary>
public sealed class EmbeddingsManager
{
    private readonly ModelStore _store;
    private readonly EmbeddingModelInfo _defaultModel;
    private readonly ILogger _logger;

    public EmbeddingsManager(ModelStore store, EmbeddingModelInfo defaultModel, ILogger? logger = null)
    {
        _store = store;
        _defaultModel = defaultModel;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Get a status snapshot for <paramref name="modelId"/> (or the default when null). Computes
    /// SHA-256 of every cached file, reports presence/size, and probes the cache volume's free
    /// disk. Files listed in the manifest but missing on disk show <c>Present = false</c>.
    /// </summary>
    public async Task<EmbeddingsStatus> GetStatusAsync(string? modelId, CancellationToken ct = default)
        => await BuildStatusAsync(modelId, populateMatch: false, ct).ConfigureAwait(false);

    /// <summary>
    /// Synchronously download the manifest for <paramref name="modelId"/> via <see cref="ModelStore.EnsureAsync"/>
    /// and return the post-download status snapshot. Idempotent — when the cache is already
    /// populated, <see cref="ModelStore.EnsureAsync"/> short-circuits without HTTP calls.
    /// </summary>
    public async Task<EmbeddingsStatus> PullAsync(string? modelId, CancellationToken ct = default)
    {
        var resolved = ResolveModelInfo(modelId);
        var manifest = ResolveManifest(resolved.ModelId);
        _logger.LogInformation("Pulling embedding model {Model} into {Dir}", resolved.ModelId, _store.DirectoryFor(resolved.ModelId));
        await _store.EnsureAsync(resolved.ModelId, manifest, ct).ConfigureAwait(false);
        return await BuildStatusAsync(modelId, populateMatch: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove the cache directory for one model (default = active) or every cached model when
    /// <paramref name="all"/> is true. Rejects the ambiguous combination of an explicit
    /// <paramref name="modelId"/> + <paramref name="all"/>=true.
    /// </summary>
    public async Task<RemoveResult> RemoveAsync(string? modelId, bool all, CancellationToken ct = default)
    {
        if (modelId is not null && all)
        {
            throw new ArgumentException("`modelId` and `all = true` cannot be combined; pick exactly one.");
        }
        if (all)
        {
            _logger.LogInformation("Removing every cached embedding model directory");
            var result = await _store.RemoveAllAsync(ct).ConfigureAwait(false);
            return new RemoveResult(ModelId: null, RemovedDirs: result.RemovedDirs, FreedBytes: result.FreedBytes);
        }
        var resolved = ResolveModelInfo(modelId);
        var dir = _store.DirectoryFor(resolved.ModelId);
        var existed = Directory.Exists(dir);
        _logger.LogInformation("Removing cached embedding model {Model} at {Dir}", resolved.ModelId, dir);
        var freed = await _store.RemoveAsync(resolved.ModelId, ct).ConfigureAwait(false);
        var removed = existed ? new[] { dir } : Array.Empty<string>();
        return new RemoveResult(ModelId: resolved.ModelId, RemovedDirs: removed, FreedBytes: freed);
    }

    /// <summary>
    /// Recompute SHA-256 for every cached file and compare against the manifest's pinned SHAs.
    /// When a manifest entry has no pinned SHA, <c>Match</c> stays null (informational mode).
    /// </summary>
    public async Task<EmbeddingsStatus> VerifyAsync(string? modelId, CancellationToken ct = default)
        => await BuildStatusAsync(modelId, populateMatch: true, ct).ConfigureAwait(false);

    private async Task<EmbeddingsStatus> BuildStatusAsync(string? modelId, bool populateMatch, CancellationToken ct)
    {
        var resolved = ResolveModelInfo(modelId);
        var manifest = ResolveManifest(resolved.ModelId);
        var cacheDir = _store.DirectoryFor(resolved.ModelId);

        var files = new List<EmbeddingsFileStatus>(manifest.Count);
        foreach (var entry in manifest)
        {
            var path = _store.FilePath(resolved.ModelId, entry.ResolvedLocalName);
            var present = File.Exists(path);
            long? size = present ? new FileInfo(path).Length : null;
            string? computed = present ? await _store.ComputeShaAsync(resolved.ModelId, entry.ResolvedLocalName, ct).ConfigureAwait(false) : null;
            bool? match = null;
            if (populateMatch && entry.ExpectedSha256 is not null && computed is not null)
            {
                match = string.Equals(computed, entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            files.Add(new EmbeddingsFileStatus(
                LocalName: entry.ResolvedLocalName,
                RemotePath: entry.RemotePath,
                Present: present,
                SizeBytes: size,
                ComputedSha: computed,
                PinnedSha: entry.ExpectedSha256,
                Match: match));
        }

        var freeDisk = TryGetFreeDiskBytes(cacheDir);
        return new EmbeddingsStatus(
            ModelId: resolved.ModelId,
            Dimension: resolved.Dimension,
            CacheDir: cacheDir,
            Files: files,
            FreeDiskBytes: freeDisk);
    }

    private EmbeddingModelInfo ResolveModelInfo(string? modelId) =>
        string.IsNullOrEmpty(modelId) || string.Equals(modelId, _defaultModel.ModelId, StringComparison.Ordinal)
            ? _defaultModel
            : new EmbeddingModelInfo(modelId, _defaultModel.Dimension);

    private static IReadOnlyList<ModelFile> ResolveManifest(string modelId) =>
        string.Equals(modelId, DefaultEmbeddingModel.ModelId, StringComparison.Ordinal)
            ? DefaultEmbeddingModel.Manifest
            : new[] { new ModelFile("model.onnx"), new ModelFile("tokenizer.json") };

    /// <summary>
    /// Best-effort free-disk probe for the volume containing <paramref name="cacheDir"/>. Walks
    /// the parent chain when the directory doesn't exist yet so a fresh checkout still gets a
    /// useful answer; returns null when nothing along the chain is mounted.
    /// </summary>
    private static long? TryGetFreeDiskBytes(string cacheDir)
    {
        var probe = cacheDir;
        while (!string.IsNullOrEmpty(probe))
        {
            try
            {
                if (Directory.Exists(probe))
                {
                    var root = Path.GetPathRoot(probe);
                    if (string.IsNullOrEmpty(root)) return null;
                    return new DriveInfo(root).AvailableFreeSpace;
                }
            }
            catch (IOException) { /* drive not ready / unavailable — try parent */ }
            catch (UnauthorizedAccessException) { /* path probe denied — try parent */ }
            catch (ArgumentException) { /* malformed root — try parent */ }
            var parent = Path.GetDirectoryName(probe);
            if (string.Equals(parent, probe, StringComparison.Ordinal)) break;
            probe = parent;
        }
        return null;
    }
}

/// <summary>
/// Status snapshot for the embedding cache associated with one model id. Returned by
/// <see cref="EmbeddingsManager.GetStatusAsync"/> / <see cref="EmbeddingsManager.PullAsync"/> /
/// <see cref="EmbeddingsManager.VerifyAsync"/>.
/// </summary>
public sealed record EmbeddingsStatus(
    string ModelId,
    int Dimension,
    string CacheDir,
    IReadOnlyList<EmbeddingsFileStatus> Files,
    long? FreeDiskBytes);

/// <summary>
/// Per-file status: presence, computed/pinned SHA-256 (lower-case hex), and the boolean match
/// when both are present. <see cref="Match"/> is null when verification mode wasn't requested
/// or the manifest has no pinned SHA yet.
/// </summary>
public sealed record EmbeddingsFileStatus(
    string LocalName,
    string RemotePath,
    bool Present,
    long? SizeBytes,
    string? ComputedSha,
    string? PinnedSha,
    bool? Match);

/// <summary>
/// Result of <see cref="EmbeddingsManager.RemoveAsync"/>: the model id (null when --all wiped
/// every directory), the absolute paths removed, and the total bytes freed.
/// </summary>
public sealed record RemoveResult(
    string? ModelId,
    IReadOnlyList<string> RemovedDirs,
    long FreedBytes);
