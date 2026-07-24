using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ProducerEdgeEvidenceReplacementTests : IAsyncLifetime
{
    private const string Producer = "interop-resolver";

    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-producer-replacement-" + Guid.NewGuid().ToString("N"));
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
    public async Task ReplaceProducerEdgeEvidence_unresolved_endpoint_rolls_back_old_result()
    {
        var producingPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var producingFileId = await SeedFileAsync(producingPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceId = await SeedSymbolAsync(
            producingFileId,
            "csharp:M:NativeMethods.Old",
            "Old");
        var oldTargetId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:old",
            "old");
        var candidateTargetId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:candidate",
            "candidate");

        await _store!.BulkInsertEdgesAsync(
        [
            EdgeWithEvidence(
                sourceId,
                oldTargetId,
                producingFileId,
                producingPath,
                line: 3,
                Producer,
                "old"),
        ]);

        var replace = () => _store.ReplaceProducerEdgeEvidenceAsync(
            producingPath,
            Producer,
            [
                Fact(
                    "csharp:M:NativeMethods.Old",
                    "native:function:candidate",
                    producingPath,
                    line: 7,
                    "candidate"),
                Fact(
                    "csharp:M:NativeMethods.Old",
                    "native:function:missing",
                    producingPath,
                    line: 8,
                    "missing"),
            ]);

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*native:function:missing*");
        var oldEvidence = await _store.ListEdgeEvidenceAsync(
            sourceId,
            oldTargetId,
            "pinvoke-maps-to");
        oldEvidence.Should().ContainSingle();
        oldEvidence[0].Producer.Should().Be(Producer);
        oldEvidence[0].Metadata.Should().Contain("value", "old");
        (await GetPayloadAsync(sourceId, oldTargetId, "pinvoke-maps-to"))
            .Should().Be("""{"value":"old"}""");
        (await EdgeExistsAsync(sourceId, candidateTargetId, "pinvoke-maps-to"))
            .Should().BeFalse("no candidate may be written before every endpoint resolves");
    }

    [Fact]
    public async Task ReplaceProducerEdgeEvidence_empty_result_preserves_other_producer_and_syncs_payload()
    {
        var producingPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var producingFileId = await SeedFileAsync(producingPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceId = await SeedSymbolAsync(
            producingFileId,
            "csharp:M:NativeMethods.Shared",
            "Shared");
        var sharedTargetId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:shared",
            "shared");
        var orphanTargetId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:orphan",
            "orphan");

        await _store!.BulkInsertEdgesAsync(
        [
            EdgeWithEvidence(
                sourceId,
                sharedTargetId,
                producingFileId,
                producingPath,
                line: 3,
                Producer,
                "owned"),
            EdgeWithEvidence(
                sourceId,
                sharedTargetId,
                producingFileId,
                producingPath,
                line: 4,
                "managed-indexer",
                "survivor"),
            EdgeWithEvidence(
                sourceId,
                orphanTargetId,
                producingFileId,
                producingPath,
                line: 5,
                Producer,
                "orphan"),
        ]);

        await _store.ReplaceProducerEdgeEvidenceAsync(
            producingPath,
            Producer,
            []);

        var sharedEvidence = await _store.ListEdgeEvidenceAsync(
            sourceId,
            sharedTargetId,
            "pinvoke-maps-to");
        sharedEvidence.Should().ContainSingle();
        sharedEvidence[0].Producer.Should().Be("managed-indexer");
        sharedEvidence[0].Metadata.Should().Contain("value", "survivor");
        (await GetPayloadAsync(sourceId, sharedTargetId, "pinvoke-maps-to"))
            .Should().Be(
                """{"value":"survivor"}""",
                "the earliest remaining evidence owns the compatibility payload");
        (await EdgeExistsAsync(sourceId, orphanTargetId, "pinvoke-maps-to"))
            .Should().BeFalse("empty replacement removes a logical edge with no surviving proof");
    }

    [Fact]
    public async Task ReplaceProducerEdgeEvidence_inserts_deduplicated_edges_in_stable_order()
    {
        var producingPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var producingFileId = await SeedFileAsync(producingPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "native:function:compute";
        var sourceId = await SeedSymbolAsync(producingFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");

        var early = Fact(
            sourceKey,
            targetKey,
            producingPath,
            line: 10,
            new Dictionary<string, string>
            {
                ["z"] = "2",
                ["a"] = "1",
            });
        var duplicateWithDifferentMapOrder = Fact(
            sourceKey,
            targetKey,
            producingPath,
            line: 10,
            new Dictionary<string, string>
            {
                ["a"] = "1",
                ["z"] = "2",
            });
        var later = Fact(
            sourceKey,
            targetKey,
            producingPath,
            line: 20,
            new Dictionary<string, string> { ["rank"] = "later" });

        await _store!.ReplaceProducerEdgeEvidenceAsync(
            producingPath,
            Producer,
            [later, duplicateWithDifferentMapOrder, early]);

        await AssertStableReplacementAsync(sourceId, targetId);

        await _store.ReplaceProducerEdgeEvidenceAsync(
            producingPath,
            Producer,
            [early, later, duplicateWithDifferentMapOrder]);

        await AssertStableReplacementAsync(sourceId, targetId);
    }

    [Fact]
    public async Task ReplaceProducerEdgeEvidence_rejects_mismatched_producer_without_cleanup()
    {
        var producingPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var producingFileId = await SeedFileAsync(producingPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "native:function:compute";
        var sourceId = await SeedSymbolAsync(producingFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await _store!.BulkInsertEdgesAsync(
        [
            EdgeWithEvidence(
                sourceId,
                targetId,
                producingFileId,
                producingPath,
                line: 3,
                Producer,
                "old"),
        ]);
        var mismatched = Fact(
            sourceKey,
            targetKey,
            producingPath,
            line: 10,
            "new") with
        {
            Evidence = new FileEvidenceFact(
                new SourceLocation(producingPath, 10, 1, 10, 2),
                EvidenceConfidence.Exact,
                "Interop-Resolver",
                new Dictionary<string, string> { ["value"] = "new" }),
        };

        var replace = () => _store.ReplaceProducerEdgeEvidenceAsync(
            producingPath,
            Producer,
            [mismatched]);

        await replace.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exactly match*");
        var evidence = await _store.ListEdgeEvidenceAsync(
            sourceId,
            targetId,
            "pinvoke-maps-to");
        evidence.Should().ContainSingle();
        evidence[0].Metadata.Should().Contain("value", "old");
    }

    [Fact]
    public async Task ReplaceProducerProjection_validates_all_files_before_replacing_any_generation()
    {
        var firstPath = Path.Join(_tempDir, "First.cs");
        var secondPath = Path.Join(_tempDir, "Second.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var firstFileId = await SeedFileAsync(firstPath);
        var secondFileId = await SeedFileAsync(secondPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var firstSourceKey = "csharp:M:NativeMethods.First";
        var secondSourceKey = "csharp:M:NativeMethods.Second";
        var firstSourceId = await SeedSymbolAsync(
            firstFileId,
            firstSourceKey,
            "First");
        var secondSourceId = await SeedSymbolAsync(
            secondFileId,
            secondSourceKey,
            "Second");
        var oldFirstId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:old_first",
            "old_first");
        var oldSecondId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:old_second",
            "old_second");
        var newFirstId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:new_first",
            "new_first");
        var newSecondId = await SeedSymbolAsync(
            nativeFileId,
            "native:function:new_second",
            "new_second");
        await _store!.BulkInsertEdgesAsync(
        [
            EdgeWithEvidence(
                firstSourceId,
                oldFirstId,
                firstFileId,
                firstPath,
                3,
                Producer,
                "old-first"),
            EdgeWithEvidence(
                secondSourceId,
                oldSecondId,
                secondFileId,
                secondPath,
                4,
                Producer,
                "old-second"),
        ]);

        var invalidReplace = () =>
            _store.ReplaceProducerEdgeEvidenceProjectionAsync(
                Producer,
                [
                    Fact(
                        firstSourceKey,
                        "native:function:new_first",
                        firstPath,
                        10,
                        "new-first"),
                    Fact(
                        secondSourceKey,
                        "native:function:missing",
                        secondPath,
                        11,
                        "missing"),
                ]);

        await invalidReplace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*native:function:missing*");
        (await _store.ListEdgeEvidenceAsync(
                firstSourceId,
                oldFirstId,
                "pinvoke-maps-to"))
            .Should().ContainSingle(evidence =>
                evidence.Metadata!["value"] == "old-first");
        (await _store.ListEdgeEvidenceAsync(
                secondSourceId,
                oldSecondId,
                "pinvoke-maps-to"))
            .Should().ContainSingle(evidence =>
                evidence.Metadata!["value"] == "old-second");
        (await EdgeExistsAsync(
                firstSourceId,
                newFirstId,
                "pinvoke-maps-to"))
            .Should().BeFalse();

        await _store.ReplaceProducerEdgeEvidenceProjectionAsync(
            Producer,
            [
                Fact(
                    secondSourceKey,
                    "native:function:new_second",
                    secondPath,
                    21,
                    "new-second"),
                Fact(
                    firstSourceKey,
                    "native:function:new_first",
                    firstPath,
                    20,
                    "new-first"),
            ]);

        (await EdgeExistsAsync(
                firstSourceId,
                oldFirstId,
                "pinvoke-maps-to"))
            .Should().BeFalse();
        (await EdgeExistsAsync(
                secondSourceId,
                oldSecondId,
                "pinvoke-maps-to"))
            .Should().BeFalse();
        (await _store.ListEdgeEvidenceAsync(
                firstSourceId,
                newFirstId,
                "pinvoke-maps-to"))
            .Should().ContainSingle(evidence =>
                evidence.Location.FilePath == firstPath);
        (await _store.ListEdgeEvidenceAsync(
                secondSourceId,
                newSecondId,
                "pinvoke-maps-to"))
            .Should().ContainSingle(evidence =>
                evidence.Location.FilePath == secondPath);
    }

    private async Task AssertStableReplacementAsync(long sourceId, long targetId)
    {
        var evidence = await _store!.ListEdgeEvidenceAsync(
            sourceId,
            targetId,
            "pinvoke-maps-to");
        evidence.Should().HaveCount(2, "the duplicate canonical occurrence is inserted once");
        evidence.Select(item => item.Location.StartLine).Should().Equal(10, 20);
        evidence.Should().OnlyContain(item =>
            item.Producer == Producer
            && item.ProducingFileId > 0);
        (await GetPayloadAsync(sourceId, targetId, "pinvoke-maps-to"))
            .Should().Be(
                """{"a":"1","z":"2"}""",
                "stable insertion order makes the earliest source occurrence authoritative");
    }

    private static ProducerEdgeEvidenceFact Fact(
        string sourceKey,
        string targetKey,
        string producingPath,
        int line,
        string value) =>
        Fact(
            sourceKey,
            targetKey,
            producingPath,
            line,
            new Dictionary<string, string> { ["value"] = value });

    private static ProducerEdgeEvidenceFact Fact(
        string sourceKey,
        string targetKey,
        string producingPath,
        int line,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            sourceKey,
            targetKey,
            "pinvoke-maps-to",
            metadata,
            new FileEvidenceFact(
                new SourceLocation(producingPath, line, 1, line, 2),
                EvidenceConfidence.Exact,
                Producer,
                Metadata: null));

    private static Edge EdgeWithEvidence(
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
            "pinvoke-maps-to",
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

    private async Task<long> SeedSymbolAsync(long fileId, string canonicalKey, string name) =>
        await _store!.UpsertSymbolAsync(
            canonicalKey,
            new Symbol(
                0,
                name,
                name,
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
}
