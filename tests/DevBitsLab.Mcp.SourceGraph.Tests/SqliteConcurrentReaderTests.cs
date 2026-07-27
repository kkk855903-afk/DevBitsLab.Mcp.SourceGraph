using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class SqliteConcurrentReaderTests
{
    [Fact]
    public async Task IndependentReaders_supportConcurrentSymbolAndEdgeQueries()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-readers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new SqliteGraphStore(Path.Join(root, "graph.db"));
            await store.EnsureSchemaAsync();
            var fileId = await store.UpsertFileAsync(
                Path.Join(root, "Source.cs"),
                new byte[32],
                DateTimeOffset.UtcNow);
            var sourceId = await store.UpsertSymbolAsync(
                "csharp:M:Fixture.Source",
                new Symbol(
                    Id: 0,
                    Name: "Source",
                    Fqn: "Fixture.Source",
                    Kind: "method",
                    FileId: fileId,
                    StartLine: 1,
                    StartCol: 1,
                    EndLine: 1,
                    EndCol: 7,
                    Signature: "void Source()",
                    ContainerId: null,
                    Modifiers: null,
                    Accessibility: 6,
                    XmlSummary: null));
            var targetId = await store.UpsertSymbolAsync(
                "csharp:M:Fixture.Target",
                new Symbol(
                    Id: 0,
                    Name: "Target",
                    Fqn: "Fixture.Target",
                    Kind: "method",
                    FileId: fileId,
                    StartLine: 2,
                    StartCol: 1,
                    EndLine: 2,
                    EndCol: 7,
                    Signature: "void Target()",
                    ContainerId: null,
                    Modifiers: null,
                    Accessibility: 6,
                    XmlSummary: null));
            await store.BulkInsertEdgesAsync([
                new Edge(
                    sourceId,
                    targetId,
                    "calls",
                    null,
                    new Evidence(
                        fileId,
                        new SourceLocation(Path.Join(root, "Source.cs"), 1, 1, 1, 7),
                        EvidenceConfidence.Semantic,
                        "test")),
            ]);

            var reads = Enumerable.Range(0, 32).Select(async _ =>
            {
                (await store.FindSymbolsAsync("Fixture.Target"))
                    .Should().ContainSingle(hit => hit.Id == targetId);
                (await store.ListAuditableOutboundEdgesAsync(sourceId))
                    .Should().ContainSingle(hit => hit.Symbol.Id == targetId);
                (await store.ListEdgeEvidenceAsync(sourceId, targetId, "calls"))
                    .Should().ContainSingle();
            });

            await Task.WhenAll(reads);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
