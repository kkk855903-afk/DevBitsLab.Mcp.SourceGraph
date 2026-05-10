using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="ModelStore"/>'s download / cache resolution logic, including the
/// <c>RemotePath</c> vs <c>LocalName</c> split that lets one manifest entry name both the
/// upstream HF path and the flat cache filename.
/// </summary>
public sealed class ModelStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "modelstore-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ModelFile_resolvedLocalName_returnsLocalName_whenSupplied()
    {
        var entry = new ModelFile("onnx/model.onnx", "model.onnx");
        entry.ResolvedLocalName.Should().Be("model.onnx");
    }

    [Fact]
    public void ModelFile_resolvedLocalName_fallsBackToTrailingSegment_whenLocalNameOmitted()
    {
        new ModelFile("tokenizer.json").ResolvedLocalName.Should().Be("tokenizer.json");
        new ModelFile("subdir/leaf.bin").ResolvedLocalName.Should().Be("leaf.bin");
    }

    [Fact]
    public void DefaultEmbeddingModel_manifestHasExpectedShape()
    {
        var manifest = DefaultEmbeddingModel.Manifest;
        manifest.Should().HaveCount(2);
        manifest.Should().ContainSingle(m => m.RemotePath == "onnx/model.onnx" && m.ResolvedLocalName == "model.onnx");
        manifest.Should().ContainSingle(m => m.RemotePath == "tokenizer.json" && m.ResolvedLocalName == "tokenizer.json");
    }

    [Fact]
    public async Task EnsureAsync_writesToLocalName_whenRemotePathContainsSubdirectory()
    {
        // Stubbed handler that records every URL it gets and returns a tiny payload.
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var manifest = new[]
        {
            new ModelFile("onnx/model.onnx", "model.onnx"),
            new ModelFile("tokenizer.json"),
        };
        await ms.EnsureAsync("vendor/model-id", manifest, CancellationToken.None);

        var modelDir = ms.DirectoryFor("vendor/model-id");
        File.Exists(Path.Join(modelDir, "model.onnx")).Should().BeTrue("flat local name");
        File.Exists(Path.Join(modelDir, "tokenizer.json")).Should().BeTrue();
        Directory.Exists(Path.Join(modelDir, "onnx")).Should().BeFalse("remote subdirectory must not appear in the cache");

        handler.Urls.Should().Contain(u => u.EndsWith("/resolve/main/onnx/model.onnx"));
        handler.Urls.Should().Contain(u => u.EndsWith("/resolve/main/tokenizer.json"));
    }

    [Fact]
    public async Task EnsureAsync_skipsDownload_whenCacheIsAlreadyPopulated()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var modelDir = ms.DirectoryFor("vendor/model-id");
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(Path.Join(modelDir, "model.onnx"), "stub-onnx");
        await File.WriteAllTextAsync(Path.Join(modelDir, "tokenizer.json"), "stub-tok");

        await ms.EnsureAsync("vendor/model-id", new[]
        {
            new ModelFile("onnx/model.onnx", "model.onnx"),
            new ModelFile("tokenizer.json"),
        }, CancellationToken.None);

        handler.Urls.Should().BeEmpty("cache hit must skip the network entirely");
    }

    [Fact]
    public async Task EnsureAsync_throwsModelDownloadException_whenServerReturns404()
    {
        var handler = new RecordingHandler { StatusCode = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var act = () => ms.EnsureAsync(
            "vendor/missing-model",
            new[] { new ModelFile("onnx/model.onnx", "model.onnx") },
            CancellationToken.None);

        await act.Should().ThrowAsync<ModelDownloadException>();
    }

    [Fact]
    public async Task RemoveAsync_emptyCache_returnsZero_andDoesNotThrow()
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        var freed = await ms.RemoveAsync("vendor/never-cached");
        freed.Should().Be(0);
    }

    [Fact]
    public async Task RemoveAsync_populatedCache_deletesDirectory_andReturnsByteCount()
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        var dir = ms.DirectoryFor("vendor/model");
        Directory.CreateDirectory(dir);
        var bytes = new byte[1024];
        await File.WriteAllBytesAsync(Path.Join(dir, "model.onnx"), bytes);
        await File.WriteAllBytesAsync(Path.Join(dir, "tokenizer.json"), bytes);

        var freed = await ms.RemoveAsync("vendor/model");

        freed.Should().Be(2048);
        Directory.Exists(dir).Should().BeFalse();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("../../escape")]
    [InlineData("vendor/../../escape")]
    [InlineData(".")]
    [InlineData("")]
    public void DirectoryFor_rejectsPathTraversalEscape(string evilModelId)
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        var act = () => ms.DirectoryFor(evilModelId);
        // Either layer of the defense (segment-level validation OR resolved-under-base check)
        // is acceptable; the contract is "throw ArgumentException".
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DirectoryFor_acceptsLegitimateHfStyleId()
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        var dir = ms.DirectoryFor("jinaai/jina-embeddings-v2-base-code");
        dir.Should().Contain("jinaai_jina-embeddings-v2-base-code");
        dir.Should().StartWith(Path.GetFullPath(_tempDir));
    }

    [Fact]
    public async Task RemoveAllAsync_multiModelCache_deletesEverySubdir_preservesBaseDir()
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        var dirA = ms.DirectoryFor("vendor/a");
        var dirB = ms.DirectoryFor("vendor/b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        await File.WriteAllBytesAsync(Path.Join(dirA, "model.onnx"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Join(dirB, "model.onnx"), new byte[200]);

        var result = await ms.RemoveAllAsync();

        result.RemovedDirs.Should().HaveCount(2);
        result.FreedBytes.Should().Be(300);
        Directory.Exists(dirA).Should().BeFalse();
        Directory.Exists(dirB).Should().BeFalse();
        Directory.Exists(_tempDir).Should().BeTrue("base dir must be preserved");
    }

    [Fact]
    public async Task RemoveAllAsync_emptyBaseDir_isNoOp()
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        Directory.CreateDirectory(_tempDir);

        var result = await ms.RemoveAllAsync();

        result.RemovedDirs.Should().BeEmpty();
        result.FreedBytes.Should().Be(0);
    }

    [Fact]
    public async Task ComputeShaAsync_returnsLowerHexHash_forKnownPayload()
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        var dir = ms.DirectoryFor("vendor/model");
        Directory.CreateDirectory(dir);
        // SHA-256("hello\n") = 5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03
        await File.WriteAllTextAsync(Path.Join(dir, "tokenizer.json"), "hello\n");

        var sha = await ms.ComputeShaAsync("vendor/model", "tokenizer.json");

        sha.Should().Be("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03");
    }

    [Fact]
    public async Task ComputeShaAsync_returnsNull_forMissingFile()
    {
        var ms = new ModelStore(overrideBaseDir: _tempDir);
        var sha = await ms.ComputeShaAsync("vendor/never-cached", "model.onnx");
        sha.Should().BeNull();
    }

    /// <summary>
    /// Stubbed <see cref="HttpMessageHandler"/> that returns a small canned payload for every
    /// request and records the requested URL. Lets us assert on call counts and URL shapes
    /// without touching huggingface.co.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public System.Collections.Generic.List<string> Urls { get; } = new();
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public byte[] Payload { get; set; } = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(BuildResponse(StatusCode, StatusCode == HttpStatusCode.OK ? Payload : null));
        }

        // Helper-method ownership-transfer pattern: a method that constructs and returns the
        // disposable is recognised by CodeQL's `cs/local-not-disposed` flow analysis as a clean
        // hand-off, unlike an inline ternary in `Task.FromResult(...)`.
        private static HttpResponseMessage BuildResponse(HttpStatusCode code, byte[]? payload)
        {
            var resp = new HttpResponseMessage(code);
            if (payload is not null) resp.Content = new ByteArrayContent(payload);
            return resp;
        }
    }
}
