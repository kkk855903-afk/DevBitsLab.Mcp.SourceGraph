using Dapper;
using Microsoft.Data.Sqlite;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Reads graph schema metadata without opening a writable <see cref="SqliteGraphStore"/>.
/// Live indexing uses this probe before <c>EnsureSchemaAsync</c> so an outdated production
/// database can be rebuilt beside the active file instead of being cleared in place.
/// </summary>
public static class GraphSchemaProbe
{
    public static async Task<int?> ReadVersionAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath)) return null;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var hasVersionTable = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version';",
            cancellationToken: ct)).ConfigureAwait(false);
        if (hasVersionTable == 0) return null;

        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(version) FROM schema_version;",
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public static async Task<bool> RequiresUpgradeAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        var version = await ReadVersionAsync(databasePath, ct).ConfigureAwait(false);
        return version is not null && version < Schema.Version;
    }
}
