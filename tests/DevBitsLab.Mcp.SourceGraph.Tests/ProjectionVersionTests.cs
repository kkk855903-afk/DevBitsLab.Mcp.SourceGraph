using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ProjectionVersionTests : IAsyncLifetime
{
    private string _directory = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _directory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-projection-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new SqliteGraphStore(Path.Join(_directory, "graph.db"));
        await _store.EnsureSchemaAsync();
    }

    [Fact]
    public async Task Version_isAbsentUntilCompleteProducerPublish()
    {
        (await _store!.GetProjectionVersionAsync("roslyn-core"))
            .Should().BeNull();

        await _store.SetProjectionVersionAsync("roslyn-core", 2);

        (await _store.GetProjectionVersionAsync("roslyn-core"))
            .Should().Be(2);
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
