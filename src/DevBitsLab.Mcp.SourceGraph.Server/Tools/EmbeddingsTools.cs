using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// MCP tools that mirror the <c>sourcegraph-mcp embeddings</c> CLI subcommand group: inspect
/// (<see cref="EmbeddingsStatusAsync"/>), download (<see cref="EmbeddingsPullAsync"/>), wipe
/// (<see cref="EmbeddingsRemoveAsync"/>), and verify (<see cref="EmbeddingsVerifyAsync"/>) the
/// embedding model cache. All four delegate to the shared <see cref="EmbeddingsManager"/>.
///
/// <para>
/// Mutating tools carry MCP <see cref="ToolAnnotationAttribute"/> hints so spec-aware hosts (e.g.
/// Claude Code) can require explicit user confirmation before invocation. The
/// <c>Use when:</c> prose on each tool reinforces this in human-readable form.
/// </para>
/// </summary>
[McpServerToolType]
public static class EmbeddingsTools
{
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(EmbeddingsStatusResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"is semantic search working?\" or \"where does the embedding model live on disk?\" — call before suggesting a manual cache fix")]
    [Description("Inspect the embedding model cache and live runtime: cache files, active model id, whether ONNX is loaded, idle-unload timing, process working set, model residency estimate, graph DB bytes, and SQLite cache limits.")]
    public static Task<CallToolResult> EmbeddingsStatusAsync(
        EmbeddingsManager manager,
        ICodeEmbeddingGenerator generator,
        ScopeRouter router,
        string? modelId = null) =>
        ToolMetrics.TrackAsync("embeddings_status", null, async () =>
        {
            // Start the stopwatch BEFORE the manager IO so latency_ms reflects whole-tool wall
            // time (manager + render), matching AudienceMetadata's convention. Doing this inside
            // BuildStatusResult would clock only the prose render, missing the SHA-256 sweep
            // that dominates wall time.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var status = await manager.GetStatusAsync(modelId).ConfigureAwait(false);
                var runtime = await BuildRuntimeStatusAsync(status, generator, router)
                    .ConfigureAwait(false);
                return BuildStatusResult(status, verifying: false, prefix: null, sw, runtime);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FileInfo.Length / SHA computation can throw IOException /
                // UnauthorizedAccessException on locked or unreadable cache files. Surface as a
                // structured tool error rather than an unhandled tool failure. Cancellation
                // still propagates.
                return BuildErrorResult($"Status read failed: {ex.Message}", sw);
            }
        });

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(EmbeddingsStatusResult))]
    [ToolAnnotation(IdempotentHint = true)]
    [ToolTrigger("user explicitly asked to download the embedding model — pre-flight before air-gapping or after `embeddings_remove`. Never invoke as a side-effect of debugging.")]
    [Description("Synchronously download the embedding model manifest into the cache. Idempotent: a populated cache is a no-op (no HTTP call, no rewrite). Returns the post-download status snapshot.")]
    public static Task<CallToolResult> EmbeddingsPullAsync(
        EmbeddingsManager manager,
        string? modelId = null) =>
        ToolMetrics.TrackAsync("embeddings_pull", null, async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var status = await manager.PullAsync(modelId).ConfigureAwait(false);
                return BuildStatusResult(status, verifying: false, prefix: "Pull complete.", sw);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Surface ModelDownloadException, IO/permission errors during file create/move,
                // and any other download-path failure as a structured tool error rather than
                // bubbling out as an unhandled tool failure. Cancellation still propagates.
                return BuildErrorResult($"Pull failed: {ex.Message}", sw);
            }
        });

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(EmbeddingsRemoveResult))]
    [ToolAnnotation(DestructiveHint = true, IdempotentHint = true)]
    [ToolTrigger("user explicitly asked to free disk or swap models — never as a side-effect of debugging. Re-downloading the default model takes minutes and ~640 MB of egress.")]
    [Description("Delete the embedding model cache directory. Defaults to the active model. Pass `all = true` to wipe every cached model, or `modelId` to target a specific one. Combining `modelId` with `all = true` is rejected.")]
    public static Task<CallToolResult> EmbeddingsRemoveAsync(
        EmbeddingsManager manager,
        string? modelId = null,
        bool all = false) =>
        ToolMetrics.TrackAsync("embeddings_remove", null, async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await manager.RemoveAsync(modelId, all).ConfigureAwait(false);
                return BuildRemoveResult(result, sw);
            }
            catch (ArgumentException ex)
            {
                return BuildErrorResult(ex.Message, sw);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Catch IO / UnauthorizedAccess / etc. that surface when the cache directory
                // is locked (notably on Windows when the live server still holds an open handle
                // to the ONNX file). Surface as a structured error with a hint rather than an
                // unhandled tool failure. Cancellation still propagates.
                return BuildErrorResult(
                    $"Remove failed: {ex.Message}. The model may be locked by a running server — stop it and retry.",
                    sw);
            }
        });

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(EmbeddingsStatusResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"do my cached files match the pinned hashes?\" — recomputes SHAs and compares against the manifest. Default model has pinned SHAs and reports match=true|false; override `--model` paths use a best-effort manifest with no pin and report match=null (informational only).")]
    [Description("Recompute SHA-256 of every cached file and compare against the manifest's pinned SHAs. The default model ships with pinned SHAs — `match` is true on a healthy cache and false on tampered/corrupt files. Override `--model <id>` paths use a best-effort manifest with no pinned SHAs, so `match` stays null (informational only). When pinned and mismatched, the response is flagged with isError=true.")]
    public static Task<CallToolResult> EmbeddingsVerifyAsync(
        EmbeddingsManager manager,
        string? modelId = null) =>
        ToolMetrics.TrackAsync("embeddings_verify", null, async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var status = await manager.VerifyAsync(modelId).ConfigureAwait(false);
                return BuildStatusResult(status, verifying: true, prefix: null, sw);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SHA recomputation reads every cached file — a locked / unreadable file
                // surfaces as IOException / UnauthorizedAccessException. Return a structured
                // error rather than an unhandled tool failure. Cancellation still propagates.
                return BuildErrorResult($"Verify failed: {ex.Message}", sw);
            }
        });

    private static CallToolResult BuildStatusResult(
        EmbeddingsStatus status,
        bool verifying,
        string? prefix,
        System.Diagnostics.Stopwatch sw,
        EmbeddingRuntimeResourceRow? runtime = null)
    {
        var prose = new StringBuilder();
        if (!string.IsNullOrEmpty(prefix)) prose.AppendLine(prefix);
        prose.AppendLine($"**model**: `{status.ModelId}` (dim={status.Dimension})");
        prose.AppendLine($"**cache dir**: `{status.CacheDir}`");
        if (status.FreeDiskBytes is { } free)
        {
            prose.AppendLine($"**free disk**: {FormatBytes(free)}");
        }
        if (runtime is not null)
        {
            prose.AppendLine($"**runtime**: {(runtime.Loaded ? "loaded" : "unloaded")} "
                + $"(idle unload: {runtime.IdleTimeoutSeconds?.ToString() ?? "n/a"}s)");
            prose.AppendLine($"**memory**: process {FormatBytes(runtime.ProcessWorkingSetBytes)}, "
                + $"model estimate {FormatBytes(runtime.ModelResidentEstimateBytes)}, "
                + $"SQLite cache limit {FormatBytes(runtime.GraphCacheLimitBytes)}");
            prose.AppendLine($"**graph DB files**: {FormatBytes(runtime.GraphDatabaseFileBytes)}");
        }
        prose.AppendLine();
        if (status.Files.Count == 0)
        {
            prose.AppendLine("_(manifest is empty for this model)_");
        }
        else
        {
            prose.AppendLine("| File | Present | Size | SHA-256 | Match |");
            prose.AppendLine("|------|---------|------|---------|-------|");
            foreach (var f in status.Files)
            {
                var size = f.SizeBytes is null ? "-" : FormatBytes(f.SizeBytes.Value);
                var sha = f.ComputedSha is null ? "-" : $"`{f.ComputedSha[..Math.Min(12, f.ComputedSha.Length)]}…`";
                var match = !verifying ? "-"
                    : f.Match switch
                    {
                        null when f.Present => "info-only",
                        null => "-",
                        true => "ok",
                        _    => "**MISMATCH**", // bool?.false — discard avoids CodeQL `cs/constant-condition` on the redundant constant arm
                    };
                prose.AppendLine($"| `{f.LocalName}` | {(f.Present ? "yes" : "no")} | {size} | {sha} | {match} |");
            }
            if (verifying && status.Files.Any(f => f.Present && f.PinnedSha is null))
            {
                prose.AppendLine();
                prose.AppendLine("_note: no pinned SHA in manifest — informational only._");
            }
        }

        var dto = new EmbeddingsStatusResult(
            ModelId: status.ModelId,
            Dimension: status.Dimension,
            CacheDir: status.CacheDir,
            Files: status.Files.Select(f => new EmbeddingsFileRow(
                LocalName: f.LocalName,
                RemotePath: f.RemotePath,
                Present: f.Present,
                SizeBytes: f.SizeBytes,
                ComputedSha: f.ComputedSha,
                PinnedSha: f.PinnedSha,
                Match: f.Match)).ToList(),
            FreeDiskBytes: status.FreeDiskBytes,
            Runtime: runtime);

        var anyMismatch = verifying && status.Files.Any(f => f.Match == false);

        var content = new List<ContentBlock>(capacity: 2)
        {
            new TextContentBlock { Text = prose.ToString().TrimEnd() },
            AudienceMetadata.Build(
                scopeId: null,
                latencyMs: sw.ElapsedMilliseconds,
                ("files", status.Files.Count.ToString())),
        };

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(dto, ToolOutputJsonContext.Default.EmbeddingsStatusResult),
            IsError = anyMismatch,
        };
    }

    private static async Task<EmbeddingRuntimeResourceRow> BuildRuntimeStatusAsync(
        EmbeddingsStatus status,
        ICodeEmbeddingGenerator generator,
        ScopeRouter router)
    {
        var activeModel = string.Equals(
            status.ModelId,
            generator.Model.ModelId,
            StringComparison.Ordinal);
        var managed = activeModel
            ? (generator as IManagedEmbeddingRuntime)?.GetRuntimeSnapshot()
            : null;
        long graphFiles = 0;
        long graphCacheLimit = 0;
        foreach (var host in router.All())
        {
            var path = ScopeLayout.ScopeDbPath(host.Scope.Root, host.Scope.Id);
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate)) graphFiles += new FileInfo(candidate).Length;
                }
                catch (IOException)
                {
                    // Status remains useful when one concurrently-rotated file cannot be read.
                }
            }
            try
            {
                graphCacheLimit += await host.Store.GetCacheCapacityBytesAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                // A rebuilding/disposed scope may disappear while the status call fans out.
            }
        }

        return new EmbeddingRuntimeResourceRow(
            ActiveModel: activeModel,
            Available: activeModel && generator.IsAvailable,
            Loaded: managed?.Loaded ?? false,
            LoadedAt: managed?.LoadedAt,
            LastUsedAt: managed?.LastUsedAt,
            IdleTimeoutSeconds: managed is null ? null : (long)managed.IdleTimeout.TotalSeconds,
            ModelFileBytes: managed?.ModelFileBytes ?? 0,
            ModelResidentEstimateBytes: managed?.ModelResidentEstimateBytes ?? 0,
            ProcessWorkingSetBytes: System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,
            GraphDatabaseFileBytes: graphFiles,
            GraphCacheLimitBytes: graphCacheLimit);
    }

    private static CallToolResult BuildRemoveResult(RemoveResult result, System.Diagnostics.Stopwatch sw)
    {
        var prose = new StringBuilder();
        if (result.RemovedDirs.Count == 0)
        {
            prose.AppendLine($"_nothing to remove (cache empty for `{result.ModelId ?? "all models"}`)_");
        }
        else
        {
            var what = result.ModelId ?? "every cached model";
            prose.AppendLine($"Removed {result.RemovedDirs.Count} director{(result.RemovedDirs.Count == 1 ? "y" : "ies")} for `{what}`, freed {FormatBytes(result.FreedBytes)}:");
            foreach (var dir in result.RemovedDirs)
            {
                prose.AppendLine($"- `{dir}`");
            }
        }

        var dto = new EmbeddingsRemoveResult(
            ModelId: result.ModelId,
            RemovedDirs: result.RemovedDirs,
            FreedBytes: result.FreedBytes);

        var content = new List<ContentBlock>(capacity: 2)
        {
            new TextContentBlock { Text = prose.ToString().TrimEnd() },
            AudienceMetadata.Build(
                scopeId: null,
                latencyMs: sw.ElapsedMilliseconds,
                ("removed", result.RemovedDirs.Count.ToString()),
                ("freed_bytes", result.FreedBytes.ToString())),
        };

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(dto, ToolOutputJsonContext.Default.EmbeddingsRemoveResult),
        };
    }

    private static CallToolResult BuildErrorResult(string message, System.Diagnostics.Stopwatch sw)
    {
        var content = new List<ContentBlock>(capacity: 2)
        {
            new TextContentBlock { Text = $"**Error**: {message}" },
            AudienceMetadata.Build(scopeId: null, latencyMs: sw.ElapsedMilliseconds, ("error", "true")),
        };
        return new CallToolResult
        {
            Content = content,
            IsError = true,
        };
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        return bytes switch
        {
            < (long)KiB => $"{bytes} B",
            < (long)MiB => $"{bytes / KiB:F1} KiB",
            < (long)GiB => $"{bytes / MiB:F1} MiB",
            _           => $"{bytes / GiB:F2} GiB",
        };
    }
}
