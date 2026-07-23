using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the storage health helpers added by add-scope-health-surface:
/// <see cref="IGraphStore.RowCountsAsync"/> and <see cref="IGraphStore.IntegrityCheckAsync"/>.
/// Both back the <c>verify_scope</c> tool's structured snapshot.
/// </summary>
public sealed class StorageHealthTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-storage-health-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Join(tmp, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RowCounts_emptyDb_returnsZeros()
    {
        var counts = await _store!.RowCountsAsync();
        counts.Should().Be(new RowCountsRow(0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task RowCounts_populated_returnsExactCounts()
    {
        // Insert one file + one symbol so we get non-zero counts that the SUM has to roll up.
        var fileId = await _store!.UpsertFileAsync("/tmp/A.cs", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow);
        await _store.UpsertSymbolAsync("S:csharp:K1", new Symbol(
            Id: 0, Name: "Foo", Fqn: "X.Foo", Kind: "class", FileId: fileId,
            StartLine: 1, StartCol: 1, EndLine: 5, EndCol: 1, Signature: "class Foo",
            ContainerId: null,
            Modifiers: null, Accessibility: (int)Accessibility.Public, XmlSummary: null));

        var counts = await _store.RowCountsAsync();
        counts.Symbols.Should().Be(1);
        counts.Files.Should().Be(1);
        counts.Refs.Should().Be(0);
        counts.Edges.Should().Be(0);
        counts.Annotations.Should().Be(0);
        counts.Diagnostics.Should().Be(0);
    }

    [Fact]
    public async Task IntegrityCheck_cleanDb_returnsOk()
    {
        var result = await _store!.IntegrityCheckAsync();
        result.Should().Be("ok");
    }

    [Fact]
    public async Task IntegrityCheck_corruptDb_returnsNonOk()
    {
        // Populate the DB with at least one symbol so there are real btree pages we can corrupt
        // beyond the header / master schema (page 1). Then close cleanly and stomp a deeper page
        // so the file still opens (header intact) but PRAGMA integrity_check trips on the rotted
        // page.
        var fileId = await _store!.UpsertFileAsync("/tmp/A.cs", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow);
        for (var i = 0; i < 50; i++)
        {
            await _store.UpsertSymbolAsync($"S:csharp:K{i}", new Symbol(
                Id: 0, Name: $"Foo{i}", Fqn: $"X.Foo{i}", Kind: "class", FileId: fileId,
                StartLine: i + 1, StartCol: 1, EndLine: i + 5, EndCol: 1, Signature: $"class Foo{i}",
                ContainerId: null,
                Modifiers: null, Accessibility: (int)Accessibility.Public, XmlSummary: null));
        }

        await _store.DisposeAsync();
        _store = null;
        SqliteConnection.ClearAllPools();

        // Stomp 800 bytes starting at offset 8192 — well past page 1, in territory used for user
        // table btrees. Header + WAL state survive so the connection opens; integrity_check
        // walks every page checksum and reports the broken one.
        var rnd = new Random(42);
        using (var stream = new FileStream(_dbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(8192, SeekOrigin.Begin);
            var bytes = new byte[800];
            rnd.NextBytes(bytes);
            await stream.WriteAsync(bytes);
        }

        _store = new SqliteGraphStore(_dbPath);
        var result = await _store.IntegrityCheckAsync();
        result.Should().NotBe("ok");
    }
}
