using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class GrpcContractBaselineStorageTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-grpc-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteGraphStore(Path.Join(_root, "graph.db"));
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task First_complete_observation_wins_and_new_keys_join_atomically()
    {
        var first = Fact(
            "proto:F:fixture.v1.Request.value",
            """{"number":1}""",
            "contracts.proto",
            line: 3);
        (await _store!.EnsureGrpcContractBaselinesAsync([first]))
            .Should().Be(1);

        var changed = first with
        {
            ContractJson = """{"number":9}""",
            StartLine = 30,
            EndLine = 30,
        };
        var rpc = Fact(
            "proto:R:fixture.v1.Api.Run",
            """{"server_streaming":false}""",
            "contracts.proto",
            line: 8);
        (await _store.EnsureGrpcContractBaselinesAsync([changed, rpc]))
            .Should().Be(1);

        var rows = await _store.ListGrpcContractBaselinesAsync(10);
        rows.Select(row => row.SymbolCanonicalKey).Should().Equal(
            first.SymbolCanonicalKey,
            rpc.SymbolCanonicalKey);
        rows[0].ContractJson.Should().Be(first.ContractJson);
        rows[0].StartLine.Should().Be(3);
        rows.Should().OnlyContain(row => row.ObservedAtUnixMs > 0);
    }

    [Fact]
    public async Task Invalid_candidate_set_does_not_insert_any_row()
    {
        var duplicate = Fact(
            "proto:F:fixture.v1.Request.value",
            """{"number":1}""",
            "contracts.proto",
            line: 3);
        var action = () => _store!.EnsureGrpcContractBaselinesAsync(
        [
            duplicate,
            duplicate with { ContractJson = """{"number":2}""" },
        ]);

        await action.Should().ThrowAsync<ArgumentException>();
        (await _store!.ListGrpcContractBaselinesAsync(10))
            .Should().BeEmpty();
    }

    private static GrpcContractBaselineFact Fact(
        string key,
        string payload,
        string path,
        int line) =>
        new(
            key,
            payload,
            path,
            line,
            1,
            line,
            20);
}
