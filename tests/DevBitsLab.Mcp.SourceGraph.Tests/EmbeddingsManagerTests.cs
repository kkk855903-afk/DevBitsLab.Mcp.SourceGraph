using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Server;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the shared <see cref="EmbeddingsManager"/> service that powers both the CLI
/// embedding subcommand group and the MCP <c>embeddings_*</c> tools.
/// </summary>
public sealed class EmbeddingsManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "embmgr-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetStatusAsync_emptyCache_reportsAllFilesAbsent()
    {
        var (_, mgr) = MakeManager();
        var status = await mgr.GetStatusAsync(modelId: null);

        status.ModelId.Should().Be(DefaultEmbeddingModel.ModelId);
        status.Dimension.Should().Be(DefaultEmbeddingModel.Dimension);
        status.Files.Should().HaveCount(2);
        status.Files.Should().AllSatisfy(f => f.Present.Should().BeFalse());
        status.Files.Should().AllSatisfy(f => f.SizeBytes.Should().BeNull());
        status.Files.Should().AllSatisfy(f => f.ComputedSha.Should().BeNull());
        status.Files.Should().AllSatisfy(f => f.Match.Should().BeNull());
    }

    [Fact]
    public async Task GetStatusAsync_populatedCache_reportsSizeAndComputedSha()
    {
        var (store, mgr) = MakeManager();
        await PopulateAsync(store, DefaultEmbeddingModel.ModelId,
            ("model.onnx", new byte[1024]),
            ("tokenizer.json", new byte[256]));

        var status = await mgr.GetStatusAsync(modelId: null);

        status.Files.Should().AllSatisfy(f => f.Present.Should().BeTrue());
        status.Files.First(f => f.LocalName == "model.onnx").SizeBytes.Should().Be(1024);
        status.Files.First(f => f.LocalName == "tokenizer.json").SizeBytes.Should().Be(256);
        status.Files.Should().AllSatisfy(f => f.ComputedSha.Should().NotBeNullOrEmpty());
        status.Files.Should().AllSatisfy(f => f.Match.Should().BeNull("populateMatch is false in GetStatusAsync"));
    }

    [Fact]
    public async Task PullAsync_emptyCache_downloadsAndReportsPresent()
    {
        // Use a non-default model id so the resolved manifest has no pinned SHAs (the default
        // model's pinned SHAs would fail against the stub handler's canned bytes). The PullAsync
        // path through ResolveManifest takes the best-effort branch for non-default ids.
        var customInfo = new EmbeddingModelInfo("vendor/custom-model", 768);
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var store = new ModelStore(overrideBaseDir: _tempDir, http: http);
        var mgr = new EmbeddingsManager(store, customInfo);

        var status = await mgr.PullAsync(modelId: null);

        status.Files.Should().AllSatisfy(f => f.Present.Should().BeTrue());
        handler.Urls.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PullAsync_warmCache_isNoOp()
    {
        // Same custom-id rationale as PullAsync_emptyCache_downloadsAndReportsPresent: the
        // default model's pinned SHAs reject the stub bytes; a custom id takes the no-SHA branch.
        var customInfo = new EmbeddingModelInfo("vendor/custom-model", 768);
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var store = new ModelStore(overrideBaseDir: _tempDir, http: http);
        await PopulateAsync(store, customInfo.ModelId,
            ("model.onnx", new byte[16]),
            ("tokenizer.json", new byte[16]));
        var mgr = new EmbeddingsManager(store, customInfo);

        var status = await mgr.PullAsync(modelId: null);

        handler.Urls.Should().BeEmpty();
        status.Files.Should().AllSatisfy(f => f.Present.Should().BeTrue());
    }

    [Fact]
    public async Task RemoveAsync_default_clearsActiveModelOnly()
    {
        var (store, mgr) = MakeManager();
        await PopulateAsync(store, DefaultEmbeddingModel.ModelId, ("model.onnx", new byte[100]));
        await PopulateAsync(store, "vendor/other", ("model.onnx", new byte[200]));

        var result = await mgr.RemoveAsync(modelId: null, all: false);

        result.ModelId.Should().Be(DefaultEmbeddingModel.ModelId);
        result.FreedBytes.Should().Be(100);
        Directory.Exists(store.DirectoryFor(DefaultEmbeddingModel.ModelId)).Should().BeFalse();
        Directory.Exists(store.DirectoryFor("vendor/other")).Should().BeTrue("non-active model is untouched");
    }

    [Fact]
    public async Task RemoveAsync_all_clearsEveryCachedModel()
    {
        var (store, mgr) = MakeManager();
        await PopulateAsync(store, DefaultEmbeddingModel.ModelId, ("model.onnx", new byte[100]));
        await PopulateAsync(store, "vendor/other", ("model.onnx", new byte[200]));

        var result = await mgr.RemoveAsync(modelId: null, all: true);

        result.ModelId.Should().BeNull();
        result.RemovedDirs.Should().HaveCount(2);
        result.FreedBytes.Should().Be(300);
        Directory.Exists(store.DirectoryFor(DefaultEmbeddingModel.ModelId)).Should().BeFalse();
        Directory.Exists(store.DirectoryFor("vendor/other")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_modelAndAllCombined_throws()
    {
        var (_, mgr) = MakeManager();

        var act = () => mgr.RemoveAsync(modelId: "vendor/x", all: true);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*cannot be combined*");
    }

    [Fact]
    public async Task RemoveAsync_specificModel_clearsThatOneOnly()
    {
        var (store, mgr) = MakeManager();
        await PopulateAsync(store, DefaultEmbeddingModel.ModelId, ("model.onnx", new byte[100]));
        await PopulateAsync(store, "vendor/other", ("model.onnx", new byte[200]));

        var result = await mgr.RemoveAsync(modelId: "vendor/other", all: false);

        result.ModelId.Should().Be("vendor/other");
        result.FreedBytes.Should().Be(200);
        Directory.Exists(store.DirectoryFor(DefaultEmbeddingModel.ModelId)).Should().BeTrue();
        Directory.Exists(store.DirectoryFor("vendor/other")).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_customModelWithNoPinnedSha_leavesMatchNull()
    {
        // Custom model id resolves to the best-effort manifest (no pinned SHAs), so VerifyAsync
        // is in informational-only mode for every file. The default model now has pinned SHAs
        // (captured 2026-05-10), so the equivalent test against the default would assert
        // Match=true — covered separately in VerifyAsync_defaultModelAgainstPinned*.
        var customInfo = new EmbeddingModelInfo("vendor/custom-model", 768);
        var store = new ModelStore(overrideBaseDir: _tempDir);
        var mgr = new EmbeddingsManager(store, customInfo);
        await PopulateAsync(store, customInfo.ModelId,
            ("model.onnx", new byte[16]),
            ("tokenizer.json", new byte[16]));

        var status = await mgr.VerifyAsync(modelId: null);

        status.Files.Should().AllSatisfy(f => f.Match.Should().BeNull());
        status.Files.Should().AllSatisfy(f => f.ComputedSha.Should().NotBeNullOrEmpty());
        status.Files.Should().AllSatisfy(f => f.PinnedSha.Should().BeNull());
    }

    [Fact]
    public async Task VerifyAsync_defaultModelAgainstPinned_reportsMismatchForStubBytes()
    {
        // Pre-populate the default model dir with stub bytes that DO NOT match the pinned SHAs.
        // VerifyAsync should populate Match=false. This is the regression guard for "are pinned
        // SHAs actually being checked" — if Match comes back null instead of false, the manifest
        // SHA capture didn't take or the verification path is bypassed.
        var (store, mgr) = MakeManager();
        await PopulateAsync(store, DefaultEmbeddingModel.ModelId,
            ("model.onnx", new byte[16]),
            ("tokenizer.json", new byte[16]));

        var status = await mgr.VerifyAsync(modelId: null);

        status.Files.Should().AllSatisfy(f => f.PinnedSha.Should().NotBeNullOrEmpty());
        status.Files.Should().AllSatisfy(f => f.Match.Should().BeFalse("stub bytes don't match the production SHAs"));
    }

    [Fact]
    public async Task GetStatusAsync_emptyTempDir_returnsFreeDiskOrNull()
    {
        var (_, mgr) = MakeManager();
        var status = await mgr.GetStatusAsync(modelId: null);

        // FreeDiskBytes can be null if no parent in the chain is mounted; otherwise positive.
        (status.FreeDiskBytes is null || status.FreeDiskBytes > 0).Should().BeTrue();
    }

    private (ModelStore store, EmbeddingsManager mgr) MakeManager()
    {
        var store = new ModelStore(overrideBaseDir: _tempDir);
        var mgr = new EmbeddingsManager(store, DefaultEmbeddingModel.Info);
        return (store, mgr);
    }

    private static async Task PopulateAsync(ModelStore store, string modelId, params (string name, byte[] bytes)[] files)
    {
        var dir = store.DirectoryFor(modelId);
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in files)
        {
            await File.WriteAllBytesAsync(Path.Join(dir, name), bytes);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        // `HttpMessageHandler.SendAsync` transfers ownership of the returned `HttpResponseMessage`
        // to `HttpClient`. Already inline-constructed inside `Task.FromResult(...)` so CodeQL's
        // `cs/local-not-disposed` ownership-transfer flow analysis is satisfied.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }),
            });
        }
    }
}
