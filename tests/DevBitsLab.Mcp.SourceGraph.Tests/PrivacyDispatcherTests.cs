using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
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

        dispatched.IndexedFiles.Should().Be(1);
        dispatched.FailedFiles.Should().BeEmpty();
        indexer.Paths.Should().Equal(allowed);
        indexer.ExcludeSnapshots.Should().ContainSingle()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task ColdDispatch_appliesScopeExcludes_andPrivacyCannotBeBypassedByExtension()
    {
        var allowed = await PlantAsync(Path.Join(_root, "src", "Allowed.privacytest"), "allowed");
        await PlantAsync(Path.Join(_root, "src", "Generated", "Hidden.privacytest"), "SCOPE-CANARY");
        await PlantAsync(Path.Join(_root, "pAtIeNtDaTa", "Hidden.privacytest"), "PATIENT-CANARY");
        await PlantAsync(Path.Join(_root, "src", "study.DCM"), "DICOM-CANARY");

        var indexer = new RecordingIndexer();
        var indexers = new LanguageIndexerRegistry();
        indexers.Register(indexer);
        var dispatcher = new LanguageIndexerDispatcher(
            indexers,
            new LanguageProjectFactoryRegistry());

        var dbPath = Path.Join(_root, "scope-aware-graph.db");
        await using var store = new SqliteGraphStore(dbPath);
        await store.EnsureSchemaAsync();
        var dispatched = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase),
            ["**/generated/**"],
            CancellationToken.None);

        dispatched.IndexedFiles.Should().Be(1);
        dispatched.FailedFiles.Should().BeEmpty();
        indexer.Paths.Should().Equal(allowed);
        indexer.ExcludeSnapshots.Should().ContainSingle()
            .Which.Should().Equal("**/generated/**");
    }

    [SkippableFact]
    public async Task ColdDispatch_neverFollowsDirectoryLinkOutsideRepository()
    {
        var outside = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-dispatch-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var allowed = await PlantAsync(
                Path.Join(_root, "src", "Allowed.privacytest"),
                "allowed");
            await PlantAsync(Path.Join(outside, "Outside.privacytest"), "OUTSIDE-CANARY");
            var link = Path.Join(_root, "src", "External");
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(link, outside),
                "This environment does not permit symbolic-link or junction creation.");

            var indexer = new RecordingIndexer();
            var indexers = new LanguageIndexerRegistry();
            indexers.Register(indexer);
            var dispatcher = new LanguageIndexerDispatcher(
                indexers,
                new LanguageProjectFactoryRegistry());

            await using var store = new SqliteGraphStore(Path.Join(_root, "link-graph.db"));
            await store.EnsureSchemaAsync();
            var dispatched = await dispatcher.DispatchAllForTestAsync(
                store,
                "test",
                _root,
                new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase));

            dispatched.IndexedFiles.Should().Be(1);
            dispatched.FailedFiles.Should().BeEmpty();
            indexer.Paths.Should().Equal(allowed);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProjectMap_forwardsAndEnforcesEveryScopeExclude()
    {
        var projectAnchor = await PlantAsync(
            Path.Join(_root, "src", "App.csproj"),
            "<Project />");
        var allowed = Path.Join(_root, "src", "Allowed.privacytest");
        var generated = Path.Join(_root, "src", "Generated", "Hidden.privacytest");
        var patient = Path.Join(_root, "pAtIeNtDaTa", "Hidden.privacytest");
        var dicom = Path.Join(_root, "src", "study.DCM");
        var project = new StubProject([allowed, generated, patient, dicom]);
        var factory = new RecordingProjectFactory(project);
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(factory);
        var dispatcher = new LanguageIndexerDispatcher(
            new LanguageIndexerRegistry(),
            factories);

        var store = new SqliteGraphStore(Path.Join(_root, "project-map.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Paths(
                Globs: ["src/**/*.csproj"],
                Exclude: ["**/generated/**"]),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.MinValue);
        var host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solutionPath: string.Empty);

        try
        {
            var result = await dispatcher.BuildProjectMapAsync(host);

            result.Succeeded.Should().BeTrue();
            factory.ObservedRoots.Should().Equal(Path.GetDirectoryName(projectAnchor));
            factory.ObservedExcludePatterns.Should().Contain("**/generated/**");
            host.ProjectByFilePath.Keys.Should().Equal(allowed);
        }
        finally
        {
            await host.DisposeAsync();
        }
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
        public List<IReadOnlyList<string>> ExcludeSnapshots { get; } = new();

        public Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
        {
            Paths.Add(ctx.FilePath);
            ExcludeSnapshots.Add(ctx.ExcludePatterns.ToArray());
            IReadOnlyList<IndexEvent> events = new IndexEvent[]
            {
                new IndexEvent.FileScanned(ctx.FilePath, SHA256.HashData(ctx.Contents)),
            };
            return Task.FromResult(events);
        }
    }

    private sealed class RecordingProjectFactory(ILanguageProject project)
        : IExclusionAwareLanguageProjectFactory
    {
        public IReadOnlyCollection<string> ProjectMarkers { get; } = ["*.privacytest"];

        public IReadOnlyList<string> ObservedExcludePatterns { get; private set; } = [];
        public List<string> ObservedRoots { get; } = [];

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct) =>
            throw new InvalidOperationException("The exclusion-aware overload should be used.");

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct)
        {
            ObservedRoots.Add(repoRoot);
            ObservedExcludePatterns = excludePatterns.ToArray();
            return Task.FromResult<IReadOnlyList<ILanguageProject>>([project]);
        }
    }

    private sealed class StubProject(IReadOnlyCollection<string> paths) : ILanguageProject
    {
        public string Id { get; } = "stub";

        public IReadOnlyCollection<string> FilePaths { get; } = paths;
    }
}
