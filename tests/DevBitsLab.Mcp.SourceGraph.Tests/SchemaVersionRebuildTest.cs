using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the open-language-contract schema rebuild path: opening a SQLite DB whose
/// <c>schema_version</c> is the previous value (v10) must trigger a drop-and-rebuild on next
/// <c>EnsureSchemaAsync</c>, leaving the v11 layout in place and discarding any sentinel data
/// from the prior schema.
/// </summary>
public sealed class SchemaVersionRebuildTest
{
    [Fact]
    public async Task EnsureSchema_dropsV10Data_andRebuildsV11()
    {
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless.
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-schemarebuild-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var dbPath = Path.Join(tmp, "graph.db");
        try
        {
            // 1) Manually scaffold a v10-flavoured DB. We don't need the entire prior schema —
            //    just enough that EnsureSchemaAsync's "is on-disk version below current?" check
            //    fires the drop branch. We seed the legacy `attributes` table and one row in it
            //    plus a `schema_version` row for v10, then close the connection.
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync("""
                    CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
                    CREATE TABLE attributes (
                        id INTEGER PRIMARY KEY,
                        symbol_id INTEGER NOT NULL,
                        name TEXT NOT NULL,
                        full_name TEXT NOT NULL,
                        args_json TEXT,
                        attribute_symbol_id INTEGER
                    );
                    INSERT INTO attributes(symbol_id, name, full_name, args_json)
                        VALUES (1, 'sentinel', 'Sample.Sentinel', '{}');
                    INSERT INTO schema_version(version) VALUES (10);
                    """);
            }

            // 2) Open via the SqliteGraphStore and ensure the schema. The store should detect
            //    on-disk v10 < v11, run DropAll, then apply V1+V2 to land on v11.
            await using (var store = new SqliteGraphStore(dbPath))
            {
                await store.EnsureSchemaAsync();
            }

            // 3) Verify: schema_version is v11, the legacy `attributes` table is gone, the new
            //    `annotations` table exists, and the sentinel row from step 1 is no longer
            //    discoverable (DropAll discards it; EnsureSchemaAsync does not migrate data).
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();

                var version = await conn.ExecuteScalarAsync<int?>(
                    "SELECT MAX(version) FROM schema_version;");
                version.Should().Be(11);

                var hasLegacyAttributes = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='attributes';");
                hasLegacyAttributes.Should().Be(0, "legacy attributes table is dropped on the v10 -> v11 rebuild");

                var hasAnnotations = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='annotations';");
                hasAnnotations.Should().Be(1, "v11 introduces annotations as the renamed attributes table");

                // The annotations table must carry the new `flavor` column (introduced by
                // open-language-contract).
                var columns = (await conn.QueryAsync<string>(
                    "SELECT name FROM pragma_table_info('annotations');")).ToList();
                columns.Should().Contain("flavor");

                // The edges table gains the JSON `payload` column in v11.
                var edgeColumns = (await conn.QueryAsync<string>(
                    "SELECT name FROM pragma_table_info('edges');")).ToList();
                edgeColumns.Should().Contain("payload");
                edgeColumns.Should().Contain("kind_name");
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch (IOException) { /* best-effort cleanup; another handle may still hold the file */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup; readonly bit or ACL drift */ }
        }
    }
}
