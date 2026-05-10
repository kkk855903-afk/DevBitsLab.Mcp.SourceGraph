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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the auto-download gate factory: which arm of the decision matrix runs for each
/// flag/cache combination. Stubs the HF endpoint via <see cref="RecordingHandler"/> so tests
/// don't reach huggingface.co.
/// </summary>
public sealed class ModelDownloadGateFactoryTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "gatefactory-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void EmbeddingsDisabled_returnsOpenGate_andNoHttpRequest()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var gate = ModelDownloadGateFactory.Build(
            ms, DefaultEmbeddingModel.Info,
            embeddingsEnabled: false, noModelDownload: false,
            logger: NullLogger.Instance);

        gate.Ready.IsCompleted.Should().BeTrue();
        handler.Urls.Should().BeEmpty();
    }

    [Fact]
    public void NoModelDownload_emptyCache_returnsOpenGate_andNoHttpRequest()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var gate = ModelDownloadGateFactory.Build(
            ms, DefaultEmbeddingModel.Info,
            embeddingsEnabled: true, noModelDownload: true,
            logger: NullLogger.Instance);

        gate.Ready.IsCompleted.Should().BeTrue();
        handler.Urls.Should().BeEmpty();
    }

    [Fact]
    public async Task NoModelDownload_populatedCache_returnsOpenGate_andNoHttpRequest()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        // Pre-populate the cache so IsCached returns true.
        var modelDir = ms.DirectoryFor(DefaultEmbeddingModel.ModelId);
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(Path.Join(modelDir, "model.onnx"), "stub-onnx");
        await File.WriteAllTextAsync(Path.Join(modelDir, "tokenizer.json"), "stub-tok");

        var gate = ModelDownloadGateFactory.Build(
            ms, DefaultEmbeddingModel.Info,
            embeddingsEnabled: true, noModelDownload: true,
            logger: NullLogger.Instance);

        gate.Ready.IsCompleted.Should().BeTrue();
        handler.Urls.Should().BeEmpty();
        File.Exists(Path.Join(modelDir, "model.onnx")).Should().BeTrue("populated cache must be left intact");
    }

    [Fact]
    public async Task DefaultPath_emptyCache_downloadsAndPopulatesCache()
    {
        // Use a custom (non-default) model id so the resolved manifest has no pinned SHAs;
        // the stub handler's canned bytes can't match the production SHAs of the default model.
        // The default-flow / fire-and-don't-await behaviour is unchanged regardless of model id.
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);
        var customInfo = new EmbeddingModelInfo("vendor/custom-model", 768);

        var gate = ModelDownloadGateFactory.Build(
            ms, customInfo,
            embeddingsEnabled: true, noModelDownload: false,
            logger: NullLogger.Instance);

        await gate.Ready;

        var modelDir = ms.DirectoryFor(customInfo.ModelId);
        File.Exists(Path.Join(modelDir, "model.onnx")).Should().BeTrue();
        File.Exists(Path.Join(modelDir, "tokenizer.json")).Should().BeTrue();
        // Custom model uses flat remote paths (no onnx/ prefix), unlike DefaultEmbeddingModel.Manifest.
        handler.Urls.Should().Contain(u => u.EndsWith("/resolve/main/model.onnx"));
        handler.Urls.Should().Contain(u => u.EndsWith("/resolve/main/tokenizer.json"));
    }

    [Fact]
    public async Task DefaultPath_populatedCache_skipsDownload()
    {
        // Same custom-id rationale: stub bytes wouldn't match pinned SHAs, so a cache-hit on the
        // default model would fail SHA verification and re-fetch. With a custom id (no pinned SHA)
        // existence-only is enough to short-circuit the download.
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);
        var customInfo = new EmbeddingModelInfo("vendor/custom-model", 768);

        var modelDir = ms.DirectoryFor(customInfo.ModelId);
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(Path.Join(modelDir, "model.onnx"), "stub-onnx");
        await File.WriteAllTextAsync(Path.Join(modelDir, "tokenizer.json"), "stub-tok");

        var gate = ModelDownloadGateFactory.Build(
            ms, customInfo,
            embeddingsEnabled: true, noModelDownload: false,
            logger: NullLogger.Instance);
        await gate.Ready;

        handler.Urls.Should().BeEmpty("warm cache must skip the network");
    }

    [Fact]
    public async Task DefaultPath_emptyCache_defaultModel_swallowsShaMismatch()
    {
        // With pinned SHAs on the default model, the stub handler's canned bytes will fail
        // verification. The gate factory's contract is that ModelDownloadException is caught
        // and logged — the gate task transitions to RanToCompletion regardless.
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var gate = ModelDownloadGateFactory.Build(
            ms, DefaultEmbeddingModel.Info,
            embeddingsEnabled: true, noModelDownload: false,
            logger: NullLogger.Instance);

        await gate.Ready;
        gate.Ready.IsCompletedSuccessfully.Should().BeTrue("gate must swallow ModelDownloadException including SHA mismatch");

        // The .tmp file is deleted on SHA mismatch, so no cache file lands.
        var modelDir = ms.DirectoryFor(DefaultEmbeddingModel.ModelId);
        File.Exists(Path.Join(modelDir, "model.onnx")).Should().BeFalse("SHA mismatch must not leave a partial download in place");
    }

    [Fact]
    public async Task DownloadFailure_isCaughtSoGateNeverFaults()
    {
        // Server returns 404 → ModelStore throws ModelDownloadException → factory must catch
        // it so the gate task transitions to RanToCompletion (not Faulted).
        var handler = new RecordingHandler { StatusCode = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var gate = ModelDownloadGateFactory.Build(
            ms, DefaultEmbeddingModel.Info,
            embeddingsEnabled: true, noModelDownload: false,
            logger: NullLogger.Instance);
        await gate.Ready;

        gate.Ready.IsCompletedSuccessfully.Should().BeTrue("gate must swallow ModelDownloadException");
    }

    [Fact]
    public async Task UnexpectedException_isCaughtSoGateNeverFaults()
    {
        // The gate's contract is that Ready completes for every non-cancellation outcome — not
        // just ModelDownloadException. A bug in the chunked-copy / IO layer that throws
        // InvalidOperationException (or anything else) must still settle the gate, otherwise
        // the awaiting LiveIndexService / EmbeddingsHostedService would observe a faulted task
        // and tear down their own background loops. Regression guard for the catch broadening
        // applied 2026-05-10 in response to PR #45 review feedback.
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);

        var gate = ModelDownloadGateFactory.Build(
            ms, DefaultEmbeddingModel.Info,
            embeddingsEnabled: true, noModelDownload: false,
            logger: NullLogger.Instance);
        await gate.Ready;

        gate.Ready.IsCompletedSuccessfully.Should().BeTrue(
            "gate must swallow every non-cancellation exception, not just ModelDownloadException");
    }

    [Fact]
    public async Task OverrideModel_usesBestEffortManifest_andDownloads()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var ms = new ModelStore(overrideBaseDir: _tempDir, http: http);
        var customInfo = new EmbeddingModelInfo("vendor/custom-model", 768);

        var gate = ModelDownloadGateFactory.Build(
            ms, customInfo,
            embeddingsEnabled: true, noModelDownload: false,
            logger: NullLogger.Instance);
        await gate.Ready;

        var modelDir = ms.DirectoryFor(customInfo.ModelId);
        File.Exists(Path.Join(modelDir, "model.onnx")).Should().BeTrue();
        File.Exists(Path.Join(modelDir, "tokenizer.json")).Should().BeTrue();
        // Override path uses flat remote names (no onnx/ prefix).
        handler.Urls.Should().Contain(u => u.EndsWith("/resolve/main/model.onnx"));
        handler.Urls.Should().NotContain(u => u.Contains("/onnx/model.onnx"));
    }

    /// <summary>
    /// Stubbed <see cref="HttpMessageHandler"/> that records every URL it sees and returns a
    /// canned payload. Shared shape with <see cref="ModelStoreTests.RecordingHandler"/> but
    /// kept private here so the test files stay independently runnable.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public byte[] Payload { get; set; } = new byte[] { 1, 2, 3, 4 };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(BuildResponse(StatusCode, StatusCode == HttpStatusCode.OK ? Payload : null));
        }

        // Helper-method ownership-transfer pattern (see ModelStoreTests.RecordingHandler for the
        // same shape) — CodeQL recognises returns-a-disposable as a clean hand-off.
        private static HttpResponseMessage BuildResponse(HttpStatusCode code, byte[]? payload)
        {
            var resp = new HttpResponseMessage(code);
            if (payload is not null) resp.Content = new ByteArrayContent(payload);
            return resp;
        }
    }

    /// <summary>
    /// Stubbed handler that throws on every send — simulates an unexpected non-network failure
    /// path inside <see cref="DevBitsLab.Mcp.SourceGraph.Embeddings.ModelStore.EnsureAsync"/>
    /// (e.g. a programmer-error <see cref="InvalidOperationException"/>). The gate factory must
    /// still complete the gate task successfully.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("simulated programmer-error inside the download path");
        }
    }
}
