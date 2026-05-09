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

    [Fact]
    public async Task ListCallers_surfacesPayloadJson_fromOriginatingEdge()
    {
        // Read-side coverage for harden-sdk-pre-xaml §7: the SQL projection on ListCallersAsync
        // includes edges.payload and SymbolHit.PayloadJson surfaces it verbatim, so the markdown
        // renderer can decode it into a per-row sub-line. The write side is already covered by the
        // tests above; this asserts the bytes round-trip through a real graph query.
        var srcId = await SeedSymbolAsync("csharp:M:Sample.ReadSide.Caller", "Caller");
        var dstId = await SeedSymbolAsync("csharp:M:Sample.ReadSide.Target", "Target");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["path"] = "User.Name",
            ["mode"] = "two-way",
        };
        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(srcId, dstId, "binds-path", metadata),
        });

        var callers = await _store.ListCallersAsync(dstId, limit: 50, edgeKind: "binds-path");

        callers.Should().HaveCount(1);
        var caller = callers[0];
        caller.Id.Should().Be(srcId);
        caller.PayloadJson.Should().NotBeNull(
            "the edge had non-empty Metadata — the read path is required to surface the JSON column");
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, string>>(caller.PayloadJson!);
        roundTripped.Should().NotBeNull();
        roundTripped!["path"].Should().Be("User.Name");
        roundTripped["mode"].Should().Be("two-way");
    }

    [Fact]
    public async Task ListCallees_surfacesPayloadJson_fromOriginatingEdge()
    {
        // Mirror of the inbound test for the outbound path (list_callees uses the dst-side join);
        // §7 plumbs payload through both edge-walking SQL strings.
        var srcId = await SeedSymbolAsync("csharp:M:Sample.Outbound.Source", "OutSource");
        var dstId = await SeedSymbolAsync("csharp:M:Sample.Outbound.Target", "OutTarget");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["event"] = "Click",
            ["handler"] = "OnClick",
        };
        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(srcId, dstId, "wires-event", metadata),
        });

        var callees = await _store.ListCalleesAsync(srcId, limit: 50, edgeKind: "wires-event");

        callees.Should().HaveCount(1);
        callees[0].PayloadJson.Should().NotBeNull();
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, string>>(callees[0].PayloadJson!);
        roundTripped.Should().NotBeNull();
        roundTripped!["event"].Should().Be("Click");
        roundTripped["handler"].Should().Be("OnClick");
    }

    [Fact]
    public async Task ListCallers_returnsNullPayloadJson_whenEdgeHasNoMetadata()
    {
        // Edges without metadata land NULL in the payload column (see the *NullPayload* tests
        // above). The read path must surface that as null on SymbolHit, not "" or "{}", so the
        // rendering helper can short-circuit and skip the sub-line entirely.
        var srcId = await SeedSymbolAsync("csharp:M:Sample.NoPayload.Caller", "NPCaller");
        var dstId = await SeedSymbolAsync("csharp:M:Sample.NoPayload.Target", "NPTarget");

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(srcId, dstId, EdgeKinds.Calls, Metadata: null),
        });

        var callers = await _store.ListCallersAsync(dstId, limit: 50, edgeKind: EdgeKinds.Calls);

        callers.Should().HaveCount(1);
        callers[0].PayloadJson.Should().BeNull();
    }
}
