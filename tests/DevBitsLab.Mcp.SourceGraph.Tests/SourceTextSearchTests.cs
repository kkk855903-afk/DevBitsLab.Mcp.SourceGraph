using System.Security.Cryptography;
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

public sealed class SourceTextSearchTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _csPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "source-text-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _csPath = Path.Join(_tempDir, "CameraService.cs");
        await File.WriteAllTextAsync(_csPath, """
            class CameraService {
                // camera camera
                void Start() { CameraInterop.Create(); }
            }
            """);
        var xamlPath = Path.Join(_tempDir, "CameraView.xaml");
        await File.WriteAllTextAsync(xamlPath, "<TextBlock Text=\"camera\" />");

        _store = new SqliteGraphStore(Path.Join(_tempDir, "graph.db"));
        await _store.EnsureSchemaAsync();
        await IndexFileAsync(_csPath);
        await IndexFileAsync(xamlPath);
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task LiteralSearch_usesContentIndexAndReportsTotalContextAndTruncation()
    {
        var page = await _store!.SearchSourceTextAsync(
            "camera", SourceTextSearchMode.Literal, caseSensitive: false,
            fileGlob: "**/*.cs", contextLines: 1, maxResults: 1);

        page.Hits.Should().ContainSingle();
        page.TotalMatches.Should().Be(4);
        page.TotalMatchingLines.Should().Be(3);
        page.Truncated.Should().BeTrue();
        page.CandidateDocuments.Should().BeGreaterThanOrEqualTo(1);
        page.Hits[0].FilePath.Should().Be(_csPath);
        page.Hits[0].Line.Should().Be(1);
        page.Hits[0].AfterContext.Should().ContainSingle().Which.Should().Contain("camera camera");
    }

    [Fact]
    public async Task LiteralSearch_honoursCaseAndFileGlob()
    {
        var page = await _store!.SearchSourceTextAsync(
            "camera", SourceTextSearchMode.Literal, caseSensitive: true,
            fileGlob: "*.cs", contextLines: 0, maxResults: 20);

        page.TotalMatches.Should().Be(2);
        page.Hits.Should().ContainSingle();
        page.Hits[0].MatchCount.Should().Be(2);
    }

    [Fact]
    public async Task RegexSearch_returnsExactLineLocationsWithoutExternalProcess()
    {
        var page = await _store!.SearchSourceTextAsync(
            "Camera(Service|Interop)", SourceTextSearchMode.Regex, caseSensitive: true,
            fileGlob: "**/*.cs", contextLines: 0, maxResults: 20);

        page.TotalMatches.Should().Be(2);
        page.TotalMatchingLines.Should().Be(2);
        page.Hits.Select(hit => hit.Line).Should().Equal(1, 3);
    }

    [Fact]
    public async Task IncrementalUpsertAndDelete_refreshTheContentIndex()
    {
        await File.WriteAllTextAsync(_csPath, "sealed class SensorService { }");
        var replacementBytes = await File.ReadAllBytesAsync(_csPath);
        await _store!.ReplaceFileFactsAsync(new FileFactsReplacement(
            _csPath,
            SHA256.HashData(replacementBytes),
            DateTimeOffset.UtcNow,
            IsGenerated: false,
            Symbols: [],
            Edges: [],
            Annotations: [],
            References: []));

        var oldPage = await _store.SearchSourceTextAsync(
            "CameraService", SourceTextSearchMode.Literal, true, null, 0, 20);
        var newPage = await _store.SearchSourceTextAsync(
            "SensorService", SourceTextSearchMode.Literal, true, null, 0, 20);
        oldPage.TotalMatches.Should().Be(0);
        newPage.TotalMatches.Should().Be(1);

        (await _store.DeleteFileAsync(_csPath)).Should().BeTrue();
        var deletedPage = await _store.SearchSourceTextAsync(
            "SensorService", SourceTextSearchMode.Literal, true, null, 0, 20);
        deletedPage.TotalMatches.Should().Be(0);
    }

    [Fact]
    public async Task ToolReturnsExcludedDirectoriesAndGeneration()
    {
        var scope = new Scope(
            "default", "default", _tempDir,
            new ScopeProjectSet.Paths(["**/*.cs"], ["bin/**", "obj/**"]),
            false, DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            _store!,
            _store!.CreateEmbeddingsStore(384),
            new RoslynIndexer(_store),
            "");
        host.ApplyIndexState(await _store.CompleteIndexGenerationAsync(DateTimeOffset.UtcNow));
        host.MarkReady();
        var router = new ScopeRouter();
        router.Register(host);
        router.SetDefaultScope("default");

        var result = await GraphTools.SearchTextAsync(
            router, "Camera", fileGlob: "**/*.cs", contextLines: 1, maxResults: 10);
        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.SearchTextResult)!;

        dto.TotalMatches.Should().Be(4);
        dto.ExcludedDirectories.Should().Contain(["bin/**", "obj/**", "**/.git/**"]);
        dto.IndexGeneration.Should().Be(1);
        dto.Hits.Should().OnlyContain(hit => hit.FilePath == _csPath);
        var prose = result.Content!.OfType<TextContentBlock>().First().Text;
        prose.Should().Contain("context before:");
        prose.Should().Contain("context after:");
        prose.Should().Contain("camera camera");
        result.Content!.OfType<TextContentBlock>().Last().Text.Should().Contain("generation=1");

        // The host owns the shared store, so prevent test teardown from disposing it twice.
        await host.DisposeAsync();
        _store = null;
    }

    [Fact]
    public async Task ToolMaxResults_limitsStructuredHits_withoutConfusingProsePreviewWithTruncation()
    {
        var lines = Enumerable.Range(1, 25)
            .Select(index => $"public sealed class PublicType{index} {{ }}");
        await File.WriteAllTextAsync(_csPath, string.Join(Environment.NewLine, lines));
        var replacementBytes = await File.ReadAllBytesAsync(_csPath);
        await _store!.ReplaceFileFactsAsync(new FileFactsReplacement(
            _csPath,
            SHA256.HashData(replacementBytes),
            DateTimeOffset.UtcNow,
            IsGenerated: false,
            Symbols: [],
            Edges: [],
            Annotations: [],
            References: []));

        var scope = new Scope(
            "default", "default", _tempDir,
            new ScopeProjectSet.Paths(["**/*.cs"], []),
            false, DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            _store,
            _store.CreateEmbeddingsStore(384),
            new RoslynIndexer(_store),
            "");
        host.ApplyIndexState(await _store.CompleteIndexGenerationAsync(DateTimeOffset.UtcNow));
        host.MarkReady();
        var router = new ScopeRouter();
        router.Register(host);
        router.SetDefaultScope("default");

        var limitedCall = await GraphTools.SearchTextAsync(
            router, "public", maxResults: 5);
        var limited = JsonSerializer.Deserialize(
            limitedCall.StructuredContent!.Value,
            ToolOutputJsonContext.Default.SearchTextResult)!;
        limited.Hits.Should().HaveCount(5);
        limited.ReturnedLines.Should().Be(5);
        limited.TotalMatchingLines.Should().Be(25);
        limited.Truncated.Should().BeTrue();

        var completeCall = await GraphTools.SearchTextAsync(
            router, "public", maxResults: 500);
        var complete = JsonSerializer.Deserialize(
            completeCall.StructuredContent!.Value,
            ToolOutputJsonContext.Default.SearchTextResult)!;
        complete.Hits.Should().HaveCount(25);
        complete.ReturnedLines.Should().Be(25);
        complete.TotalMatchingLines.Should().Be(25);
        complete.Truncated.Should().BeFalse();
        completeCall.Content!.OfType<TextContentBlock>().First().Text.Should()
            .Contain("Prose preview shows 20 of 25 returned matching lines")
            .And.Contain("all 25 returned lines are present in structured content");

        await host.DisposeAsync();
        _store = null;
    }

    [Fact]
    public async Task RoslynColdIndex_populatesFirstPartySourceDocuments()
    {
        var solution = LocateSampleSolution();
        await RoslynIndexer.IndexSolutionOnceAsync(solution, _store!);

        var page = await _store!.SearchSourceTextAsync(
            "class Calculator", SourceTextSearchMode.Literal, true,
            "**/Calculator.cs", contextLines: 0, maxResults: 10);

        page.TotalMatches.Should().BeGreaterThan(0);
        page.Hits.Should().OnlyContain(hit => hit.FilePath.EndsWith("Calculator.cs"));
    }

    private async Task IndexFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        await _store!.UpsertFileAsync(path, SHA256.HashData(bytes), DateTimeOffset.UtcNow);
    }

    private static string LocateSampleSolution()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Join(directory.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate Sample.sln.");
    }
}
