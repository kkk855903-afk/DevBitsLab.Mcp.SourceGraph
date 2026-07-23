using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class PrivacyDispatcherTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "sourcegraph-privacy-dispatch-" + Guid.NewGuid().ToString("N"));

    public PrivacyDispatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task DispatchAll_skipsPrivacyExcludedFiles_beforeInvokingIndexer()
    {
        var allowed = await PlantAsync(Path.Join(_root, "src", "Allowed.privacytest"), "allowed");
        await PlantAsync(Path.Join(_root, "PatientData", "patient.privacytest"), "PATIENT-CANARY");
        await PlantAsync(Path.Join(_root, "Images", "scan.privacytest"), "IMAGE-CANARY");
        await PlantAsync(Path.Join(_root, "Release", "generated.privacytest"), "BUILD-CANARY");
        await PlantAsync(Path.Join(_root, "src", "scan.dcm"), "DICOM-CANARY");

        var indexer = new RecordingIndexer();
        var indexers = new LanguageIndexerRegistry();
        indexers.Register(indexer);
        var dispatcher = new LanguageIndexerDispatcher(
            indexers,
            new LanguageProjectFactoryRegistry());

        var dbPath = Path.Join(_root, "graph.db");
        await using var store = new SqliteGraphStore(dbPath);
        await store.EnsureSchemaAsync();
        var dispatched = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase));

        dispatched.Should().Be(1);
        indexer.Paths.Should().Equal(allowed);
    }

    private static async Task<string> PlantAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private sealed class RecordingIndexer : ILanguageIndexer
    {
        public IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".privacytest", ".dcm" };

        public List<string> Paths { get; } = new();

        public Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
        {
            Paths.Add(ctx.FilePath);
            IReadOnlyList<IndexEvent> events = new IndexEvent[]
            {
                new IndexEvent.FileScanned(ctx.FilePath, SHA256.HashData(ctx.Contents)),
            };
            return Task.FromResult(events);
        }
    }
}
