using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// End-to-end coverage for the four <c>embeddings_*</c> MCP tools driven through the real
/// <c>tools/call</c> boundary. Each test redirects <c>XDG_CACHE_HOME</c> to a per-test temp
/// directory so cache state is isolated, then asserts on the structured output of each tool.
/// </summary>
public sealed class EmbeddingsToolsIntegrationTests : IDisposable
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);

    private readonly string _cacheRoot = Path.Join(Path.GetTempPath(), "embtools-it-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true);
    }

    /// <summary>Cache directory that <see cref="DevBitsLab.Mcp.SourceGraph.Embeddings.ModelStore"/>
    /// resolves for the default model when <c>XDG_CACHE_HOME</c> points at <see cref="_cacheRoot"/>.
    /// Mirrors the path resolution in <c>ModelStore.DefaultCacheDir</c> +
    /// <c>SanitiseId("jinaai/jina-embeddings-v2-base-code")</c>.</summary>
    private string DefaultCacheDir => Path.Join(_cacheRoot, "devbitslab.sourcegraph", "models", "jinaai_jina-embeddings-v2-base-code");

    private void PrePopulateDefaultCache(int sizeBytes = 16)
    {
        Directory.CreateDirectory(DefaultCacheDir);
        File.WriteAllBytes(Path.Join(DefaultCacheDir, "model.onnx"), new byte[sizeBytes]);
        File.WriteAllBytes(Path.Join(DefaultCacheDir, "tokenizer.json"), new byte[sizeBytes]);
    }

    private async Task<ServerHarness> StartServerAsync()
    {
        // --no-embeddings keeps the embedding pipeline idle so the indexer doesn't try to load the
        // ONNX file (our pre-populated bytes aren't valid ONNX). The embeddings_* tools are
        // registered unconditionally so they still work for cache management.
        // --no-history keeps the cold path quick (no git-blame).
        // No --solution: the host comes up against an empty scope, which is the cheapest fixture
        // for tools that don't depend on the graph (like ours).
        var args = new[] { "--no-embeddings", "--no-history" };
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["XDG_CACHE_HOME"] = _cacheRoot,
            // LOCALAPPDATA only matters on Windows; setting it harmlessly here keeps the resolution
            // deterministic across platforms.
            ["LOCALAPPDATA"] = _cacheRoot,
        };
        return await ServerHarness.StartAsync(args, env);
    }

    [Fact]
    public async Task EmbeddingsStatus_emptyCache_reportsAllFilesAbsent()
    {
        await using var harness = await StartServerAsync();
        using var cts = new CancellationTokenSource(QueryTimeout);

        var result = await harness.Client.CallToolAsync(
            "embeddings_status",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
            progress: null, options: null, cancellationToken: cts.Token);

        result.IsError.Should().NotBe(true);
        var s = (JsonElement)result.StructuredContent!;
        s.GetProperty("model_id").GetString().Should().Be("jinaai/jina-embeddings-v2-base-code");
        s.GetProperty("cache_dir").GetString().Should().Be(DefaultCacheDir);
        var files = s.GetProperty("files");
        files.GetArrayLength().Should().Be(2);
        foreach (var f in files.EnumerateArray())
        {
            f.GetProperty("present").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task EmbeddingsStatus_populatedCache_reportsPresent_andSize()
    {
        PrePopulateDefaultCache(sizeBytes: 256);
        await using var harness = await StartServerAsync();
        using var cts = new CancellationTokenSource(QueryTimeout);

        var result = await harness.Client.CallToolAsync(
            "embeddings_status",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
            progress: null, options: null, cancellationToken: cts.Token);

        result.IsError.Should().NotBe(true);
        var files = ((JsonElement)result.StructuredContent!).GetProperty("files");
        foreach (var f in files.EnumerateArray())
        {
            f.GetProperty("present").GetBoolean().Should().BeTrue();
            f.GetProperty("size_bytes").GetInt64().Should().Be(256);
            f.GetProperty("computed_sha").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task EmbeddingsRemove_default_clearsCache_andLeavesNothing()
    {
        PrePopulateDefaultCache();
        await using var harness = await StartServerAsync();
        using var cts = new CancellationTokenSource(QueryTimeout);

        var result = await harness.Client.CallToolAsync(
            "embeddings_remove",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
            progress: null, options: null, cancellationToken: cts.Token);

        result.IsError.Should().NotBe(true);
        var s = (JsonElement)result.StructuredContent!;
        s.GetProperty("model_id").GetString().Should().Be("jinaai/jina-embeddings-v2-base-code");
        s.GetProperty("removed_dirs").GetArrayLength().Should().Be(1);
        s.GetProperty("freed_bytes").GetInt64().Should().BeGreaterThan(0);

        // Cache directory was wiped — a follow-up status call shows present=false for every file.
        Directory.Exists(DefaultCacheDir).Should().BeFalse();
    }

    [Fact]
    public async Task EmbeddingsRemove_all_wipesEveryCachedModel()
    {
        // Pre-populate two model directories.
        PrePopulateDefaultCache();
        var otherDir = Path.Join(_cacheRoot, "devbitslab.sourcegraph", "models", "vendor_other");
        Directory.CreateDirectory(otherDir);
        File.WriteAllBytes(Path.Join(otherDir, "model.onnx"), new byte[100]);

        await using var harness = await StartServerAsync();
        using var cts = new CancellationTokenSource(QueryTimeout);

        var result = await harness.Client.CallToolAsync(
            "embeddings_remove",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["all"] = true },
            progress: null, options: null, cancellationToken: cts.Token);

        result.IsError.Should().NotBe(true);
        var s = (JsonElement)result.StructuredContent!;
        s.GetProperty("model_id").ValueKind.Should().Be(JsonValueKind.Null);
        s.GetProperty("removed_dirs").GetArrayLength().Should().Be(2);
        Directory.Exists(DefaultCacheDir).Should().BeFalse();
        Directory.Exists(otherDir).Should().BeFalse();
    }

    [Fact]
    public async Task EmbeddingsRemove_modelAndAllCombined_returnsErrorResult()
    {
        await using var harness = await StartServerAsync();
        using var cts = new CancellationTokenSource(QueryTimeout);

        var result = await harness.Client.CallToolAsync(
            "embeddings_remove",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["modelId"] = "jinaai/x",
                ["all"] = true,
            },
            progress: null, options: null, cancellationToken: cts.Token);

        result.IsError.Should().Be(true);
        var prose = result.Content.OfType<TextContentBlock>().Select(b => b.Text ?? "").FirstOrDefault();
        prose.Should().Contain("cannot be combined");
    }

    [Fact]
    public async Task EmbeddingsVerify_defaultModelStubBytes_reportsMismatch()
    {
        // The default model ships with pinned SHAs (captured 2026-05-10). Pre-populating the
        // cache with stub bytes and calling verify must surface the mismatch as Match=false
        // and flag the response with isError=true. Regression guard for the SHA-pinning swap.
        PrePopulateDefaultCache();
        await using var harness = await StartServerAsync();
        using var cts = new CancellationTokenSource(QueryTimeout);

        var result = await harness.Client.CallToolAsync(
            "embeddings_verify",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
            progress: null, options: null, cancellationToken: cts.Token);

        result.IsError.Should().Be(true, "stub bytes don't match the production SHAs");
        var files = ((JsonElement)result.StructuredContent!).GetProperty("files");
        foreach (var f in files.EnumerateArray())
        {
            f.GetProperty("match").GetBoolean().Should().BeFalse();
            f.GetProperty("pinned_sha").GetString().Should().NotBeNullOrEmpty();
            f.GetProperty("computed_sha").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task ToolsList_includesAllFourEmbeddingsTools_withCorrectAnnotations()
    {
        await using var harness = await StartServerAsync();
        using var cts = new CancellationTokenSource(QueryTimeout);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);

        var byName = tools.ToDictionary(t => t.Name, t => t);
        byName.Should().ContainKey("embeddings_status");
        byName.Should().ContainKey("embeddings_pull");
        byName.Should().ContainKey("embeddings_remove");
        byName.Should().ContainKey("embeddings_verify");

        // Annotations were applied via ApplyAnnotationsFromAttributes — verify the destructive
        // hint is set on remove and not on the others.
        byName["embeddings_remove"].ProtocolTool.Annotations!.DestructiveHint.Should().BeTrue();
        byName["embeddings_remove"].ProtocolTool.Annotations!.IdempotentHint.Should().BeTrue();
        byName["embeddings_status"].ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
        byName["embeddings_status"].ProtocolTool.Annotations!.DestructiveHint.Should().BeFalse();
        byName["embeddings_verify"].ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
    }
}
