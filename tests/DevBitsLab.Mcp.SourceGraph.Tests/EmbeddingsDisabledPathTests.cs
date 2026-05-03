using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the graceful-disable contract when the model isn't cached, the extension
/// didn't load, or <c>--no-embeddings</c> was passed. The semantic_search tool is wired in
/// the Server project, so we test the underlying primitives that the tool branches on.
/// </summary>
public sealed class EmbeddingsDisabledPathTests
{
    [Fact]
    public void DisabledEmbeddingGenerator_isNotAvailable_andProducesNoVectors()
    {
        var gen = new DisabledEmbeddingGenerator(DefaultEmbeddingModel.Info);
        gen.IsAvailable.Should().BeFalse();
        gen.Model.ModelId.Should().Be(DefaultEmbeddingModel.ModelId);
    }

    [Fact]
    public async Task DisabledEmbeddingGenerator_returnsEmptyBatch()
    {
        var gen = new DisabledEmbeddingGenerator(DefaultEmbeddingModel.Info);
        var results = await gen.EmbedAsync(new[] { "anything" });
        results.Should().BeEmpty();
    }

    [Fact]
    public void JinaCodeEmbeddingGenerator_pointedAtMissingFile_isNotAvailable()
    {
        // The simulate-no-cache scenario: instantiate against bogus paths. The generator must
        // return IsAvailable=false rather than throwing on construction or first use.
        var gen = new JinaCodeEmbeddingGenerator(
            modelOnnxPath: "/no/such/file.onnx",
            tokenizerJsonPath: "/no/such/tokenizer.json",
            model: DefaultEmbeddingModel.Info);
        gen.IsAvailable.Should().BeFalse();
        gen.Dispose();
    }

    [Fact]
    public void NoOpEmbeddingsRequestSink_swallowsRequests_andReportsDisabled()
    {
        var sink = new NoOpEmbeddingsRequestSink();
        sink.IsEnabled.Should().BeFalse();
        // Should not throw.
        sink.Enqueue(new EmbedRequest(1, "x", new byte[32]));
    }

    [Fact]
    public void ModelStore_defaultCacheDir_isNotEmpty_andEndsWithModels()
    {
        // Smoke: the default cache resolver picks a sensible path. We don't assert the exact
        // root because XDG_CACHE_HOME / LOCALAPPDATA / HOME differ per host.
        var dir = ModelStore.DefaultCacheDir();
        dir.Should().NotBeNullOrEmpty();
        dir.Should().EndWith("models");
    }

    [Fact]
    public void ModelStore_directoryFor_collapsesSlashesToUnderscores()
    {
        var ms = new ModelStore(overrideBaseDir: "/tmp/x");
        ms.DirectoryFor("vendor/model-id").Should().EndWith("vendor_model-id");
    }

    [Fact]
    public void EmbeddingModelInfo_versionRollsModelAndDimension()
    {
        new EmbeddingModelInfo("vendor/x", 768).Version.Should().Be("vendor/x/768");
    }

    [Fact]
    public void DefaultEmbeddingModel_isJinaV2Code768Dim()
    {
        DefaultEmbeddingModel.Info.ModelId.Should().Be("jinaai/jina-embeddings-v2-base-code");
        DefaultEmbeddingModel.Info.Dimension.Should().Be(768);
        DefaultEmbeddingModel.Info.Version.Should().Be("jinaai/jina-embeddings-v2-base-code/768");
    }
}
