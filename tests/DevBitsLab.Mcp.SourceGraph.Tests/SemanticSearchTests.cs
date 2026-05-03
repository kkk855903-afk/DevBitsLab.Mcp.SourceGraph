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
}
