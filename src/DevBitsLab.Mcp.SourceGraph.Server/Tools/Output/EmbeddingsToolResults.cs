using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>embeddings_status</c>, <c>embeddings_pull</c>, and
/// <c>embeddings_verify</c> MCP tools. Mirrors <see cref="EmbeddingsStatus"/> (the top-level
/// record returned by <see cref="EmbeddingsManager"/>) but uses snake_case JSON property names
/// per the SDK convention. The shared shape lets agents chain calls (e.g. pull → verify)
/// without re-parsing.
/// </summary>
public sealed record EmbeddingsStatusResult(
    [property: JsonPropertyName("model_id")] string ModelId,
    int Dimension,
    [property: JsonPropertyName("cache_dir")] string CacheDir,
    IReadOnlyList<EmbeddingsFileRow> Files,
    [property: JsonPropertyName("free_disk_bytes")] long? FreeDiskBytes,
    [property: JsonPropertyName("queues")] IReadOnlyList<EmbeddingsQueueRow>? Queues = null,
    [property: JsonPropertyName("inference")] EmbeddingsInferenceRow? Inference = null);

/// <summary>Live queue counters for one indexed scope.</summary>
public sealed record EmbeddingsQueueRow(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    long Pending,
    long Completed,
    long Dropped);

/// <summary>Process-local wait-time counters for the shared ONNX inference gate.</summary>
public sealed record EmbeddingsInferenceRow(
    [property: JsonPropertyName("query_calls")] long QueryCalls,
    [property: JsonPropertyName("query_wait_ms")] double QueryWaitMs,
    [property: JsonPropertyName("background_calls")] long BackgroundCalls,
    [property: JsonPropertyName("background_wait_ms")] double BackgroundWaitMs);

/// <summary>
/// One file row inside <see cref="EmbeddingsStatusResult.Files"/>. <see cref="Match"/> is null
/// when verification mode wasn't requested, the file is missing, or the manifest has no pinned
/// SHA for this entry (informational mode — the override-model path uses a best-effort
/// manifest with no pinned SHAs, so `Match` stays null while `ComputedSha` is reported).
/// </summary>
public sealed record EmbeddingsFileRow(
    [property: JsonPropertyName("local_name")] string LocalName,
    [property: JsonPropertyName("remote_path")] string RemotePath,
    bool Present,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("computed_sha")] string? ComputedSha,
    [property: JsonPropertyName("pinned_sha")] string? PinnedSha,
    bool? Match);

/// <summary>
/// Typed structured output for <c>embeddings_remove</c>. <see cref="ModelId"/> is null when the
/// caller passed <c>all = true</c> (every cached model directory was wiped). Otherwise it's the
/// id of the single model whose cache directory was removed.
/// </summary>
public sealed record EmbeddingsRemoveResult(
    [property: JsonPropertyName("model_id")] string? ModelId,
    [property: JsonPropertyName("removed_dirs")] IReadOnlyList<string> RemovedDirs,
    [property: JsonPropertyName("freed_bytes")] long FreedBytes);
