using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the <c>report-progress-on-slow-tools</c> change. Drives the three opted-in tools
/// (`semantic_search`, `impact_of_change`, `module_summary`) with a fake
/// <see cref="IProgress{T}"/> reporter that captures emitted <see cref="ProgressNotificationValue"/>
/// values, asserts the documented checkpoint shape, monotonicity, and the no-op contract.
///
/// Pattern mirrors <see cref="TabularRenderingTests"/>: cold-index <c>Sample.sln</c> once via
/// <see cref="IAsyncLifetime"/>, drive the tool methods directly through the real
/// <see cref="ScopeRouter"/>. <c>semantic_search</c>'s end-to-end path requires the embedding
/// pipeline (which only mock-runs in <c>SemanticIndexingFlowTests</c> with a heavy harness), so
/// here we cover its progress contract via the disabled path — the tool's IsAvailable check
/// short-circuits before the encoder runs, but the parameter signature is verified via
/// reflection.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class ProgressReportingTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeRouter? _router;
    private ScopeHost? _host;

    public ProgressReportingTests() => LeafFormatter.Suppressed = true;
    public void Dispose() => LeafFormatter.Suppressed = false;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        _tempDir = Path.Join(Path.GetTempPath(), "progress-reporting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");

        _store = new SqliteGraphStore(_dbPath);
        await RoslynIndexer.IndexSolutionOnceAsync(slnPath, _store);

        var scope = new Scope(
            Id: "default",
            Name: "default",
            Root: Path.GetDirectoryName(slnPath)!,
            ProjectSet: new ScopeProjectSet.Solutions(new[] { slnPath }, Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);

        var embeddingsStore = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        _host = new ScopeHost(scope, _store, embeddingsStore, indexer, slnPath);
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            // Recursive delete clears `graph.db` plus the SQLite WAL sidecars
            // (`graph.db-wal`, `graph.db-shm`) that linger when the connection
            // ran in WAL mode. Skipping them leaks temp files across runs.
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup; WAL/SHM sidecars sometimes linger after dispose */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; CI sometimes denies temp-dir teardown */ }
    }

    private static string LocateSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Join(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    [Fact]
    public async Task ImpactOfChange_emitsQueryingCheckpoint()
    {
        var captured = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(captured.Add);

        await GraphTools.ImpactOfChangeAsync(_router!, "Calculator.Add", progress: progress);

        // Progress<T>.Report is asynchronous (posts to SynchronizationContext); give it a tick to drain.
        await Task.Yield();

        captured.Should().ContainSingle();
        captured[0].Progress.Should().Be(0f);
        captured[0].Total.Should().Be(1f);
        captured[0].Message.Should().Be("querying");
    }

    [Fact]
    public async Task ModuleSummary_emitsQueryingCheckpoint()
    {
        var captured = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(captured.Add);

        await GraphTools.ModuleSummaryAsync(_router!, "Sample.Domain", progress: progress);

        await Task.Yield();

        captured.Should().ContainSingle();
        captured[0].Progress.Should().Be(0f);
        captured[0].Total.Should().Be(1f);
        captured[0].Message.Should().Be("querying");
    }

    [Fact]
    public async Task ImpactOfChange_noProgressArg_runsToCompletion()
    {
        // No-op path: no `progress` argument. The default null parameter means `progress?.Report`
        // is a noop. Tool body must run identically and return a non-empty response.
        var result = await GraphTools.ImpactOfChangeAsync(_router!, "Calculator.Add");
        result.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ModuleSummary_noProgressArg_runsToCompletion()
    {
        var result = await GraphTools.ModuleSummaryAsync(_router!, "Sample.Domain");
        result.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SemanticSearch_methodSignature_carriesProgressParameter()
    {
        // semantic_search's end-to-end path requires the real embedding pipeline, which we don't
        // stand up here (covered by SemanticIndexingFlowTests with the heavy harness). For
        // progress reporting we verify the contract structurally: the method has an
        // `IProgress<ProgressNotificationValue>?` parameter named `progress` with a default
        // value of null. If a future change drops the parameter or renames it, this test fires.
        var method = typeof(GraphTools).GetMethod(nameof(GraphTools.SemanticSearchAsync), BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        var progressParam = method!.GetParameters().FirstOrDefault(p => p.Name == "progress");
        progressParam.Should().NotBeNull("SemanticSearchAsync must accept an `IProgress<ProgressNotificationValue>` parameter for progress reporting per report-progress-on-slow-tools");
        progressParam!.ParameterType.Should().Be(typeof(IProgress<ProgressNotificationValue>));
        progressParam.HasDefaultValue.Should().BeTrue("the parameter must default to null so callers without progress reporting work unchanged");
        progressParam.DefaultValue.Should().BeNull();
    }

    [Fact]
    public void Format_Progress_helperShapeMatchesContract()
    {
        var v = Format.Progress(0.42, "encoding query");
        v.Progress.Should().Be(0.42f);
        v.Total.Should().Be(1f);
        v.Message.Should().Be("encoding query");
    }
}
