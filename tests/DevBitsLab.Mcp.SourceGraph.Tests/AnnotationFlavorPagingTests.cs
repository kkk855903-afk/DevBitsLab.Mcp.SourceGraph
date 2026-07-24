using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class AnnotationFlavorPagingTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-annotation-paging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new SqliteGraphStore(Path.Join(_tempDir, "graph.db"));
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Pages_exact_flavor_in_stable_id_order_with_owner_identity()
    {
        var firstPath = Path.Join(_tempDir, "First.cs");
        var secondPath = Path.Join(_tempDir, "Second.cs");
        var firstFile = await SeedFileAsync(firstPath);
        var secondFile = await SeedFileAsync(secondPath);
        var firstKey = "csharp:M:Native.First";
        var secondKey = "csharp:M:Native.Second";
        var firstSymbol = await SeedSymbolAsync(firstFile, firstKey, "First");
        var secondSymbol = await SeedSymbolAsync(secondFile, secondKey, "Second");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(firstSymbol, "One", "interop-managed-import", """{"v":1}"""),
            Annotation(firstSymbol, "Ignored", "csharp-attribute", null),
            Annotation(secondSymbol, "Two", "interop-managed-import", """{"v":2}"""),
            Annotation(firstSymbol, "Three", "interop-managed-import", """{"v":3}"""),
        ]);

        var firstPage = await _store.ListAnnotationsByFlavorAsync(
            "interop-managed-import",
            afterId: 0,
            limit: 2);
        var secondPage = await _store.ListAnnotationsByFlavorAsync(
            "interop-managed-import",
            afterId: firstPage[^1].AnnotationId,
            limit: 2);

        firstPage.Should().HaveCount(2);
        secondPage.Should().ContainSingle();
        firstPage.Concat(secondPage)
            .Select(row => row.Name)
            .Should().Equal("One", "Two", "Three");
        firstPage.Concat(secondPage)
            .Select(row => row.AnnotationId)
            .Should().BeInAscendingOrder();
        firstPage[0].Should().BeEquivalentTo(
            new
            {
                SymbolId = firstSymbol,
                SymbolCanonicalKey = firstKey,
                FileId = firstFile,
                FilePath = firstPath,
                Flavor = "interop-managed-import",
                ArgsJson = """{"v":1}""",
            },
            options => options.ExcludingMissingMembers());
        firstPage[1].Should().BeEquivalentTo(
            new
            {
                SymbolId = secondSymbol,
                SymbolCanonicalKey = secondKey,
                FileId = secondFile,
                FilePath = secondPath,
            },
            options => options.ExcludingMissingMembers());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Rejects_unbounded_page_sizes(int limit)
    {
        var act = () => _store!.ListAnnotationsByFlavorAsync(
            "interop-managed-import",
            afterId: 0,
            limit);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Rejects_invalid_flavor_and_cursor()
    {
        var emptyFlavor = () => _store!.ListAnnotationsByFlavorAsync(
            " ",
            afterId: 0,
            limit: 1);
        var negativeCursor = () => _store!.ListAnnotationsByFlavorAsync(
            "interop-managed-import",
            afterId: -1,
            limit: 1);

        await emptyFlavor.Should().ThrowAsync<ArgumentException>();
        await negativeCursor.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => _store!.ListAnnotationsByFlavorAsync(
            "interop-managed-import",
            afterId: 0,
            limit: 1,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);

    private async Task<long> SeedSymbolAsync(
        long fileId,
        string canonicalKey,
        string name) =>
        await _store!.UpsertSymbolAsync(
            canonicalKey,
            new Symbol(
                0,
                name,
                name,
                "method",
                fileId,
                1,
                1,
                2,
                1,
                $"void {name}()",
                null));

    private static AnnotationRecord Annotation(
        long symbolId,
        string name,
        string flavor,
        string? argsJson) =>
        new(
            symbolId,
            name,
            $"MedInterop.{name}",
            flavor,
            argsJson,
            AttributeSymbolId: null);
}
