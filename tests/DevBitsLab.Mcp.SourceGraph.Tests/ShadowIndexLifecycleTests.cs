using Dapper;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ShadowIndexLifecycleTests
{
    [Fact]
    public async Task SchemaProbe_readsVersion_withoutMutatingDatabase()
    {
        var directory = CreateTempDirectory();
        var database = Path.Join(directory, "graph.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    "CREATE TABLE schema_version(version INTEGER PRIMARY KEY); "
                    + "INSERT INTO schema_version(version) VALUES (@version);",
                    new { version = Schema.Version - 1 });
            }

            (await GraphSchemaProbe.ReadVersionAsync(database)).Should().Be(Schema.Version - 1);
            (await GraphSchemaProbe.RequiresUpgradeAsync(database)).Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Activate_atomicallyPromotesShadow_andRetainsPreviousDatabase()
    {
        var directory = CreateTempDirectory();
        var primary = Path.Join(directory, "default.db");
        var shadow = primary + ".shadow-test";
        var archives = Path.Join(directory, "orphans");
        try
        {
            await CreateMarkerDatabaseAsync(primary, "old");
            await CreateMarkerDatabaseAsync(shadow, "new");

            var archive = ShadowDatabaseActivator.Activate(
                primary,
                shadow,
                archives,
                "upgrade");

            File.Exists(shadow).Should().BeFalse();
            File.Exists(archive).Should().BeTrue();
            (await ReadMarkerAsync(primary)).Should().Be("new");
            (await ReadMarkerAsync(archive)).Should().Be("old");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Activate_rejectsUncheckpointedShadow_withoutChangingPrimary()
    {
        var directory = CreateTempDirectory();
        var primary = Path.Join(directory, "default.db");
        var shadow = primary + ".shadow-test";
        try
        {
            await CreateMarkerDatabaseAsync(primary, "old");
            await CreateMarkerDatabaseAsync(shadow, "new");
            await File.WriteAllTextAsync(shadow + "-wal", "pending");

            var act = () => ShadowDatabaseActivator.Activate(
                primary,
                shadow,
                Path.Join(directory, "orphans"),
                "upgrade");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*uncheckpointed*");
            (await ReadMarkerAsync(primary)).Should().Be("old");
            (await ReadMarkerAsync(shadow)).Should().Be("new");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Activate_faultBeforeAtomicPromotion_keepsPrimaryAndShadowRecoverable()
    {
        var directory = CreateTempDirectory();
        var primary = Path.Join(directory, "default.db");
        var shadow = primary + ".shadow-test";
        try
        {
            await CreateMarkerDatabaseAsync(primary, "old");
            await CreateMarkerDatabaseAsync(shadow, "new");

            var act = () => ShadowDatabaseActivator.Activate(
                primary,
                shadow,
                Path.Join(directory, "orphans"),
                "rebuild",
                stage => throw new InjectedShadowFailureException(stage));

            act.Should().Throw<InjectedShadowFailureException>();
            (await ReadMarkerAsync(primary)).Should().Be("old");
            (await ReadMarkerAsync(shadow)).Should().Be("new");
            Directory.EnumerateFiles(Path.Join(directory, "orphans"), "*.db")
                .Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CreateMarkerDatabaseAsync(string path, string marker)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "CREATE TABLE marker(value TEXT NOT NULL); INSERT INTO marker(value) VALUES (@marker);",
            new { marker });
    }

    private static async Task<string> ReadMarkerAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<string>("SELECT value FROM marker;"))!;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"sourcegraph-shadow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InjectedShadowFailureException(ShadowActivationStage stage)
        : Exception($"Injected failure at {stage}");
}
