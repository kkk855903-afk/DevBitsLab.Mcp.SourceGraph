using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Integration tests for the semantic-search pipeline. Use the deterministic mock embedding
/// generator so the tests don't depend on a 280 MB ONNX file being present on the runner.
/// The tests still exercise the real <c>sqlite-vec</c> extension and the real
/// <see cref="SqliteEmbeddingsStore"/>; if the extension can't load (e.g., on a runtime where
/// the native binary isn't packaged) the tests fall back to asserting the disabled-path
/// graceful-degradation contract.
/// </summary>
public sealed class SemanticSearchTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private bool _vec0Loaded;
    private const int Dim = 64; // mock vectors at 64-dim — keeps the test fixture cheap

    public async Task InitializeAsync()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sourcegraph-vec-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "graph.db");

        _store = new SqliteGraphStore(_dbPath);
        _vec0Loaded = _store.TryLoadVectorExtension(Dim);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Vec0Extension_loadsOrGracefullyDisables()
    {
        // The point of this test is to fail loud if neither path works on this runner.
        // _vec0Loaded == false means we should have a DisabledEmbeddingsStore that returns
        // empty results without throwing. Either path is acceptable, but exactly one must hold.
        var embStore = _store!.CreateEmbeddingsStore(Dim);
        if (_vec0Loaded)
        {
            embStore.IsAvailable.Should().BeTrue();
        }
        else
        {
            embStore.IsAvailable.Should().BeFalse();
            var hits = await embStore.SearchAsync(new float[Dim], k: 5);
            hits.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Upsert_thenSearch_returnsTopK_byCosine()
    {
        if (!_vec0Loaded)
        {
            // Skip-style: still document the disabled-path contract without throwing.
            var disabled = _store!.CreateEmbeddingsStore(Dim);
            disabled.IsAvailable.Should().BeFalse();
            (await disabled.SearchAsync(new float[Dim], 5)).Should().BeEmpty();
            return;
        }

        var embStore = _store!.CreateEmbeddingsStore(Dim);
        embStore.IsAvailable.Should().BeTrue();

        // Three "documents". query + a + b are tightly related ("calculator add"), c is unrelated.
        var query = DeterministicMockEmbeddingGenerator.Embed("calculator add multiply", Dim);
        var a = DeterministicMockEmbeddingGenerator.Embed("calculator add multiply numbers", Dim);
        var b = DeterministicMockEmbeddingGenerator.Embed("calculator subtract", Dim);
        var c = DeterministicMockEmbeddingGenerator.Embed("logger trace warn", Dim);

        // Insert symbols (just enough to satisfy the symbol_id FK).
        // We can use UpsertAsync directly: it doesn't require a row in `symbols`.
        await embStore.UpsertAsync(symbolId: 1, contentHash: new byte[] { 1 }, embedding: a, modelVersion: "test/v1");
        await embStore.UpsertAsync(symbolId: 2, contentHash: new byte[] { 2 }, embedding: b, modelVersion: "test/v1");
        await embStore.UpsertAsync(symbolId: 3, contentHash: new byte[] { 3 }, embedding: c, modelVersion: "test/v1");

        (await embStore.CountAsync()).Should().Be(3);

        var hits = await embStore.SearchAsync(query, k: 3);
        hits.Should().HaveCount(3);
        // Top hit must be #1 ("calculator add multiply numbers"), worst must be #3 (logger).
        hits[0].SymbolId.Should().Be(1);
        hits[0].Score.Should().BeGreaterThan(hits[2].Score, "cosine similarity orders by relevance descending");
    }

    [Fact]
    public async Task CandidateSearch_ranksOnlyFtsCandidateIds()
    {
        if (!_vec0Loaded) return;
        var embStore = _store!.CreateEmbeddingsStore(Dim);
        var query = DeterministicMockEmbeddingGenerator.Embed("calculator add", Dim);
        var bestButExcluded = DeterministicMockEmbeddingGenerator.Embed("calculator add", Dim);
        var candidateA = DeterministicMockEmbeddingGenerator.Embed("calculator subtract", Dim);
        var candidateB = DeterministicMockEmbeddingGenerator.Embed("logger warning", Dim);

        await embStore.UpsertAsync(201, [1], bestButExcluded, "test/v1");
        await embStore.UpsertAsync(202, [2], candidateA, "test/v1");
        await embStore.UpsertAsync(203, [3], candidateB, "test/v1");

        var hits = await embStore.SearchCandidatesAsync(
            query,
            candidateSymbolIds: [202, 203],
            k: 10);

        hits.Select(hit => hit.SymbolId).Should().Equal(202, 203);
        hits.Should().NotContain(hit => hit.SymbolId == 201);
    }

    [Fact]
    public async Task ShouldReembed_returnsFalseForUnchangedHashAndModel()
    {
        if (!_vec0Loaded) return;
        var embStore = _store!.CreateEmbeddingsStore(Dim);
        var v = DeterministicMockEmbeddingGenerator.Embed("hash-gating-test", Dim);
        var hash = new byte[] { 0x42, 0x42 };

        await embStore.UpsertAsync(symbolId: 99, contentHash: hash, embedding: v, modelVersion: "model/v1");
        (await embStore.ShouldReembedAsync(99, hash, "model/v1")).Should().BeFalse("same hash + same model = no re-embed");
        (await embStore.ShouldReembedAsync(99, new byte[] { 0xFF }, "model/v1")).Should().BeTrue("different hash forces re-embed");
        (await embStore.ShouldReembedAsync(99, hash, "model/v2")).Should().BeTrue("different model invalidates the row");
        (await embStore.ShouldReembedAsync(symbolId: 100, contentHash: hash, modelVersion: "model/v1")).Should().BeTrue("missing row -> re-embed");
    }

    [Fact]
    public async Task EmbeddingEligibilityCount_matchesRoslynProducerScope()
    {
        var sourcePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "Main.cs");
        var generatedPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "Generated.g.cs");
        var designerPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "Form.Designer.cs");
        var sourceFileId = await _store!.UpsertFileAsync(
            sourcePath,
            [1],
            DateTimeOffset.UtcNow);
        var generatedFileId = await _store.UpsertFileAsync(
            generatedPath,
            [2],
            DateTimeOffset.UtcNow,
            isGenerated: true);
        var designerFileId = await _store.UpsertFileAsync(
            designerPath,
            [3],
            DateTimeOffset.UtcNow);

        await _store.UpsertSymbolAsync(
            "csharp:T:Fixture.View",
            Symbol("View", "Fixture.View", "class", sourceFileId));
        await _store.UpsertSymbolAsync(
            "csharp:F:Fixture.View._field",
            Symbol("_field", "Fixture.View._field", "field", sourceFileId));
        await _store.UpsertSymbolAsync(
            "csharp:F:Fixture.View.Documented",
            Symbol("Documented", "Fixture.View.Documented", "field", sourceFileId, "A documented field."));
        await _store.UpsertSymbolAsync(
            "xaml:view:View.xaml",
            Symbol("View.xaml", "View.xaml", "xaml-view", sourceFileId));
        await _store.UpsertSymbolAsync(
            "cpp:function:fixture_view",
            Symbol("fixture_view", "fixture_view", "function", sourceFileId));
        await _store.UpsertSymbolAsync(
            "csharp:T:Fixture.Generated",
            Symbol("Generated", "Fixture.Generated", "class", generatedFileId));
        await _store.UpsertSymbolAsync(
            "csharp:T:Fixture.Designer",
            Symbol("Designer", "Fixture.Designer", "class", designerFileId));

        (await _store.CountEmbeddingEligibleSymbolsAsync()).Should().Be(2);
    }

    private static Symbol Symbol(
        string name,
        string fqn,
        string kind,
        long fileId,
        string? xmlSummary = null) =>
        new(
            Id: 0,
            Name: name,
            Fqn: fqn,
            Kind: kind,
            FileId: fileId,
            StartLine: 1,
            StartCol: 1,
            EndLine: 1,
            EndCol: 2,
            Signature: null,
            ContainerId: null,
            XmlSummary: xmlSummary);
}
