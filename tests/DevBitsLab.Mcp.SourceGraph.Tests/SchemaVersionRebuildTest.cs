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
/// Coverage for destructive graph-schema boundaries. A v11 graph can contain rows indexed
/// before the medical privacy policy existed, so upgrading it must purge every patient-derived
/// graph artifact. Older layouts must still rebuild, while opening the current layout remains
/// idempotent.
/// </summary>
public sealed class SchemaVersionRebuildTest
{
    private const string PatientDicomPath = "C:/repo/PatientData/Patient-0001/study.dcm";
    private const string PatientImagePath = "C:/repo/Images/Patient-0001-preview.jpg";
    private const long PatientSymbolId = 1001;
    private const long PatientImageSymbolId = 1002;

    [Fact]
    public async Task EnsureSchema_dropsV15SemanticProjection_soFixedBindingsAreReindexed()
    {
        var tmp = CreateTempDirectory("semantic-reprojection");
        var dbPath = Path.Join(tmp, "graph.db");
        const string stalePath = "C:/repo/Startup.cs";
        try
        {
            await using (var store = new SqliteGraphStore(dbPath))
            {
                await store.EnsureSchemaAsync();
            }

            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
                    DELETE FROM schema_version;
                    INSERT INTO schema_version(version) VALUES (15);
                    INSERT INTO files(
                        id, path, content_sha256, last_indexed_at, is_generated)
                    VALUES (1, @Path, X'01020304', 1700000000000, 0);
                    """,
                    new { Path = stalePath });
            }

            await using (var store = new SqliteGraphStore(dbPath))
            {
                await store.EnsureSchemaAsync();
                (await store.GetFileContentHashAsync(stalePath)).Should().BeNull(
                    "v15's SHA fast path could otherwise retain stale extension-method and WPF projections");
                (await store.GetStatsAsync()).Should().Be(new GraphStats(0, 0, 0, 0));
            }

            await using var verify = new SqliteConnection($"Data Source={dbPath}");
            await verify.OpenAsync();
            (await verify.ExecuteScalarAsync<int?>(
                    "SELECT MAX(version) FROM schema_version;"))
                .Should().Be(Schema.Version);
        }
        finally
        {
            DeleteTempDirectory(tmp);
        }
    }

    [Fact]
    public async Task EnsureSchema_dropsV11PatientCanaries_andRebuildsCurrentSchema()
    {
        var tmp = CreateTempDirectory("privacy-purge");
        var dbPath = Path.Join(tmp, "graph.db");
        try
        {
            // Create the current layout through the public entry point, seed every privacy-
            // relevant table, then downgrade the marker to v11. This directly exercises the
            // production version gate while the separate v10 test below covers historical DDL.
            await using (var store = new SqliteGraphStore(dbPath))
            {
                await store.EnsureSchemaAsync();
            }

            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
                    DELETE FROM schema_version;
                    INSERT INTO schema_version(version) VALUES (11);

                    INSERT INTO files(id, path, content_sha256, last_indexed_at, is_generated)
                    VALUES
                        (101, @DicomPath, X'01020304', 1700000000000, 0),
                        (102, @ImagePath, X'05060708', 1700000000000, 1);

                    INSERT INTO symbols(
                        id, canonical_key, name, fqn, kind_name, file_id,
                        start_line, start_col, end_line, end_col,
                        signature, accessibility)
                    VALUES
                        (@PatientSymbolId, 'S:csharp:Patient0001Record', 'Patient0001Record',
                         'Private.Patient0001Record', 'class', 101,
                         1, 1, 20, 1, 'class Patient0001Record', 6),
                        (@PatientImageSymbolId, 'S:csharp:Patient0001Preview', 'Patient0001Preview',
                         'Private.Patient0001Preview', 'method', 102,
                         4, 5, 8, 6, 'void Patient0001Preview()', 6);

                    INSERT INTO refs(id, symbol_id, file_id, line, col, kind)
                    VALUES (2001, @PatientImageSymbolId, 101, 12, 9, 2);

                    INSERT INTO edges(src, dst, kind_name, payload)
                    VALUES (
                        @PatientSymbolId,
                        @PatientImageSymbolId,
                        'calls',
                        '{"patient":"Patient-0001"}');

                    INSERT INTO edge_evidence(
                        src, dst, kind_name, producing_file_id, file_path,
                        start_line, start_col, end_line, end_col,
                        confidence, producer, payload)
                    VALUES (
                        @PatientSymbolId,
                        @PatientImageSymbolId,
                        'calls',
                        101,
                        @DicomPath,
                        12,
                        9,
                        12,
                        18,
                        2,
                        'pre-policy-canary',
                        '{"patient":"Patient-0001"}');

                    INSERT INTO diagnostics(
                        id, symbol_id, file_id, severity, code, message, line, col)
                    VALUES (
                        3001,
                        @PatientSymbolId,
                        101,
                        2,
                        'PHI001',
                        'Patient-0001 DICOM metadata',
                        3,
                        2);

                    -- embedding_meta is always present. The vec0-backed symbol_embeddings table
                    -- is optional and only exists when the native extension loaded, so this is
                    -- the deterministic embedding canary supported by every graph layout here.
                    INSERT INTO embedding_meta(symbol_id, content_hash, model_version)
                    VALUES
                        (@PatientSymbolId, X'0A0B0C0D', 'patient-canary/v1'),
                        (@PatientImageSymbolId, X'0E0F1011', 'patient-canary/v1');
                    """,
                    new
                    {
                        DicomPath = PatientDicomPath,
                        ImagePath = PatientImagePath,
                        PatientSymbolId,
                        PatientImageSymbolId,
                    });
            }

            await using (var store = new SqliteGraphStore(dbPath))
            {
                await store.EnsureSchemaAsync();

                (await store.GetFileContentHashAsync(PatientDicomPath)).Should().BeNull();
                (await store.GetFileContentHashAsync(PatientImagePath)).Should().BeNull();
                (await store.FindSymbolsAsync("Patient0001")).Should().BeEmpty();
                (await store.FindReferencesAsync(PatientImageSymbolId)).Should().BeEmpty();
                (await store.ListCalleesAsync(PatientSymbolId)).Should().BeEmpty();
                (await store.FindDiagnosticsAsync(
                    severity: null,
                    code: "PHI001",
                    symbolId: null)).Should().BeEmpty();

                (await store.GetStatsAsync()).Should().Be(new GraphStats(
                    FileCount: 0,
                    SymbolCount: 0,
                    ReferenceCount: 0,
                    EdgeCount: 0));
            }

            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();

                var version = await conn.ExecuteScalarAsync<int?>(
                    "SELECT MAX(version) FROM schema_version;");
                version.Should().Be(Schema.Version);

                foreach (var table in new[]
                         {
                             "files",
                             "symbols",
                             "refs",
                             "edges",
                             "edge_evidence",
                             "diagnostics",
                             "embedding_meta",
                         })
                {
                    var count = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table};");
                    count.Should().Be(0, $"{table} must not retain pre-policy patient records");
                }
            }
        }
        finally
        {
            DeleteTempDirectory(tmp);
        }
    }

    [Fact]
    public async Task EnsureSchema_dropsV10Data_andRebuildsCurrentSchema()
    {
        var tmp = CreateTempDirectory("legacy-v10");
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
            //    the stale version, run DropAll, then apply V1+V2 to land on the current schema.
            await using (var store = new SqliteGraphStore(dbPath))
            {
                await store.EnsureSchemaAsync();
            }

            // 3) Verify: schema_version is current, the legacy `attributes` table is gone, the
            //    `annotations` table exists, and the sentinel row from step 1 is no longer
            //    discoverable (DropAll discards it; EnsureSchemaAsync does not migrate data).
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();

                var version = await conn.ExecuteScalarAsync<int?>(
                    "SELECT MAX(version) FROM schema_version;");
                version.Should().Be(Schema.Version);

                var hasLegacyAttributes = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='attributes';");
                hasLegacyAttributes.Should().Be(0, "legacy attributes table is dropped on the v10 -> current rebuild");

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
            DeleteTempDirectory(tmp);
        }
    }

    [Fact]
    public async Task EnsureSchema_preservesData_whenSchemaVersionIsCurrent()
    {
        var tmp = CreateTempDirectory("current-idempotent");
        var dbPath = Path.Join(tmp, "graph.db");
        var contentHash = new byte[] { 9, 8, 7, 6 };
        const string sourcePath = "C:/repo/src/KeepIndexed.cs";
        try
        {
            await using (var store = new SqliteGraphStore(dbPath))
            {
                await store.EnsureSchemaAsync();
                await store.UpsertFileAsync(sourcePath, contentHash, DateTimeOffset.UtcNow);

                await store.EnsureSchemaAsync();

                (await store.GetFileContentHashAsync(sourcePath))
                    .Should().BeEquivalentTo(contentHash);
                (await store.GetStatsAsync()).FileCount.Should().Be(1);
            }

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            (await conn.ExecuteScalarAsync<int?>("SELECT MAX(version) FROM schema_version;"))
                .Should().Be(Schema.Version);
        }
        finally
        {
            DeleteTempDirectory(tmp);
        }
    }

    private static string CreateTempDirectory(string scenario)
    {
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless.
        var tmp = Path.Join(
            Path.GetTempPath(),
            $"sourcegraph-schemarebuild-{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    private static void DeleteTempDirectory(string tmp)
    {
        try { Directory.Delete(tmp, recursive: true); }
        catch (IOException) { /* best-effort cleanup; another handle may still hold the file */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; readonly bit or ACL drift */ }
    }
}
