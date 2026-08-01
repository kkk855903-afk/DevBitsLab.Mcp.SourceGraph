using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

internal static class BenchmarkCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(CommandLine cli, CancellationToken ct = default)
    {
        var root = cli.ResolvedRepoRoot();
        var scope = cli.ScopeId ?? ResolveDefaultScopeId(root);
        var dbPath = string.IsNullOrEmpty(cli.DatabasePath)
            ? ScopeLayout.ScopeDbPath(root, scope)
            : cli.ResolvedDbPath();
        if (!File.Exists(dbPath))
        {
            await Console.Error.WriteLineAsync(
                $"error: no graph database for scope '{scope}' at {dbPath}").ConfigureAwait(false);
            return 1;
        }

        BenchmarkGoldenFile? golden = null;
        if (!string.IsNullOrWhiteSpace(cli.GoldenPath))
        {
            var goldenPath = Path.GetFullPath(cli.GoldenPath);
            if (!File.Exists(goldenPath))
            {
                await Console.Error.WriteLineAsync($"error: golden file not found: {goldenPath}")
                    .ConfigureAwait(false);
                return 1;
            }
            try
            {
                await using var stream = File.OpenRead(goldenPath);
                golden = await JsonSerializer.DeserializeAsync<BenchmarkGoldenFile>(
                    stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                await Console.Error.WriteLineAsync($"error: invalid golden JSON: {ex.Message}")
                    .ConfigureAwait(false);
                return 1;
            }
            if (golden is null || golden.Version != 1 || golden.Tasks is null || golden.Tasks.Count == 0)
            {
                await Console.Error.WriteLineAsync(
                    "error: golden file requires version=1 and at least one task.")
                    .ConfigureAwait(false);
                return 1;
            }
        }

        var runner = new BenchmarkRunner(dbPath, cli.Cold, cli.Model, embeddingsEnabled: cli.EmbeddingsEnabled);
        var results = golden is null
            ? await runner.RunBuiltInAsync(ct).ConfigureAwait(false)
            : await runner.RunGoldenAsync(golden.Tasks, ct).ConfigureAwait(false);
        var passed = results.Count(result => result.Status == "passed");
        var failed = results.Count(result => result.Status == "failed");
        var skipped = results.Count(result => result.Status == "skipped");
        var report = new BenchmarkReport(
            Version: 1,
            Scope: scope,
            Database: dbPath,
            Cold: cli.Cold,
            IndexGeneration: await ReadGenerationAsync(dbPath, ct).ConfigureAwait(false),
            CreatedAt: DateTimeOffset.UtcNow,
            Passed: passed,
            Failed: failed,
            Skipped: skipped,
            AverageRecall: results.Where(result => result.Recall is not null)
                .Select(result => result.Recall!.Value)
                .DefaultIfEmpty(1.0)
                .Average(),
            Tasks: results);

        if (cli.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            PrintHuman(report);
        }
        return failed == 0 ? 0 : 2;
    }

    private static async Task<long> ReadGenerationAsync(string dbPath, CancellationToken ct)
    {
        await using var store = new SqliteGraphStore(dbPath);
        store.TryLoadVectorExtension(DefaultEmbeddingModel.Dimension);
        await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
        return (await store.GetIndexStateAsync(ct).ConfigureAwait(false)).Generation;
    }

    private static void PrintHuman(BenchmarkReport report)
    {
        Console.WriteLine("SourceGraph benchmark");
        Console.WriteLine($"  scope={report.Scope} generation={report.IndexGeneration} cold={report.Cold}");
        Console.WriteLine($"  db={report.Database}");
        foreach (var task in report.Tasks)
        {
            var mark = task.Status switch { "passed" => "PASS", "skipped" => "SKIP", _ => "FAIL" };
            var recall = task.Recall is null ? "" : $" recall={task.Recall:P1}";
            Console.WriteLine(
                $"[{mark}] {task.Name} ({task.Kind}) {task.ElapsedMs:F2} ms results={task.ResultCount} bytes={task.ResponseBytes}{recall} — {task.Message}");
        }
        Console.WriteLine(
            $"summary: passed={report.Passed} failed={report.Failed} skipped={report.Skipped} average_recall={report.AverageRecall:P1}");
    }

    private static string ResolveDefaultScopeId(string root)
    {
        try
        {
            var config = ScopeConfigLoader.Load(root);
            if (!string.IsNullOrEmpty(config.DefaultScope)) return config.DefaultScope;
            if (config.Scopes.Count > 0) return config.Scopes[0].Id;
        }
        catch (ScopeConfigException) { }
        catch (IOException) { }
        return "default";
    }
}

internal sealed class BenchmarkRunner
{
    private readonly string _dbPath;
    private readonly bool _cold;
    private readonly EmbeddingModelInfo _model;
    private readonly bool _embeddingsEnabled;
    private readonly Func<ICodeEmbeddingGenerator> _embeddingGeneratorFactory;
    private SqliteGraphStore? _warmStore;

    public BenchmarkRunner(
        string dbPath,
        bool cold,
        string? modelId = null,
        bool embeddingsEnabled = false,
        Func<ICodeEmbeddingGenerator>? embeddingGeneratorFactory = null)
    {
        _dbPath = dbPath;
        _cold = cold;
        _model = new EmbeddingModelInfo(
            modelId ?? DefaultEmbeddingModel.ModelId,
            DefaultEmbeddingModel.Dimension);
        _embeddingsEnabled = embeddingsEnabled;
        _embeddingGeneratorFactory = embeddingGeneratorFactory ?? CreateGenerator;
    }

    public async Task<IReadOnlyList<BenchmarkTaskResult>> RunBuiltInAsync(CancellationToken ct)
    {
        var results = new List<BenchmarkTaskResult>();
        await using var lease = await OpenAsync(ct).ConfigureAwait(false);
        var store = lease.Store;
        var stats = await MeasureAsync(
            "graph-health", "health", null, null,
            async () =>
            {
                var value = await store.GetStatsAsync(ct).ConfigureAwait(false);
                var ok = value.FileCount > 0 && value.SymbolCount > 0;
                return Outcome(ok ? "passed" : "failed", value.SymbolCount,
                    new { value.FileCount, value.SymbolCount, value.ReferenceCount, value.EdgeCount },
                    ok ? "graph contains indexed files and symbols" : "graph is empty");
            }).ConfigureAwait(false);
        results.Add(stats);

        var keys = await store.GetAllSymbolKeysAsync(ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            results.Add(Skipped("canonical-roundtrip", "definition", "no symbol available"));
            results.Add(Skipped("reference-probe", "references", "no symbol available"));
            results.Add(Skipped("semantic-probe", "semantic", "no symbol available"));
        }
        else
        {
            var sample = await store.GetSymbolByIdAsync(keys[0].Id, ct).ConfigureAwait(false);
            results.Add(await MeasureAsync(
                "canonical-roundtrip", "definition", null, null,
                async () =>
                {
                    var hit = sample?.CanonicalKey is null ? null
                        : await store.GetSymbolByCanonicalKeyAsync(sample.CanonicalKey, ct).ConfigureAwait(false);
                    var ok = hit?.Id == sample?.Id;
                    return Outcome(ok ? "passed" : "failed", hit is null ? 0 : 1, hit,
                        ok ? "canonical key resolves to the original symbol" : "canonical roundtrip failed");
                }).ConfigureAwait(false));

            var referenceProbe = await FindReferenceProbeAsync(store, keys, ct).ConfigureAwait(false);
            results.Add(referenceProbe is null
                ? Skipped("reference-probe", "references", "index contains no references for sampled symbols")
                : await ExecuteAsync(new BenchmarkGoldenTask(
                    Name: "reference-probe", Kind: "references", Symbol: referenceProbe.CanonicalKey,
                    MinResults: 1), ct).ConfigureAwait(false));

            results.Add(await ExecuteAsync(new BenchmarkGoldenTask(
                Name: "semantic-probe", Kind: "semantic", Query: sample?.Name,
                MinResults: 1, TopK: 10), ct).ConfigureAwait(false));
        }

        var edgeKinds = await store.GetDistinctEdgeKindsAsync(ct).ConfigureAwait(false);
        foreach (var (name, kinds) in new[]
        {
            ("wpf-projection", new[] { "handles-event", "binds-to", "binds-path", "code-behind" }),
            ("interop-projection", new[] { "native-implementation", "pinvoke", "calls-native" }),
        })
        {
            var present = kinds.Where(edgeKinds.Contains).ToList();
            results.Add(present.Count == 0
                ? Skipped(name, "edge-kind", "domain is not present in this index")
                : Passed(name, "edge-kind", present.Count, present, $"present: {string.Join(", ", present)}"));
        }

        await DisposeWarmAsync().ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<BenchmarkTaskResult>> RunGoldenAsync(
        IReadOnlyList<BenchmarkGoldenTask> tasks,
        CancellationToken ct)
    {
        var results = new List<BenchmarkTaskResult>(tasks.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Name) || !names.Add(task.Name))
            {
                results.Add(Failed(task.Name ?? "(unnamed)", task.Kind, "task names must be non-empty and unique"));
                continue;
            }
            try
            {
                results.Add(await ExecuteAsync(task, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                results.Add(Failed(task.Name, task.Kind, ex.Message));
            }
        }
        await DisposeWarmAsync().ConfigureAwait(false);
        return results;
    }

    private async Task<BenchmarkTaskResult> ExecuteAsync(BenchmarkGoldenTask task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.Kind))
            throw new ArgumentException("benchmark task requires non-empty 'kind'");
        if (task.TopK is < 1 or > 1000)
            throw new ArgumentException("benchmark task topK must be between 1 and 1000");
        if (task.MaxDepth is < 1 or > 32)
            throw new ArgumentException("benchmark task maxDepth must be between 1 and 32");
        if (task.MinResults < 0 || task.MinHops < 0)
            throw new ArgumentException("benchmark task result/hop thresholds cannot be negative");
        if (task.MinRecall is < 0 or > 1)
            throw new ArgumentException("benchmark task minRecall must be between 0 and 1");
        if (task.MaxLatencyMs is <= 0 || task.MaxResponseBytes is <= 0)
            throw new ArgumentException("benchmark latency/response limits must be positive");
        await using var lease = await OpenAsync(ct).ConfigureAwait(false);
        var store = lease.Store;
        return task.Kind.Trim().ToLowerInvariant() switch
        {
            "definition" => await MeasureSymbolsAsync(task, "definition",
                async () => Optional(await ResolveAsync(store, task.Query ?? task.Symbol, ct).ConfigureAwait(false))).ConfigureAwait(false),
            "search" => await MeasureSymbolsAsync(task, "search",
                () => store.SearchSymbolsAsync(Required(task.Query, "query"), null, task.TopK, ct)).ConfigureAwait(false),
            "references" => await MeasureCountAsync(task, "references", async () =>
            {
                var target = await RequireSymbolAsync(store, task.Symbol ?? task.Query, ct).ConfigureAwait(false);
                return await store.FindReferencesAsync(target.Id, task.TopK, ct).ConfigureAwait(false);
            }).ConfigureAwait(false),
            "implementations" => await MeasureSymbolsAsync(task, "implementations", async () =>
            {
                var target = await RequireSymbolAsync(store, task.Symbol ?? task.Query, ct).ConfigureAwait(false);
                return await store.ListImplementationsAsync(target.Id, task.TopK, ct).ConfigureAwait(false);
            }).ConfigureAwait(false),
            "edge-kind" => await MeasureEdgeKindAsync(task, store, ct).ConfigureAwait(false),
            "path" => await MeasurePathAsync(task, store, ct).ConfigureAwait(false),
            "semantic" => await MeasureSemanticAsync(task, store, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"unsupported benchmark task kind '{task.Kind}'"),
        };
    }

    private async Task<BenchmarkTaskResult> MeasureSymbolsAsync(
        BenchmarkGoldenTask task,
        string kind,
        Func<Task<IReadOnlyList<SymbolHit>>> query) =>
        await MeasureAsync(task.Name, kind, task.MaxLatencyMs, task.MaxResponseBytes, async () =>
        {
            var rows = await query().ConfigureAwait(false);
            return Evaluate(task, rows.Select(row => row.CanonicalKey).Where(key => key is not null)!, rows);
        }).ConfigureAwait(false);

    private async Task<BenchmarkTaskResult> MeasureCountAsync<T>(
        BenchmarkGoldenTask task,
        string kind,
        Func<Task<IReadOnlyList<T>>> query) =>
        await MeasureAsync(task.Name, kind, task.MaxLatencyMs, task.MaxResponseBytes, async () =>
        {
            var rows = await query().ConfigureAwait(false);
            var ok = rows.Count >= task.MinResults;
            return Outcome(ok ? "passed" : "failed", rows.Count, rows,
                ok ? $"minimum result count {task.MinResults} satisfied" : $"expected at least {task.MinResults} results");
        }).ConfigureAwait(false);

    private async Task<BenchmarkTaskResult> MeasureEdgeKindAsync(
        BenchmarkGoldenTask task, IGraphStore store, CancellationToken ct) =>
        await MeasureAsync(task.Name, "edge-kind", task.MaxLatencyMs, task.MaxResponseBytes, async () =>
        {
            var expected = task.RelationList.Count > 0 ? task.RelationList : [Required(task.Query, "query")];
            var actual = await store.GetDistinctEdgeKindsAsync(ct).ConfigureAwait(false);
            var found = expected.Where(actual.Contains).ToList();
            var recall = found.Count / (double)expected.Count;
            var ok = found.Count == expected.Count;
            return Outcome(ok ? "passed" : "failed", found.Count, found,
                ok ? "all expected edge kinds are present" : $"missing: {string.Join(", ", expected.Except(found))}", recall);
        }).ConfigureAwait(false);

    private async Task<BenchmarkTaskResult> MeasurePathAsync(
        BenchmarkGoldenTask task, IGraphStore store, CancellationToken ct) =>
        await MeasureAsync(task.Name, "path", task.MaxLatencyMs, task.MaxResponseBytes, async () =>
        {
            var source = await RequireSymbolAsync(store, task.From, ct).ConfigureAwait(false);
            var target = await RequireSymbolAsync(store, task.To, ct).ConfigureAwait(false);
            var relations = task.RelationList.Count == 0 ? ["calls"] : task.RelationList;
            var path = await FindPathAsync(store, source, target, relations, task.MaxDepth, ct).ConfigureAwait(false);
            var requiredHops = Math.Max(1, task.MinHops);
            var ok = path is not null && path.Hops.Count >= requiredHops;
            return Outcome(ok ? "passed" : "failed", path?.Hops.Count ?? 0, path,
                ok ? $"found {path!.Hops.Count}-hop evidence-backed path" : $"no path with at least {requiredHops} hops");
        }).ConfigureAwait(false);

    private async Task<BenchmarkTaskResult> MeasureSemanticAsync(
        BenchmarkGoldenTask task, SqliteGraphStore store, CancellationToken ct) =>
        await MeasureAsync(task.Name, "semantic", task.MaxLatencyMs, task.MaxResponseBytes, async () =>
        {
            var query = Required(task.Query, "query");
            IReadOnlyList<SymbolHit> rows;
            var strategy = "lexical";
            store.TryLoadVectorExtension(_model.Dimension);
            var embeddings = store.CreateEmbeddingsStore(_model.Dimension);
            using var generator = _embeddingsEnabled ? _embeddingGeneratorFactory() : null;
            if (generator is not null
                && embeddings.IsAvailable
                && generator.IsAvailable
                && await embeddings.CountAsync(ct).ConfigureAwait(false) > 0)
            {
                var vectors = await generator.EmbedAsync([query], ct).ConfigureAwait(false);
                var hits = vectors.Count == 0 ? [] : await embeddings.SearchAsync(vectors[0], task.TopK, null, ct).ConfigureAwait(false);
                var resolved = new List<SymbolHit>(hits.Count);
                foreach (var hit in hits)
                {
                    var symbol = await store.GetSymbolByIdAsync(hit.SymbolId, ct).ConfigureAwait(false);
                    if (symbol is not null) resolved.Add(symbol);
                }
                rows = resolved;
                strategy = "semantic";
            }
            else
            {
                rows = await store.SearchSymbolsAsync(query, null, task.TopK, ct).ConfigureAwait(false);
            }
            return Evaluate(task, rows.Select(row => row.CanonicalKey).Where(key => key is not null)!,
                new { strategy_used = strategy, rows });
        }).ConfigureAwait(false);

    private JinaCodeEmbeddingGenerator CreateGenerator()
    {
        var models = new ModelStore();
        return new JinaCodeEmbeddingGenerator(
            models.FilePath(_model.ModelId, "model.onnx"),
            models.FilePath(_model.ModelId, "tokenizer.json"),
            _model);
    }

    private static BenchmarkOutcome Evaluate(
        BenchmarkGoldenTask task,
        IEnumerable<string> actualKeys,
        object payload)
    {
        var actual = actualKeys.ToHashSet(StringComparer.Ordinal);
        var expected = task.ExpectedCanonicalKeyList;
        var matched = expected.Count == 0 ? 0 : expected.Count(actual.Contains);
        var recall = expected.Count == 0 ? (double?)null : matched / (double)expected.Count;
        var countOk = actual.Count >= task.MinResults;
        var recallOk = recall is null || recall >= task.MinRecall;
        var ok = countOk && recallOk;
        return Outcome(ok ? "passed" : "failed", actual.Count, payload,
            ok ? "result count and recall thresholds satisfied"
                : $"expected min_results={task.MinResults}, min_recall={task.MinRecall:F2}; actual recall={recall?.ToString("F2") ?? "n/a"}",
            recall);
    }

    private static async Task<BenchmarkPath?> FindPathAsync(
        IGraphStore store,
        SymbolHit source,
        SymbolHit target,
        IReadOnlyList<string> relations,
        int maxDepth,
        CancellationToken ct)
    {
        var queue = new Queue<(SymbolHit Symbol, List<BenchmarkPathHop> Hops)>();
        var visited = new HashSet<long> { source.Id };
        queue.Enqueue((source, []));
        while (queue.Count > 0)
        {
            var (current, hops) = queue.Dequeue();
            if (hops.Count >= maxDepth) continue;
            var outgoing = await store.ListAuditableOutboundEdgesByKindsAsync(
                current.Id, relations, 1000, ct).ConfigureAwait(false);
            foreach (var edge in outgoing)
            {
                var nextHops = new List<BenchmarkPathHop>(hops)
                {
                    new(current.Id, current.CanonicalKey, edge.Symbol.Id,
                        edge.Symbol.CanonicalKey, edge.Relation),
                };
                if (edge.Symbol.Id == target.Id)
                {
                    return new BenchmarkPath(source.CanonicalKey, target.CanonicalKey, nextHops);
                }
                if (visited.Add(edge.Symbol.Id)) queue.Enqueue((edge.Symbol, nextHops));
            }
        }
        return null;
    }

    private static async Task<SymbolHit?> FindReferenceProbeAsync(
        IGraphStore store, IReadOnlyList<SymbolKeyRow> keys, CancellationToken ct)
    {
        foreach (var key in keys.Take(100))
        {
            var refs = await store.FindReferencesAsync(key.Id, 1, ct).ConfigureAwait(false);
            if (refs.Count > 0) return await store.GetSymbolByIdAsync(key.Id, ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<StoreLease> OpenAsync(CancellationToken ct)
    {
        if (!_cold && _warmStore is not null) return new StoreLease(_warmStore, Owns: false);
        var store = new SqliteGraphStore(_dbPath);
        store.TryLoadVectorExtension(_model.Dimension);
        await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
        if (!_cold) _warmStore = store;
        return new StoreLease(store, Owns: _cold);
    }

    private async Task DisposeWarmAsync()
    {
        if (_warmStore is null) return;
        await _warmStore.DisposeAsync().ConfigureAwait(false);
        _warmStore = null;
    }

    private static async Task<SymbolHit?> ResolveAsync(IGraphStore store, string? value, CancellationToken ct)
    {
        var query = Required(value, "symbol/query");
        var exact = CanonicalKeyValidator.IsValid(query)
            ? await store.GetSymbolByCanonicalKeyAsync(query, ct).ConfigureAwait(false)
            : null;
        if (exact is not null) return exact;
        var candidates = await store.FindSymbolsAsync(query, null, 10, ct).ConfigureAwait(false);
        if (candidates.Count == 1) return candidates[0];
        var fqn = candidates.Where(candidate => candidate.Fqn == query).ToList();
        if (fqn.Count == 1) return fqn[0];
        if (candidates.Count > 1) throw new InvalidOperationException($"symbol '{query}' is ambiguous; use a canonical key");
        return null;
    }

    private static async Task<SymbolHit> RequireSymbolAsync(IGraphStore store, string? value, CancellationToken ct) =>
        await ResolveAsync(store, value, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"symbol '{value}' was not found");

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"benchmark task requires non-empty '{field}'")
            : value.Trim();

    private static IReadOnlyList<T> Optional<T>(T? value) where T : class => value is null ? [] : [value];

    private static async Task<BenchmarkTaskResult> MeasureAsync(
        string name,
        string kind,
        double? maxLatencyMs,
        int? maxResponseBytes,
        Func<Task<BenchmarkOutcome>> action)
    {
        var sw = Stopwatch.StartNew();
        var outcome = await action().ConfigureAwait(false);
        sw.Stop();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(outcome.Payload).Length;
        var latencyOk = maxLatencyMs is null || sw.Elapsed.TotalMilliseconds <= maxLatencyMs;
        var sizeOk = maxResponseBytes is null || bytes <= maxResponseBytes;
        var status = outcome.Status == "passed" && latencyOk && sizeOk ? "passed" : "failed";
        var diagnostics = new List<string>();
        if (!latencyOk) diagnostics.Add($"latency {sw.Elapsed.TotalMilliseconds:F2} ms > {maxLatencyMs:F2} ms");
        if (!sizeOk) diagnostics.Add($"response {bytes} bytes > {maxResponseBytes} bytes");
        var message = diagnostics.Count == 0 ? outcome.Message : outcome.Message + "; " + string.Join("; ", diagnostics);
        return new BenchmarkTaskResult(name, kind, status, sw.Elapsed.TotalMilliseconds,
            outcome.ResultCount, bytes, outcome.Recall, message);
    }

    private static BenchmarkOutcome Outcome(
        string status, int resultCount, object? payload, string message, double? recall = null) =>
        new(status, resultCount, payload, recall, message);

    private static BenchmarkTaskResult Passed(string name, string kind, int count, object payload, string message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload).Length;
        return new(name, kind, "passed", 0, count, bytes, null, message);
    }

    private static BenchmarkTaskResult Failed(string name, string kind, string message) =>
        new(name, kind, "failed", 0, 0, 0, null, message);

    private static BenchmarkTaskResult Skipped(string name, string kind, string message) =>
        new(name, kind, "skipped", 0, 0, 0, null, message);

    private readonly record struct StoreLease(SqliteGraphStore Store, bool Owns) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Owns ? Store.DisposeAsync() : ValueTask.CompletedTask;
    }

    private sealed record BenchmarkOutcome(
        string Status, int ResultCount, object? Payload, double? Recall, string Message);
}

internal sealed record BenchmarkGoldenFile(int Version, IReadOnlyList<BenchmarkGoldenTask> Tasks);

internal sealed record BenchmarkGoldenTask(
    string Name,
    string Kind,
    string? Query = null,
    string? Symbol = null,
    string? From = null,
    string? To = null,
    [property: JsonPropertyName("relations")] IReadOnlyList<string>? Relations = null,
    [property: JsonPropertyName("expected_canonical_keys")] IReadOnlyList<string>? ExpectedCanonicalKeys = null,
    int MinResults = 1,
    int MinHops = 0,
    int MaxDepth = 8,
    int TopK = 10,
    double MinRecall = 1.0,
    double? MaxLatencyMs = null,
    int? MaxResponseBytes = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> RelationList => Relations ?? [];

    [JsonIgnore]
    public IReadOnlyList<string> ExpectedCanonicalKeyList => ExpectedCanonicalKeys ?? [];
}

internal sealed record BenchmarkReport(
    int Version,
    string Scope,
    string Database,
    bool Cold,
    long IndexGeneration,
    DateTimeOffset CreatedAt,
    int Passed,
    int Failed,
    int Skipped,
    double AverageRecall,
    IReadOnlyList<BenchmarkTaskResult> Tasks);

internal sealed record BenchmarkTaskResult(
    string Name,
    string Kind,
    string Status,
    double ElapsedMs,
    int ResultCount,
    int ResponseBytes,
    double? Recall,
    string Message);

internal sealed record BenchmarkPath(
    string? FromCanonicalKey,
    string? ToCanonicalKey,
    IReadOnlyList<BenchmarkPathHop> Hops);

internal sealed record BenchmarkPathHop(
    long SourceId,
    string? SourceCanonicalKey,
    long TargetId,
    string? TargetCanonicalKey,
    string Relation);
