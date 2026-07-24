using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class AnnotationFlavorReplacementTests : IAsyncLifetime
{
    private const string Flavor = "interop-finding";

    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-annotation-replacement-" + Guid.NewGuid().ToString("N"));
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
    public async Task Empty_replacement_clears_only_selected_file_and_flavor()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var otherPath = Path.Join(_tempDir, "Other.cs");
        var managedFileId = await SeedFileAsync(managedPath);
        var otherFileId = await SeedFileAsync(otherPath);
        var managedId = await SeedSymbolAsync(
            managedFileId,
            "csharp:M:NativeMethods.Compute",
            "Compute");
        var otherId = await SeedSymbolAsync(
            otherFileId,
            "csharp:M:Other.Compute",
            "Compute");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Stored(managedId, "OldFinding", Flavor),
            Stored(managedId, "DllImport", "csharp-attribute"),
            Stored(otherId, "OtherFinding", Flavor),
        ]);

        await _store.ReplaceAnnotationsForFileByFlavorAsync(managedPath, Flavor, []);

        (await _store.GetAnnotationsForSymbolAsync(managedId))
            .Should().ContainSingle()
            .Which.Flavor.Should().Be("csharp-attribute");
        (await _store.GetAnnotationsForSymbolAsync(otherId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OtherFinding");
    }

    [Fact]
    public async Task Missing_host_rolls_back_old_projection_before_any_candidate_is_written()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var hostKey = "csharp:M:NativeMethods.Compute";
        var hostId = await SeedSymbolAsync(fileId, hostKey, "Compute");
        await _store!.BulkInsertAnnotationsAsync([Stored(hostId, "OldFinding", Flavor)]);

        var replace = () => _store.ReplaceAnnotationsForFileByFlavorAsync(
            path,
            Flavor,
            [
                Fact(hostKey, "Candidate"),
                Fact("csharp:M:NativeMethods.Missing", "Missing"),
            ]);

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NativeMethods.Missing*");
        var annotations = await _store.GetAnnotationsForSymbolAsync(hostId);
        annotations.Should().ContainSingle();
        annotations[0].Name.Should().Be("OldFinding");
    }

    [Fact]
    public async Task Host_owned_by_another_file_rolls_back_old_projection()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var otherPath = Path.Join(_tempDir, "Other.cs");
        var managedFileId = await SeedFileAsync(managedPath);
        var otherFileId = await SeedFileAsync(otherPath);
        var managedKey = "csharp:M:NativeMethods.Compute";
        var externalKey = "csharp:M:Other.Compute";
        var managedId = await SeedSymbolAsync(managedFileId, managedKey, "Compute");
        var externalId = await SeedSymbolAsync(otherFileId, externalKey, "Compute");
        await _store!.BulkInsertAnnotationsAsync([Stored(managedId, "OldFinding", Flavor)]);

        var replace = () => _store.ReplaceAnnotationsForFileByFlavorAsync(
            managedPath,
            Flavor,
            [
                Fact(managedKey, "Candidate"),
                Fact(externalKey, "External"),
            ]);

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*external*");
        (await _store.GetAnnotationsForSymbolAsync(managedId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OldFinding");
        (await _store.GetAnnotationsForSymbolAsync(externalId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Mismatched_fact_flavor_is_rejected_without_cleanup()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var hostKey = "csharp:M:NativeMethods.Compute";
        var hostId = await SeedSymbolAsync(fileId, hostKey, "Compute");
        await _store!.BulkInsertAnnotationsAsync([Stored(hostId, "OldFinding", Flavor)]);
        var invalid = Fact(hostKey, "Candidate") with { Flavor = "csharp-attribute" };

        var replace = () => _store.ReplaceAnnotationsForFileByFlavorAsync(
            path,
            Flavor,
            [invalid]);

        await replace.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exactly match*");
        (await _store.GetAnnotationsForSymbolAsync(hostId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OldFinding");
    }

    [Fact]
    public async Task Invalid_args_json_is_rejected_without_cleanup()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var hostKey = "csharp:M:NativeMethods.Compute";
        var hostId = await SeedSymbolAsync(fileId, hostKey, "Compute");
        await _store!.BulkInsertAnnotationsAsync([Stored(hostId, "OldFinding", Flavor)]);
        var invalid = Fact(hostKey, "Candidate") with { ArgsJson = "{" };

        var replace = () => _store.ReplaceAnnotationsForFileByFlavorAsync(
            path,
            Flavor,
            [invalid]);

        await replace.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*valid JSON*");
        (await _store.GetAnnotationsForSymbolAsync(hostId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OldFinding");
    }

    [Fact]
    public async Task Sql_failure_after_cleanup_rolls_back_old_projection()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var hostKey = "csharp:M:NativeMethods.Compute";
        var hostId = await SeedSymbolAsync(fileId, hostKey, "Compute");
        await _store!.BulkInsertAnnotationsAsync([Stored(hostId, "OldFinding", Flavor)]);
        await ExecuteAsync(
            """
            CREATE TRIGGER fail_new_interop_annotation
            BEFORE INSERT ON annotations
            WHEN NEW.flavor = 'interop-finding' AND NEW.name = 'Candidate'
            BEGIN
                SELECT RAISE(ABORT, 'forced annotation insert failure');
            END;
            """);

        var replace = () => _store.ReplaceAnnotationsForFileByFlavorAsync(
            path,
            Flavor,
            [Fact(hostKey, "Candidate")]);

        await replace.Should().ThrowAsync<SqliteException>();
        (await _store.GetAnnotationsForSymbolAsync(hostId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OldFinding");
    }

    [Fact]
    public async Task Cancellation_before_transaction_preserves_old_projection()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var hostKey = "csharp:M:NativeMethods.Compute";
        var hostId = await SeedSymbolAsync(fileId, hostKey, "Compute");
        await _store!.BulkInsertAnnotationsAsync([Stored(hostId, "OldFinding", Flavor)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var replace = () => _store.ReplaceAnnotationsForFileByFlavorAsync(
            path,
            Flavor,
            [],
            cancellation.Token);

        await replace.Should().ThrowAsync<OperationCanceledException>();
        (await _store.GetAnnotationsForSymbolAsync(hostId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OldFinding");
    }

    [Fact]
    public async Task Replacement_deduplicates_and_inserts_in_canonical_order()
    {
        var path = Path.Join(_tempDir, "Managed.cs");
        var fileId = await SeedFileAsync(path);
        var alphaKey = "csharp:M:NativeMethods.Alpha";
        var betaKey = "csharp:M:NativeMethods.Beta";
        await SeedSymbolAsync(fileId, betaKey, "Beta");
        await SeedSymbolAsync(fileId, alphaKey, "Alpha");
        var alphaA = Fact(alphaKey, "A") with { ArgsJson = """{"rank":1}""" };
        var alphaB = Fact(alphaKey, "B") with { ArgsJson = """{"rank":2}""" };
        var beta = Fact(betaKey, "Z");
        var expected = new[]
        {
            new AnnotationProjection(alphaKey, "A", """{"rank":1}"""),
            new AnnotationProjection(alphaKey, "B", """{"rank":2}"""),
            new AnnotationProjection(betaKey, "Z", null),
        };

        await _store!.ReplaceAnnotationsForFileByFlavorAsync(
            path,
            Flavor,
            [beta, alphaB, alphaA, alphaA]);
        (await GetFlavorRowsAsync()).Should().Equal(expected);

        await _store.ReplaceAnnotationsForFileByFlavorAsync(
            path,
            Flavor,
            [alphaA, beta, alphaA, alphaB]);
        (await GetFlavorRowsAsync()).Should().Equal(expected);
    }

    private static FileAnnotationFact Fact(string canonicalKey, string name) =>
        new(
            canonicalKey,
            name,
            $"MedInterop.{name}",
            Flavor,
            ArgsJson: null,
            AttributeCanonicalKey: null);

    private static AnnotationRecord Stored(long symbolId, string name, string flavor) =>
        new(
            symbolId,
            name,
            $"MedInterop.{name}",
            flavor,
            ArgsJson: null,
            AttributeSymbolId: null);

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

    private async Task<IReadOnlyList<AnnotationProjection>> GetFlavorRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<AnnotationProjection>(
            """
            SELECT s.canonical_key AS SymbolCanonicalKey,
                   a.name AS Name,
                   a.args_json AS ArgsJson
            FROM annotations a
            JOIN symbols s ON s.id = a.symbol_id
            WHERE a.flavor = @Flavor
            ORDER BY a.id;
            """,
            new { Flavor });
        return rows.AsList();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    private sealed record AnnotationProjection(
        string SymbolCanonicalKey,
        string Name,
        string? ArgsJson);
}
