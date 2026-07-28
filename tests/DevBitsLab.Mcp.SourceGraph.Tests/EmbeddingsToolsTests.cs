using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Direct invocation of the <see cref="EmbeddingsTools"/> static methods (no MCP transport).
/// Asserts on the structured output shape, destructive-flag annotations, and the
/// <c>isError</c> flag for invalid input.
/// </summary>
public sealed class EmbeddingsToolsTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "embtools-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task EmbeddingsStatusAsync_returnsTypedStructuredContent()
    {
        var (mgr, _) = MakeManager();
        var result = await EmbeddingsTools.EmbeddingsStatusAsync(
            mgr,
            new ScopeRouter(),
            new DisabledEmbeddingGenerator(DefaultEmbeddingModel.Info));

        result.IsError.Should().NotBe(true);
        result.StructuredContent.HasValue.Should().BeTrue();
        var json = result.StructuredContent!.Value;
        json.GetProperty("model_id").GetString().Should().Be(DefaultEmbeddingModel.ModelId);
        json.GetProperty("dimension").GetInt32().Should().Be(DefaultEmbeddingModel.Dimension);
        json.GetProperty("cache_dir").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("files").GetArrayLength().Should().Be(2);
        json.GetProperty("queues").GetArrayLength().Should().Be(0);
        json.GetProperty("inference").GetProperty("query_wait_ms").GetDouble().Should().Be(0);
    }

    [Fact]
    public async Task EmbeddingsRemoveAsync_returnsTypedStructuredContent()
    {
        var (mgr, store) = MakeManager();
        var dir = store.DirectoryFor(DefaultEmbeddingModel.ModelId);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Join(dir, "model.onnx"), new byte[123]);

        var result = await EmbeddingsTools.EmbeddingsRemoveAsync(mgr);

        result.IsError.Should().NotBe(true);
        var json = result.StructuredContent!.Value;
        json.GetProperty("model_id").GetString().Should().Be(DefaultEmbeddingModel.ModelId);
        json.GetProperty("removed_dirs").GetArrayLength().Should().Be(1);
        json.GetProperty("freed_bytes").GetInt64().Should().Be(123);
    }

    [Fact]
    public async Task EmbeddingsRemoveAsync_modelAndAllCombined_returnsErrorResult()
    {
        var (mgr, _) = MakeManager();

        var result = await EmbeddingsTools.EmbeddingsRemoveAsync(mgr, modelId: "vendor/x", all: true);

        result.IsError.Should().Be(true);
        result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Any(t => t.Text != null && t.Text.Contains("cannot be combined"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task EmbeddingsVerifyAsync_customModelNoPinnedSha_returnsInfoOnly_notError()
    {
        // Custom model id → manifest has no pinned SHA → Verify is in informational mode and
        // the response is not flagged as an error.
        var customStore = new ModelStore(overrideBaseDir: _tempDir);
        var customMgr = new EmbeddingsManager(customStore, new EmbeddingModelInfo("vendor/custom-model", 768));
        var dir = customStore.DirectoryFor("vendor/custom-model");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Join(dir, "model.onnx"), new byte[16]);
        await File.WriteAllBytesAsync(Path.Join(dir, "tokenizer.json"), new byte[16]);

        var result = await EmbeddingsTools.EmbeddingsVerifyAsync(customMgr);

        result.IsError.Should().NotBe(true, "no pinned SHA → informational, not an error");
        var json = result.StructuredContent!.Value;
        foreach (var f in json.GetProperty("files").EnumerateArray())
        {
            f.TryGetProperty("match", out var match).Should().BeTrue();
            match.ValueKind.Should().Be(JsonValueKind.Null);
            f.GetProperty("pinned_sha").ValueKind.Should().Be(JsonValueKind.Null);
            f.GetProperty("computed_sha").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void EmbeddingsRemoveAsync_carriesDestructiveHint()
    {
        var method = typeof(EmbeddingsTools).GetMethod(nameof(EmbeddingsTools.EmbeddingsRemoveAsync), BindingFlags.Public | BindingFlags.Static)!;
        var attr = method.GetCustomAttribute<ToolAnnotationAttribute>();

        attr.Should().NotBeNull();
        attr!.DestructiveHint.Should().BeTrue();
        attr.IdempotentHint.Should().BeTrue();
        attr.ReadOnlyHint.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingsStatusAsync_carriesReadOnlyAndIdempotentHints()
    {
        var method = typeof(EmbeddingsTools).GetMethod(nameof(EmbeddingsTools.EmbeddingsStatusAsync), BindingFlags.Public | BindingFlags.Static)!;
        var attr = method.GetCustomAttribute<ToolAnnotationAttribute>();

        attr.Should().NotBeNull();
        attr!.ReadOnlyHint.Should().BeTrue();
        attr.IdempotentHint.Should().BeTrue();
        attr.DestructiveHint.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingsPullAsync_carriesIdempotentHint_butNotDestructive()
    {
        var method = typeof(EmbeddingsTools).GetMethod(nameof(EmbeddingsTools.EmbeddingsPullAsync), BindingFlags.Public | BindingFlags.Static)!;
        var attr = method.GetCustomAttribute<ToolAnnotationAttribute>();

        attr.Should().NotBeNull();
        attr!.IdempotentHint.Should().BeTrue();
        attr.DestructiveHint.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingsVerifyAsync_carriesReadOnlyHint()
    {
        var method = typeof(EmbeddingsTools).GetMethod(nameof(EmbeddingsTools.EmbeddingsVerifyAsync), BindingFlags.Public | BindingFlags.Static)!;
        var attr = method.GetCustomAttribute<ToolAnnotationAttribute>();

        attr.Should().NotBeNull();
        attr!.ReadOnlyHint.Should().BeTrue();
    }

    [Fact]
    public void AllFourEmbeddingsTools_carryToolTrigger()
    {
        foreach (var name in new[] { nameof(EmbeddingsTools.EmbeddingsStatusAsync), nameof(EmbeddingsTools.EmbeddingsPullAsync), nameof(EmbeddingsTools.EmbeddingsRemoveAsync), nameof(EmbeddingsTools.EmbeddingsVerifyAsync) })
        {
            var method = typeof(EmbeddingsTools).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            method.GetCustomAttribute<ToolTriggerAttribute>().Should().NotBeNull(name);
        }
    }

    private (EmbeddingsManager mgr, ModelStore store) MakeManager()
    {
        var store = new ModelStore(overrideBaseDir: _tempDir);
        return (new EmbeddingsManager(store, DefaultEmbeddingModel.Info), store);
    }
}
