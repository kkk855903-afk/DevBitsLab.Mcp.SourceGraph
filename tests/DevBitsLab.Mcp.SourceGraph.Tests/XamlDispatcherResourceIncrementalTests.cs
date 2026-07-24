using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class XamlDispatcherResourceIncrementalTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "sourcegraph-xaml-dispatcher-resource-" + Guid.NewGuid().ToString("N"));

    public XamlDispatcherResourceIncrementalTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ResourceContributorChangesFanOutThroughDispatcherAndRefreshStoredConsumers()
    {
        var projectPath = Path.Join(_root, "Fixture.csproj");
        var appPath = Path.Join(_root, "App.xaml");
        var viewPath = Path.Join(_root, "View.xaml");
        var excludedPath = Path.Join(_root, "excluded", "Secret.xaml");
        await File.WriteAllTextAsync(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(appPath, AppResources(duplicate: false));
        await File.WriteAllTextAsync(viewPath, ConsumerView());
        Directory.CreateDirectory(Path.GetDirectoryName(excludedPath)!);
        await File.WriteAllTextAsync(excludedPath, AppResources(duplicate: false));

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory());
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var store = new SqliteGraphStore(Path.Join(_root, "graph.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                ["excluded/**"]),
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
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            var cold = await dispatcher.DispatchAllAsync(host);
            cold.FailedFiles.Should().BeEmpty();
            cold.IndexedFiles.Should().Be(2);
            await AssertResolvedAsync(store, viewPath);

            await File.WriteAllTextAsync(appPath, AppResources(duplicate: true));
            var ambiguous = await dispatcher.DispatchChangedFilesAsync(
                host,
                [appPath, appPath, excludedPath]);

            ambiguous.FailedFiles.Should().BeEmpty();
            ambiguous.IndexedFiles.Should().Be(2,
                "the contributor and its one consumer are each reindexed once");
            ambiguous.SkippedFiles.Should().Be(1,
                "the excluded event remains outside the fanout boundary");
            (await store.GetAllFilesAsync()).Should().NotContain(file =>
                string.Equals(file.Path, excludedPath, StringComparison.OrdinalIgnoreCase));
            await AssertAmbiguousAsync(store, viewPath);

            File.Delete(appPath);
            var deleted = await dispatcher.DispatchChangedFilesAsync(host, [appPath]);

            deleted.FailedFiles.Should().BeEmpty();
            deleted.DeletedFiles.Should().Be(1);
            deleted.IndexedFiles.Should().Be(1,
                "the surviving consumer is refreshed against the rebuilt empty cascade");
            await AssertMissingAsync(store, viewPath);

            await File.WriteAllTextAsync(appPath, AppResources(duplicate: false));
            var restored = await dispatcher.DispatchChangedFilesAsync(host, [appPath]);

            restored.FailedFiles.Should().BeEmpty();
            restored.IndexedFiles.Should().Be(2);
            (await store.GetAllFilesAsync()).Should().HaveCount(2);
            await AssertResolvedAsync(store, viewPath);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeletedConsumerRefreshesMembershipBeforeDeletingFacts()
    {
        var projectPath = Path.Join(_root, "Fixture.csproj");
        var appPath = Path.Join(_root, "App.xaml");
        var deletedViewPath = Path.Join(_root, "DeletedView.xaml");
        var survivingViewPath = Path.Join(_root, "SurvivingView.xaml");
        await File.WriteAllTextAsync(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(appPath, AppResources(duplicate: false));
        await File.WriteAllTextAsync(deletedViewPath, ConsumerView());
        await File.WriteAllTextAsync(survivingViewPath, ConsumerView());

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factory = new ToggleFailingXamlProjectFactory();
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(factory);
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var store = new SqliteGraphStore(Path.Join(_root, "consumer-delete.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()),
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
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            (await dispatcher.DispatchAllAsync(host)).FailedFiles.Should().BeEmpty();
            await AssertResolvedAsync(store, deletedViewPath);
            await AssertResolvedAsync(store, survivingViewPath);
            var priorDeletedHash =
                await store.GetFileContentHashAsync(deletedViewPath);

            File.Delete(deletedViewPath);
            factory.FailDiscovery = true;
            var failed = await dispatcher.DispatchChangedFilesAsync(
                host,
                [deletedViewPath]);

            failed.DeletedFiles.Should().Be(0);
            failed.FailedProjects.Should().ContainSingle();
            host.ProjectByFilePath.Should().ContainKey(deletedViewPath,
                "failed discovery must retain the last complete project map");
            (await store.GetFileContentHashAsync(deletedViewPath))
                .Should().Equal(priorDeletedHash!,
                    "failed discovery must retain the deleted consumer's prior facts");

            factory.FailDiscovery = false;
            var deleted = await dispatcher.DispatchChangedFilesAsync(
                host,
                [deletedViewPath]);

            deleted.FailedFiles.Should().BeEmpty();
            deleted.FailedProjects.Should().BeEmpty();
            deleted.DeletedFiles.Should().Be(1);
            host.ProjectByFilePath.Should().NotContainKey(deletedViewPath);
            host.LanguageProjects
                .OfType<XamlLanguageProject>()
                .Should().OnlyContain(project =>
                    !project.FilePaths.Contains(
                        deletedViewPath,
                        StringComparer.OrdinalIgnoreCase));
            (await store.GetFileContentHashAsync(deletedViewPath)).Should().BeNull();

            await File.WriteAllTextAsync(appPath, AppResources(duplicate: true));
            var resourceEdit = await dispatcher.DispatchChangedFilesAsync(
                host,
                [appPath]);

            resourceEdit.FailedFiles.Should().BeEmpty(
                "the refreshed project membership must not retain the deleted path");
            resourceEdit.IndexedFiles.Should().Be(2,
                "the declaration and surviving consumer are refreshed");
            await AssertAmbiguousAsync(store, survivingViewPath);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ResourceSnapshotRebuildFailureKeepsPriorStoredConsumerFacts()
    {
        var projectPath = Path.Join(_root, "Fixture.csproj");
        var appPath = Path.Join(_root, "App.xaml");
        var viewPath = Path.Join(_root, "View.xaml");
        await File.WriteAllTextAsync(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(appPath, AppResources(duplicate: false));
        await File.WriteAllTextAsync(viewPath, ConsumerView());

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory());
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var store = new SqliteGraphStore(Path.Join(_root, "failure-graph.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()),
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
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            (await dispatcher.DispatchAllAsync(host)).FailedFiles.Should().BeEmpty();
            var priorAppHash = await store.GetFileContentHashAsync(appPath);
            var priorViewHash = await store.GetFileContentHashAsync(viewPath);
            await AssertResolvedAsync(store, viewPath);

            await File.WriteAllTextAsync(
                appPath,
                "<Application xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">");
            var failed = await dispatcher.DispatchChangedFilesAsync(host, [appPath]);

            failed.IndexedFiles.Should().Be(0);
            failed.DeletedFiles.Should().Be(0);
            failed.FailedFiles.Should().ContainSingle(failure =>
                string.Equals(
                    failure.Path,
                    appPath,
                    StringComparison.OrdinalIgnoreCase));
            (await store.GetFileContentHashAsync(appPath)).Should().Equal(priorAppHash!);
            (await store.GetFileContentHashAsync(viewPath)).Should().Equal(priorViewHash!);
            await AssertResolvedAsync(store, viewPath);
            host.ProjectByFilePath[viewPath]
                .Should().BeOfType<XamlLanguageProject>()
                .Subject.ResolveResource("Accent").Status.Should()
                .Be(ResourceResolutionStatus.Resolved,
                    "a failed builder must not publish its partial snapshot");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task LaterProjectRebuildFailureDoesNotPublishEarlierProjectSnapshot()
    {
        var firstRoot = Path.Join(_root, "A");
        var secondRoot = Path.Join(_root, "B");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var firstProjectPath = Path.Join(firstRoot, "Fixture.csproj");
        var firstAppPath = Path.Join(firstRoot, "App.xaml");
        var firstViewPath = Path.Join(firstRoot, "View.xaml");
        var secondProjectPath = Path.Join(secondRoot, "Fixture.csproj");
        var secondAppPath = Path.Join(secondRoot, "App.xaml");
        var secondViewPath = Path.Join(secondRoot, "View.xaml");
        foreach (var projectPath in new[] { firstProjectPath, secondProjectPath })
        {
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        }
        foreach (var appPath in new[] { firstAppPath, secondAppPath })
        {
            await File.WriteAllTextAsync(appPath, AppResources(duplicate: false));
        }
        foreach (var viewPath in new[] { firstViewPath, secondViewPath })
        {
            await File.WriteAllTextAsync(viewPath, ConsumerView());
        }

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory());
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var store = new SqliteGraphStore(Path.Join(_root, "multi-project-failure.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()),
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
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            (await dispatcher.DispatchAllAsync(host)).FailedFiles.Should().BeEmpty();
            await AssertResolvedAsync(
                store,
                firstViewPath,
                "xaml:resource:A/App.xaml#Accent");
            await AssertResolvedAsync(
                store,
                secondViewPath,
                "xaml:resource:B/App.xaml#Accent");
            var firstAppHash = await store.GetFileContentHashAsync(firstAppPath);
            var firstViewHash = await store.GetFileContentHashAsync(firstViewPath);
            var secondAppHash = await store.GetFileContentHashAsync(secondAppPath);
            var secondViewHash = await store.GetFileContentHashAsync(secondViewPath);

            await File.WriteAllTextAsync(
                firstAppPath,
                AppResources(duplicate: true));
            await File.WriteAllTextAsync(
                secondAppPath,
                "<Application xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">");
            var failed = await dispatcher.DispatchChangedFilesAsync(
                host,
                [firstAppPath, secondAppPath]);

            failed.IndexedFiles.Should().Be(0);
            failed.DeletedFiles.Should().Be(0);
            failed.FailedFiles.Should().ContainSingle(failure =>
                string.Equals(
                    failure.Path,
                    secondAppPath,
                    StringComparison.OrdinalIgnoreCase));
            (await store.GetFileContentHashAsync(firstAppPath)).Should()
                .Equal(firstAppHash!);
            (await store.GetFileContentHashAsync(firstViewPath)).Should()
                .Equal(firstViewHash!);
            (await store.GetFileContentHashAsync(secondAppPath)).Should()
                .Equal(secondAppHash!);
            (await store.GetFileContentHashAsync(secondViewPath)).Should()
                .Equal(secondViewHash!);
            host.ProjectByFilePath[firstViewPath]
                .Should().BeOfType<XamlLanguageProject>()
                .Subject.ResolveResource("Accent").Status.Should()
                .Be(ResourceResolutionStatus.Resolved,
                    "no prepared snapshot may publish until every affected project succeeds");
            await AssertResolvedAsync(
                store,
                firstViewPath,
                "xaml:resource:A/App.xaml#Accent");
            await AssertResolvedAsync(
                store,
                secondViewPath,
                "xaml:resource:B/App.xaml#Accent");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SharedContributorEditFansOutToEveryOwningProject()
    {
        var sharedRoot = Path.Join(_root, "Shared");
        var firstRoot = Path.Join(_root, "A");
        var secondRoot = Path.Join(_root, "B");
        Directory.CreateDirectory(sharedRoot);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var sharedPath = Path.Join(sharedRoot, "Colors.xaml");
        var firstViewPath = Path.Join(firstRoot, "View.xaml");
        var secondViewPath = Path.Join(secondRoot, "View.xaml");
        await File.WriteAllTextAsync(
            Path.Join(firstRoot, "Fixture.csproj"),
            SharedProjectFile());
        await File.WriteAllTextAsync(
            Path.Join(secondRoot, "Fixture.csproj"),
            SharedProjectFile());
        await File.WriteAllTextAsync(
            Path.Join(firstRoot, "App.xaml"),
            SharedResourceApp());
        await File.WriteAllTextAsync(
            Path.Join(secondRoot, "App.xaml"),
            SharedResourceApp());
        await File.WriteAllTextAsync(firstViewPath, ConsumerView());
        await File.WriteAllTextAsync(secondViewPath, ConsumerView());
        await File.WriteAllTextAsync(
            sharedPath,
            ResourceDictionary(duplicate: false));

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory());
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var store = new SqliteGraphStore(Path.Join(_root, "shared-owner.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Solutions(
                Array.Empty<string>(),
                Array.Empty<string>()),
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
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            host.LanguageProjects.OfType<XamlLanguageProject>().Should().HaveCount(2);
            (await dispatcher.DispatchAllAsync(host)).FailedFiles.Should().BeEmpty();
            await AssertResolvedAsync(
                store,
                firstViewPath,
                "xaml:resource:Shared/Colors.xaml#Accent");
            await AssertResolvedAsync(
                store,
                secondViewPath,
                "xaml:resource:Shared/Colors.xaml#Accent");

            await File.WriteAllTextAsync(
                sharedPath,
                ResourceDictionary(duplicate: true));
            var changed = await dispatcher.DispatchChangedFilesAsync(
                host,
                [sharedPath]);

            changed.FailedFiles.Should().BeEmpty();
            await AssertAmbiguousAsync(store, firstViewPath);
            await AssertAmbiguousAsync(store, secondViewPath);
            host.LanguageProjects
                .OfType<XamlLanguageProject>()
                .Should().OnlyContain(project =>
                    project.ResolveResource("Accent").Status
                    == ResourceResolutionStatus.Ambiguous);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task OneShotDispatchOrdersSharedDeclarationByEveryOwningProject()
    {
        var sharedRoot = Path.Join(_root, "Shared");
        var firstRoot = Path.Join(_root, "A");
        var secondRoot = Path.Join(_root, "B");
        Directory.CreateDirectory(sharedRoot);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var firstProjectPath = Path.Join(firstRoot, "Fixture.csproj");
        var secondProjectPath = Path.Join(secondRoot, "Fixture.csproj");
        var sharedPath = Path.Join(sharedRoot, "Colors.xaml");
        var firstViewPath = Path.Join(firstRoot, "View.xaml");
        await File.WriteAllTextAsync(firstProjectPath, SharedProjectFile());
        await File.WriteAllTextAsync(secondProjectPath, SharedProjectFile());
        await File.WriteAllTextAsync(
            Path.Join(firstRoot, "App.xaml"),
            SharedResourceApp());
        await File.WriteAllTextAsync(
            Path.Join(secondRoot, "App.xaml"),
            EmptyApp());
        await File.WriteAllTextAsync(firstViewPath, ConsumerView());
        await File.WriteAllTextAsync(
            sharedPath,
            ResourceDictionary(duplicate: false));

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory());
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        await using var store = new SqliteGraphStore(
            Path.Join(_root, "one-shot-shared-owner.db"));
        await store.EnsureSchemaAsync();
        var projectSet = new ScopeProjectSet.Solutions(
            Array.Empty<string>(),
            Array.Empty<string>());
        var discovery = await dispatcher.DiscoverProjectMapAsync(
            _root,
            projectSet,
            "test");

        discovery.Succeeded.Should().BeTrue();
        discovery.Projects.OfType<XamlLanguageProject>().Should().HaveCount(2);
        discovery.ProjectByFilePath[sharedPath].Id.Should().Be(secondProjectPath,
            "the compatibility map intentionally retains only the first owner");

        var result = await dispatcher.DispatchAllForTestAsync(
            store,
            "test",
            _root,
            discovery.ProjectByFilePath,
            Array.Empty<string>(),
            CancellationToken.None,
            projectSet,
            discovery.Projects);

        result.FailedFiles.Should().BeEmpty();
        await AssertResolvedAsync(
            store,
            firstViewPath,
            "xaml:resource:Shared/Colors.xaml#Accent");
    }

    [Fact]
    public async Task SuccessfulCSharpSemanticChangeReindexesXamlConsumers()
    {
        var projectPath = Path.Join(_root, "Fixture.csproj");
        var csharpPath = Path.Join(_root, "Vm.cs");
        var viewPath = Path.Join(_root, "View.xaml");
        const string missingSource =
            "namespace Test { public sealed class Vm { } }";
        const string resolvedSource =
            "namespace Test { public sealed class Vm { public string Existing => \"\"; } }";
        await File.WriteAllTextAsync(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(csharpPath, missingSource);
        await File.WriteAllTextAsync(viewPath, BindingConsumerView());

        using var workspace = new AdhocWorkspace();
        var roslynProjectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(roslynProjectId);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                roslynProjectId,
                VersionStamp.Create(),
                "Fixture",
                "Fixture",
                LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: PlatformReferences()))
            .AddDocument(
                documentId,
                "Vm.cs",
                missingSource,
                filePath: csharpPath);

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory(
            () => solution,
            _ => true));
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var store = new SqliteGraphStore(Path.Join(_root, "csharp-fanout.db"));
        await store.EnsureSchemaAsync();
        var csharpFileId = await store.UpsertFileAsync(
            csharpPath,
            [1],
            DateTimeOffset.UtcNow);
        await store.UpsertSymbolAsync(
            "csharp:P:Test.Vm.Existing",
            new Symbol(
                0,
                "Existing",
                "Test.Vm.Existing",
                "property",
                csharpFileId,
                1,
                1,
                1,
                10,
                "string Existing",
                null));
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Paths(
                ["**/*.csproj"],
                Array.Empty<string>()),
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
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            (await dispatcher.DispatchAllAsync(host)).FailedFiles.Should().BeEmpty();
            await AssertBindingMissingAsync(store, viewPath);

            solution = solution.WithDocumentText(
                documentId,
                SourceText.From(resolvedSource));
            await File.WriteAllTextAsync(csharpPath, resolvedSource);
            var failedRoslynBatch = await dispatcher.DispatchChangedFilesAsync(
                host,
                [csharpPath],
                csharpSemanticUpdateSucceeded: false);

            failedRoslynBatch.IndexedFiles.Should().Be(0,
                "a failed Roslyn update must retain the last successful XAML facts");
            await AssertBindingMissingAsync(store, viewPath);

            var resolved = await dispatcher.DispatchChangedFilesAsync(
                host,
                [csharpPath],
                csharpSemanticUpdateSucceeded: true);

            resolved.FailedFiles.Should().BeEmpty();
            resolved.IndexedFiles.Should().Be(1);
            await AssertBindingResolvedAsync(store, viewPath);

            solution = solution.WithDocumentText(
                documentId,
                SourceText.From(missingSource));
            await File.WriteAllTextAsync(csharpPath, missingSource);
            var missing = await dispatcher.DispatchChangedFilesAsync(
                host,
                [csharpPath],
                csharpSemanticUpdateSucceeded: true);

            missing.FailedFiles.Should().BeEmpty();
            missing.IndexedFiles.Should().Be(1);
            await AssertBindingMissingAsync(store, viewPath);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SharedXamlConsumerWithDivergentOwnersFailsClosed()
    {
        var sharedRoot = Path.Join(_root, "Shared");
        var firstRoot = Path.Join(_root, "A");
        var secondRoot = Path.Join(_root, "B");
        Directory.CreateDirectory(sharedRoot);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var sharedViewPath = Path.Join(sharedRoot, "View.xaml");
        await File.WriteAllTextAsync(
            Path.Join(firstRoot, "Fixture.csproj"),
            SharedViewProjectFile());
        await File.WriteAllTextAsync(
            Path.Join(secondRoot, "Fixture.csproj"),
            SharedViewProjectFile());
        await File.WriteAllTextAsync(
            Path.Join(firstRoot, "App.xaml"),
            AppResources(duplicate: false));
        await File.WriteAllTextAsync(
            Path.Join(secondRoot, "App.xaml"),
            AppResources(duplicate: false));
        await File.WriteAllTextAsync(sharedViewPath, ConsumerView());

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory());
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var store = new SqliteGraphStore(Path.Join(_root, "shared-consumer.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Solutions(
                Array.Empty<string>(),
                Array.Empty<string>()),
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
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            host.LanguageProjects.OfType<XamlLanguageProject>().Should().HaveCount(2);
            (await dispatcher.DispatchAllAsync(host)).FailedFiles.Should().BeEmpty();

            var consumer = await FindConsumerAsync(store, sharedViewPath);
            (await store.ListCalleesAsync(
                    consumer.Id,
                    limit: 20,
                    edgeKind: "uses-resource"))
                .Should().BeEmpty();
            (await store.GetAnnotationsForSymbolAsync(consumer.Id)).Should()
                .ContainSingle(annotation =>
                    annotation.Flavor == "xaml-resource-outcome"
                    && annotation.FullName == "unknown")
                .And.NotContain(annotation =>
                    annotation.Flavor == "xaml-resource-finding");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeclarationConsumersRetryAfterEveryDeclarationSymbolExists()
    {
        var projectPath = Path.Join(_root, "Fixture.csproj");
        var appPath = Path.Join(_root, "App.xaml");
        var colorsPath = Path.Join(_root, "ZColors.xaml");
        await File.WriteAllTextAsync(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(
            appPath,
            AppWithDeclarationConsumer("Accent"));
        await File.WriteAllTextAsync(
            colorsPath,
            SingleResourceDictionary("Accent"));

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory());
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var projectSet = new ScopeProjectSet.Paths(
            ["**/*.csproj"],
            Array.Empty<string>());
        var discovery = await dispatcher.DiscoverProjectMapAsync(
            _root,
            projectSet,
            "test");
        discovery.Succeeded.Should().BeTrue();

        await using (var oneShotStore = new SqliteGraphStore(
                         Path.Join(_root, "declaration-one-shot.db")))
        {
            await oneShotStore.EnsureSchemaAsync();
            var oneShot = await dispatcher.DispatchAllForTestAsync(
                oneShotStore,
                "test",
                _root,
                discovery.ProjectByFilePath,
                Array.Empty<string>(),
                CancellationToken.None,
                projectSet,
                discovery.Projects);

            oneShot.FailedFiles.Should().BeEmpty();
            oneShot.IndexedFiles.Should().Be(2,
                "declaration retries must not double-count physical files");
            await AssertDeclarationResourceEdgeAsync(
                oneShotStore,
                appPath,
                "Accent");
        }

        var liveStore = new SqliteGraphStore(
            Path.Join(_root, "declaration-live.db"));
        await liveStore.EnsureSchemaAsync();
        var scope = new Scope(
            Id: "test",
            Name: "test",
            Root: _root,
            ProjectSet: projectSet,
            Isolated: false,
            LastIndexedAt: DateTimeOffset.MinValue);
        var host = new ScopeHost(
            scope,
            liveStore,
            liveStore.CreateEmbeddingsStore(384),
            new RoslynIndexer(liveStore),
            solutionPath: string.Empty);
        try
        {
            (await dispatcher.BuildProjectMapAsync(host)).Succeeded.Should().BeTrue();
            var cold = await dispatcher.DispatchAllAsync(host);
            cold.FailedFiles.Should().BeEmpty();
            cold.IndexedFiles.Should().Be(2);
            await AssertDeclarationResourceEdgeAsync(
                liveStore,
                appPath,
                "Accent");

            await File.WriteAllTextAsync(
                appPath,
                AppWithDeclarationConsumer("Accent2"));
            await File.WriteAllTextAsync(
                colorsPath,
                SingleResourceDictionary("Accent2"));
            var changed = await dispatcher.DispatchChangedFilesAsync(
                host,
                [appPath, colorsPath]);

            changed.FailedFiles.Should().BeEmpty();
            changed.IndexedFiles.Should().Be(2);
            await AssertDeclarationResourceEdgeAsync(
                liveStore,
                appPath,
                "Accent2");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task AssertResolvedAsync(
        IGraphStore store,
        string viewPath,
        string expectedCanonicalKey = "xaml:resource:App.xaml#Accent")
    {
        var consumer = await FindConsumerAsync(store, viewPath);
        (await store.ListCalleesAsync(
                consumer.Id,
                limit: 20,
                edgeKind: "uses-resource"))
            .Should().ContainSingle(target =>
                target.CanonicalKey == expectedCanonicalKey);
        (await store.GetAnnotationsForSymbolAsync(consumer.Id)).Should()
            .NotContain(annotation =>
                annotation.Flavor == "xaml-resource-finding"
                || annotation.Flavor == "xaml-resource-outcome");
    }

    private static async Task AssertAmbiguousAsync(
        IGraphStore store,
        string viewPath)
    {
        var consumer = await FindConsumerAsync(store, viewPath);
        (await store.ListCalleesAsync(
                consumer.Id,
                limit: 20,
                edgeKind: "uses-resource"))
            .Should().BeEmpty();
        (await store.GetAnnotationsForSymbolAsync(consumer.Id)).Should()
            .ContainSingle(annotation =>
                annotation.Flavor == "xaml-resource-outcome"
                && annotation.FullName == "ambiguous");
    }

    private static async Task AssertMissingAsync(
        IGraphStore store,
        string viewPath)
    {
        var consumer = await FindConsumerAsync(store, viewPath);
        (await store.ListCalleesAsync(
                consumer.Id,
                limit: 20,
                edgeKind: "uses-resource"))
            .Should().BeEmpty();
        (await store.GetAnnotationsForSymbolAsync(consumer.Id)).Should()
            .ContainSingle(annotation =>
                annotation.Flavor == "xaml-resource-finding"
                && annotation.FullName == "XAMLRESOURCE001");
    }

    private static async Task AssertBindingResolvedAsync(
        IGraphStore store,
        string viewPath)
    {
        var consumer = await FindConsumerAsync(store, viewPath);
        (await store.ListCalleesAsync(
                consumer.Id,
                limit: 20,
                edgeKind: "binds-path"))
            .Should().ContainSingle(target =>
                target.CanonicalKey == "csharp:P:Test.Vm.Existing");
        (await store.GetAnnotationsForSymbolAsync(consumer.Id)).Should()
            .NotContain(annotation =>
                annotation.Flavor == "xaml-binding-finding"
                || annotation.Flavor == "xaml-binding-outcome");
    }

    private static async Task AssertBindingMissingAsync(
        IGraphStore store,
        string viewPath)
    {
        var consumer = await FindConsumerAsync(store, viewPath);
        (await store.ListCalleesAsync(
                consumer.Id,
                limit: 20,
                edgeKind: "binds-path"))
            .Should().BeEmpty();
        (await store.GetAnnotationsForSymbolAsync(consumer.Id)).Should()
            .ContainSingle(annotation =>
                annotation.Flavor == "xaml-binding-finding"
                && annotation.FullName == "XAMLBINDING001");
    }

    private static async Task AssertDeclarationResourceEdgeAsync(
        IGraphStore store,
        string appPath,
        string key)
    {
        var alias = (await store.FindSymbolsAsync("Alias"))
            .Single(symbol =>
                string.Equals(
                    symbol.FilePath,
                    appPath,
                    StringComparison.OrdinalIgnoreCase));
        (await store.ListCalleesAsync(
                alias.Id,
                limit: 20,
                edgeKind: "uses-resource"))
            .Should().ContainSingle(target =>
                target.CanonicalKey
                == $"xaml:resource:ZColors.xaml#{key}");
    }

    private static async Task<SymbolHit> FindConsumerAsync(
        IGraphStore store,
        string viewPath) =>
        (await store.FindSymbolsAsync("Consumer"))
        .Single(symbol =>
            symbol.Kind == "xaml-element"
            && string.Equals(
                symbol.FilePath,
                viewPath,
                StringComparison.OrdinalIgnoreCase));

    private static string AppResources(bool duplicate) =>
        $$"""
        <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Application.Resources>
                <ResourceDictionary>
                    <SolidColorBrush x:Key="Accent" Color="Blue" />
        {{(duplicate
            ? "            <SolidColorBrush x:Key=\"Accent\" Color=\"Red\" />"
            : string.Empty)}}
                </ResourceDictionary>
            </Application.Resources>
        </Application>
        """;

    private static string ConsumerView() =>
        """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border x:Name="Consumer"
                    Background="{StaticResource Accent}" />
        </Window>
        """;

    private static string BindingConsumerView() =>
        """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Test"
                x:DataType="vm:Vm">
            <TextBlock x:Name="Consumer"
                       Text="{Binding Existing}" />
        </Window>
        """;

    private static string SharedProjectFile() =>
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <Page Include="../Shared/Colors.xaml" />
          </ItemGroup>
        </Project>
        """;

    private static string SharedViewProjectFile() =>
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <Page Include="../Shared/View.xaml" />
          </ItemGroup>
        </Project>
        """;

    private static string SharedResourceApp() =>
        """
        <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Application.Resources>
                <ResourceDictionary>
                    <ResourceDictionary.MergedDictionaries>
                        <ResourceDictionary Source="../Shared/Colors.xaml" />
                    </ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
            </Application.Resources>
        </Application>
        """;

    private static string EmptyApp() =>
        """
        <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Application.Resources>
                <ResourceDictionary />
            </Application.Resources>
        </Application>
        """;

    private static string AppWithDeclarationConsumer(string key) =>
        $$"""
        <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Application.Resources>
                <ResourceDictionary>
                    <ResourceDictionary.MergedDictionaries>
                        <ResourceDictionary Source="ZColors.xaml" />
                    </ResourceDictionary.MergedDictionaries>
                    <SolidColorBrush x:Key="Alias"
                                     Color="{StaticResource {{key}}}" />
                </ResourceDictionary>
            </Application.Resources>
        </Application>
        """;

    private static string ResourceDictionary(bool duplicate) =>
        $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <SolidColorBrush x:Key="Accent" Color="Blue" />
        {{(duplicate
            ? "    <SolidColorBrush x:Key=\"Accent\" Color=\"Red\" />"
            : string.Empty)}}
        </ResourceDictionary>
        """;

    private static string SingleResourceDictionary(string key) =>
        $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <SolidColorBrush x:Key="{{key}}" Color="Blue" />
        </ResourceDictionary>
        """;

    private static IReadOnlyList<MetadataReference> PlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
         ?? throw new InvalidOperationException(
             "Trusted platform assemblies are unavailable."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private sealed class ToggleFailingXamlProjectFactory
        : IExclusionAwareLanguageProjectFactory
    {
        private readonly XamlLanguageProjectFactory _inner = new();

        public IReadOnlyCollection<string> ProjectMarkers =>
            _inner.ProjectMarkers;

        public bool FailDiscovery { get; set; }

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            CancellationToken ct) =>
            DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

        public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
            string repoRoot,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct)
        {
            if (FailDiscovery)
            {
                throw new InvalidOperationException(
                    "Synthetic XAML discovery failure.");
            }

            return _inner.DiscoverAsync(repoRoot, excludePatterns, ct);
        }
    }
}
