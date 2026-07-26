using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ReferenceLookupTests : IAsyncLifetime
{
    private string _tempDirectory = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-reference-lookup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _store = new SqliteGraphStore(Path.Join(_tempDirectory, "graph.db"));
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task More_specific_reference_kind_hides_generic_duplicate_at_same_location()
    {
        var (fileId, symbolId) = await AddSymbolAsync();
        await _store!.BulkInsertReferencesAsync(
        [
            new SymbolReference(0, symbolId, fileId, 12, 9, ReferenceKind.Reference),
            new SymbolReference(0, symbolId, fileId, 12, 9, ReferenceKind.Call),
        ]);

        var references = await _store.FindReferencesAsync(symbolId);

        references.Should().ContainSingle()
            .Which.Kind.Should().Be(ReferenceKind.Call);
    }

    [Fact]
    public async Task Read_and_write_evidence_at_same_location_remain_distinct()
    {
        var (fileId, symbolId) = await AddSymbolAsync();
        await _store!.BulkInsertReferencesAsync(
        [
            new SymbolReference(0, symbolId, fileId, 18, 5, ReferenceKind.Read),
            new SymbolReference(0, symbolId, fileId, 18, 5, ReferenceKind.Write),
        ]);

        var references = await _store.FindReferencesAsync(symbolId);

        references.Select(reference => reference.Kind)
            .Should().BeEquivalentTo(
                [ReferenceKind.Read, ReferenceKind.Write]);
    }

    [Fact]
    public async Task Call_reference_uses_matching_edge_evidence_confidence()
    {
        var (fileId, targetSymbolId) = await AddSymbolAsync();
        var sourceSymbolId = await _store!.UpsertSymbolAsync(
            "cpp:F:CameraService.cpp::syntax::Run()",
            new Symbol(
                Id: 0,
                Name: "Run",
                Fqn: "CameraService::Run",
                Kind: DevBitsLab.Mcp.SourceGraph.Sdk.SymbolKinds.Function,
                FileId: fileId,
                StartLine: 8,
                StartCol: 1,
                EndLine: 14,
                EndCol: 2,
                Signature: "void Run()",
                ContainerId: null));
        await _store.BulkInsertReferencesAsync(
        [
            new SymbolReference(
                0,
                targetSymbolId,
                fileId,
                12,
                9,
                ReferenceKind.Call),
        ]);
        await _store.BulkInsertEdgesAsync(
        [
            new Edge(
                sourceSymbolId,
                targetSymbolId,
                DevBitsLab.Mcp.SourceGraph.Sdk.EdgeKinds.Calls)
            {
                Evidence = new Evidence(
                    fileId,
                    new SourceLocation(
                        @"D:\repo\CameraService.cs",
                        12,
                        9,
                        12,
                        14),
                    EvidenceConfidence.Inferred,
                    "tree-sitter-cpp"),
            },
        ]);

        var reference = (await _store.FindReferencesAsync(targetSymbolId))
            .Should().ContainSingle().Subject;

        reference.Confidence.Should().Be(EvidenceConfidence.Inferred);
        reference.Producer.Should().Be("tree-sitter-cpp");
    }

    private async Task<(long FileId, long SymbolId)> AddSymbolAsync()
    {
        var fileId = await _store!.UpsertFileAsync(
            @"D:\repo\CameraService.cs",
            [1, 2, 3],
            DateTimeOffset.UtcNow);
        var symbolId = await _store.UpsertSymbolAsync(
            "csharp:M:CameraService.Start",
            new Symbol(
                Id: 0,
                Name: "Start",
                Fqn: "CameraService.Start",
                Kind: "method",
                FileId: fileId,
                StartLine: 1,
                StartCol: 1,
                EndLine: 2,
                EndCol: 1,
                Signature: "void Start()",
                ContainerId: null));
        return (fileId, symbolId);
    }
}
