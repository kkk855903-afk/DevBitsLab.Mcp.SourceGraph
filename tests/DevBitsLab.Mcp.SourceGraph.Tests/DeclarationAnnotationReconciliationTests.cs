using System.Reflection;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class DeclarationAnnotationReconciliationTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-declaration-reconcile-" + Guid.NewGuid().ToString("N"));
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
    public async Task Success_reconcilesStaleFactsAndPublishesAllAnnotationsInStableOrder()
    {
        var targetPath = Path.Join(_tempDir, "Managed.cs");
        var otherPath = Path.Join(_tempDir, "Attributes.cs");
        var targetFileId = await SeedFileAsync(targetPath);
        var otherFileId = await SeedFileAsync(otherPath);
        var keepKey = "csharp:M:NativeMethods.Keep";
        var staleKey = "csharp:M:NativeMethods.Removed";
        var attributeKey = "csharp:T:External.MarkerAttribute";
        var otherHostKey = "csharp:M:External.Other";
        var keepId = await SeedSymbolAsync(targetFileId, keepKey, "Keep");
        var staleId = await SeedSymbolAsync(targetFileId, staleKey, "Removed");
        var attributeId =
            await SeedSymbolAsync(otherFileId, attributeKey, "MarkerAttribute");
        var otherHostId =
            await SeedSymbolAsync(otherFileId, otherHostKey, "Other", staleId);

        await _store!.BulkInsertAnnotationsAsync(
        [
            Stored(keepId, "OldKeep", "csharp-attribute"),
            Stored(staleId, "OldRemoved", "interop-managed-import"),
            Stored(
                otherHostId,
                "CrossFileReference",
                "csharp-attribute",
                attributeSymbolId: staleId),
            Stored(
                otherHostId,
                "OtherPreserved",
                "interop-native-export",
                attributeSymbolId: attributeId),
        ]);
        await SeedDependentFactsAsync(
            targetPath,
            otherPath,
            targetFileId,
            otherFileId,
            keepId,
            staleId,
            attributeId,
            otherHostId);

        var alpha = Fact(
            keepKey,
            "Alpha",
            "csharp-attribute",
            """{"rank":1}""",
            attributeKey);
        var beta = Fact(
            keepKey,
            "Beta",
            "interop-managed-import",
            """{"rank":2}""",
            attributeKey);

        await _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            targetPath,
            [keepKey, keepKey],
            [beta, alpha, alpha]);

        var targetSymbols = await _store.ListSymbolsInFileAsync(targetPath);
        targetSymbols.Should().ContainSingle().Which.CanonicalKey.Should().Be(keepKey);
        (await FindSymbolIdAsync(staleKey)).Should().BeNull();

        var annotations = await _store.GetAnnotationsForSymbolAsync(keepId);
        annotations.Select(annotation => annotation.Name)
            .Should().Equal("Alpha", "Beta");
        annotations.Should().OnlyContain(annotation =>
            annotation.AttributeSymbolId == attributeId);

        var otherAnnotations = await _store.GetAnnotationsForSymbolAsync(otherHostId);
        otherAnnotations.Should().HaveCount(2);
        otherAnnotations.Single(annotation => annotation.Name == "CrossFileReference")
            .AttributeSymbolId.Should().BeNull();
        otherAnnotations.Single(annotation => annotation.Name == "OtherPreserved")
            .AttributeSymbolId.Should().Be(attributeId);

        (await ScalarAsync(
            "SELECT COUNT(*) FROM refs WHERE symbol_id = @id;",
            new { id = staleId })).Should().Be(0);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM refs WHERE symbol_id = @id;",
            new { id = attributeId })).Should().Be(1);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM edges WHERE src = @id OR dst = @id;",
            new { id = staleId })).Should().Be(0);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM edge_evidence WHERE src = @id OR dst = @id;",
            new { id = staleId })).Should().Be(0);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM edges WHERE src = @src AND dst = @dst;",
            new { src = otherHostId, dst = attributeId })).Should().Be(1);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM edge_evidence WHERE src = @src AND dst = @dst;",
            new { src = otherHostId, dst = attributeId })).Should().Be(1);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM diagnostics WHERE file_id = @id;",
            new { id = targetFileId })).Should().Be(0);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM diagnostics WHERE file_id = @id;",
            new { id = otherFileId })).Should().Be(1);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM symbol_history WHERE symbol_id = @id;",
            new { id = staleId })).Should().Be(0);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM symbol_history WHERE symbol_id = @id;",
            new { id = attributeId })).Should().Be(1);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM embedding_meta WHERE symbol_id = @id;",
            new { id = staleId })).Should().Be(0);
        (await ScalarAsync(
            "SELECT COUNT(*) FROM embedding_meta WHERE symbol_id = @id;",
            new { id = attributeId })).Should().Be(1);
        (await ScalarNullableAsync(
            "SELECT container_id FROM symbols WHERE id = @id;",
            new { id = otherHostId })).Should().BeNull();
    }

    [Fact]
    public async Task Empty_annotations_isSuccessfulCompleteCleanup()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var key = "csharp:M:NativeMethods.Keep";
        var symbolId = await SeedSymbolAsync(fileId, key, "Keep");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Stored(symbolId, "Attribute", "csharp-attribute"),
            Stored(symbolId, "Import", "interop-managed-import"),
        ]);

        await _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            [key],
            []);

        (await _store.ListSymbolsInFileAsync(path)).Should().ContainSingle();
        (await _store.GetAnnotationsForSymbolAsync(symbolId)).Should().BeEmpty();
    }

    [Fact]
    public async Task HostOutsideKeepSet_isRejectedWithoutChangingOldState()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var keepKey = "csharp:M:NativeMethods.Keep";
        var staleKey = "csharp:M:NativeMethods.Stale";
        var keepId = await SeedSymbolAsync(fileId, keepKey, "Keep");
        await SeedSymbolAsync(fileId, staleKey, "Stale");
        await _store!.BulkInsertAnnotationsAsync(
            [Stored(keepId, "Old", "csharp-attribute")]);

        var reconcile = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            [keepKey],
            [Fact(staleKey, "Candidate", "csharp-attribute")]);

        await reconcile.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*keep set*");
        (await _store.ListSymbolsInFileAsync(path)).Should().HaveCount(2);
        (await _store.GetAnnotationsForSymbolAsync(keepId))
            .Should().ContainSingle().Which.Name.Should().Be("Old");
    }

    [Fact]
    public async Task ExternalKeepKey_isRejectedWithoutChangingOldState()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var otherPath = Path.Join(_tempDir, "Other.cs");
        var fileId = await SeedFileAsync(path);
        var otherFileId = await SeedFileAsync(otherPath);
        var keepKey = "csharp:M:NativeMethods.Keep";
        var externalKey = "csharp:M:Other.External";
        var keepId = await SeedSymbolAsync(fileId, keepKey, "Keep");
        await SeedSymbolAsync(otherFileId, externalKey, "External");
        await _store!.BulkInsertAnnotationsAsync(
            [Stored(keepId, "Old", "csharp-attribute")]);

        var reconcile = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            [keepKey, externalKey],
            []);

        await reconcile.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*external*");
        (await _store.ListSymbolsInFileAsync(path)).Should().ContainSingle();
        (await _store.GetAnnotationsForSymbolAsync(keepId))
            .Should().ContainSingle().Which.Name.Should().Be("Old");
    }

    [Fact]
    public async Task InvalidJsonAndCanonicalKey_areRejectedWithoutChangingOldState()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var key = "csharp:M:NativeMethods.Keep";
        var symbolId = await SeedSymbolAsync(fileId, key, "Keep");
        await _store!.BulkInsertAnnotationsAsync(
            [Stored(symbolId, "Old", "csharp-attribute")]);

        var invalidJson = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            [key],
            [Fact(key, "Candidate", "csharp-attribute", argsJson: "{")]);
        var invalidKey = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            ["missing-scheme"],
            []);

        await invalidJson.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*valid JSON*");
        await invalidKey.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*scheme prefix*");
        (await _store.ListSymbolsInFileAsync(path)).Should().ContainSingle();
        (await _store.GetAnnotationsForSymbolAsync(symbolId))
            .Should().ContainSingle().Which.Name.Should().Be("Old");
    }

    [Fact]
    public async Task MissingAttributeDefinition_rollsBackBeforeCleanup()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var key = "csharp:M:NativeMethods.Keep";
        var symbolId = await SeedSymbolAsync(fileId, key, "Keep");
        await _store!.BulkInsertAnnotationsAsync(
            [Stored(symbolId, "Old", "csharp-attribute")]);

        var reconcile = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            [key],
            [
                Fact(
                    key,
                    "Candidate",
                    "csharp-attribute",
                    attributeCanonicalKey: "csharp:T:Missing.Attribute"),
            ]);

        await reconcile.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Missing.Attribute*");
        (await _store.ListSymbolsInFileAsync(path)).Should().ContainSingle();
        (await _store.GetAnnotationsForSymbolAsync(symbolId))
            .Should().ContainSingle().Which.Name.Should().Be("Old");
    }

    [Fact]
    public async Task MissingFile_doesNotChangeOtherFiles()
    {
        var existingPath = Path.Join(_tempDir, "Existing.cs");
        var missingPath = Path.Join(_tempDir, "Missing.cs");
        var fileId = await SeedFileAsync(existingPath);
        var key = "csharp:M:Existing.Keep";
        var symbolId = await SeedSymbolAsync(fileId, key, "Keep");
        await _store!.BulkInsertAnnotationsAsync(
            [Stored(symbolId, "Old", "csharp-attribute")]);

        var reconcile = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            missingPath,
            [],
            []);

        await reconcile.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Missing.cs*");
        (await _store.ListSymbolsInFileAsync(existingPath)).Should().ContainSingle();
        (await _store.GetAnnotationsForSymbolAsync(symbolId))
            .Should().ContainSingle().Which.Name.Should().Be("Old");
    }

    [Fact]
    public async Task SqlFailureAfterCleanup_rollsBackDeclarationsAndAnnotations()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var keepKey = "csharp:M:NativeMethods.Keep";
        var staleKey = "csharp:M:NativeMethods.Stale";
        var keepId = await SeedSymbolAsync(fileId, keepKey, "Keep");
        var staleId = await SeedSymbolAsync(fileId, staleKey, "Stale");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Stored(keepId, "OldKeep", "csharp-attribute"),
            Stored(staleId, "OldStale", "csharp-attribute"),
        ]);
        await ExecuteAsync(
            """
            CREATE TRIGGER fail_reconciled_annotation
            BEFORE INSERT ON annotations
            WHEN NEW.name = 'Candidate'
            BEGIN
                SELECT RAISE(ABORT, 'forced reconciliation failure');
            END;
            """);

        var reconcile = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            [keepKey],
            [Fact(keepKey, "Candidate", "csharp-attribute")]);

        await reconcile.Should().ThrowAsync<SqliteException>();
        (await _store.ListSymbolsInFileAsync(path)).Should().HaveCount(2);
        (await FindSymbolIdAsync(staleKey)).Should().Be(staleId);
        (await _store.GetAnnotationsForSymbolAsync(keepId))
            .Should().ContainSingle().Which.Name.Should().Be("OldKeep");
        (await _store.GetAnnotationsForSymbolAsync(staleId))
            .Should().ContainSingle().Which.Name.Should().Be("OldStale");
    }

    [Fact]
    public async Task CancellationAfterCleanup_rollsBackDeclarationsAndAnnotations()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var keepKey = "csharp:M:NativeMethods.Keep";
        var staleKey = "csharp:M:NativeMethods.Stale";
        var keepId = await SeedSymbolAsync(fileId, keepKey, "Keep");
        var staleId = await SeedSymbolAsync(fileId, staleKey, "Stale");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Stored(keepId, "OldKeep", "csharp-attribute"),
            Stored(staleId, "OldStale", "csharp-attribute"),
        ]);
        using var cancellation = new CancellationTokenSource();
        var connectionField = typeof(SqliteGraphStore).GetField(
            "_connection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var storeConnection = connectionField!.GetValue(_store)
            .Should().BeOfType<SqliteConnection>().Subject;
        storeConnection.CreateFunction(
            "cancel_reconciliation",
            () =>
            {
                cancellation.Cancel();
                return 0;
            });
        await ExecuteAsync(
            $"""
            CREATE TRIGGER cancel_during_stale_symbol_delete
            BEFORE DELETE ON symbols
            WHEN OLD.id = {staleId}
            BEGIN
                SELECT cancel_reconciliation();
            END;
            """);

        var reconcile = () => _store.ReconcileFileDeclarationsAndAnnotationsAsync(
            path,
            [keepKey],
            [Fact(keepKey, "Candidate", "csharp-attribute")],
            cancellation.Token);

        await reconcile.Should().ThrowAsync<OperationCanceledException>();
        (await _store.ListSymbolsInFileAsync(path)).Should().HaveCount(2);
        (await _store.GetAnnotationsForSymbolAsync(keepId))
            .Should().ContainSingle().Which.Name.Should().Be("OldKeep");
        (await _store.GetAnnotationsForSymbolAsync(staleId))
            .Should().ContainSingle().Which.Name.Should().Be("OldStale");
    }

    private async Task SeedDependentFactsAsync(
        string targetPath,
        string otherPath,
        long targetFileId,
        long otherFileId,
        long keepId,
        long staleId,
        long attributeId,
        long otherHostId)
    {
        await ExecuteAsync(
            """
            INSERT INTO refs(symbol_id, file_id, line, col, kind)
            VALUES (@StaleId, @OtherFileId, 1, 1, 0),
                   (@AttributeId, @OtherFileId, 2, 1, 0);

            INSERT INTO edges(src, dst, kind_name, payload)
            VALUES (@KeepId, @StaleId, 'calls', NULL),
                   (@OtherHostId, @AttributeId, 'calls', NULL);

            INSERT INTO edge_evidence(
                src, dst, kind_name, producing_file_id, file_path,
                start_line, start_col, end_line, end_col,
                confidence, producer, payload)
            VALUES (@KeepId, @StaleId, 'calls', @TargetFileId, @TargetPath,
                    1, 1, 1, 2, 2, 'test', ''),
                   (@OtherHostId, @AttributeId, 'calls', @OtherFileId, @OtherPath,
                    1, 1, 1, 2, 2, 'test', '');

            INSERT INTO diagnostics(
                symbol_id, file_id, severity, code, message, line, col)
            VALUES (@KeepId, @TargetFileId, 1, 'TARGET', 'target', 1, 1),
                   (@AttributeId, @OtherFileId, 1, 'OTHER', 'other', 1, 1);

            INSERT INTO symbol_history(
                symbol_id, last_commit_sha, last_author, last_authored_at,
                line_count, blamed_content_sha)
            VALUES (@StaleId, 'stale', 'test', 1, 1, X'01'),
                   (@AttributeId, 'other', 'test', 1, 1, X'02');

            INSERT INTO embedding_meta(symbol_id, content_hash, model_version)
            VALUES (@StaleId, X'01', 'test'),
                   (@AttributeId, X'02', 'test');
            """,
            new
            {
                TargetPath = targetPath,
                OtherPath = otherPath,
                TargetFileId = targetFileId,
                OtherFileId = otherFileId,
                KeepId = keepId,
                StaleId = staleId,
                AttributeId = attributeId,
                OtherHostId = otherHostId,
            });
    }

    private static FileAnnotationFact Fact(
        string canonicalKey,
        string name,
        string flavor,
        string? argsJson = null,
        string? attributeCanonicalKey = null) =>
        new(
            canonicalKey,
            name,
            $"Test.{name}",
            flavor,
            argsJson,
            attributeCanonicalKey);

    private static AnnotationRecord Stored(
        long symbolId,
        string name,
        string flavor,
        long? attributeSymbolId = null) =>
        new(
            symbolId,
            name,
            $"Test.{name}",
            flavor,
            ArgsJson: null,
            attributeSymbolId);

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);

    private async Task<long> SeedSymbolAsync(
        long fileId,
        string canonicalKey,
        string name,
        long? containerId = null) =>
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
                containerId));

    private async Task<long?> FindSymbolIdAsync(string canonicalKey)
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM symbols WHERE canonical_key = @canonicalKey;",
            new { canonicalKey });
    }

    private async Task<long> ScalarAsync(string sql, object parameters)
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(sql, parameters);
    }

    private async Task<long?> ScalarNullableAsync(string sql, object parameters)
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long?>(sql, parameters);
    }

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        return connection;
    }
}
