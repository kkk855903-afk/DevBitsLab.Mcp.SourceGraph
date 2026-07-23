using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit coverage for <see cref="Views"/>: the embedded <c>Views.sql</c> scaffolding and the
/// per-view per-scope templates substitute correctly into a working multi-statement DDL,
/// produce the column shape the agent-facing contract promises, and the convenience
/// projections (<c>v_symbols.is_public</c>, <c>v_symbols.is_type</c>, <c>v_references.kind</c>
/// integer-to-text mapping) compute the right values.
///
/// <para>Builds a tiny single-scope SQLite DB by hand (via <see cref="SqliteGraphStore"/>'s
/// public schema setup), seeds one row in each underlying table, then opens an in-memory
/// connection and ATTACHes the temp file as alias <c>"test"</c>, substitutes <c>Views.sql</c>
/// with one branch per template, and executes the resulting CREATE TEMP VIEW DDL. Each view
/// is then queried; column shapes and row values are asserted directly.</para>
/// </summary>
public sealed class ViewsTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _scopeDbPath = string.Empty;
    private string _metaDbPath = string.Empty;
    private SqliteConnection? _connection;

    public async Task InitializeAsync()
    {
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless.
        _tempDir = Path.Join(Path.GetTempPath(), "sourcegraph-views-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _scopeDbPath = Path.Join(_tempDir, "test.db");
        _metaDbPath = Path.Join(_tempDir, "_meta.db");

        // Apply the per-scope schema via the public store entry-point; it's the same path
        // the production indexer uses and avoids needing InternalsVisibleTo on Schema.cs.
        await using (var store = new SqliteGraphStore(_scopeDbPath))
        {
            await store.EnsureSchemaAsync();
        }

        // Seed one row in each table we surface via views. Direct SQL is the right call here
        // since we want exact control over the row shape we'll assert against — going through
        // the IGraphStore upserts would couple the view shape to the store's id-allocation
        // and overwrite policies.
        await using (var seed = new SqliteConnection($"Data Source={_scopeDbPath}"))
        {
            await seed.OpenAsync();
            await seed.ExecuteAsync("""
                INSERT INTO files(id, path, content_sha256, last_indexed_at, is_generated)
                VALUES (10, '/repo/src/Foo.cs', X'00112233', 1700000000000, 0);

                -- Symbol with accessibility=6 (Public) AND kind_name='class' so both convenience
                -- columns (is_public, is_type) compute to 1.
                INSERT INTO symbols(
                    id, canonical_key, name, fqn, kind_name, file_id,
                    start_line, start_col, end_line, end_col,
                    signature, container_id, modifiers, accessibility, xml_summary, test_framework)
                VALUES (
                    100, 'cs:Foo', 'Foo', 'Sample.Foo', 'class', 10,
                    7, 4, 12, 4,
                    'public class Foo', NULL, 'public', 6,
                    'A test class.', NULL);

                -- A second symbol so we can join to a non-self container_id later if we need to.
                INSERT INTO symbols(
                    id, canonical_key, name, fqn, kind_name, file_id,
                    start_line, start_col, end_line, end_col,
                    signature, container_id, modifiers, accessibility, xml_summary, test_framework)
                VALUES (
                    101, 'cs:Foo.Bar', 'Bar', 'Sample.Foo.Bar', 'method', 10,
                    9, 8, 11, 8,
                    'private void Bar()', 100, NULL, 1,
                    NULL, NULL);

                INSERT INTO edges(src, dst, kind_name, payload)
                VALUES (101, 100, 'uses-type', '{"call-site":"local"}');

                -- A textual reference site with kind=2 (Call) so we can verify the
                -- v_references integer-to-text mapping for ReferenceKind.Call -> 'call'.
                INSERT INTO refs(id, symbol_id, file_id, line, col, kind)
                VALUES (1, 100, 10, 9, 16, 2);

                -- One annotation row tagged on the public class. Carries a JSON args payload
                -- so the v_annotations.args_json column has a non-NULL value to assert against.
                INSERT INTO annotations(
                    id, symbol_id, name, full_name, flavor, args_json, attribute_symbol_id)
                VALUES (
                    1, 100, 'Obsolete', 'System.ObsoleteAttribute', 'csharp-attribute',
                    '["use Foo"]', NULL);

                -- Two diagnostic rows so we can exercise the severity_name CASE mapping
                -- against both a Warning (2) and an Error (3). symbol_id targets the public
                -- class so an INNER JOIN against v_symbols would surface them.
                INSERT INTO diagnostics(id, symbol_id, file_id, severity, code, message, line, col)
                VALUES (1, 100, 10, 2, 'CS0612', 'Type is obsolete', 7, 4);
                INSERT INTO diagnostics(id, symbol_id, file_id, severity, code, message, line, col)
                VALUES (2, 100, 10, 3, 'CS0029', 'Cannot implicitly convert', 8, 5);

                -- One symbol_history row so v_history has data to project. last_authored_at
                -- is Unix-millis matching the documented unit (mirrors v_files.last_indexed_at).
                INSERT INTO symbol_history(
                    symbol_id, last_commit_sha, last_author, last_authored_at,
                    line_count, blamed_content_sha)
                VALUES (
                    100, 'abc123def456', 'Jacques Bourque', 1700000005000,
                    6, X'00112233');
                """);
        }

        // Build a no-op meta DB just so we can ATTACH it for v_scopes coverage.
        await using (var registry = new SqliteScopeRegistry(_metaDbPath))
        {
            await registry.EnsureSchemaAsync();
            await registry.UpsertAsync(new ScopeRow(
                Id: "test",
                Name: "test",
                Root: _tempDir,
                ProjectSetJson: "{}",
                Isolated: false,
                LastIndexedAt: DateTimeOffset.FromUnixTimeMilliseconds(1700000000000),
                Status: "ok"));
        }

        // Open the test connection: in-memory main, ATTACH meta + the seeded scope DB.
        // Plain read-write ATTACH on purpose — this test exercises the Views layer in
        // isolation; read-only enforcement is covered by MultiScopeReadOnlyConnectionTests
        // (which uses the production helper's PRAGMA query_only path).
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        await using (var attach = _connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE @meta AS meta; ATTACH DATABASE @scope AS \"test\";";
            attach.Parameters.AddWithValue("@meta", _metaDbPath);
            attach.Parameters.AddWithValue("@scope", _scopeDbPath);
            await attach.ExecuteNonQueryAsync();
        }

        // Substitute the Views.Sql tokens with a single-scope UNION block per view template.
        var ddl = SubstituteSingleScope(Views.Sql, "test");
        await using (var ddlCmd = _connection.CreateCommand())
        {
            ddlCmd.CommandText = ddl;
            await ddlCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup; another handle may still hold the file */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; readonly bit drift */ }
    }

    [Fact]
    public void Views_Sql_and_PerScopeBlockTemplates_are_populated()
    {
        // Sanity-check the surface: the embedded resource loaded, and every per-scope template
        // is keyed off a known view name. A regression here means either the embedded resource
        // wasn't packed (Views.cs's static ctor would have thrown) or BuildTemplates dropped
        // a view.
        Views.Sql.Should().NotBeNullOrWhiteSpace("Views.sql is loaded as an embedded resource");
        Views.Sql.Should().Contain("{{SCOPE_UNION_BLOCK_v_symbols}}");
        Views.Sql.Should().Contain("{{SCOPE_UNION_BLOCK_v_files}}");
        Views.Sql.Should().Contain("{{SCOPE_UNION_BLOCK_v_edges}}");
        Views.Sql.Should().Contain("{{SCOPE_UNION_BLOCK_v_references}}");
        Views.Sql.Should().Contain("{{SCOPE_UNION_BLOCK_v_annotations}}");
        Views.Sql.Should().Contain("{{SCOPE_UNION_BLOCK_v_diagnostics}}");
        Views.Sql.Should().Contain("{{SCOPE_UNION_BLOCK_v_history}}");

        Views.PerScopeBlockTemplates.Keys.Should().BeEquivalentTo(
            new[]
            {
                "v_symbols", "v_files", "v_edges", "v_references",
                "v_annotations", "v_diagnostics", "v_history",
            });

        Views.SchemaVersion.Should().Be(2,
            "the extended-views change bumps the version to signal the v_annotations / "
            + "v_diagnostics / v_history additions to cache-aware clients");
    }

    [Fact]
    public async Task v_symbols_returns_seeded_row_with_convenience_columns()
    {
        // Verify the column shape: SELECT * LIMIT 0 against the view should expose the columns
        // declared in the Views.All descriptor. We snapshot the descriptor's column names and
        // compare.
        var descriptor = Views.All.First(v => v.Name == "v_symbols");

        var rows = (await _connection!.QueryAsync(
            "SELECT * FROM v_symbols ORDER BY id;")).ToList();
        rows.Should().HaveCount(2, "two symbol rows were seeded");

        // First row: the public class.
        var firstRow = (IDictionary<string, object?>)rows[0]!;
        firstRow.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name),
            "view column names must match the curated descriptor");
        firstRow["scope"].Should().Be("test");
        firstRow["id"].Should().Be(100L);
        firstRow["name"].Should().Be("Foo");
        firstRow["fqn"].Should().Be("Sample.Foo");
        firstRow["kind"].Should().Be("class");
        Convert.ToInt32(firstRow["accessibility"], CultureInfo.InvariantCulture)
            .Should().Be(6, "the seeded symbol is Public (Roslyn enum value 6)");
        Convert.ToInt32(firstRow["is_public"], CultureInfo.InvariantCulture).Should().Be(1,
            "is_public should compute to 1 when accessibility = 6");
        Convert.ToInt32(firstRow["is_type"], CultureInfo.InvariantCulture).Should().Be(1,
            "is_type should compute to 1 for kind='class'");
        firstRow["modifiers"].Should().Be("public");
        firstRow["xml_summary"].Should().Be("A test class.");
        firstRow["container_id"].Should().BeNull("the seeded class is top-level");
        firstRow["file_id"].Should().Be(10L);
        Convert.ToInt32(firstRow["start_line"], CultureInfo.InvariantCulture).Should().Be(7);
        Convert.ToInt32(firstRow["start_column"], CultureInfo.InvariantCulture).Should().Be(4,
            "start_column is the renamed projection of the underlying start_col column");

        // Second row: the private method on Foo. Accessibility=1 (Private), kind=method.
        // is_public=0, is_type=0.
        var secondRow = (IDictionary<string, object?>)rows[1]!;
        secondRow["id"].Should().Be(101L);
        Convert.ToInt32(secondRow["is_public"], CultureInfo.InvariantCulture).Should().Be(0,
            "Private symbols compute is_public=0");
        Convert.ToInt32(secondRow["is_type"], CultureInfo.InvariantCulture).Should().Be(0,
            "Methods compute is_type=0");
        secondRow["container_id"].Should().Be(100L,
            "the method's container_id should point at the parent class");
    }

    [Fact]
    public async Task v_files_returns_seeded_row()
    {
        var descriptor = Views.All.First(v => v.Name == "v_files");

        var rows = (await _connection!.QueryAsync("SELECT * FROM v_files;")).ToList();
        rows.Should().HaveCount(1);

        var row = (IDictionary<string, object?>)rows[0]!;
        row.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name));
        row["scope"].Should().Be("test");
        row["id"].Should().Be(10L);
        row["path"].Should().Be("/repo/src/Foo.cs");
        row["sha"].Should().BeOfType<byte[]>("BLOB columns surface as byte[] in Microsoft.Data.Sqlite");
        ((byte[])row["sha"]!).Should().Equal(0x00, 0x11, 0x22, 0x33);
        Convert.ToInt64(row["last_indexed_at"], CultureInfo.InvariantCulture).Should().Be(1700000000000L);
        Convert.ToInt32(row["is_generated"], CultureInfo.InvariantCulture).Should().Be(0);
    }

    [Fact]
    public async Task v_edges_returns_seeded_row()
    {
        var descriptor = Views.All.First(v => v.Name == "v_edges");

        var rows = (await _connection!.QueryAsync("SELECT * FROM v_edges;")).ToList();
        rows.Should().HaveCount(1);

        var row = (IDictionary<string, object?>)rows[0]!;
        row.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name));
        row["scope"].Should().Be("test");
        row["src"].Should().Be(101L);
        row["dst"].Should().Be(100L);
        row["kind"].Should().Be("uses-type");
        row["payload"].Should().Be("{\"call-site\":\"local\"}");
    }

    [Fact]
    public async Task v_references_maps_kind_integer_to_text()
    {
        var descriptor = Views.All.First(v => v.Name == "v_references");

        var rows = (await _connection!.QueryAsync("SELECT * FROM v_references;")).ToList();
        rows.Should().HaveCount(1);

        var row = (IDictionary<string, object?>)rows[0]!;
        row.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name));
        row["scope"].Should().Be("test");
        row["symbol_id"].Should().Be(100L);
        row["file_id"].Should().Be(10L);
        Convert.ToInt32(row["line"], CultureInfo.InvariantCulture).Should().Be(9);
        Convert.ToInt32(row["column_number"], CultureInfo.InvariantCulture).Should().Be(16,
            "column_number is the renamed projection of the underlying refs.col column "
            + "(SQL reserves the bare identifier COLUMN)");
        row["kind"].Should().Be("call",
            "refs.kind=2 (ReferenceKind.Call) should project to 'call' through the CASE map");
    }

    [Fact]
    public async Task v_scopes_reads_meta_db_registry_row()
    {
        var descriptor = Views.All.First(v => v.Name == "v_scopes");

        var rows = (await _connection!.QueryAsync("SELECT * FROM v_scopes;")).ToList();
        rows.Should().HaveCount(1, "one scope was registered in the meta DB");

        var row = (IDictionary<string, object?>)rows[0]!;
        row.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name));
        row["scope"].Should().Be("test", "the scope id surfaces as the scope column");
        row["name"].Should().Be("test");
        row["root"].Should().Be(_tempDir);
        Convert.ToInt32(row["isolated"], CultureInfo.InvariantCulture).Should().Be(0);
        row["status"].Should().Be("ok");
        Convert.ToInt64(row["last_indexed_at"], CultureInfo.InvariantCulture).Should().Be(1700000000000L);
    }

    [Fact]
    public async Task Substituted_v_annotations_returnsExpectedColumns()
    {
        var descriptor = Views.All.First(v => v.Name == "v_annotations");

        var rows = (await _connection!.QueryAsync(
            "SELECT * FROM v_annotations ORDER BY id;")).ToList();
        rows.Should().HaveCount(1, "one annotation row was seeded");

        var row = (IDictionary<string, object?>)rows[0]!;
        row.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name),
            "view column names must match the curated descriptor");
        row["scope"].Should().Be("test");
        row["id"].Should().Be(1L);
        row["symbol_id"].Should().Be(100L,
            "the annotation row was tagged on the public class (id=100)");
        row["name"].Should().Be("Obsolete");
        row["full_name"].Should().Be("System.ObsoleteAttribute");
        row["flavor"].Should().Be("csharp-attribute",
            "csharp-attribute is the documented flavor for .NET attributes");
        row["args_json"].Should().Be("[\"use Foo\"]",
            "args_json projects raw TEXT (the seeded JSON literal)");
        row["attribute_symbol_id"].Should().BeNull(
            "the seeded annotation's defining type isn't itself indexed");
    }

    [Fact]
    public async Task Substituted_v_diagnostics_mapsSeverityToText()
    {
        var descriptor = Views.All.First(v => v.Name == "v_diagnostics");

        var rows = (await _connection!.QueryAsync(
            "SELECT * FROM v_diagnostics ORDER BY id;")).ToList();
        rows.Should().HaveCount(2, "two diagnostic rows were seeded (warning + error)");

        // First row: severity=2 → 'warning'
        var first = (IDictionary<string, object?>)rows[0]!;
        first.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name),
            "view column names must match the curated descriptor");
        first["scope"].Should().Be("test");
        first["id"].Should().Be(1L);
        first["symbol_id"].Should().Be(100L);
        first["file_id"].Should().Be(10L);
        Convert.ToInt32(first["severity"], CultureInfo.InvariantCulture).Should().Be(2,
            "raw severity stays available for ordering / range queries");
        first["severity_name"].Should().Be("warning",
            "severity=2 maps to 'warning' via the documented CASE expression");
        first["code"].Should().Be("CS0612");
        first["message"].Should().Be("Type is obsolete");
        Convert.ToInt32(first["line"], CultureInfo.InvariantCulture).Should().Be(7);
        Convert.ToInt32(first["column_number"], CultureInfo.InvariantCulture).Should().Be(4,
            "column_number is the renamed projection of the underlying diagnostics.col");

        // Second row: severity=3 → 'error'
        var second = (IDictionary<string, object?>)rows[1]!;
        Convert.ToInt32(second["severity"], CultureInfo.InvariantCulture).Should().Be(3);
        second["severity_name"].Should().Be("error",
            "severity=3 maps to 'error' via the documented CASE expression");
        second["code"].Should().Be("CS0029");
    }

    [Fact]
    public async Task Substituted_v_history_returnsExpectedColumns()
    {
        var descriptor = Views.All.First(v => v.Name == "v_history");

        var rows = (await _connection!.QueryAsync("SELECT * FROM v_history;")).ToList();
        rows.Should().HaveCount(1, "one symbol_history row was seeded");

        var row = (IDictionary<string, object?>)rows[0]!;
        row.Keys.Should().BeEquivalentTo(descriptor.Columns.Select(c => c.Name),
            "view column names must match the curated descriptor");
        row["scope"].Should().Be("test");
        row["symbol_id"].Should().Be(100L);
        row["last_commit_sha"].Should().Be("abc123def456");
        row["last_author"].Should().Be("Jacques Bourque");
        Convert.ToInt64(row["last_authored_at"], CultureInfo.InvariantCulture).Should().Be(1700000005000L,
            "last_authored_at is Unix-millis (matches v_files.last_indexed_at units)");
        Convert.ToInt32(row["line_count"], CultureInfo.InvariantCulture).Should().Be(6);
        row["blamed_content_sha"].Should().BeOfType<byte[]>(
            "BLOB columns surface as byte[] in Microsoft.Data.Sqlite");
        ((byte[])row["blamed_content_sha"]!).Should().Equal(0x00, 0x11, 0x22, 0x33);
    }

    /// <summary>
    /// Replace each <c>{{SCOPE_UNION_BLOCK_&lt;view&gt;}}</c> token in <see cref="Views.Sql"/>
    /// with a single per-scope branch (no UNION ALL needed for one scope). Mirrors the
    /// substitution that <see cref="MultiScopeReadOnlyConnection"/> does, but kept inline here
    /// so this test exercises the embedded SQL and templates directly without taking a
    /// dependency on the helper under test.
    ///
    /// <para>Strips line comments before substitution — <c>Views.sql</c>'s leading
    /// documentation contains the literal token name (<c>-- Tokens like
    /// {{SCOPE_UNION_BLOCK_v_symbols}} are replaced …</c>); without stripping, the
    /// substitution would drop a multi-line SELECT into the middle of that comment and
    /// corrupt the resulting SQL.</para>
    /// </summary>
    private static string SubstituteSingleScope(string sql, string scopeId)
    {
        var commentless = new StringBuilder(sql.Length);
        var first = true;
        foreach (var line in sql.Split('\n'))
        {
            if (line.AsSpan().TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            if (!first) commentless.Append('\n');
            commentless.Append(line);
            first = false;
        }
        var result = commentless.ToString();
        foreach (var (viewName, template) in Views.PerScopeBlockTemplates)
        {
            var token = "{{SCOPE_UNION_BLOCK_" + viewName + "}}";
            var block = template.Replace("{SCOPE_ID}", scopeId, StringComparison.Ordinal);
            result = result.Replace(token, block, StringComparison.Ordinal);
        }
        return result;
    }

}
