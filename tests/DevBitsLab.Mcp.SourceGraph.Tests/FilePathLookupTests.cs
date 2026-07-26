using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class FilePathLookupTests : IAsyncLifetime
{
    private string _tempDirectory = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-file-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _store = new SqliteGraphStore(
            Path.Join(_tempDirectory, "graph.db"));
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
    public async Task Forward_slash_relative_path_matches_windows_absolute_path()
    {
        await AddSymbolAsync(
            @"D:\repo\PostureGuard.App\Services\CameraService.cs",
            "CameraService");

        var symbols = await _store!.ListSymbolsInFileAsync(
            "PostureGuard.App/Services/CameraService.cs");

        symbols.Should().ContainSingle()
            .Which.Name.Should().Be("CameraService");
    }

    [Fact]
    public async Task Backslash_relative_path_matches_forward_slash_stored_path()
    {
        await AddSymbolAsync(
            "/repo/PostureGuard.App/Services/CameraService.cs",
            "CameraService");

        var symbols = await _store!.ListSymbolsInFileAsync(
            @"PostureGuard.App\Services\CameraService.cs");

        symbols.Should().ContainSingle()
            .Which.Name.Should().Be("CameraService");
    }

    [Fact]
    public async Task File_name_suffix_requires_a_path_segment_boundary()
    {
        await AddSymbolAsync(
            @"D:\repo\Services\OtherCameraService.cs",
            "OtherCameraService");

        var symbols = await _store!.ListSymbolsInFileAsync(
            "CameraService.cs");

        symbols.Should().BeEmpty();
    }

    private async Task AddSymbolAsync(string path, string name)
    {
        var fileId = await _store!.UpsertFileAsync(
            path,
            [1, 2, 3],
            DateTimeOffset.UtcNow);
        await _store.UpsertSymbolAsync(
            $"csharp:T:{name}",
            new Symbol(
                Id: 0,
                Name: name,
                Fqn: name,
                Kind: "class",
                FileId: fileId,
                StartLine: 1,
                StartCol: 1,
                EndLine: 2,
                EndCol: 1,
                Signature: $"class {name}",
                ContainerId: null,
                Modifiers: null,
                Accessibility: 6,
                XmlSummary: null));
    }
}
