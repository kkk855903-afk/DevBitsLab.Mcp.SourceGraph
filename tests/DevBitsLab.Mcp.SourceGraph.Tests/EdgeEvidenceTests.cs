using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class EdgeEvidenceTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-edge-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RepeatedLogicalEdge_preservesEveryEvidenceOccurrence_andCleansByProducer()
    {
        var sourcePath = Path.Join(_tempDir, "Source.cs");
        var secondProducerPath = Path.Join(_tempDir, "Partial.Source.cs");
        var targetPath = Path.Join(_tempDir, "Target.cs");
        var sourceFileId = await SeedFileAsync(sourcePath);
        var secondProducerFileId = await SeedFileAsync(secondProducerPath);
        var targetFileId = await SeedFileAsync(targetPath);
        var sourceId = await SeedSymbolAsync(sourceFileId, "Source", "Evidence.Source");
        var targetId = await SeedSymbolAsync(targetFileId, "Target", "Evidence.Target");

        var first = new Evidence(
            sourceFileId,
            new SourceLocation(sourcePath, 10, 9, 10, 17),
            EvidenceConfidence.Exact,
            "roslyn",
            new Dictionary<string, string> { ["operation"] = "first" });
        var second = new Evidence(
            secondProducerFileId,
            new SourceLocation(secondProducerPath, 20, 13, 20, 21),
            EvidenceConfidence.Semantic,
            "roslyn",
            new Dictionary<string, string> { ["operation"] = "second" });

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(sourceId, targetId, "calls") { Evidence = first },
            new Edge(sourceId, targetId, "calls") { Evidence = second },
            new Edge(sourceId, targetId, "calls") { Evidence = first },
        });

        (await CountAsync("edges")).Should().Be(1);
        (await CountAsync("edge_evidence")).Should().Be(2);
        var evidence = await _store.ListEdgeEvidenceAsync(sourceId, targetId, "calls");
        evidence.Should().HaveCount(2);
        evidence.Select(item => item.Location.StartLine).Should().Equal(20, 10);
        evidence.Should().Contain(item =>
            item.Confidence == EvidenceConfidence.Exact
            && item.Metadata!["operation"] == "first");
        evidence.Should().Contain(item =>
            item.Confidence == EvidenceConfidence.Semantic
            && item.Metadata!["operation"] == "second");

        await _store.ClearFileOutgoingAsync(sourceFileId);

        (await CountAsync("edges")).Should().Be(1,
            "another file still supplies evidence for the same logical edge");
        var surviving = await _store.ListEdgeEvidenceAsync(sourceId, targetId, "calls");
        surviving.Should().ContainSingle();
        surviving[0].ProducingFileId.Should().Be(secondProducerFileId);
        (await GetEdgePayloadAsync(sourceId, targetId, "calls")).Should().Be(
            """{"operation":"second"}""",
            "the compatibility payload must follow the earliest surviving evidence");

        await _store.ClearFileOutgoingAsync(secondProducerFileId);

        (await CountAsync("edge_evidence")).Should().Be(0);
        (await CountAsync("edges")).Should().Be(0,
            "a logical edge without supporting evidence must not survive");
    }

    [Fact]
    public async Task Outgoing_cleanup_can_preserve_a_separately_published_producer()
    {
        var sourcePath = Path.Join(_tempDir, "Source.cs");
        var targetPath = Path.Join(_tempDir, "Target.cs");
        var sourceFileId = await SeedFileAsync(sourcePath);
        var targetFileId = await SeedFileAsync(targetPath);
        var sourceId = await SeedSymbolAsync(
            sourceFileId,
            "Source",
            "Evidence.Source");
        var targetId = await SeedSymbolAsync(
            targetFileId,
            "Target",
            "Evidence.Target");

        await _store!.BulkInsertReferencesAsync(
        [
            new SymbolReference(
                0,
                targetId,
                sourceFileId,
                4,
                8,
                ReferenceKind.Call),
        ]);
        await _store.BulkInsertEdgesAsync(
        [
            new Edge(sourceId, targetId, "calls")
            {
                Evidence = new Evidence(
                    sourceFileId,
                    new SourceLocation(sourcePath, 4, 8, 4, 14),
                    EvidenceConfidence.Exact,
                    "roslyn",
                    new Dictionary<string, string>
                    {
                        ["owner"] = "source",
                    }),
            },
            new Edge(sourceId, targetId, "calls")
            {
                Evidence = new Evidence(
                    sourceFileId,
                    new SourceLocation(sourcePath, 7, 3, 7, 20),
                    EvidenceConfidence.Semantic,
                    "interop-analysis",
                    new Dictionary<string, string>
                    {
                        ["owner"] = "analysis",
                    }),
            },
        ]);

        await _store.ClearFileOutgoingAsync(
            sourceFileId,
            ["interop-analysis"]);

        (await CountAsync("refs")).Should().Be(0);
        var surviving = await _store.ListEdgeEvidenceAsync(
            sourceId,
            targetId,
            "calls");
        surviving.Should().ContainSingle();
        surviving[0].Producer.Should().Be("interop-analysis");
        (await GetEdgePayloadAsync(sourceId, targetId, "calls")).Should().Be(
            """{"owner":"analysis"}""");
    }

    [Fact]
    public async Task LegacyEdge_withoutEvidence_getsInferredSourceDeclarationEvidence()
    {
        var sourcePath = Path.Join(_tempDir, "LegacySource.cs");
        var targetPath = Path.Join(_tempDir, "LegacyTarget.cs");
        var sourceFileId = await SeedFileAsync(sourcePath);
        var targetFileId = await SeedFileAsync(targetPath);
        var sourceId = await SeedSymbolAsync(sourceFileId, "LegacySource", "Evidence.LegacySource");
        var targetId = await SeedSymbolAsync(targetFileId, "LegacyTarget", "Evidence.LegacyTarget");

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(
                sourceId,
                targetId,
                "uses-type",
                new Dictionary<string, string> { ["reason"] = "legacy" }),
        });

        var evidence = await _store.ListEdgeEvidenceAsync(sourceId, targetId, "uses-type");
        evidence.Should().ContainSingle();
        evidence[0].ProducingFileId.Should().Be(sourceFileId);
        evidence[0].Location.FilePath.Should().Be(sourcePath);
        evidence[0].Location.StartLine.Should().Be(1);
        evidence[0].Confidence.Should().Be(EvidenceConfidence.Inferred);
        evidence[0].Producer.Should().Be("legacy-declaration");
        evidence[0].Metadata.Should().Contain("reason", "legacy");
    }

    [Fact]
    public async Task RemovingEndpointSymbol_removesItsLogicalEdgeAndEvidence()
    {
        var sourceFileId = await SeedFileAsync(Path.Join(_tempDir, "EndpointSource.cs"));
        var targetFileId = await SeedFileAsync(Path.Join(_tempDir, "EndpointTarget.cs"));
        var sourceId = await SeedSymbolAsync(sourceFileId, "EndpointSource", "Evidence.EndpointSource");
        var targetId = await SeedSymbolAsync(targetFileId, "EndpointTarget", "Evidence.EndpointTarget");
        await _store!.BulkInsertEdgesAsync(new[] { new Edge(sourceId, targetId, "calls") });

        await _store.DeleteSymbolsForFileNotInAsync(targetFileId, Array.Empty<string>());

        (await CountAsync("edges")).Should().Be(0);
        (await CountAsync("edge_evidence")).Should().Be(0);
    }

    [Fact]
    public async Task ListingEvidence_rejectsUnboundedOrMalformedQueries()
    {
        var zeroLimit = () => _store!.ListEdgeEvidenceAsync(1, 2, "calls", limit: 0);
        var excessiveLimit = () => _store!.ListEdgeEvidenceAsync(1, 2, "calls", limit: 1001);
        var emptyKind = () => _store!.ListEdgeEvidenceAsync(1, 2, " ");

        await zeroLimit.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await excessiveLimit.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await emptyKind.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Evidence_requiresAnIndexedProducerWithTheSamePath()
    {
        var sourcePath = Path.Join(_tempDir, "ValidatedSource.cs");
        var targetPath = Path.Join(_tempDir, "ValidatedTarget.cs");
        var sourceFileId = await SeedFileAsync(sourcePath);
        var targetFileId = await SeedFileAsync(targetPath);
        var sourceId = await SeedSymbolAsync(sourceFileId, "ValidatedSource", "Evidence.ValidatedSource");
        var targetId = await SeedSymbolAsync(targetFileId, "ValidatedTarget", "Evidence.ValidatedTarget");

        var missingProducer = new Evidence(
            999_999,
            new SourceLocation(sourcePath, 1, 1, 1, 2),
            EvidenceConfidence.Exact,
            "test");
        var wrongPath = new Evidence(
            sourceFileId,
            new SourceLocation(Path.Join(_tempDir, "PatientData", "wrong.cs"), 1, 1, 1, 2),
            EvidenceConfidence.Exact,
            "test");

        var insertMissing = () => _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(sourceId, targetId, "calls") { Evidence = missingProducer },
        });
        var insertMismatch = () => _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(sourceId, targetId, "calls") { Evidence = wrongPath },
        });

        await insertMissing.Should().ThrowAsync<InvalidOperationException>();
        await insertMismatch.Should().ThrowAsync<InvalidOperationException>();
        (await CountAsync("edges")).Should().Be(0, "failed evidence inserts are transactional");
        (await CountAsync("edge_evidence")).Should().Be(0);
    }

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(path, new byte[] { 1, 2, 3, 4 }, DateTimeOffset.UtcNow);

    private async Task<long> SeedSymbolAsync(long fileId, string name, string fqn) =>
        await _store!.UpsertSymbolAsync(
            $"csharp:M:{fqn}",
            new Symbol(
                0,
                name,
                fqn,
                "method",
                fileId,
                1,
                1,
                5,
                1,
                $"void {name}()",
                null));

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table};");
    }

    private async Task<string?> GetEdgePayloadAsync(long src, long dst, string kind)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<string?>(
            """
            SELECT payload
            FROM edges
            WHERE src = @src AND dst = @dst AND kind_name = @kind;
            """,
            new { src, dst, kind });
    }
}
