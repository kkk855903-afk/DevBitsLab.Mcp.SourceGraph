using System.Security.Cryptography;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using SdkEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Sdk.EvidenceConfidence;
using SdkSourceLocation = DevBitsLab.Mcp.SourceGraph.Sdk.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class LanguageIndexerIncrementalTests : IDisposable
{
    private const string Extension = ".medidx";
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "sourcegraph-language-incremental-" + Guid.NewGuid().ToString("N"));

    public LanguageIndexerIncrementalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ColdDispatch_discoversRegisteredExtension_withoutSolution()
    {
        var sourcePath = await PlantAsync(Path.Join(_root, "contracts", "service.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            dispatcher.RegisteredSourceExtensions.Should().Equal(Extension);

            await dispatcher.BuildProjectMapAsync(host);
            var indexed = await dispatcher.DispatchAllAsync(host);

            indexed.IndexedFiles.Should().Be(1);
            indexed.UsableOutputFiles.Should().Be(1);
            indexed.FailedFiles.Should().BeEmpty();
            indexer.Paths.Should().Equal(sourcePath);
            (await host.Store.GetAllFilesAsync()).Should().ContainSingle(file => file.Path == sourcePath);
            (await host.Store.GetAllSymbolKeysAsync()).Should().HaveCount(2);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task IncrementalDispatch_replacesEmptyResults_andDeleteRemovesAllOwnedGraphState()
    {
        var sourcePath = await PlantAsync(Path.Join(_root, "src", "service.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            var created = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);
            created.Should().BeEquivalentTo(new
            {
                IndexedFiles = 1,
                UsableOutputFiles = 1,
                DeletedFiles = 0,
                SkippedFiles = 0,
            });
            created.FailedFiles.Should().BeEmpty();

            var firstKeys = await host.Store.GetAllSymbolKeysAsync();
            firstKeys.Should().HaveCount(2);
            var firstSource = firstKeys.Single(row => row.CanonicalKey.EndsWith("/source/v1", StringComparison.Ordinal));
            var firstTarget = firstKeys.Single(row => row.CanonicalKey.EndsWith("/target/v1", StringComparison.Ordinal));
            (await host.Store.ListEdgeEvidenceAsync(
                    firstSource.Id,
                    firstTarget.Id,
                    EdgeKinds.Calls))
                .Should().ContainSingle();
            (await host.Store.GetAnnotationsForSymbolAsync(firstSource.Id))
                .Should().ContainSingle(annotation => annotation.Name == "Indexed");
            (await host.Store.FindReferencesAsync(firstTarget.Id))
                .Should().ContainSingle(reference => reference.FilePath == sourcePath);

            await File.WriteAllTextAsync(sourcePath, "empty");
            var emptied = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            emptied.Should().BeEquivalentTo(new
            {
                IndexedFiles = 1,
                UsableOutputFiles = 0,
                DeletedFiles = 0,
                SkippedFiles = 0,
            });
            emptied.FailedFiles.Should().BeEmpty();
            (await host.Store.GetAllFilesAsync()).Should().ContainSingle(file => file.Path == sourcePath);
            (await host.Store.GetAllSymbolKeysAsync()).Should().BeEmpty(
                "an empty successful index result replaces the previous declarations");
            (await host.Store.ListEdgeEvidenceAsync(
                    firstSource.Id,
                    firstTarget.Id,
                    EdgeKinds.Calls))
                .Should().BeEmpty();
            (await host.Store.GetAnnotationsForSymbolAsync(firstSource.Id)).Should().BeEmpty();
            (await host.Store.FindReferencesAsync(firstTarget.Id)).Should().BeEmpty();

            await File.WriteAllTextAsync(sourcePath, "v2");
            await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);
            var secondKeys = await host.Store.GetAllSymbolKeysAsync();
            var secondSource = secondKeys.Single(row => row.CanonicalKey.EndsWith("/source/v2", StringComparison.Ordinal));
            var secondTarget = secondKeys.Single(row => row.CanonicalKey.EndsWith("/target/v2", StringComparison.Ordinal));

            File.Delete(sourcePath);
            var deleted = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            deleted.Should().BeEquivalentTo(new
            {
                IndexedFiles = 0,
                UsableOutputFiles = 0,
                DeletedFiles = 1,
                SkippedFiles = 0,
            });
            deleted.FailedFiles.Should().BeEmpty();
            (await host.Store.GetAllFilesAsync()).Should().BeEmpty();
            (await host.Store.GetAllSymbolKeysAsync()).Should().BeEmpty();
            (await host.Store.ListEdgeEvidenceAsync(
                    secondSource.Id,
                    secondTarget.Id,
                    EdgeKinds.Calls))
                .Should().BeEmpty("file deletion removes the final occurrence and logical edge");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task IncrementalDispatch_treatsRenameAsDeleteThenCreate()
    {
        var oldPath = await PlantAsync(Path.Join(_root, "src", "old.medidx"), "v1");
        var newPath = Path.Join(_root, "src", "new.medidx");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            await dispatcher.DispatchChangedFilesAsync(host, [oldPath]);
            File.Move(oldPath, newPath);

            var renamed = await dispatcher.DispatchChangedFilesAsync(host, [oldPath, newPath]);

            renamed.Should().BeEquivalentTo(new
            {
                IndexedFiles = 1,
                UsableOutputFiles = 1,
                DeletedFiles = 1,
                SkippedFiles = 0,
            });
            renamed.FailedFiles.Should().BeEmpty();
            (await host.Store.GetAllFilesAsync())
                .Select(file => file.Path)
                .Should().Equal(newPath);
            (await host.Store.GetAllSymbolKeysAsync())
                .Should().OnlyContain(row => row.CanonicalKey.Contains("/new/", StringComparison.Ordinal));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task IncrementalDispatch_indexerFailure_preservesLastSuccessfulGraph()
    {
        var sourcePath = await PlantAsync(Path.Join(_root, "src", "stable.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);
            var priorHash = await host.Store.GetFileContentHashAsync(sourcePath);
            var priorKeys = await host.Store.GetAllSymbolKeysAsync();

            await File.WriteAllTextAsync(sourcePath, "throw");
            var failed = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            failed.Should().BeEquivalentTo(new
            {
                IndexedFiles = 0,
                UsableOutputFiles = 0,
                DeletedFiles = 0,
                SkippedFiles = 1,
            });
            failed.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains("synthetic index failure", StringComparison.Ordinal));
            (await host.Store.GetFileContentHashAsync(sourcePath)).Should().Equal(priorHash);
            (await host.Store.GetAllSymbolKeysAsync()).Should().BeEquivalentTo(priorKeys);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task IncrementalDispatch_storageFailure_rollsBackShaAndEveryFileFact()
    {
        var sourcePath = await PlantAsync(Path.Join(_root, "src", "atomic.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);
            var priorHash = await host.Store.GetFileContentHashAsync(sourcePath);
            var priorKeys = await host.Store.GetAllSymbolKeysAsync();
            var priorSource = priorKeys.Single(row =>
                row.CanonicalKey.EndsWith("/source/v1", StringComparison.Ordinal));
            var priorTarget = priorKeys.Single(row =>
                row.CanonicalKey.EndsWith("/target/v1", StringComparison.Ordinal));

            // This is absolute and range-valid, so event compilation succeeds. Storage detects
            // the producing-file/path mismatch only after the replacement transaction has
            // upserted the new file row and symbols; rollback must restore all prior facts.
            await File.WriteAllTextAsync(sourcePath, "bad-evidence");
            var failed = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            failed.IndexedFiles.Should().Be(0);
            failed.UsableOutputFiles.Should().Be(0);
            failed.SkippedFiles.Should().Be(1);
            failed.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains("Evidence path does not match", StringComparison.Ordinal));
            (await host.Store.GetFileContentHashAsync(sourcePath)).Should().Equal(priorHash);
            (await host.Store.GetAllSymbolKeysAsync()).Should().BeEquivalentTo(priorKeys);
            (await host.Store.ListEdgeEvidenceAsync(
                    priorSource.Id,
                    priorTarget.Id,
                    EdgeKinds.Calls))
                .Should().ContainSingle();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task IncrementalDispatch_evidenceLessCrossFileEdge_isOwnedAndRemovedByProducingFile()
    {
        var declarationsPath =
            await PlantAsync(Path.Join(_root, "src", "ADeclarations.medidx"), "declarations");
        var consumerPath =
            await PlantAsync(Path.Join(_root, "src", "ZConsumer.medidx"), "external-edge");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            await dispatcher.DispatchChangedFilesAsync(host, [consumerPath, declarationsPath]);
            var keys = await host.Store.GetAllSymbolKeysAsync();
            var source = keys.Single(row => row.CanonicalKey == "proto:test/shared/source");
            var target = keys.Single(row => row.CanonicalKey == "proto:test/shared/target");
            var consumerFile = (await host.Store.GetAllFilesAsync())
                .Single(file => file.Path == consumerPath);

            var evidence = await host.Store.ListEdgeEvidenceAsync(
                source.Id,
                target.Id,
                EdgeKinds.Calls);
            evidence.Should().ContainSingle();
            evidence[0].ProducingFileId.Should().Be(
                consumerFile.Id,
                "fallback evidence must retain the file that emitted the edge as its owner");
            evidence[0].Location.FilePath.Should().Be(consumerPath);

            await File.WriteAllTextAsync(consumerPath, "empty");
            var emptied = await dispatcher.DispatchChangedFilesAsync(host, [consumerPath]);

            emptied.IndexedFiles.Should().Be(1);
            emptied.UsableOutputFiles.Should().Be(0);
            (await host.Store.ListEdgeEvidenceAsync(
                    source.Id,
                    target.Id,
                    EdgeKinds.Calls))
                .Should().BeEmpty(
                    "replacing the consumer file must remove its evidence-less outgoing edge");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ColdDispatch_reportsFailuresAndDoesNotClaimUsableOutput()
    {
        var sourcePath = await PlantAsync(Path.Join(_root, "src", "broken.medidx"), "throw");
        var dispatcher = CreateDispatcher(new MutableLanguageIndexer());
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            var result = await dispatcher.DispatchAllAsync(host);

            result.IndexedFiles.Should().Be(0);
            result.UsableOutputFiles.Should().Be(0);
            result.SkippedFiles.Should().Be(1);
            result.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains("synthetic index failure", StringComparison.Ordinal));
            (await host.Store.GetAllFilesAsync()).Should().BeEmpty();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task OneShotDispatch_reportsPerFileFailure()
    {
        var sourcePath = await PlantAsync(Path.Join(_root, "src", "oneshot.medidx"), "throw");
        var dispatcher = CreateDispatcher(new MutableLanguageIndexer());
        await using var store = new SqliteGraphStore(Path.Join(_root, "oneshot.db"));
        await store.EnsureSchemaAsync();

        var result = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase));

        result.IndexedFiles.Should().Be(0);
        result.UsableOutputFiles.Should().Be(0);
        result.SkippedFiles.Should().Be(1);
        result.FailedFiles.Should().ContainSingle(failure =>
            failure.Path == sourcePath
                && failure.Reason.Contains("synthetic index failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneShotDispatch_honorsPositiveProjectSet()
    {
        await PlantAsync(Path.Join(_root, "allowed", "Allowed.csproj"), "<Project />");
        await PlantAsync(Path.Join(_root, "vendor", "Vendor.csproj"), "<Project />");
        var allowed = await PlantAsync(Path.Join(_root, "allowed", "service.medidx"), "v1");
        await PlantAsync(Path.Join(_root, "vendor", "service.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        await using var store = new SqliteGraphStore(Path.Join(_root, "oneshot-scope.db"));
        await store.EnsureSchemaAsync();
        var projectSet = new ScopeProjectSet.Paths(
            ["allowed/**/*.csproj"],
            Array.Empty<string>());

        var result = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase),
            projectSet.Exclude,
            ct: CancellationToken.None,
            projectSet: projectSet);

        result.IndexedFiles.Should().Be(1);
        result.FailedFiles.Should().BeEmpty();
        indexer.Paths.Should().Equal(allowed);
        (await store.GetAllFilesAsync()).Select(file => file.Path).Should().Equal(allowed);
    }

    [Fact]
    public async Task PathsScope_coldAndLiveDispatchNeverCrossPositiveBoundary()
    {
        await PlantAsync(Path.Join(_root, "allowed", "Allowed.csproj"), "<Project />");
        await PlantAsync(Path.Join(_root, "vendor", "Vendor.csproj"), "<Project />");
        var allowed = await PlantAsync(Path.Join(_root, "allowed", "service.medidx"), "v1");
        var outsideSelection =
            await PlantAsync(Path.Join(_root, "vendor", "service.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(
            solutionPath: string.Empty,
            globs: ["allowed/**/*.csproj"]);

        try
        {
            var cold = await dispatcher.DispatchAllAsync(host);

            cold.IndexedFiles.Should().Be(1);
            indexer.Paths.Should().Equal(allowed);
            (await host.Store.GetAllFilesAsync())
                .Select(file => file.Path)
                .Should().Equal(allowed);

            indexer.Paths.Clear();
            await File.WriteAllTextAsync(outsideSelection, "v2");
            var live = await dispatcher.DispatchChangedFilesAsync(host, [outsideSelection]);

            live.IndexedFiles.Should().Be(0);
            live.SkippedFiles.Should().Be(1);
            live.FailedFiles.Should().BeEmpty();
            indexer.Paths.Should().BeEmpty();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task IncrementalDispatch_rejectsScopePrivacyAndOutsidePaths_beforeIndexer()
    {
        var allowed = await PlantAsync(Path.Join(_root, "src", "allowed.medidx"), "v1");
        var excluded = await PlantAsync(Path.Join(_root, "generated", "hidden.medidx"), "v1");
        var privatePath = await PlantAsync(Path.Join(_root, "PatientData", "hidden.medidx"), "v1");
        var outsideRoot = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-language-outside-" + Guid.NewGuid().ToString("N"));
        var outside = await PlantAsync(Path.Join(outsideRoot, "outside.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var dispatcher = CreateDispatcher(indexer);
        var host = await CreateHostAsync(solutionPath: string.Empty, excludes: ["generated/**"]);

        try
        {
            var result = await dispatcher.DispatchChangedFilesAsync(
                host,
                [allowed, excluded, privatePath, outside]);

            result.Should().BeEquivalentTo(new
            {
                IndexedFiles = 1,
                UsableOutputFiles = 1,
                DeletedFiles = 0,
                SkippedFiles = 3,
            });
            result.FailedFiles.Should().BeEmpty();
            indexer.Paths.Should().Equal(allowed);
            (await host.Store.GetAllFilesAsync())
                .Select(file => file.Path)
                .Should().Equal(allowed);
        }
        finally
        {
            await host.DisposeAsync();
            try { Directory.Delete(outsideRoot, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task IncrementalDispatch_rejectsPhysicalDirectoryLinkEscape()
    {
        var allowed = await PlantAsync(Path.Join(_root, "src", "allowed.medidx"), "v1");
        var outsideRoot = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-language-link-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        try
        {
            await PlantAsync(Path.Join(outsideRoot, "outside.medidx"), "v1");
            var link = Path.Join(_root, "src", "external");
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(link, outsideRoot),
                "This environment does not permit symbolic-link or junction creation.");

            var linkedPath = Path.Join(link, "outside.medidx");
            var indexer = new MutableLanguageIndexer();
            var dispatcher = CreateDispatcher(indexer);
            var host = await CreateHostAsync(solutionPath: string.Empty);
            try
            {
                var result = await dispatcher.DispatchChangedFilesAsync(
                    host,
                    [allowed, linkedPath]);

                result.Should().BeEquivalentTo(new
                {
                    IndexedFiles = 1,
                    UsableOutputFiles = 1,
                    DeletedFiles = 0,
                    SkippedFiles = 1,
                });
                result.FailedFiles.Should().BeEmpty();
                indexer.Paths.Should().Equal(allowed);
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(outsideRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ColdAndLiveDispatch_prioritizeProjectDeclarationFiles_beforePathOrder()
    {
        var consumer = await PlantAsync(Path.Join(_root, "ui", "AView.medidx"), "v1");
        var declaration = await PlantAsync(Path.Join(_root, "ui", "ZResources.medidx"), "v1");
        var indexer = new MutableLanguageIndexer();
        var project = new DeclarationFirstProject(
            [consumer, declaration],
            [declaration]);
        var factory = new FixedProjectFactory(project);
        var dispatcher = CreateDispatcher(indexer, factory);
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            await dispatcher.BuildProjectMapAsync(host);
            await dispatcher.DispatchAllAsync(host);
            indexer.Paths.Should().Equal(
                [declaration, consumer],
                "declaration priority must override reverse alphabetical file names");

            indexer.Paths.Clear();
            await File.WriteAllTextAsync(consumer, "v2");
            await File.WriteAllTextAsync(declaration, "v2");
            await dispatcher.DispatchChangedFilesAsync(host, [consumer, declaration]);

            indexer.Paths.Should().Equal(
                [declaration, consumer],
                "watcher batch order must use the same declaration-first rule as cold discovery");
            factory.DiscoveryCount.Should().Be(
                1,
                "ordinary source edits reuse the last complete heavy project instance");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildProjectMap_propagatesCancellation()
    {
        var dispatcher = CreateDispatcher(
            new MutableLanguageIndexer(),
            new CancelledProjectFactory());
        var host = await CreateHostAsync(solutionPath: string.Empty);

        try
        {
            Func<Task> act = () => dispatcher.BuildProjectMapAsync(host);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private LanguageIndexerDispatcher CreateDispatcher(
        ILanguageIndexer indexer,
        ILanguageProjectFactory? factory = null)
    {
        var registry = new LanguageIndexerRegistry();
        registry.Register(indexer);
        var factories = new LanguageProjectFactoryRegistry();
        if (factory is not null)
        {
            factories.Register(factory);
        }
        return new LanguageIndexerDispatcher(registry, factories);
    }

    private async Task<ScopeHost> CreateHostAsync(
        string solutionPath,
        IReadOnlyList<string>? excludes = null,
        IReadOnlyList<string>? globs = null)
    {
        if (globs is null)
        {
            await PlantAsync(Path.Join(_root, "Test.csproj"), "<Project />");
        }
        var store = new SqliteGraphStore(
            Path.Join(_root, "graph-" + Guid.NewGuid().ToString("N") + ".db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Paths(
                Globs: globs ?? ["**/*.csproj"],
                Exclude: excludes ?? Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.MinValue);
        return new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            solutionPath);
    }

    private static async Task<string> PlantAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private sealed class MutableLanguageIndexer : ILanguageIndexer
    {
        public IReadOnlyCollection<string> FileExtensions { get; } = [Extension];

        public List<string> Paths { get; } = new();

        public Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
        {
            Paths.Add(ctx.FilePath);
            var version = Encoding.UTF8.GetString(ctx.Contents).Trim();
            if (version == "throw")
            {
                throw new InvalidOperationException("synthetic index failure");
            }
            if (version == "empty")
            {
                return Task.FromResult<IReadOnlyList<IndexEvent>>(Array.Empty<IndexEvent>());
            }
            if (version == "declarations")
            {
                return Task.FromResult<IReadOnlyList<IndexEvent>>(
                [
                    new IndexEvent.SymbolDeclared(
                        "proto:test/shared/source",
                        "SharedSource",
                        "Shared.Source",
                        SymbolKinds.Message,
                        1,
                        1,
                        1,
                        2),
                    new IndexEvent.SymbolDeclared(
                        "proto:test/shared/target",
                        "SharedTarget",
                        "Shared.Target",
                        SymbolKinds.Message,
                        1,
                        3,
                        1,
                        4),
                ]);
            }
            if (version == "external-edge")
            {
                return Task.FromResult<IReadOnlyList<IndexEvent>>(
                [
                    new IndexEvent.EdgeEmitted(
                        "proto:test/shared/source",
                        "proto:test/shared/target",
                        EdgeKinds.Calls),
                ]);
            }

            var stem = Path.GetFileNameWithoutExtension(ctx.FilePath);
            var sourceKey = $"proto:test/{stem}/source/{version}";
            var targetKey = $"proto:test/{stem}/target/{version}";
            var evidencePath = version == "bad-evidence"
                ? ctx.FilePath + ".different"
                : ctx.FilePath;
            IReadOnlyList<IndexEvent> events =
            [
                new IndexEvent.SymbolDeclared(
                    sourceKey,
                    "Source",
                    $"{stem}.Source",
                    SymbolKinds.Message,
                    1,
                    1,
                    1,
                    2),
                new IndexEvent.SymbolDeclared(
                    targetKey,
                    "Target",
                    $"{stem}.Target",
                    SymbolKinds.Message,
                    1,
                    3,
                    1,
                    4),
                new IndexEvent.EdgeEmitted(sourceKey, targetKey, EdgeKinds.Calls)
                {
                    Evidence = new EdgeEvidence(
                        new SdkSourceLocation(evidencePath, 1, 1, 1, 2),
                        SdkEvidenceConfidence.Exact,
                        "test-language-indexer"),
                },
                new IndexEvent.AnnotationAttached(
                    sourceKey,
                    "Indexed",
                    "test-annotation",
                    fullName: "Test.Indexed"),
                new IndexEvent.ReferenceFound(targetKey, 1, 3, "read"),
                new IndexEvent.FileScanned(
                    ctx.FilePath,
                    SHA256.HashData(ctx.Contents)),
            ];
            return Task.FromResult(events);
        }
    }

    private sealed class FixedProjectFactory(ILanguageProject project)
        : IExclusionAwareLanguageProjectFactory
    {
        public IReadOnlyCollection<string> ProjectMarkers { get; } = ["*.medidx"];

        public int DiscoveryCount { get; private set; }

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct) =>
            DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct)
        {
            DiscoveryCount++;
            return Task.FromResult<IReadOnlyList<ILanguageProject>>([project]);
        }
    }

    private sealed class CancelledProjectFactory
        : IExclusionAwareLanguageProjectFactory
    {
        public IReadOnlyCollection<string> ProjectMarkers { get; } = ["*.medidx"];

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct) =>
            DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct) =>
            throw new OperationCanceledException("synthetic factory cancellation", ct);
    }

    private sealed class DeclarationFirstProject(
        IReadOnlyCollection<string> filePaths,
        IReadOnlyCollection<string> declarationFilePaths)
        : IDeclarationFirstLanguageProject
    {
        public string Id { get; } = "declaration-first";

        public IReadOnlyCollection<string> FilePaths { get; } = filePaths;

        public IReadOnlyCollection<string> DeclarationFilePaths { get; } =
            declarationFilePaths;
    }
}
