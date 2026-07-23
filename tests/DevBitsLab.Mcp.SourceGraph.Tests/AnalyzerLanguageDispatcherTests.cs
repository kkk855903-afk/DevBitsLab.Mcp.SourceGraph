using System.Reflection;
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

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class AnalyzerLanguageDispatcherTests : IDisposable
{
    private const string Extension = ".anidx";
    private readonly string _root =
        Path.Join(
            Path.GetTempPath(),
            "sourcegraph-analyzer-language-" + Guid.NewGuid().ToString("N"));

    public AnalyzerLanguageDispatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task LiveEdit_atomicallyReplacesLanguageAndAnalyzerFacts()
    {
        var sourcePath = await PlantAsync("src/service.anidx", "v1");
        var (pipeline, _) = CreatePipeline(new VersionedAnalyzer());
        var dispatcher = CreateDispatcher(pipeline);
        var host = await CreateHostAsync();

        try
        {
            var first = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            first.IndexedFiles.Should().Be(1);
            first.FailedFiles.Should().BeEmpty();
            (await host.Store.GetAllSymbolKeysAsync())
                .Select(row => row.CanonicalKey)
                .Should().BeEquivalentTo(
                [
                    "csharp:T:Analyzer.service.Language.v1",
                    "csharp:T:Analyzer.service.Plugin.v1",
                ]);

            await File.WriteAllTextAsync(sourcePath, "v2");
            var second = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            second.IndexedFiles.Should().Be(1);
            second.UsableOutputFiles.Should().Be(1);
            second.FailedFiles.Should().BeEmpty();
            var keys = await host.Store.GetAllSymbolKeysAsync();
            keys.Select(row => row.CanonicalKey).Should().BeEquivalentTo(
            [
                "csharp:T:Analyzer.service.Language.v2",
                "csharp:T:Analyzer.service.Plugin.v2",
            ]);
            var pluginSymbol = keys.Single(row =>
                row.CanonicalKey == "csharp:T:Analyzer.service.Plugin.v2");
            (await host.Store.GetAnnotationsForSymbolAsync(pluginSymbol.Id))
                .Should().ContainSingle(annotation => annotation.Name == "Analyzed");
            (await host.Store.FindReferencesAsync(pluginSymbol.Id))
                .Should().ContainSingle(reference => reference.FilePath == sourcePath);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnalyzerStorageFailure_rollsBackShaAndCombinedFileFacts()
    {
        var sourcePath = await PlantAsync("src/atomic.anidx", "v1");
        var (pipeline, _) = CreatePipeline(new VersionedAnalyzer());
        var dispatcher = CreateDispatcher(pipeline);
        var host = await CreateHostAsync();

        try
        {
            await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);
            var priorHash = await host.Store.GetFileContentHashAsync(sourcePath);
            var priorKeys = await host.Store.GetAllSymbolKeysAsync();

            await File.WriteAllTextAsync(sourcePath, "bad-evidence");
            var failed = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            failed.IndexedFiles.Should().Be(0);
            failed.SkippedFiles.Should().Be(1);
            failed.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains(
                    "Evidence path does not match",
                    StringComparison.Ordinal));
            (await host.Store.GetFileContentHashAsync(sourcePath)).Should().Equal(priorHash);
            (await host.Store.GetAllSymbolKeysAsync()).Should().BeEquivalentTo(priorKeys);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ThrowingAnalyzer_discardsItsPartialEvents_andLaterAnalyzerStillCommits()
    {
        var sourcePath = await PlantAsync("src/isolation.anidx", "v1");
        var (pipeline, record) = CreatePipeline(
            new EmitThenThrowAnalyzer(),
            new SuccessfulIsolationAnalyzer());
        var dispatcher = CreateDispatcher(pipeline);
        var host = await CreateHostAsync();

        try
        {
            var result = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            result.IndexedFiles.Should().Be(1);
            result.FailedFiles.Should().BeEmpty();
            record.Status.Should().Be(PluginStatus.Failed);
            var keys = (await host.Store.GetAllSymbolKeysAsync())
                .Select(row => row.CanonicalKey)
                .ToArray();
            keys.Should().Contain("csharp:T:Analyzer.Isolation.Success");
            keys.Should().NotContain("csharp:T:Analyzer.Isolation.Partial");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TimedOutAnalyzer_discardsItsPartialEvents_andLaterAnalyzerStillCommits()
    {
        var sourcePath = await PlantAsync("src/timeout.anidx", "v1");
        var (pipeline, record) = CreatePipeline(
            TimeSpan.FromMilliseconds(25),
            new EmitThenWaitForCancellationAnalyzer(),
            new SuccessfulIsolationAnalyzer());
        var dispatcher = CreateDispatcher(pipeline);
        var host = await CreateHostAsync();

        try
        {
            var result = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            result.IndexedFiles.Should().Be(1);
            result.FailedFiles.Should().BeEmpty();
            record.Status.Should().Be(PluginStatus.Failed);
            record.StatusMessage.Should().Contain("timeout");
            var keys = (await host.Store.GetAllSymbolKeysAsync())
                .Select(row => row.CanonicalKey)
                .ToArray();
            keys.Should().Contain("csharp:T:Analyzer.Isolation.Success");
            keys.Should().NotContain("csharp:T:Analyzer.Isolation.TimeoutPartial");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SelfCancelledAnalyzer_discardsItsPartialEvents_andLaterAnalyzerStillCommits()
    {
        var sourcePath = await PlantAsync("src/self-cancel.anidx", "v1");
        var (pipeline, record) = CreatePipeline(
            new EmitThenSelfCancelAnalyzer(),
            new SuccessfulIsolationAnalyzer());
        var dispatcher = CreateDispatcher(pipeline);
        var host = await CreateHostAsync();

        try
        {
            var result = await dispatcher.DispatchChangedFilesAsync(host, [sourcePath]);

            result.IndexedFiles.Should().Be(1);
            result.FailedFiles.Should().BeEmpty();
            record.Status.Should().Be(PluginStatus.Failed);
            record.StatusMessage.Should().Contain("cancelled itself");
            var keys = (await host.Store.GetAllSymbolKeysAsync())
                .Select(row => row.CanonicalKey)
                .ToArray();
            keys.Should().Contain("csharp:T:Analyzer.Isolation.Success");
            keys.Should().NotContain("csharp:T:Analyzer.Isolation.SelfCancelledPartial");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task LegacyCSharpAnalyzerPath_isolatesSelfCancellation_andContinues()
    {
        var sourcePath = await PlantAsync("src/self-cancel.cs", "class C {}");
        var (pipeline, record) = CreatePipeline(
            new EmitThenSelfCancelAnalyzer(),
            new SuccessfulIsolationAnalyzer());
        var host = await CreateHostAsync();
        try
        {
            var contents = Encoding.UTF8.GetBytes("class C {}");
            var fileId = await host.Store.UpsertFileAsync(
                sourcePath,
                SHA256.HashData(contents),
                DateTimeOffset.UtcNow);

            await pipeline.RunAsync(
                host.Store,
                fileId,
                sourcePath,
                contents,
                "test",
                _root,
                Array.Empty<IndexEvent>(),
                new Dictionary<string, long>(StringComparer.Ordinal));

            record.Status.Should().Be(PluginStatus.Failed);
            var keys = (await host.Store.GetAllSymbolKeysAsync())
                .Select(row => row.CanonicalKey)
                .ToArray();
            keys.Should().Contain("csharp:T:Analyzer.Isolation.Success");
            keys.Should().NotContain("csharp:T:Analyzer.Isolation.SelfCancelledPartial");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task LegacyCSharpAnalyzerPath_propagatesCallerCancellation()
    {
        var sourcePath = await PlantAsync("src/cancel.cs", "class C {}");
        var (pipeline, record) = CreatePipeline(new CancellationAnalyzer());
        var host = await CreateHostAsync();
        try
        {
            var fileId = await host.Store.UpsertFileAsync(
                sourcePath,
                SHA256.HashData(Encoding.UTF8.GetBytes("class C {}")),
                DateTimeOffset.UtcNow);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = () => pipeline.RunAsync(
                host.Store,
                fileId,
                sourcePath,
                Encoding.UTF8.GetBytes("class C {}"),
                "test",
                _root,
                Array.Empty<IndexEvent>(),
                new Dictionary<string, long>(StringComparer.Ordinal),
                cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            record.Status.Should().Be(
                PluginStatus.Loaded,
                "caller cancellation is not an analyzer failure");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private LanguageIndexerDispatcher CreateDispatcher(AnalyzerPipeline pipeline)
    {
        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new AnalyzerTestLanguageIndexer());
        return new LanguageIndexerDispatcher(
            indexers,
            new LanguageProjectFactoryRegistry(),
            analyzerPipeline: pipeline);
    }

    private (AnalyzerPipeline Pipeline, PluginRecord Record) CreatePipeline(
        params ICodeAnalyzer[] analyzers) =>
        CreatePipeline(perDocumentTimeout: null, analyzers);

    private (AnalyzerPipeline Pipeline, PluginRecord Record) CreatePipeline(
        TimeSpan? perDocumentTimeout,
        params ICodeAnalyzer[] analyzers)
    {
        var host = new PluginHost(_root, Array.Empty<PluginRef>());
        var record = new PluginRecord("test-analyzers", null, "test", false);
        foreach (var analyzer in analyzers)
        {
            record.Analyzers.Add(analyzer);
        }

        var pluginsField = typeof(PluginHost).GetField(
            "_plugins",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginHost plugin collection was not found.");
        ((List<PluginRecord>)pluginsField.GetValue(host)!).Add(record);
        return (
            new AnalyzerPipeline(
                host,
                perDocumentTimeout: perDocumentTimeout),
            record);
    }

    private async Task<ScopeHost> CreateHostAsync()
    {
        await PlantAsync("Test.csproj", "<Project />");
        var store = new SqliteGraphStore(
            Path.Join(_root, "graph-" + Guid.NewGuid().ToString("N") + ".db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            "test",
            "test",
            _root,
            new ScopeProjectSet.Paths(["**/*.csproj"], Array.Empty<string>()),
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

    private sealed class AnalyzerTestLanguageIndexer : ILanguageIndexer
    {
        public IReadOnlyCollection<string> FileExtensions { get; } = [Extension];

        public Task<IReadOnlyList<IndexEvent>> IndexAsync(
            IndexContext ctx,
            CancellationToken ct)
        {
            var version = ctx.GetText().Trim();
            var stem = Path.GetFileNameWithoutExtension(ctx.FilePath);
            var key = $"csharp:T:Analyzer.{stem}.Language.{version}";
            IReadOnlyList<IndexEvent> events =
            [
                new IndexEvent.SymbolDeclared(
                    key,
                    "Language",
                    $"Analyzer.{stem}.Language.{version}",
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

    private sealed class VersionedAnalyzer : ICodeAnalyzer
    {
        public string Name => "versioned";

        public Task AnalyzeAsync(
            AnalyzerContext ctx,
            IGraphEmitter emitter,
            CancellationToken ct)
        {
            var version = ctx.GetText().Trim();
            var stem = Path.GetFileNameWithoutExtension(ctx.FilePath);
            var languageKey = ctx.IndexerEvents
                .OfType<IndexEvent.SymbolDeclared>()
                .Single()
                .CanonicalKey;
            var pluginKey = $"csharp:T:Analyzer.{stem}.Plugin.{version}";
            emitter.EmitSymbol(new IndexEvent.SymbolDeclared(
                pluginKey,
                "Plugin",
                $"Analyzer.{stem}.Plugin.{version}",
                SymbolKinds.Class,
                1,
                3,
                1,
                4));
            var evidencePath = version == "bad-evidence"
                ? ctx.FilePath + ".different"
                : ctx.FilePath;
            emitter.EmitEdge(new IndexEvent.EdgeEmitted(
                languageKey,
                pluginKey,
                EdgeKinds.Calls)
            {
                Evidence = new EdgeEvidence(
                    new DevBitsLab.Mcp.SourceGraph.Sdk.SourceLocation(
                        evidencePath,
                        1,
                        1,
                        1,
                        2),
                    DevBitsLab.Mcp.SourceGraph.Sdk.EvidenceConfidence.Exact,
                    "test-analyzer"),
            });
            emitter.EmitAnnotation(new IndexEvent.AnnotationAttached(
                pluginKey,
                "Analyzed",
                "test-analyzer"));
            emitter.EmitReference(new IndexEvent.ReferenceFound(
                pluginKey,
                1,
                3,
                "read"));
            return Task.CompletedTask;
        }
    }

    private sealed class EmitThenThrowAnalyzer : ICodeAnalyzer
    {
        public string Name => "partial";

        public Task AnalyzeAsync(
            AnalyzerContext ctx,
            IGraphEmitter emitter,
            CancellationToken ct)
        {
            emitter.EmitSymbol(new IndexEvent.SymbolDeclared(
                "csharp:T:Analyzer.Isolation.Partial",
                "Partial",
                "Analyzer.Isolation.Partial",
                SymbolKinds.Class,
                1,
                1,
                1,
                2));
            throw new InvalidOperationException("synthetic analyzer failure");
        }
    }

    private sealed class SuccessfulIsolationAnalyzer : ICodeAnalyzer
    {
        public string Name => "success";

        public Task AnalyzeAsync(
            AnalyzerContext ctx,
            IGraphEmitter emitter,
            CancellationToken ct)
        {
            emitter.EmitSymbol(new IndexEvent.SymbolDeclared(
                "csharp:T:Analyzer.Isolation.Success",
                "Success",
                "Analyzer.Isolation.Success",
                SymbolKinds.Class,
                1,
                3,
                1,
                4));
            return Task.CompletedTask;
        }
    }

    private sealed class EmitThenWaitForCancellationAnalyzer : ICodeAnalyzer
    {
        public string Name => "timeout-partial";

        public async Task AnalyzeAsync(
            AnalyzerContext ctx,
            IGraphEmitter emitter,
            CancellationToken ct)
        {
            emitter.EmitSymbol(new IndexEvent.SymbolDeclared(
                "csharp:T:Analyzer.Isolation.TimeoutPartial",
                "TimeoutPartial",
                "Analyzer.Isolation.TimeoutPartial",
                SymbolKinds.Class,
                1,
                1,
                1,
                2));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private sealed class EmitThenSelfCancelAnalyzer : ICodeAnalyzer
    {
        public string Name => "self-cancel";

        public Task AnalyzeAsync(
            AnalyzerContext ctx,
            IGraphEmitter emitter,
            CancellationToken ct)
        {
            emitter.EmitSymbol(new IndexEvent.SymbolDeclared(
                "csharp:T:Analyzer.Isolation.SelfCancelledPartial",
                "SelfCancelledPartial",
                "Analyzer.Isolation.SelfCancelledPartial",
                SymbolKinds.Class,
                1,
                1,
                1,
                2));
            throw new OperationCanceledException("synthetic self cancellation");
        }
    }

    private sealed class CancellationAnalyzer : ICodeAnalyzer
    {
        public string Name => "cancel";

        public Task AnalyzeAsync(
            AnalyzerContext ctx,
            IGraphEmitter emitter,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
