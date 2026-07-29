using System.Diagnostics;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Benchmarks;

/// <summary>
/// Reproducible first-call versus warm-call probe. Every sample uses the same store instance,
/// query string, and limit in one process. Symbol search uses independent read-only SQLite
/// readers and therefore has no managed graph-store lock; that fact is reported explicitly
/// rather than folding transport or process-start time into query latency.
/// </summary>
internal static class SameProcessQueryProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine(
                "usage: --same-process-query <graph.db> <symbol-query> [warm-iterations]");
            return 2;
        }

        var dbPath = Path.GetFullPath(args[0]);
        var query = args[1];
        var warmIterations = args.Length == 3 && int.TryParse(args[2], out var parsed)
            ? parsed
            : 20;
        if (!File.Exists(dbPath) || warmIterations is < 1 or > 10_000)
        {
            Console.Error.WriteLine("database must exist and warm-iterations must be 1..10000");
            return 2;
        }

        const int limit = 25;
        await using var store = new SqliteGraphStore(dbPath);

        var cold = Stopwatch.StartNew();
        var coldResult = await store.FindSymbolsAsync(query, limit: limit).ConfigureAwait(false);
        cold.Stop();

        var warmSamples = new double[warmIterations];
        var warmHits = 0;
        for (var i = 0; i < warmSamples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            var result = await store.FindSymbolsAsync(query, limit: limit).ConfigureAwait(false);
            sw.Stop();
            warmSamples[i] = sw.Elapsed.TotalMilliseconds;
            warmHits = result.Count;
        }
        Array.Sort(warmSamples);

        var output = new
        {
            process_id = Environment.ProcessId,
            operation = "find_symbols",
            arguments = new { query, limit },
            cold_ms = cold.Elapsed.TotalMilliseconds,
            warm = new
            {
                iterations = warmIterations,
                min_ms = warmSamples[0],
                median_ms = Median(warmSamples),
                max_ms = warmSamples[^1],
            },
            lock_wait_ms = 0d,
            lock_wait_source = "read-only-reader-no-managed-lock",
            cold_hits = coldResult.Count,
            warm_hits = warmHits,
        };
        Console.WriteLine(JsonSerializer.Serialize(
            output,
            new JsonSerializerOptions { WriteIndented = true }));
        return coldResult.Count == warmHits ? 0 : 1;
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }
}
