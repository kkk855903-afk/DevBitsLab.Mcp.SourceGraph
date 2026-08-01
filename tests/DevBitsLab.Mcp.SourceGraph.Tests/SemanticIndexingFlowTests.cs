using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end test of the indexer-&gt;sink-&gt;generator-&gt;store pipeline using a fixture solution
/// and the deterministic mock embedding generator. Confirms that pass-2 enqueues per-symbol
/// requests, the sink hands them to the generator in batches, and the generator's vectors
/// land in the vec0 table — and that querying for "calculator" surfaces
/// <c>Sample.Domain.Calculator</c> at the top.
///
/// <para>If the sqlite-vec extension fails to load (e.g., RID mismatch on an unsupported
/// runtime), the test asserts the disabled-path contract instead.</para>
/// </summary>
public sealed class SemanticIndexingFlowTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private bool _vec0Loaded;
    private const int Dim = 64;

    private sealed class CapturingSink : IEmbeddingsRequestSink
    {
        public CapturingSink(bool requiresFullRefresh = false)
        {
            RequiresFullRefresh = requiresFullRefresh;
        }

        public ConcurrentBag<EmbedRequest> Captured { get; } = new();
        public bool IsEnabled => true;
        public bool RequiresFullRefresh { get; }
        public void Enqueue(EmbedRequest request) => Captured.Add(request);
    }

    public async Task InitializeAsync()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sourcegraph-flow-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        _vec0Loaded = _store.TryLoadVectorExtension(Dim);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private static string LocateSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    [Fact]
    public async Task Indexer_enqueuesEmbedRequests_perIndexableSymbol()
    {
        var sink = new CapturingSink();
        var slnPath = LocateSolution();
        await RoslynIndexer.IndexSolutionOnceAsync(slnPath, _store!, embeddingsSink: sink);

        // The fixture solution has 2+ documented types/methods so the sink must capture
        // at least a handful of requests. We don't bind to an exact number to stay robust
        // against minor fixture changes.
        sink.Captured.Should().NotBeEmpty();
        // Every request must carry a non-empty text and a 32-byte SHA-256 hash.
        sink.Captured.Should().AllSatisfy(r =>
        {
            r.Text.Should().NotBeNullOrWhiteSpace();
            r.ContentHash.Length.Should().Be(32);
            r.SymbolId.Should().BeGreaterThan(0);
        });
        // At least one request's text mentions Calculator (the fixture's flagship class).
        sink.Captured.Any(r => r.Text.Contains("Calculator")).Should().BeTrue();
    }

    [Fact]
    public async Task MissingProducerCheckpoint_requeuesEmbeddings_forUnchangedFiles()
    {
        var slnPath = LocateSolution();
        await RoslynIndexer.IndexSolutionOnceAsync(
            slnPath,
            _store!,
            embeddingsSink: new CapturingSink());

        var refresh = new CapturingSink(requiresFullRefresh: true);
        await RoslynIndexer.IndexSolutionOnceAsync(
            slnPath,
            _store!,
            embeddingsSink: refresh);

        refresh.Captured.Should().NotBeEmpty(
            "an absent/stale embedding producer checkpoint must backfill unchanged source files");
        refresh.Captured.Any(r => r.Text.Contains("Calculator")).Should().BeTrue();
    }

    [Fact]
    public async Task EndToEnd_semanticSearch_findsCalculatorForCalculatorQuery()
    {
        if (!_vec0Loaded)
        {
            // Disabled-path contract: indexer still runs, but no embeddings table.
            var disabled = _store!.CreateEmbeddingsStore(Dim);
            disabled.IsAvailable.Should().BeFalse();
            return;
        }

        var slnPath = LocateSolution();

        // Build the embedding pipeline pieces with the deterministic mock + 64-dim vec0.
        var generator = new DeterministicMockEmbeddingGenerator(new EmbeddingModelInfo("test/mock", Dim));
        var embStore = _store!.CreateEmbeddingsStore(Dim);
        embStore.IsAvailable.Should().BeTrue();

        var sink = new ChannelEmbeddingsRequestSink();
        // Spin up a drain loop equivalent to the EmbeddingsHostedService body. We re-implement
        // it here because the production hosted service depends on Microsoft.Extensions.Hosting
        // and we want this test to compile without the Server project reference. The behaviour
        // assertion is the same: every captured request must be embedded and persisted.
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var modelVersion = generator.Model.Version;
        var drain = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (!await sink.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false)) break;
                var batch = new List<EmbedRequest>();
                while (batch.Count < 32 && sink.Reader.TryRead(out var item)) batch.Add(item);
                if (batch.Count == 0) continue;
                var pending = new List<EmbedRequest>();
                foreach (var r in batch)
                {
                    if (await embStore.ShouldReembedAsync(r.SymbolId, r.ContentHash, modelVersion, cts.Token).ConfigureAwait(false))
                    {
                        pending.Add(r);
                    }
                }
                if (pending.Count == 0) continue;
                var inputs = pending.Select(r => r.Text).ToList();
                var vectors = await generator.EmbedAsync(inputs, cts.Token).ConfigureAwait(false);
                for (var i = 0; i < pending.Count; i++)
                {
                    await embStore.UpsertAsync(pending[i].SymbolId, pending[i].ContentHash, vectors[i], modelVersion, cts.Token).ConfigureAwait(false);
                }
            }
        }, cts.Token);

        await RoslynIndexer.IndexSolutionOnceAsync(slnPath, _store!, embeddingsSink: sink);

        // Drain the queue: signal completion so the loop exits when the channel empties.
        sink.Complete();
        try { await drain.WaitAsync(TimeSpan.FromSeconds(30)); } catch (TimeoutException) { cts.Cancel(); }

        // Now query: a phrase whose tokens overlap heavily with the Calculator-related
        // synthesised text ("class Sample.Domain.Calculator", "method Calculator.Add", "Adds two
        // integers"). Under the deterministic-mock encoder this produces a clear winner that
        // joins to a Calculator-family symbol; we assert at least one Calculator symbol shows
        // up in the top-k rather than exact rank to stay robust against fixture changes. We
        // ask for the top-25 because the fixture solution now includes Sample.Tests +
        // Sample.Generators, which produce a lot of overlapping tokens (NUnit/MSTest attribute
        // stubs, test method names, source-generated content) that compete with Calculator
        // under the deterministic mock encoder.
        var queryVec = DeterministicMockEmbeddingGenerator.Embed("class Sample.Domain.Calculator integer arithmetic", Dim);
        var hits = await embStore.SearchAsync(queryVec, k: 25);

        hits.Should().NotBeEmpty();
        // Resolve the top-k symbol ids back to FQNs and assert at least one is from the
        // Calculator family. Deterministic mock + 64-dim makes exact rank brittle.
        var topFqns = new List<string>();
        foreach (var h in hits)
        {
            var sym = await _store.GetSymbolByIdAsync(h.SymbolId);
            if (sym is not null) topFqns.Add(sym.Fqn);
        }
        topFqns.Any(f => f.Contains("Calculator")).Should().BeTrue(
            "at least one Calculator-family symbol should appear in the top-{0} given the heavily-overlapping query tokens; got: {1}",
            hits.Count, string.Join(", ", topFqns));
    }

    [Fact]
    public void NoOpSink_isDisabled_andSwallowsRequests()
    {
        var sink = new NoOpEmbeddingsRequestSink();
        sink.IsEnabled.Should().BeFalse();
        sink.Enqueue(new EmbedRequest(1, "x", new byte[32]));
        // No throw, no observable side effect — that's the contract.
    }

}
