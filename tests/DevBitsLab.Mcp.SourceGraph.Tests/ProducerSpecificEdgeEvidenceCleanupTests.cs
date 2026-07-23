using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ProducerSpecificEdgeEvidenceCleanupTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-producer-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
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

    [Fact]
    public async Task ClearEdgeEvidence_matchesFileAndProducer_andRepairsLogicalEdges()
    {
        var firstPath = Path.Join(_tempDir, "First.cs");
        var secondPath = Path.Join(_tempDir, "Second.cs");
        var firstFileId = await SeedFileAsync(firstPath);
        var secondFileId = await SeedFileAsync(secondPath);
        var sourceId = await SeedSymbolAsync(firstFileId, "Source");
        var sharedTargetId = await SeedSymbolAsync(firstFileId, "SharedTarget");
        var removedTargetId = await SeedSymbolAsync(firstFileId, "RemovedTarget");
        var retainedTargetId = await SeedSymbolAsync(firstFileId, "RetainedTarget");

        await _store!.BulkInsertEdgesAsync(
        [
            EdgeWithEvidence(
                sourceId,
                sharedTargetId,
                firstFileId,
                firstPath,
                1,
                "native-alpha",
                "alpha"),
            EdgeWithEvidence(
                sourceId,
                sharedTargetId,
                firstFileId,
                firstPath,
                2,
                "native-beta",
                "beta"),
            EdgeWithEvidence(
                sourceId,
                sharedTargetId,
                secondFileId,
                secondPath,
                3,
                "native-alpha",
                "other-file"),
            EdgeWithEvidence(
                sourceId,
                removedTargetId,
                firstFileId,
                firstPath,
                4,
                "native-alpha",
                "remove"),
            EdgeWithEvidence(
                sourceId,
                retainedTargetId,
                firstFileId,
                firstPath,
                5,
                "native-beta",
                "retain"),
        ]);
        await ExecuteAsync(
            """
            INSERT INTO edges(src, dst, kind_name, payload)
            VALUES (@src, @dst, 'legacy-without-evidence', '{"legacy":"keep"}');
            """,
            new { src = sourceId, dst = retainedTargetId });

        var removed = await _store.ClearEdgeEvidenceAsync(
            firstFileId,
            "native-alpha");

        removed.Should().Be(2);
        var sharedEvidence = await _store.ListEdgeEvidenceAsync(
            sourceId,
            sharedTargetId,
            "calls");
        sharedEvidence.Should().HaveCount(2);
        sharedEvidence.Should().Contain(item =>
            item.ProducingFileId == firstFileId
            && item.Producer == "native-beta");
        sharedEvidence.Should().Contain(item =>
            item.ProducingFileId == secondFileId
            && item.Producer == "native-alpha");
        (await GetPayloadAsync(sourceId, sharedTargetId, "calls"))
            .Should().Be(
                """{"value":"beta"}""",
                "the earliest surviving occurrence supplies the compatibility payload");

        (await EdgeExistsAsync(sourceId, removedTargetId, "calls"))
            .Should().BeFalse(
                "the producer cleanup removed this logical edge's final evidence");
        (await EdgeExistsAsync(sourceId, retainedTargetId, "calls"))
            .Should().BeTrue(
                "another producer in the same file is outside the exact cleanup pair");
        (await EdgeExistsAsync(
                sourceId,
                retainedTargetId,
                "legacy-without-evidence"))
            .Should().BeTrue(
                "precise cleanup must not sweep unrelated legacy logical edges");

        (await _store.ClearEdgeEvidenceAsync(firstFileId, "native-alpha"))
            .Should().Be(0, "repeating the exact cleanup is idempotent");
    }

    [Fact]
    public async Task ClearEdgeEvidence_validatesExactSelector()
    {
        var invalidFile = () => _store!.ClearEdgeEvidenceAsync(0, "native");
        var invalidProducer = () => _store!.ClearEdgeEvidenceAsync(1, " ");

        await invalidFile.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await invalidProducer.Should().ThrowAsync<ArgumentException>();
    }

    private Edge EdgeWithEvidence(
        long sourceId,
        long targetId,
        long producingFileId,
        string path,
        int line,
        string producer,
        string value) =>
        new Edge(
            sourceId,
            targetId,
            "calls",
            new Dictionary<string, string> { ["value"] = value })
        {
            Evidence = new Evidence(
                producingFileId,
                new SourceLocation(path, line, 1, line, 2),
                EvidenceConfidence.Exact,
                producer,
                new Dictionary<string, string> { ["value"] = value }),
        };

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);

    private async Task<long> SeedSymbolAsync(long fileId, string name) =>
        await _store!.UpsertSymbolAsync(
            $"csharp:M:Evidence.{name}",
            new Symbol(
                0,
                name,
                $"Evidence.{name}",
                "method",
                fileId,
                1,
                1,
                2,
                1,
                $"void {name}()",
                null));

    private async Task<bool> EdgeExistsAsync(long src, long dst, string kind) =>
        await ScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM edges
            WHERE src = @src AND dst = @dst AND kind_name = @kind;
            """,
            new { src, dst, kind }) > 0;

    private async Task<string?> GetPayloadAsync(long src, long dst, string kind) =>
        await ScalarAsync<string?>(
            """
            SELECT payload
            FROM edges
            WHERE src = @src AND dst = @dst AND kind_name = @kind;
            """,
            new { src, dst, kind });

    private async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
