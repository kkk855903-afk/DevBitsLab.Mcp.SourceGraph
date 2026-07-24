using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ExecutionTraceStorageTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _databasePath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-execution-trace-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Join(_root, "graph.db");
        _store = new SqliteGraphStore(_databasePath);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task CanonicalKeyLookup_isExact_andReturnsTheCompleteSymbol()
    {
        var symbol = await SeedSymbolAsync(
            "Lookup.cs",
            "Run",
            "Graph.Lookup.Run",
            "csharp:M:Graph.Lookup.Run");

        var hit = await _store!.GetSymbolByCanonicalKeyAsync(
            "csharp:M:Graph.Lookup.Run");

        hit.Should().NotBeNull();
        hit!.Id.Should().Be(symbol.SymbolId);
        hit.Name.Should().Be("Run");
        hit.Fqn.Should().Be("Graph.Lookup.Run");
        hit.FilePath.Should().Be(symbol.FilePath);
        hit.CanonicalKey.Should().Be("csharp:M:Graph.Lookup.Run");
        hit.PayloadJson.Should().BeNull();

        (await _store.GetSymbolByCanonicalKeyAsync(
                "csharp:M:Graph.Lookup.run"))
            .Should().BeNull("canonical-key identity is ordinal and case-sensitive");
    }

    [Fact]
    public async Task MultiKindOutboundTraversal_filtersBeforeLimit_andRequiresEvidence()
    {
        var source = await SeedSymbolAsync(
            "Source.cs",
            "Source",
            "Graph.Source",
            "csharp:M:Graph.Source");
        var unrelated = await SeedSymbolAsync(
            "00-Unrelated.cs",
            "Unrelated",
            "Graph.Unrelated",
            "csharp:M:Graph.Unrelated");
        var unsupported = await SeedSymbolAsync(
            "01-Unsupported.cs",
            "Unsupported",
            "Graph.Unsupported",
            "csharp:M:Graph.Unsupported");
        var call = await SeedSymbolAsync(
            "20-Call.cs",
            "Call",
            "Graph.Call",
            "csharp:M:Graph.Call");
        var grpc = await SeedSymbolAsync(
            "10-Grpc.cs",
            "Grpc",
            "Graph.Grpc",
            "csharp:M:Graph.Grpc");

        await _store!.BulkInsertEdgesAsync(
        [
            AuditableEdge(source, unrelated, "aaa-noise", 2),
            AuditableEdge(source, call, "calls", 3),
            AuditableEdge(source, grpc, "grpc-calls", 4),
        ]);
        await InsertUnsupportedLogicalEdgeAsync(
            source.SymbolId,
            unsupported.SymbolId,
            "calls");

        var limited = await _store.ListAuditableOutboundEdgesByKindsAsync(
            source.SymbolId,
            ["grpc-calls", "calls"],
            limit: 1);

        limited.Should().ContainSingle();
        limited[0].Relation.Should().Be("calls");
        limited[0].Symbol.CanonicalKey.Should().Be("csharp:M:Graph.Call",
            "the excluded relation and unsupported logical edge must not consume the limit");

        var all = await _store.ListAuditableOutboundEdgesByKindsAsync(
            source.SymbolId,
            ["grpc-calls", "calls", "calls"],
            limit: 10);

        all.Select(row => (row.Relation, row.Symbol.CanonicalKey))
            .Should().Equal(
                ("calls", "csharp:M:Graph.Call"),
                ("grpc-calls", "csharp:M:Graph.Grpc"));
    }

    [Fact]
    public async Task ExecutionTraceQueries_rejectMalformedOrUnboundedParameters()
    {
        var nullKinds = () => _store!.ListAuditableOutboundEdgesByKindsAsync(
            1,
            null!);
        var noKinds = () => _store!.ListAuditableOutboundEdgesByKindsAsync(
            1,
            Array.Empty<string>());
        var tooManyKinds = () => _store!.ListAuditableOutboundEdgesByKindsAsync(
            1,
            Enumerable.Range(0, 33).Select(index => $"kind-{index}").ToArray());
        var malformedKind = () => _store!.ListAuditableOutboundEdgesByKindsAsync(
            1,
            ["Calls"]);
        var zeroLimit = () => _store!.ListAuditableOutboundEdgesByKindsAsync(
            1,
            ["calls"],
            limit: 0);
        var excessiveLimit = () => _store!.ListAuditableOutboundEdgesByKindsAsync(
            1,
            ["calls"],
            limit: 1025);
        var malformedCanonicalKey = () =>
            _store!.GetSymbolByCanonicalKeyAsync("not-a-canonical-key");

        await nullKinds.Should().ThrowAsync<ArgumentNullException>();
        await noKinds.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooManyKinds.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await malformedKind.Should().ThrowAsync<ArgumentException>();
        await zeroLimit.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await excessiveLimit.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await malformedCanonicalKey.Should().ThrowAsync<ArgumentException>();
        (await _store!.ListAuditableOutboundEdgesByKindsAsync(
                1,
                ["calls"],
                limit: 1024))
            .Should().BeEmpty();
    }

    private async Task<SeededSymbol> SeedSymbolAsync(
        string relativePath,
        string name,
        string fqn,
        string canonicalKey)
    {
        var path = Path.Join(_root, relativePath);
        var fileId = await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
        var symbolId = await _store.UpsertSymbolAsync(
            canonicalKey,
            new Symbol(
                0,
                name,
                fqn,
                "method",
                fileId,
                1,
                1,
                5,
                1,
                $"void {name}()",
                null));
        return new SeededSymbol(symbolId, fileId, path);
    }

    private static Edge AuditableEdge(
        SeededSymbol source,
        SeededSymbol target,
        string relation,
        int line) =>
        new(source.SymbolId, target.SymbolId, relation)
        {
            Evidence = new Evidence(
                source.FileId,
                new SourceLocation(source.FilePath, line, 1, line, 8),
                EvidenceConfidence.Exact,
                "execution-trace-storage-test"),
        };

    private async Task InsertUnsupportedLogicalEdgeAsync(
        long sourceSymbolId,
        long targetSymbolId,
        string relation)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO edges(src, dst, kind_name) VALUES ($src, $dst, $kind);";
        command.Parameters.AddWithValue("$src", sourceSymbolId);
        command.Parameters.AddWithValue("$dst", targetSymbolId);
        command.Parameters.AddWithValue("$kind", relation);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SeededSymbol(
        long SymbolId,
        long FileId,
        string FilePath);
}
