using System.Security.Cryptography;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class SemanticPipelineFingerprintTests
{
    [Fact]
    public async Task Changed_fingerprint_transactionally_invalidates_all_derived_rows()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            $"sourcegraph-pipeline-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGraphStore(dbPath);
            await store.EnsureSchemaAsync();
            (await store.EnsureSemanticPipelineAsync("pipeline-v1")).Should().BeTrue();

            var fileId = await store.UpsertFileAsync(
                "Sample.cs",
                SHA256.HashData("sample"u8),
                DateTimeOffset.UtcNow);
            var symbolId = await store.UpsertSymbolAsync(
                "csharp:T:Sample",
                new Symbol(
                    Id: 0,
                    Name: "Sample",
                    Fqn: "Sample",
                    Kind: "class",
                    FileId: fileId,
                    StartLine: 0,
                    StartCol: 0,
                    EndLine: 0,
                    EndCol: 6,
                    Signature: null,
                    ContainerId: null,
                    Modifiers: null,
                    Accessibility: 0,
                    XmlSummary: null,
                    TestFramework: null));
            await store.UpsertDiagnosticsForFileAsync(
                fileId,
                [
                    new DiagnosticRecord(
                        symbolId,
                        fileId,
                        3,
                        "CS0001",
                        "stale",
                        0,
                        0),
                ]);

            (await store.EnsureSemanticPipelineAsync("pipeline-v2")).Should().BeTrue();
            var counts = await store.RowCountsAsync();
            counts.Files.Should().Be(0);
            counts.Symbols.Should().Be(0);
            counts.Diagnostics.Should().Be(0);
            (await store.EnsureSemanticPipelineAsync("pipeline-v2")).Should().BeFalse();

            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
            await connection.OpenAsync();
            var persisted = await connection.ExecuteScalarAsync<string>(
                """
                SELECT value
                FROM index_metadata
                WHERE key = 'semantic-pipeline-fingerprint';
                """);
            persisted.Should().Be("pipeline-v2");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
