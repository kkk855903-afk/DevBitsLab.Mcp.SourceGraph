using System.Text.Json;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// SQLite-backed <see cref="IScopeRegistry"/>. Lives in <c>&lt;repo&gt;/.sourcegraph/_meta.db</c>,
/// independent of the per-scope graph DBs so adding/removing a scope never has to touch the
/// other scopes' files.
/// </summary>
public sealed class SqliteScopeRegistry : IScopeRegistry
{
    // v2 adds failed_projects_json + failed_files_json columns for partial-scope reporting.
    // EnsureSchemaAsync's existing version-bump handler does a destructive DROP + recreate of
    // the entire `scopes` table when the on-disk version trails the current SchemaVersion;
    // every scope row (id, name, root, status, project_set_json, last_indexed_at, …) is lost
    // and repopulated by the host's bring-up loop on the next start. That's fine for this
    // change because LiveIndexService re-upserts every configured scope from `.sourcegraph.json`
    // on startup, and the failure lists are themselves display-only — they fill back in on
    // the first cold index after the upgrade. If a future schema change touches a column
    // whose value is NOT trivially recoverable from config + index, the migration strategy
    // will need to switch to ALTER TABLE rather than DROP.
    private const int SchemaVersion = 3;

    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteScopeRegistry> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteScopeRegistry(string databasePath, ILogger<SqliteScopeRegistry>? logger = null)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Use Default cache (per-connection) to avoid cross-connection lock contention
            // with the per-scope graph stores that share the same directory.
            Cache = SqliteCacheMode.Default,
            Pooling = false,
            ForeignKeys = true,
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        _connection.Execute("PRAGMA journal_mode=WAL;");
        _connection.Execute("PRAGMA synchronous=NORMAL;");
        _logger = logger ?? NullLogger<SqliteScopeRegistry>.Instance;
    }

    public Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        // Microsoft.Data.Sqlite has no real async I/O — running the schema setup synchronously
        // avoids the long-tail interactions with the BackgroundService startup path that we saw
        // (the awaited Dapper calls inside the host's startup lock would never resume).
        _writeLock.Wait(ct);
        try
        {
            _connection.Execute(
                "CREATE TABLE IF NOT EXISTS meta_schema_version (version INTEGER PRIMARY KEY);");
            var current = _connection.ExecuteScalar<int?>(
                "SELECT MAX(version) FROM meta_schema_version;");
            if (current == 2)
            {
                _logger.LogInformation(
                    "Scope registry schema v2 -> v3; preserving rows while adding project_count");
                _connection.Execute(
                    "ALTER TABLE scopes ADD COLUMN project_count INTEGER;");
                _connection.Execute("DELETE FROM meta_schema_version;");
            }
            else if (current is not null && current < SchemaVersion)
            {
                _logger.LogInformation("Scope registry schema v{Old} -> v{New}; rebuilding", current, SchemaVersion);
                _connection.Execute("DROP TABLE IF EXISTS scopes;");
                _connection.Execute("DELETE FROM meta_schema_version;");
            }

            using var tx = _connection.BeginTransaction();
            _connection.Execute("""
                CREATE TABLE IF NOT EXISTS scopes (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root TEXT NOT NULL,
                    project_set_json TEXT NOT NULL,
                    isolated INTEGER NOT NULL DEFAULT 0,
                    last_indexed_at INTEGER NOT NULL,
                    status TEXT NOT NULL DEFAULT 'ok',
                    status_message TEXT,
                    failed_projects_json TEXT NOT NULL DEFAULT '[]',
                    failed_files_json TEXT NOT NULL DEFAULT '[]',
                    project_count INTEGER
                );
                """, transaction: tx);
            _connection.Execute(
                "INSERT OR REPLACE INTO meta_schema_version(version) VALUES (@v);",
                new { v = SchemaVersion }, transaction: tx);
            tx.Commit();
            return Task.CompletedTask;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ScopeRow>> ListAsync(CancellationToken ct = default)
    {
        var rows = await _connection.QueryAsync<RawScopeRow>(new CommandDefinition(
            """
            SELECT id                  AS Id,
                   name                AS Name,
                   root                AS Root,
                   project_set_json    AS ProjectSetJson,
                   isolated            AS IsolatedInt,
                   last_indexed_at     AS LastIndexedAtMs,
                   status              AS Status,
                   status_message      AS StatusMessage,
                   failed_projects_json AS FailedProjectsJson,
                   failed_files_json   AS FailedFilesJson,
                   project_count       AS ProjectCount
            FROM scopes
            ORDER BY id;
            """,
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToRow()).ToList();
    }

    public async Task<ScopeRow?> GetAsync(string id, CancellationToken ct = default)
    {
        var row = await _connection.QueryFirstOrDefaultAsync<RawScopeRow>(new CommandDefinition(
            """
            SELECT id                  AS Id,
                   name                AS Name,
                   root                AS Root,
                   project_set_json    AS ProjectSetJson,
                   isolated            AS IsolatedInt,
                   last_indexed_at     AS LastIndexedAtMs,
                   status              AS Status,
                   status_message      AS StatusMessage,
                   failed_projects_json AS FailedProjectsJson,
                   failed_files_json   AS FailedFilesJson,
                   project_count       AS ProjectCount
            FROM scopes WHERE id = @id;
            """, new { id }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToRow();
    }

    public async Task UpsertAsync(ScopeRow row, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO scopes(id, name, root, project_set_json, isolated, last_indexed_at, status, status_message, failed_projects_json, failed_files_json, project_count)
                VALUES (@Id, @Name, @Root, @ProjectSetJson, @IsolatedInt, @LastIndexedAtMs, @Status, @StatusMessage, @FailedProjectsJson, @FailedFilesJson, @ProjectCount)
                ON CONFLICT(id) DO UPDATE SET
                    name                 = excluded.name,
                    root                 = excluded.root,
                    project_set_json     = excluded.project_set_json,
                    isolated             = excluded.isolated,
                    last_indexed_at      = excluded.last_indexed_at,
                    status               = excluded.status,
                    status_message       = excluded.status_message,
                    failed_projects_json = excluded.failed_projects_json,
                    failed_files_json    = excluded.failed_files_json,
                    project_count        = COALESCE(excluded.project_count, scopes.project_count);
                """,
                new
                {
                    row.Id,
                    row.Name,
                    row.Root,
                    row.ProjectSetJson,
                    IsolatedInt = row.Isolated ? 1 : 0,
                    LastIndexedAtMs = row.LastIndexedAt.ToUnixTimeMilliseconds(),
                    row.Status,
                    row.StatusMessage,
                    FailedProjectsJson = SerializeProjectFailures(row.FailedProjects),
                    FailedFilesJson = SerializeFileFailures(row.FailedFiles),
                    row.ProjectCount,
                },
                cancellationToken: ct)).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static readonly JsonSerializerOptions FailureJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static string SerializeProjectFailures(IReadOnlyList<ProjectFailure>? failures)
        => failures is null || failures.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(failures, FailureJsonOptions);

    private static string SerializeFileFailures(IReadOnlyList<FileFailure>? failures)
        => failures is null || failures.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(failures, FailureJsonOptions);

    private static IReadOnlyList<ProjectFailure> DeserializeProjectFailures(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return Array.Empty<ProjectFailure>();
        try
        {
            return JsonSerializer.Deserialize<List<ProjectFailure>>(json, FailureJsonOptions)
                   ?? (IReadOnlyList<ProjectFailure>)Array.Empty<ProjectFailure>();
        }
        catch (JsonException)
        {
            // Corrupted row — treat as empty rather than failing the whole list call. The
            // failures will repopulate on the next index pass.
            return Array.Empty<ProjectFailure>();
        }
    }

    private static IReadOnlyList<FileFailure> DeserializeFileFailures(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return Array.Empty<FileFailure>();
        try
        {
            return JsonSerializer.Deserialize<List<FileFailure>>(json, FailureJsonOptions)
                   ?? (IReadOnlyList<FileFailure>)Array.Empty<FileFailure>();
        }
        catch (JsonException)
        {
            return Array.Empty<FileFailure>();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM scopes WHERE id = @id;",
                new { id }, cancellationToken: ct)).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private sealed record RawScopeRow(string Id, string Name, string Root, string ProjectSetJson,
        long IsolatedInt, long LastIndexedAtMs, string Status, string? StatusMessage,
        string? FailedProjectsJson, string? FailedFilesJson, long? ProjectCount)
    {
        public ScopeRow ToRow() => new(
            Id, Name, Root, ProjectSetJson, IsolatedInt != 0,
            DateTimeOffset.FromUnixTimeMilliseconds(LastIndexedAtMs), Status, StatusMessage,
            DeserializeProjectFailures(FailedProjectsJson),
            DeserializeFileFailures(FailedFilesJson),
            ProjectCount is null ? null : checked((int)ProjectCount.Value));
    }
}
