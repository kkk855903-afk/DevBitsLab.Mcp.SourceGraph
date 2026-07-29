using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ReferenceDeduplicationTests
{
    [Fact]
    public async Task Repeated_semantic_occurrence_is_stored_and_returned_once()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-reference-dedupe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Join(root, "graph.db");
        try
        {
            await using var store = new SqliteGraphStore(dbPath);
            await store.EnsureSchemaAsync();
            var fileId = await store.UpsertFileAsync(
                Path.Join(root, "Caller.cs"),
                [1, 2, 3],
                DateTimeOffset.UtcNow);
            var symbolId = await store.UpsertSymbolAsync(
                "csharp:M:Sample.Target.Run",
                new Symbol(
                    Id: 0,
                    Name: "Run",
                    Fqn: "void Sample.Target.Run()",
                    Kind: "method",
                    FileId: fileId,
                    StartLine: 1,
                    StartCol: 1,
                    EndLine: 1,
                    EndCol: 10,
                    Signature: "void Run()",
                    ContainerId: null));
            var occurrence = new SymbolReference(
                0,
                symbolId,
                fileId,
                12,
                9,
                ReferenceKind.Call);

            await store.BulkInsertReferencesAsync([occurrence, occurrence]);
            await store.BulkInsertReferencesAsync([occurrence]);

            (await store.GetStatsAsync()).ReferenceCount.Should().Be(1);
            (await store.FindReferencesAsync(symbolId)).Should().ContainSingle();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
