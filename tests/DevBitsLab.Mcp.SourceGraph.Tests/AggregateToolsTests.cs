using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class AggregateToolsTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private ScopeHost? _host;
    private ScopeRouter? _router;

    public async Task InitializeAsync()
    {
        var solution = LocateSolution();
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "aggregate-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var store = new SqliteGraphStore(Path.Join(_tempDir, "graph.db"));
        await RoslynIndexer.IndexSolutionOnceAsync(solution, store);
        var scope = new Scope(
            "default",
            "default",
            Path.GetDirectoryName(solution)!,
            new ScopeProjectSet.Solutions([solution], []),
            false,
            DateTimeOffset.UtcNow);
        _host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solution);
        _host.ApplyIndexState(
            await store.CompleteIndexGenerationAsync(DateTimeOffset.UtcNow));
        _host.MarkReady();
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ResolveAndReferences_returnsOneCompactStructuredPayload()
    {
        var result = await AggregateTools.ResolveAndReferencesAsync(
            _router!,
            "Sample.Domain.Calculator.Add");

        result.Content!.OfType<ResourceLinkBlock>().Should().BeEmpty();
        result.Content.OfType<TextContentBlock>().Should().HaveCount(2);
        result.Content.OfType<TextContentBlock>().Last().Text.Should()
            .Contain("generation=1")
            .And.Contain("watcher_lag_ms=0");
        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.ResolveAndReferencesResult)!;
        dto.Status.Should().Be("ok");
        dto.Definition.Should().NotBeNull();
        dto.Definition!.CanonicalKey.Should().NotBeNullOrEmpty();
        dto.Definition.FilePath.Should().BeNull();
        dto.Definition.FileRef.Should().MatchRegex("^f[0-9]+$");
        dto.Files.Should().ContainKey(dto.Definition.FileRef!);
        dto.References.Should().NotBeEmpty();
        dto.References.Should().OnlyContain(reference =>
            reference.FilePath == null && reference.FileRef != null);
    }

    [Fact]
    public async Task SymbolOverview_combinesDefinitionMembersCallersAndImplementations()
    {
        var result = await AggregateTools.SymbolOverviewAsync(
            _router!,
            "Sample.Domain.Calculator");

        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.SymbolOverviewResult)!;
        dto.Status.Should().Be("ok");
        dto.Definition.Should().NotBeNull();
        dto.Members.Should().NotBeEmpty();
        dto.Files.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SymbolOverview_summaryReturnsCountsWithoutLocationRows()
    {
        var result = await AggregateTools.SymbolOverviewAsync(
            _router!,
            "Sample.Domain.Calculator",
            detail: "summary");

        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.SymbolOverviewResult)!;
        dto.Detail.Should().Be("summary");
        dto.Counts.Members.Should().BeGreaterThan(0);
        dto.Members.Should().BeEmpty();
        dto.Callers.Should().BeEmpty();
        dto.Implementations.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAndReferences_nonCompactKeepsPathsInline()
    {
        var result = await AggregateTools.ResolveAndReferencesAsync(
            _router!,
            "Sample.Domain.Calculator.Add",
            compact: false);

        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.ResolveAndReferencesResult)!;
        dto.Files.Should().BeEmpty();
        dto.Definition!.FilePath.Should().NotBeNullOrEmpty();
        dto.Definition.FileRef.Should().BeNull();
        dto.References.Should().OnlyContain(reference =>
            reference.FilePath != null && reference.FileRef == null);
    }

    [Fact]
    public async Task ResolveAndReferences_doesNotGuessAmbiguousName()
    {
        var result = await AggregateTools.ResolveAndReferencesAsync(
            _router!,
            "Greet");

        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.ResolveAndReferencesResult)!;
        dto.Status.Should().Be("ambiguous");
        dto.Definition.Should().BeNull();
        dto.Candidates.Should().HaveCountGreaterThan(1);
        dto.References.Should().BeEmpty();
    }

    [Fact]
    public async Task BatchQuery_preservesOrderAndIsolatesStatuses()
    {
        var result = await AggregateTools.BatchQueryAsync(
            _router!,
            [
                new BatchQueryRequest(
                    "resolve_and_references",
                    "Sample.Domain.Calculator.Add"),
                new BatchQueryRequest(
                    "symbol_overview",
                    "DefinitelyMissingSymbol"),
            ]);

        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.BatchQueryResult)!;
        dto.Results.Select(item => item.Operation).Should().ContainInOrder(
            "resolve_and_references",
            "symbol_overview");
        dto.Results.Select(item => item.Status).Should().ContainInOrder(
            "ok",
            "not_found");
    }

    private static string LocateSolution()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Join(
                directory.FullName,
                "tests",
                "fixtures",
                "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate Sample.sln.");
    }
}
