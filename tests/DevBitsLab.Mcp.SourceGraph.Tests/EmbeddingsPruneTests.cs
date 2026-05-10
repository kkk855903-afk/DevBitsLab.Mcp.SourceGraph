using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="IEmbeddingsStore.PruneOrphanedAsync"/>: deletes <c>symbol_embeddings</c>
/// rows whose <c>symbol_id</c> no longer exists in <c>symbols</c>; mirrored on
/// <c>embedding_meta</c>; safe no-op when the vec0 extension didn't load.
/// </summary>
public sealed class EmbeddingsPruneTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private bool _vec0Loaded;
    private const int Dim = 64;

    public async Task InitializeAsync()
    {
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-prune-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Join(tmp, "graph.db");
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
    public async Task PruneOrphaned_returnsCountOfDeletedRows_whenSymbolsAreMissing()
    {
        if (!_vec0Loaded)
        {
            // Disabled-path contract: still callable, returns 0 without throwing.
            var disabled = _store!.CreateEmbeddingsStore(Dim);
            (await disabled.PruneOrphanedAsync()).Should().Be(0);
            return;
        }

        // Plant 5 rows in `symbols` for ids 1..5 then upsert 7 embeddings for ids 1..7 (so 6 + 7
        // are orphans). Embeddings are dense float[Dim]; content matters only by hash, so reuse a
        // stable byte for each.
        var fileId = await _store!.UpsertFileAsync("/tmp/A.cs", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow);
        for (var i = 1; i <= 5; i++)
        {
            await _store.UpsertSymbolAsync($"S:csharp:K{i}", new Symbol(
                Id: 0, Name: $"Foo{i}", Fqn: $"X.Foo{i}", Kind: "class", FileId: fileId,
                StartLine: i, StartCol: 1, EndLine: i + 1, EndCol: 1, Signature: $"class Foo{i}",
                ContainerId: null, Modifiers: null, Accessibility: (int)Accessibility.Public, XmlSummary: null));
        }
        var emb = _store.CreateEmbeddingsStore(Dim);
        var v = new float[Dim];
        for (var i = 1; i <= 7; i++)
        {
            await emb.UpsertAsync(symbolId: i, contentHash: new byte[] { (byte)i }, embedding: v, modelVersion: "test/v1");
        }
        (await emb.CountAsync()).Should().Be(7);

        var pruned = await emb.PruneOrphanedAsync();
        pruned.Should().Be(2);
        (await emb.CountAsync()).Should().Be(5);

        // Re-running is a no-op.
        (await emb.PruneOrphanedAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PruneOrphaned_returnsZero_whenNoOrphansExist()
    {
        if (!_vec0Loaded) return;

        var emb = _store!.CreateEmbeddingsStore(Dim);
        (await emb.PruneOrphanedAsync()).Should().Be(0);
    }
}
