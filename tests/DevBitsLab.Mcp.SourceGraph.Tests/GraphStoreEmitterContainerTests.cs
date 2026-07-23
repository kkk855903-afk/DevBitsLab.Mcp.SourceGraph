using Dapper;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class GraphStoreEmitterContainerTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private long _fileId;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-emitter-containers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
        _fileId = await _store.UpsertFileAsync(
            Path.Join(_tempDir, "Symbols.cs"),
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Flush_resolvesSameBatchContainer_regardlessOfDeclarationOrder(
        bool childFirst)
    {
        const string parentKey = "csharp:T:Medical.ScanService";
        const string childKey = "csharp:M:Medical.ScanService.Start";
        var parent = Declare(
            parentKey,
            "ScanService",
            "Medical.ScanService",
            SymbolKinds.Class);
        var child = Declare(
            childKey,
            "Start",
            "Medical.ScanService.Start",
            SymbolKinds.Method,
            parentKey);
        var emitter = new GraphStoreEmitter(
            _store!,
            _fileId,
            new Dictionary<string, long>(StringComparer.Ordinal));

        foreach (var symbol in childFirst
                     ? new[] { child, parent }
                     : new[] { parent, child })
        {
            emitter.EmitSymbol(symbol);
        }
        await emitter.FlushAsync();

        var rows = await ReadContainerRowsAsync();
        rows.Should().ContainSingle(row =>
            row.CanonicalKey == childKey
            && row.ContainerId == rows.Single(parentRow =>
                parentRow.CanonicalKey == parentKey).Id);
    }

    [Fact]
    public async Task Flush_leavesUnknownContainerUnresolved()
    {
        const string childKey = "csharp:M:Medical.Orphan.Start";
        var emitter = new GraphStoreEmitter(
            _store!,
            _fileId,
            new Dictionary<string, long>(StringComparer.Ordinal));
        emitter.EmitSymbol(Declare(
            childKey,
            "Start",
            "Medical.Orphan.Start",
            SymbolKinds.Method,
            "csharp:T:Medical.MissingContainer"));

        await emitter.FlushAsync();

        var rows = await ReadContainerRowsAsync();
        rows.Should().ContainSingle();
        rows[0].CanonicalKey.Should().Be(childKey);
        rows[0].ContainerId.Should().BeNull(
            "the emitter must not guess a container from the child's FQN");
    }

    [Fact]
    public async Task Flush_doesNotPersistContainerFromStaleExternalKeyMap()
    {
        const string childKey = "csharp:M:Medical.Stale.Start";
        const string missingParentKey = "csharp:T:Medical.Stale";
        var symbolIds = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [missingParentKey] = long.MaxValue,
        };
        var emitter = new GraphStoreEmitter(
            _store!,
            _fileId,
            symbolIds);
        emitter.EmitSymbol(Declare(
            childKey,
            "Start",
            "Medical.Stale.Start",
            SymbolKinds.Method,
            missingParentKey));

        await emitter.FlushAsync();

        var rows = await ReadContainerRowsAsync();
        rows.Should().ContainSingle();
        rows[0].ContainerId.Should().BeNull(
            "the storage layer must verify that the mapped parent id still exists");
    }

    [Fact]
    public async Task Flush_clearsPreviouslyResolvedContainer_whenNewKeyIsUnknown()
    {
        const string parentKey = "csharp:T:Medical.FormerContainer";
        const string childKey = "csharp:M:Medical.FormerContainer.Start";
        var firstMap = new Dictionary<string, long>(StringComparer.Ordinal);
        var firstEmitter = new GraphStoreEmitter(_store!, _fileId, firstMap);
        firstEmitter.EmitSymbol(Declare(
            parentKey,
            "FormerContainer",
            "Medical.FormerContainer",
            SymbolKinds.Class));
        firstEmitter.EmitSymbol(Declare(
            childKey,
            "Start",
            "Medical.FormerContainer.Start",
            SymbolKinds.Method,
            parentKey));
        await firstEmitter.FlushAsync();

        var secondEmitter = new GraphStoreEmitter(
            _store!,
            _fileId,
            new Dictionary<string, long>(StringComparer.Ordinal));
        secondEmitter.EmitSymbol(Declare(
            childKey,
            "Start",
            "Medical.FormerContainer.Start",
            SymbolKinds.Method,
            "csharp:T:Medical.UnknownContainer"));
        await secondEmitter.FlushAsync();

        var child = (await ReadContainerRowsAsync())
            .Single(row => row.CanonicalKey == childKey);
        child.ContainerId.Should().BeNull(
            "an unresolved key must not leave a stale relationship from an earlier pass");
    }

    private static IndexEvent.SymbolDeclared Declare(
        string canonicalKey,
        string name,
        string fqn,
        string kind,
        string? containerCanonicalKey = null) =>
        new(
            canonicalKey,
            name,
            fqn,
            kind,
            1,
            1,
            2,
            1,
            containerCanonicalKey: containerCanonicalKey);

    private async Task<List<ContainerRow>> ReadContainerRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        return (await connection.QueryAsync<ContainerRow>(
            """
            SELECT
                id AS Id,
                canonical_key AS CanonicalKey,
                container_id AS ContainerId
            FROM symbols
            ORDER BY id;
            """)).ToList();
    }

    private sealed record ContainerRow(
        long Id,
        string CanonicalKey,
        long? ContainerId);
}
