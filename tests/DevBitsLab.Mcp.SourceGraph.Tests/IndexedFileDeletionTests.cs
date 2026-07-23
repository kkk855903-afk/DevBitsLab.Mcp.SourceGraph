using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class IndexedFileDeletionTests : IAsyncLifetime
{
    private const int EmbeddingDimension = 4;
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private bool _vec0Loaded;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-file-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        _vec0Loaded = _store.TryLoadVectorExtension(EmbeddingDimension);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task DeleteFileAsync_byId_removesOwnedAndInboundArtifactsTransactionally()
    {
        var deletedPath = Path.Join(_tempDir, "Generated", "Secret.cs");
        var survivorPath = Path.Join(_tempDir, "src", "Survivor.cs");
        var deletedFileId = await SeedFileAsync(deletedPath);
        var survivorFileId = await SeedFileAsync(survivorPath);

        var deletedSymbolId = await SeedSymbolAsync(
            deletedFileId,
            "Secret",
            "Deletion.Secret");
        var survivorSourceId = await SeedSymbolAsync(
            survivorFileId,
            "SurvivorSource",
            "Deletion.SurvivorSource");
        var survivorTargetId = await SeedSymbolAsync(
            survivorFileId,
            "SurvivorTarget",
            "Deletion.SurvivorTarget");
        var survivingChildId = await _store!.UpsertSymbolAsync(
            "csharp:M:Deletion.SurvivingChild",
            new Symbol(
                0,
                "SurvivingChild",
                "Deletion.SurvivingChild",
                "method",
                survivorFileId,
                10,
                1,
                12,
                1,
                "void SurvivingChild()",
                deletedSymbolId));

        await _store.BulkInsertReferencesAsync(
        [
            new SymbolReference(0, deletedSymbolId, survivorFileId, 2, 1, ReferenceKind.Reference),
            new SymbolReference(0, survivorSourceId, deletedFileId, 3, 1, ReferenceKind.Reference),
            new SymbolReference(0, survivorTargetId, survivorFileId, 4, 1, ReferenceKind.Reference),
        ]);

        var deletedEvidence = new Evidence(
            deletedFileId,
            new SourceLocation(deletedPath, 3, 1, 3, 8),
            EvidenceConfidence.Exact,
            "test",
            new Dictionary<string, string> { ["owner"] = "deleted" });
        var survivingEvidence = new Evidence(
            survivorFileId,
            new SourceLocation(survivorPath, 6, 1, 6, 8),
            EvidenceConfidence.Semantic,
            "test",
            new Dictionary<string, string> { ["owner"] = "survivor" });
        await _store.BulkInsertEdgesAsync(
        [
            new Edge(deletedSymbolId, survivorSourceId, "calls") { Evidence = deletedEvidence },
            new Edge(survivorSourceId, survivorTargetId, "calls") { Evidence = deletedEvidence },
            new Edge(survivorSourceId, survivorTargetId, "calls") { Evidence = survivingEvidence },
        ]);

        await _store.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                deletedSymbolId,
                "DeleteMe",
                "Deletion.DeleteMeAttribute",
                "csharp-attribute",
                null,
                null),
            new AnnotationRecord(
                survivorSourceId,
                "Secret",
                "Deletion.Secret",
                "csharp-attribute",
                null,
                deletedSymbolId),
        ]);
        await _store.UpsertDiagnosticsForFileAsync(
            deletedFileId,
            [new DiagnosticRecord(deletedSymbolId, deletedFileId, 2, "DEL001", "delete", 1, 1)]);
        await _store.UpsertDiagnosticsForFileAsync(
            survivorFileId,
            [
                new DiagnosticRecord(deletedSymbolId, survivorFileId, 2, "DEL002", "delete inbound", 2, 1),
                new DiagnosticRecord(survivorSourceId, survivorFileId, 1, "KEEP001", "keep", 3, 1),
            ]);
        await _store.UpsertSymbolHistoryAsync(
            new SymbolHistory(
                deletedSymbolId,
                "deleted-sha",
                "Deleted Author",
                DateTimeOffset.UtcNow,
                1,
                [1]));
        await _store.UpsertSymbolHistoryAsync(
            new SymbolHistory(
                survivorSourceId,
                "survivor-sha",
                "Surviving Author",
                DateTimeOffset.UtcNow,
                1,
                [2]));

        var embeddings = _store.CreateEmbeddingsStore(EmbeddingDimension);
        if (_vec0Loaded)
        {
            await embeddings.UpsertAsync(
                deletedSymbolId,
                [1],
                new float[EmbeddingDimension],
                "test/v1");
            await embeddings.UpsertAsync(
                survivorSourceId,
                [2],
                new float[EmbeddingDimension],
                "test/v1");
        }
        else
        {
            await ExecuteAsync(
                """
                INSERT INTO embedding_meta(symbol_id, content_hash, model_version)
                VALUES (@deletedId, X'01', 'test/v1'), (@survivorId, X'02', 'test/v1');
                """,
                new { deletedId = deletedSymbolId, survivorId = survivorSourceId });
        }

        (await _store.DeleteFileAsync(deletedFileId)).Should().BeTrue();

        (await ScalarAsync<long>("SELECT COUNT(*) FROM files WHERE id = @id;", new { id = deletedFileId }))
            .Should().Be(0);
        (await _store.FindSymbolsAsync("Secret")).Should().BeEmpty();
        (await ScalarAsync<long>("SELECT COUNT(*) FROM refs;")).Should().Be(1);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM edges;")).Should().Be(1);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM edge_evidence;")).Should().Be(1);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM annotations;")).Should().Be(1);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM annotations WHERE symbol_id = @id AND attribute_symbol_id IS NULL;",
            new { id = survivorSourceId })).Should().Be(1);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM diagnostics;")).Should().Be(1);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM symbol_history;")).Should().Be(1);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM embedding_meta;")).Should().Be(1);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM symbols WHERE id = @id AND container_id IS NULL;",
            new { id = survivingChildId })).Should().Be(1);

        var evidence = await _store.ListEdgeEvidenceAsync(
            survivorSourceId,
            survivorTargetId,
            "calls");
        evidence.Should().ContainSingle();
        evidence[0].ProducingFileId.Should().Be(survivorFileId);
        evidence[0].Metadata.Should().Contain("owner", "survivor");
        if (_vec0Loaded)
        {
            (await embeddings.CountAsync()).Should().Be(1);
        }

        (await _store.DeleteFileAsync(deletedFileId)).Should().BeFalse(
            "deleting an already absent file is idempotent");
    }

    [Fact]
    public async Task DeleteFileAsync_byPath_resolvesAndDeletesInsideTheTransaction()
    {
        var path = Path.Join(_tempDir, "Generated", "ByPath.cs");
        var fileId = await SeedFileAsync(path);
        await SeedSymbolAsync(fileId, "ByPath", "Deletion.ByPath");

        (await _store!.DeleteFileAsync(path)).Should().BeTrue();
        (await _store.GetAllFilesAsync()).Should().NotContain(file => file.Path == path);
        (await _store.FindSymbolsAsync("ByPath")).Should().BeEmpty();
        (await _store.DeleteFileAsync(path)).Should().BeFalse();
    }

    [Fact]
    public async Task ReplaceFileFactsAsync_removingDeclaration_cleansInboundLinksAndOrphanMetadata()
    {
        var replacedPath = Path.Join(_tempDir, "src", "Replaced.cs");
        var survivorPath = Path.Join(_tempDir, "src", "Survivor.cs");
        var replacedFileId = await SeedFileAsync(replacedPath);
        var survivorFileId = await SeedFileAsync(survivorPath);
        var staleSymbolId = await SeedSymbolAsync(
            replacedFileId,
            "StaleAttribute",
            "Replacement.StaleAttribute");
        var keptSymbolId = await SeedSymbolAsync(
            replacedFileId,
            "Keep",
            "Replacement.Keep");
        var survivorHostId = await SeedSymbolAsync(
            survivorFileId,
            "SurvivorHost",
            "Replacement.SurvivorHost");
        var survivorChildId = await SeedSymbolAsync(
            survivorFileId,
            "SurvivorChild",
            "Replacement.SurvivorChild");

        await _store!.BatchUpdateContainerIdsAsync([(survivorChildId, staleSymbolId)]);
        await _store.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                survivorHostId,
                "StaleAttribute",
                "Replacement.StaleAttribute",
                "csharp-attribute",
                null,
                staleSymbolId),
        ]);
        await _store.UpsertSymbolHistoryAsync(
            new SymbolHistory(
                staleSymbolId,
                "stale-sha",
                "Stale Author",
                DateTimeOffset.UtcNow,
                1,
                [1]));

        var embeddings = _store.CreateEmbeddingsStore(EmbeddingDimension);
        if (_vec0Loaded)
        {
            await embeddings.UpsertAsync(
                staleSymbolId,
                [1],
                new float[EmbeddingDimension],
                "test/v1");
        }
        else
        {
            await ExecuteAsync(
                """
                INSERT INTO embedding_meta(symbol_id, content_hash, model_version)
                VALUES (@id, X'01', 'test/v1');
                """,
                new { id = staleSymbolId });
        }

        var newSha = new byte[] { 9, 8, 7, 6 };
        await _store.ReplaceFileFactsAsync(
            new FileFactsReplacement(
                replacedPath,
                newSha,
                DateTimeOffset.UtcNow,
                IsGenerated: false,
                Symbols:
                [
                    new FileSymbolFact(
                        "csharp:M:Replacement.Keep",
                        "Keep",
                        "Replacement.Keep",
                        "method",
                        2,
                        1,
                        4,
                        1,
                        "void Keep()",
                        ContainerCanonicalKey: null,
                        Modifiers: null,
                        Accessibility: 0,
                        XmlSummary: null),
                ],
                Edges: Array.Empty<FileEdgeFact>(),
                Annotations: Array.Empty<FileAnnotationFact>(),
                References: Array.Empty<FileReferenceFact>()));

        (await _store.GetFileContentHashAsync(replacedPath)).Should().Equal(newSha);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM symbols WHERE id = @id;",
            new { id = staleSymbolId })).Should().Be(0);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM symbols WHERE id = @id AND start_line = 2;",
            new { id = keptSymbolId })).Should().Be(
            1,
            "kept declarations retain their stable id and receive updated facts");
        (await ScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM annotations
            WHERE symbol_id = @hostId
              AND attribute_symbol_id IS NULL;
            """,
            new { hostId = survivorHostId })).Should().Be(1);
        (await ScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM symbols
            WHERE id = @childId
              AND container_id IS NULL;
            """,
            new { childId = survivorChildId })).Should().Be(1);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM symbol_history WHERE symbol_id = @id;",
            new { id = staleSymbolId })).Should().Be(0);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM embedding_meta WHERE symbol_id = @id;",
            new { id = staleSymbolId })).Should().Be(0);
        if (_vec0Loaded)
        {
            (await embeddings.CountAsync()).Should().Be(0);
        }
    }

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(path, [1, 2, 3, 4], DateTimeOffset.UtcNow);

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
