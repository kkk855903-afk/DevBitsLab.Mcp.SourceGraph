using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class IndexFreshnessStateTests
{
    [Fact]
    public async Task GenerationAndFreshness_persistAndRemainMonotonic()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "index-freshness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "graph.db");
        var changedAt = DateTimeOffset.Parse("2026-08-01T01:02:03.000Z");
        var indexedAt = changedAt.AddMilliseconds(37);
        try
        {
            await using (var store = new SqliteGraphStore(path))
            {
                await store.EnsureSchemaAsync();
                var pending = await store.RecordSourceChangedAsync(changedAt);
                pending.Generation.Should().Be(0);
                pending.SourceChangedAt.Should().Be(changedAt.ToUnixTimeMilliseconds());

                var complete = await store.CompleteIndexGenerationAsync(indexedAt);
                complete.Generation.Should().Be(1);
                complete.IndexedAt.Should().Be(indexedAt.ToUnixTimeMilliseconds());
            }

            await using (var reopened = new SqliteGraphStore(path))
            {
                await reopened.EnsureSchemaAsync();
                (await reopened.GetIndexStateAsync()).Generation.Should().Be(1);

                await reopened.SeedIndexGenerationAsync(40);
                var next = await reopened.CompleteIndexGenerationAsync(indexedAt.AddSeconds(1));
                next.Generation.Should().Be(41);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedQuery_refreshesGenerationChangedByAnotherConnection()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "index-freshness-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "graph.db");
        ScopeHost? host = null;
        try
        {
            var store = new SqliteGraphStore(path);
            await store.EnsureSchemaAsync();
            var scope = new Scope(
                "default",
                "default",
                directory,
                new ScopeProjectSet.Solutions([], []),
                false,
                DateTimeOffset.MinValue);
            host = new ScopeHost(
                scope,
                store,
                store.CreateEmbeddingsStore(4),
                new RoslynIndexer(store),
                string.Empty);
            host.ApplyIndexState(await store.GetIndexStateAsync());
            host.MarkReady();

            await using (var external = new SqliteGraphStore(path))
            {
                await external.EnsureSchemaAsync();
                await external.CompleteIndexGenerationAsync(DateTimeOffset.UtcNow);
            }

            var router = new ScopeRouter();
            router.Register(host);
            router.SetDefaultScope("default");
            var result = await ScopedExecution.RunAsync(
                router,
                scope: null,
                _ => Task.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "ok" }],
                }),
                CancellationToken.None);

            host.IndexGeneration.Should().Be(1);
            result.Content!.OfType<TextContentBlock>().Last().Text.Should()
                .Contain("generation=1");
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
