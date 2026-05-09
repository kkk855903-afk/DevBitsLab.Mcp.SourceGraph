using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the <c>edges.payload</c> column added by open-language-contract: a single
/// <see cref="Edge"/> emitted with a <see cref="Edge.Metadata"/> dictionary should land as a
/// JSON object in the column, and a follow-up SQL read should recover the same key/value pairs.
///
/// <para>The JSON shape is opaque to the contract — the test only asserts that the keys and
/// values round-trip; the exact serialiser settings are an implementation detail.</para>
/// </summary>
public sealed class EdgeMetadataRoundTripTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless.
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-edge-meta-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Join(tmp, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch (IOException) { /* best-effort cleanup; another handle may still hold the file */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; readonly bit or ACL drift */ }
    }

    private async Task<long> SeedSymbolAsync(string canonicalKey, string name)
    {
        // Each symbol must own a file row to satisfy the symbols.file_id NOT NULL constraint.
        var fileId = await _store!.UpsertFileAsync(
            path: $"/virtual/{canonicalKey.Replace(':', '_').Replace('/', '_')}.cs",
            contentSha256: new byte[32],
            indexedAt: DateTimeOffset.UtcNow,
            isGenerated: false);

        return await _store.UpsertSymbolAsync(canonicalKey, new Symbol(
            Id: 0,
            Name: name,
            Fqn: $"Sample.{name}",
            Kind: SymbolKinds.Method,
            FileId: fileId,
            StartLine: 1, StartCol: 1, EndLine: 5, EndCol: 1,
            Signature: $"void {name}()",
            ContainerId: null,
            Modifiers: null,
            Accessibility: 6,
            XmlSummary: null,
            TestFramework: null));
    }

    [Fact]
    public async Task BulkInsertEdges_persistsMetadataAsJsonPayload()
    {
        var srcId = await SeedSymbolAsync("csharp:M:Sample.Source", "Source");
        var dstId = await SeedSymbolAsync("csharp:M:Sample.Target", "Target");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["path"] = "User.Name",
            ["mode"] = "two-way",
        };

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(srcId, dstId, "binds-path", metadata),
        });

        // Read back the raw payload column via Dapper to assert the JSON shape independently of
        // any read-side helper. The contract guarantees a JSON object; deserialising into a
        // Dictionary recovers the original key/value pairs.
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var payload = await conn.ExecuteScalarAsync<string?>(
            "SELECT payload FROM edges WHERE src = @s AND dst = @d AND kind_name = @k;",
            new { s = srcId, d = dstId, k = "binds-path" });

        payload.Should().NotBeNull();
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, string>>(payload!);
        roundTripped.Should().NotBeNull();
        roundTripped!.Should().ContainKey("path");
        roundTripped["path"].Should().Be("User.Name");
        roundTripped.Should().ContainKey("mode");
        roundTripped["mode"].Should().Be("two-way");
    }

    [Fact]
    public async Task BulkInsertEdges_persistsNullPayload_whenMetadataIsNull()
    {
        var srcId = await SeedSymbolAsync("csharp:M:Sample.NoMeta.Source", "MetaSource");
        var dstId = await SeedSymbolAsync("csharp:M:Sample.NoMeta.Target", "MetaTarget");

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(srcId, dstId, EdgeKinds.Calls, Metadata: null),
        });

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var payload = await conn.ExecuteScalarAsync<string?>(
            "SELECT payload FROM edges WHERE src = @s AND dst = @d AND kind_name = @k;",
            new { s = srcId, d = dstId, k = "calls" });

        // The contract requires a NULL payload (not "null", not "{}") when no metadata is
        // supplied — the column stays opaque and storage queries can SQL-test for NULL.
        payload.Should().BeNull();
    }

    [Fact]
    public async Task BulkInsertEdges_persistsNullPayload_whenMetadataIsEmpty()
    {
        var srcId = await SeedSymbolAsync("csharp:M:Sample.EmptyMeta.Source", "EmptySource");
        var dstId = await SeedSymbolAsync("csharp:M:Sample.EmptyMeta.Target", "EmptyTarget");

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(srcId, dstId, EdgeKinds.Calls, Metadata: new Dictionary<string, string>()),
        });

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var payload = await conn.ExecuteScalarAsync<string?>(
            "SELECT payload FROM edges WHERE src = @s AND dst = @d AND kind_name = @k;",
            new { s = srcId, d = dstId, k = "calls" });

        // Empty dictionary is treated as "no metadata"; the implementation skips the JSON
        // serialise step and writes NULL. Tools that introspect payload should see a clean NULL.
        payload.Should().BeNull();
    }
}
