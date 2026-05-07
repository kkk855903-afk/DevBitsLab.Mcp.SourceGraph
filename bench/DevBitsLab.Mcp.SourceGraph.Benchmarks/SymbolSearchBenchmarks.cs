using BenchmarkDotNet.Attributes;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Benchmarks;

/// <summary>
/// Baseline graph-query latency benchmarks. Populates a fresh on-disk SQLite store with a
/// synthetic symbol corpus, then exercises the FTS5 name search path. Adjust
/// <see cref="SymbolCount"/> to chart how query latency scales with corpus size.
///
/// All <see cref="IGraphStore"/> entry points are async, so the benchmark methods themselves
/// return <see cref="Task"/> — BenchmarkDotNet awaits them and times the full async transition.
/// The benchmark inherits the host runtime (matches the project's TargetFramework).
/// </summary>
[MemoryDiagnoser]
public class SymbolSearchBenchmarks
{
    [Params(1_000, 10_000)]
    public int SymbolCount { get; set; }

    private string _dbPath = string.Empty;
    private SqliteGraphStore _store = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync().ConfigureAwait(false);

        var fileId = await _store.UpsertFileAsync(
            path: "Synthetic.cs",
            contentSha256: new byte[32],
            indexedAt: DateTimeOffset.UtcNow).ConfigureAwait(false);

        for (var i = 0; i < SymbolCount; i++)
        {
            var symbol = new Symbol(
                Id: 0,
                Name: $"Method{i}",
                Fqn: $"Bench.Synthetic.Class{i % 100}.Method{i}",
                Kind: SymbolKind.Method,
                FileId: fileId,
                StartLine: i,
                StartCol: 0,
                EndLine: i,
                EndCol: 10,
                Signature: $"void Method{i}()",
                ContainerId: null);
            await _store.UpsertSymbolAsync(
                canonicalKey: $"bench:method:{i}",
                symbol: symbol).ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task<int> SearchByName_PrefixHit()
        => (await _store.SearchSymbolsAsync("Method1", limit: 50).ConfigureAwait(false)).Count;

    [Benchmark]
    public async Task<int> SearchByName_FragmentHit()
        => (await _store.SearchSymbolsAsync("ethod", limit: 50).ConfigureAwait(false)).Count;

    [Benchmark]
    public async Task<int> SearchByName_Miss()
        => (await _store.SearchSymbolsAsync("zzznotfound", limit: 50).ConfigureAwait(false)).Count;

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _store.DisposeAsync().ConfigureAwait(false);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
