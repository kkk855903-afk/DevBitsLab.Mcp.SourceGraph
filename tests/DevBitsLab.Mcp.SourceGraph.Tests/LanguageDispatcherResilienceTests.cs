using System.Security.Cryptography;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class LanguageDispatcherResilienceTests : IDisposable
{
    private const string Extension = ".residx";
    private readonly string _root =
        Path.Join(
            Path.GetTempPath(),
            "sourcegraph-language-resilience-" + Guid.NewGuid().ToString("N"));

    public LanguageDispatcherResilienceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ProjectDiscovery_usesOnlySelectedRoot_andPrunesPrivacyBeforeRead()
    {
        var projectPath = await PlantAsync("src/App/App.csproj", "<Project />");
        var allowed = await PlantAsync("src/App/allowed.residx", "allowed");
        var canary = await PlantAsync("src/App/PatientData/canary.residx", "private");
        await PlantAsync("src/Vendor/Vendor.csproj", "<Project />");
        await PlantAsync("src/Vendor/vendor.residx", "vendor");
        var factory = new PruningRecordingFactory();
        var dispatcher = CreateDispatcher(new RecordingIndexer(), factory);
        var projectSet = new ScopeProjectSet.Projects(
            ["src/App/App.csproj"],
            Array.Empty<string>());
        var host = await CreateHostAsync(projectSet);

        try
        {
            var result = await dispatcher.BuildProjectMapAsync(host);

            result.Succeeded.Should().BeTrue();
            factory.ObservedRoots.Should().Equal(Path.GetDirectoryName(projectPath));
            factory.ObservedExcludePatterns.Should().Contain("**/PatientData/**");
            factory.ReadPaths.Should().Contain(allowed);
            factory.ReadPaths.Should().NotContain(canary);
            factory.ReadPaths.Should().NotContain(path =>
                path.Contains(
                    $"{Path.DirectorySeparatorChar}Vendor{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));
            host.ProjectByFilePath.Keys.Should().Equal(allowed);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonExclusionAwareFactory_isRejectedWithoutInvocation()
    {
        await PlantAsync("App.csproj", "<Project />");
        var factory = new NonAwareFactory();
        var dispatcher = CreateDispatcher(new RecordingIndexer(), factory);
        var host = await CreateHostAsync(
            new ScopeProjectSet.Projects(
                ["App.csproj"],
                Array.Empty<string>()));

        try
        {
            var result = await dispatcher.BuildProjectMapAsync(host);

            result.Succeeded.Should().BeFalse();
            result.FailedProjects.Should().ContainSingle(failure =>
                failure.Reason.Contains(
                    "IExclusionAwareLanguageProjectFactory",
                    StringComparison.Ordinal));
            factory.WasInvoked.Should().BeFalse();
            host.ProjectByFilePath.Should().BeEmpty();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task FactoryFailure_keepsPriorMapAndLiveGraph_andPropagatesFailure()
    {
        var sourcePath = await PlantAsync("src/stable.residx", "v1");
        await PlantAsync("Test.csproj", "<Project />");
        var stableDispatcher = CreateDispatcher(new RecordingIndexer());
        var host = await CreateHostAsync(
            new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()));

        try
        {
            await stableDispatcher.DispatchChangedFilesAsync(host, [sourcePath]);
            var priorHash = await host.Store.GetFileContentHashAsync(sourcePath);
            var oldProject = new TestProject("prior", [sourcePath]);
            host.ProjectByFilePath[sourcePath] = oldProject;
            host.ProjectMapReady = false;

            await File.WriteAllTextAsync(sourcePath, "v2");
            var failingDispatcher = CreateDispatcher(
                new RecordingIndexer(),
                new ThrowingAwareFactory());
            var result = await failingDispatcher.DispatchChangedFilesAsync(
                host,
                [sourcePath]);

            result.IndexedFiles.Should().Be(0);
            result.DeletedFiles.Should().Be(0);
            result.SkippedFiles.Should().Be(1);
            result.FailedProjects.Should().ContainSingle(failure =>
                failure.Reason.Contains(
                    "synthetic project discovery failure",
                    StringComparison.Ordinal));
            result.FailedFiles.Should().BeEmpty();
            host.ProjectByFilePath.Should().ContainKey(sourcePath)
                .WhoseValue.Should().BeSameAs(oldProject);
            (await host.Store.GetFileContentHashAsync(sourcePath)).Should().Equal(priorHash);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ColdFactoryFailure_withStoredGraph_isPartialWatchable_andNextSourceRetriesMap()
    {
        var sourcePath = await PlantAsync("src/stale.residx", "v1");
        await PlantAsync("Test.csproj", "<Project />");
        var seedDispatcher = CreateDispatcher(new RecordingIndexer());
        var projectSet = new ScopeProjectSet.Paths(
            ["**/*.csproj"],
            Array.Empty<string>());
        var host = await CreateHostAsync(projectSet);

        try
        {
            await seedDispatcher.DispatchChangedFilesAsync(host, [sourcePath]);
            host.ProjectByFilePath.Clear();
            host.ProjectMapReady = false;
            var factory = new ToggleAwareFactory(sourcePath)
            {
                ShouldFail = true,
            };
            var dispatcher = CreateDispatcher(new RecordingIndexer(), factory);

            var projectMapResult = await dispatcher.BuildProjectMapAsync(host);
            projectMapResult.Succeeded.Should().BeFalse();
            host.ProjectByFilePath.Should().BeEmpty();
            host.ProjectMapReady.Should().BeFalse();
            var counts = await host.Store.RowCountsAsync();
            var status = LiveIndexService.ResolveColdIndexStatus(
                currentPassProducedUsableOutput: false,
                counts,
                failedProjectCount: projectMapResult.FailedProjects.Count,
                failedFileCount: 0,
                projectDiscoveryFailed: true);
            host.Status = status.Status;
            host.StatusMessage = status.StatusMessage;

            status.Status.Should().Be("partial");
            status.UsesRetainedGraph.Should().BeTrue();
            status.StatusMessage.Should().Contain("serving stale stored graph");
            LiveIndexService.IsWatchable(host).Should().BeTrue();
            (await host.Store.GetAllSymbolKeysAsync()).Should().NotBeEmpty(
                "failed discovery must not delete the last queryable graph");

            factory.ShouldFail = false;
            await File.WriteAllTextAsync(sourcePath, "v2");
            var retried = await dispatcher.DispatchChangedFilesAsync(
                host,
                [sourcePath]);

            retried.IndexedFiles.Should().Be(1);
            retried.FailedProjects.Should().BeEmpty();
            host.ProjectMapReady.Should().BeTrue(
                "an ordinary source event retries a pending cold map discovery");
            host.ProjectByFilePath.Should().ContainKey(sourcePath);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ColdFactoryFailure_withoutUsableFacts_isDegraded()
    {
        await PlantAsync("Test.csproj", "<Project />");
        var host = await CreateHostAsync(
            new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()));
        var dispatcher = CreateDispatcher(
            new RecordingIndexer(),
            new ThrowingAwareFactory());
        try
        {
            var projectMapResult = await dispatcher.BuildProjectMapAsync(host);
            var counts = await host.Store.RowCountsAsync();
            var status = LiveIndexService.ResolveColdIndexStatus(
                currentPassProducedUsableOutput: false,
                counts,
                failedProjectCount: projectMapResult.FailedProjects.Count,
                failedFileCount: 0,
                projectDiscoveryFailed: true);

            status.Status.Should().Be("degraded");
            status.UsesRetainedGraph.Should().BeFalse();
            status.StatusMessage.Should().Contain("no usable graph remains");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProjectAnchorDeleteRetainsOldGraphWhenRoslynReloadFails()
    {
        var projectPath = await PlantAsync("src/App/App.csproj", "<Project />");
        var sourcePath = await PlantAsync("src/App/app.residx", "v1");
        var dispatcher = CreateDispatcher(new RecordingIndexer());
        var host = await CreateHostAsync(
            new ScopeProjectSet.Paths(
                ["src/**/*.csproj"],
                Array.Empty<string>()));

        try
        {
            var initial = await dispatcher.DispatchChangedFilesAsync(
                host,
                [projectPath]);
            initial.IndexedFiles.Should().Be(1);
            (await host.Store.GetAllFilesAsync())
                .Should().ContainSingle(file => file.Path == sourcePath);

            File.Delete(projectPath);
            var languageCalled = false;
            var reconciliation =
                await LiveIndexService.RunControlReconciliationAsync(
                    _ => Task.FromException<IndexResult?>(
                        new InvalidOperationException(
                            "synthetic Roslyn reload failure")),
                    async ct =>
                    {
                        languageCalled = true;
                        return await dispatcher.DispatchChangedFilesAsync(
                            host,
                            [projectPath],
                            ct);
                    },
                    CancellationToken.None);
            var removed = reconciliation.LanguageResult;

            languageCalled.Should().BeFalse();
            removed.DeletedFiles.Should().Be(0);
            removed.FailedFiles.Should().BeEmpty();
            removed.FailedProjects.Should().ContainSingle(failure =>
                failure.Name == "roslyn-reload"
                && failure.Reason.Contains(
                    "synthetic Roslyn reload failure",
                    StringComparison.Ordinal));
            (await host.Store.GetAllFilesAsync())
                .Should().ContainSingle(file => file.Path == sourcePath);
            LiveIndexService.ApplyLiveLanguageFailures(host, removed)
                .Should().BeTrue();
            host.Status.Should().Be("partial");

            await File.WriteAllTextAsync(projectPath, "<Project />");
            await File.WriteAllTextAsync(sourcePath, "v2");
            var added = await dispatcher.DispatchChangedFilesAsync(
                host,
                [projectPath]);

            added.IndexedFiles.Should().Be(1);
            added.UsableOutputFiles.Should().Be(1);
            added.FailedFiles.Should().BeEmpty();
            (await host.Store.GetAllFilesAsync())
                .Should().ContainSingle(file => file.Path == sourcePath);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ControlReconciliation_languageFailure_keepsRoslynResultVisible()
    {
        var roslyn = new IndexResult(
            FilesIndexed: 1,
            SymbolsIndexed: 2,
            ReferencesIndexed: 3,
            Elapsed: TimeSpan.Zero);

        var result = await LiveIndexService.RunControlReconciliationAsync(
            _ => Task.FromResult<IndexResult?>(roslyn),
            _ => Task.FromException<LanguageDispatchResult>(
                new InvalidOperationException(
                    "synthetic registered-language failure")),
            CancellationToken.None);

        result.RoslynResult.Should().BeSameAs(roslyn);
        result.LanguageResult.FailedProjects.Should().ContainSingle(failure =>
            failure.Name == "registered-language-reconciliation"
            && failure.Reason.Contains(
                "synthetic registered-language failure",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ControlReconciliation_partialRoslynResultRetainsLanguageFacts()
    {
        var roslyn = new IndexResult(
            FilesIndexed: 1,
            SymbolsIndexed: 2,
            ReferencesIndexed: 3,
            Elapsed: TimeSpan.Zero)
        {
            FailedFiles =
            [
                new FileFailure("Broken.cs", "synthetic Roslyn file failure"),
            ],
        };
        var languageCalled = false;

        var result = await LiveIndexService.RunControlReconciliationAsync(
            _ => Task.FromResult<IndexResult?>(roslyn),
            _ =>
            {
                languageCalled = true;
                return Task.FromResult(LanguageDispatchResult.Empty);
            },
            CancellationToken.None);

        languageCalled.Should().BeFalse();
        result.RoslynResult.Should().BeSameAs(roslyn);
        result.RoslynFailure.Should().NotBeNull();
        result.LanguageResult.IndexedFiles.Should().Be(0);
        result.LanguageResult.DeletedFiles.Should().Be(0);
        result.LanguageResult.FailedFiles.Should().Equal(
            roslyn.FailedFiles);
    }

    [Fact]
    public async Task ControlReconciliation_cancellationPropagates_withoutRunningSecondChannel()
    {
        using var cts = new CancellationTokenSource();
        var languageCalled = false;

        Func<Task> act = () => LiveIndexService.RunControlReconciliationAsync(
            _ =>
            {
                cts.Cancel();
                return Task.FromResult<IndexResult?>(null);
            },
            _ =>
            {
                languageCalled = true;
                return Task.FromResult(LanguageDispatchResult.Empty);
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        languageCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ColdDispatch_fileVanishingAfterEnumeration_countsOneRaceSkip()
    {
        var firstPath = await PlantAsync("src/A.residx", "first");
        var vanishedPath = await PlantAsync("src/Z.residx", "vanish");
        await PlantAsync("Test.csproj", "<Project />");
        var indexer = new RecordingIndexer(ctx =>
        {
            if (string.Equals(ctx.FilePath, firstPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(vanishedPath);
            }
        });
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(
            new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()));

        try
        {
            var result = await dispatcher.DispatchAllAsync(host);

            result.IndexedFiles.Should().Be(1);
            result.SkippedFiles.Should().Be(1);
            result.FailedFiles.Should().BeEmpty();
            indexer.Paths.Should().Equal(firstPath);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StaleDeleteStorageFailure_isReportedPerFile_andOtherFilesContinue()
    {
        var stalePath = await PlantAsync("src/stale.residx", "stale");
        await PlantAsync("Test.csproj", "<Project />");
        var dbPath = Path.Join(_root, "stale-delete.db");
        var dispatcher = CreateDispatcher(new RecordingIndexer());
        var host = await CreateHostAsync(
            new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()),
            dbPath);

        try
        {
            await dispatcher.DispatchChangedFilesAsync(host, [stalePath]);
            File.Delete(stalePath);
            var currentPath = await PlantAsync("src/current.residx", "current");

            await using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Shared,
                }.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TRIGGER fail_stale_file_delete
                    BEFORE DELETE ON files
                    BEGIN
                        SELECT RAISE(ABORT, 'synthetic stale delete failure');
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var result = await dispatcher.DispatchAllAsync(host);

            result.IndexedFiles.Should().Be(1);
            result.DeletedFiles.Should().Be(0);
            result.SkippedFiles.Should().Be(1);
            result.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == stalePath
                && failure.Reason.Contains(
                    "synthetic stale delete failure",
                    StringComparison.Ordinal));
            (await host.Store.GetAllFilesAsync())
                .Select(file => file.Path)
                .Should().BeEquivalentTo([stalePath, currentPath]);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task ColdWalker_doesNotTraverseSelectedProjectLinkToSibling()
    {
        var selectedRoot = Path.Join(_root, "src", "App");
        var siblingRoot = Path.Join(_root, "src", "Vendor");
        await PlantAsync("src/App/App.csproj", "<Project />");
        var allowed = await PlantAsync("src/App/allowed.residx", "allowed");
        await PlantAsync("src/Vendor/Vendor.csproj", "<Project />");
        await PlantAsync("src/Vendor/private.residx", "private");
        var link = Path.Join(selectedRoot, "LinkedVendor");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, siblingRoot),
            "This environment does not permit symbolic-link or junction creation.");
        var linkedPrivate = Path.Join(link, "private.residx");
        var indexer = new RecordingIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(
            new ScopeProjectSet.Projects(
                ["src/App/App.csproj"],
                Array.Empty<string>()));

        try
        {
            var result = await dispatcher.DispatchAllAsync(host);

            result.IndexedFiles.Should().Be(1);
            result.FailedFiles.Should().BeEmpty();
            indexer.Paths.Should().Equal(allowed);
            indexer.Paths.Should().NotContain(linkedPrivate);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static LanguageIndexerDispatcher CreateDispatcher(
        ILanguageIndexer indexer,
        ILanguageProjectFactory? factory = null)
    {
        var indexers = new LanguageIndexerRegistry();
        indexers.Register(indexer);
        var factories = new LanguageProjectFactoryRegistry();
        if (factory is not null) factories.Register(factory);
        return new LanguageIndexerDispatcher(indexers, factories);
    }

    private async Task<ScopeHost> CreateHostAsync(
        ScopeProjectSet projectSet,
        string? dbPath = null)
    {
        var store = new SqliteGraphStore(
            dbPath
            ?? Path.Join(_root, "graph-" + Guid.NewGuid().ToString("N") + ".db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            "test",
            "test",
            _root,
            projectSet,
            Isolated: false,
            DateTimeOffset.MinValue);
        return new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solutionPath: string.Empty);
    }

    private async Task<string> PlantAsync(string relativePath, string contents)
    {
        var path = Path.Join(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private sealed class RecordingIndexer(Action<IndexContext>? onIndex = null)
        : ILanguageIndexer
    {
        public IReadOnlyCollection<string> FileExtensions { get; } = [Extension];

        public List<string> Paths { get; } = [];

        public Task<IReadOnlyList<IndexEvent>> IndexAsync(
            IndexContext ctx,
            CancellationToken ct)
        {
            onIndex?.Invoke(ctx);
            Paths.Add(ctx.FilePath);
            var value = ctx.GetText().Trim();
            var stem = Path.GetFileNameWithoutExtension(ctx.FilePath);
            IReadOnlyList<IndexEvent> events =
            [
                new IndexEvent.SymbolDeclared(
                    $"csharp:T:Resilience.{stem}.{value}",
                    stem,
                    $"Resilience.{stem}.{value}",
                    SymbolKinds.Class,
                    1,
                    1,
                    1,
                    2),
                new IndexEvent.FileScanned(
                    ctx.FilePath,
                    SHA256.HashData(ctx.Contents)),
            ];
            return Task.FromResult(events);
        }
    }

    private sealed class PruningRecordingFactory
        : IExclusionAwareLanguageProjectFactory
    {
        public IReadOnlyCollection<string> ProjectMarkers { get; } = ["*.csproj"];

        public List<string> ObservedRoots { get; } = [];

        public IReadOnlyList<string> ObservedExcludePatterns { get; private set; } = [];

        public List<string> ReadPaths { get; } = [];

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct) =>
            DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct)
        {
            ObservedRoots.Add(repoRoot);
            ObservedExcludePatterns = excludePatterns.ToArray();
            var policy = new ScopePathPolicy(
                Path.GetFullPath(repoRoot),
                excludePatterns);
            var paths = EnumerateAllowedFiles(repoRoot, policy).ToArray();
            ReadPaths.AddRange(paths);
            IReadOnlyList<ILanguageProject> projects =
            [
                new TestProject(Path.Join(repoRoot, "App.csproj"), paths),
            ];
            return Task.FromResult(projects);
        }

        private static IEnumerable<string> EnumerateAllowedFiles(
            string root,
            ScopePathPolicy policy)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var directory = stack.Pop();
                if (policy.IsExcluded(directory)) continue;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!policy.IsExcluded(child)) stack.Push(child);
                }
                foreach (var file in Directory.EnumerateFiles(
                             directory,
                             $"*{Extension}"))
                {
                    if (!policy.IsExcluded(file)) yield return file;
                }
            }
        }
    }

    private sealed class ThrowingAwareFactory
        : IExclusionAwareLanguageProjectFactory
    {
        public IReadOnlyCollection<string> ProjectMarkers { get; } = ["*.csproj"];

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct) =>
            DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct) =>
            throw new InvalidOperationException(
                "synthetic project discovery failure");
    }

    private sealed class ToggleAwareFactory(string sourcePath)
        : IExclusionAwareLanguageProjectFactory
    {
        public IReadOnlyCollection<string> ProjectMarkers { get; } = ["*.csproj"];

        public bool ShouldFail { get; set; }

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct) =>
            DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct)
        {
            if (ShouldFail)
            {
                throw new InvalidOperationException(
                    "synthetic project discovery failure");
            }
            return Task.FromResult<IReadOnlyList<ILanguageProject>>(
            [
                new TestProject("recovered", [sourcePath]),
            ]);
        }
    }

    private sealed class NonAwareFactory : ILanguageProjectFactory
    {
        public IReadOnlyCollection<string> ProjectMarkers { get; } = ["*.csproj"];

        public bool WasInvoked { get; private set; }

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult<IReadOnlyList<ILanguageProject>>([]);
        }
    }

    private sealed class TestProject(
        string id,
        IReadOnlyCollection<string> paths)
        : ILanguageProject
    {
        public string Id { get; } = id;

        public IReadOnlyCollection<string> FilePaths { get; } = paths;
    }
}
