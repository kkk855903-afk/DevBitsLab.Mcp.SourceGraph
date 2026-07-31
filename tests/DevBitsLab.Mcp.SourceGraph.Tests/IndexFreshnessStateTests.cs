using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
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
}
