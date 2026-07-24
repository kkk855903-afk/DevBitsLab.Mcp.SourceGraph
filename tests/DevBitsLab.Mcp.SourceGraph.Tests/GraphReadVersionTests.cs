using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class GraphReadVersionTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _databasePath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-read-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Join(_root, "graph.db");
        _store = new SqliteGraphStore(_databasePath);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task SameConnectionWrite_advancesConnectionChanges()
    {
        var before = await _store!.GetReadVersionAsync();

        await _store.UpsertFileAsync(
            Path.Join(_root, "SameConnection.cs"),
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);

        var after = await _store.GetReadVersionAsync();

        after.ConnectionChanges.Should().BeGreaterThan(before.ConnectionChanges);
        after.DataVersion.Should().Be(before.DataVersion);
    }

    [Fact]
    public async Task OtherConnectionWrite_advancesDataVersion()
    {
        await using var otherStore = new SqliteGraphStore(_databasePath);
        var before = await _store!.GetReadVersionAsync();

        await otherStore.UpsertFileAsync(
            Path.Join(_root, "OtherConnection.cs"),
            [4, 3, 2, 1],
            DateTimeOffset.UtcNow);

        var after = await _store.GetReadVersionAsync();

        after.ConnectionChanges.Should().Be(before.ConnectionChanges);
        after.DataVersion.Should().BeGreaterThan(before.DataVersion);
    }

    [Fact]
    public async Task PureReads_preserveReadVersion()
    {
        var before = await _store!.GetReadVersionAsync();

        _ = await _store.GetAllFilesAsync();
        _ = await _store.GetFileContentHashAsync("missing.cs");

        var after = await _store.GetReadVersionAsync();

        after.Should().Be(before);
    }

    [Fact]
    public async Task ReadVersion_honorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var read = () => _store!.GetReadVersionAsync(cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
