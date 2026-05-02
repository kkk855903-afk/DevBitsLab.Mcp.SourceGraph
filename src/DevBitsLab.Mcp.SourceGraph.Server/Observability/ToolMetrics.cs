using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DevBitsLab.Mcp.SourceGraph.Server.Observability;

/// <summary>
/// Tracks tool-call activity. Each tool wraps its body in <see cref="TrackAsync"/>; the helper
/// records per-tool counters in memory and appends a JSONL line to <c>usage.jsonl</c> next to the
/// graph database. Inspectable at runtime via the <c>usage_stats</c> MCP tool.
/// </summary>
public static class ToolMetrics
{
    private static readonly ConcurrentDictionary<string, ToolStats> _stats = new();
    private static readonly Lock _writeLock = new();
    private static string? _logPath;
    private static readonly DateTimeOffset _processStart = DateTimeOffset.UtcNow;

    /// <summary>Configures the JSONL output path. Call once at process start.</summary>
    public static void Configure(string logPath)
    {
        _logPath = logPath;
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public static async Task<string> TrackAsync(string toolName, object? args, Func<Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        var ok = true;
        var result = string.Empty;
        try
        {
            result = await body().ConfigureAwait(false);
            return result;
        }
        catch
        {
            ok = false;
            throw;
        }
        finally
        {
            sw.Stop();
            Record(toolName, args, result.Length, sw.Elapsed, ok);
        }
    }

    public static string TrackSync(string toolName, object? args, Func<string> body)
    {
        var sw = Stopwatch.StartNew();
        var ok = true;
        var result = string.Empty;
        try
        {
            result = body();
            return result;
        }
        catch
        {
            ok = false;
            throw;
        }
        finally
        {
            sw.Stop();
            Record(toolName, args, result.Length, sw.Elapsed, ok);
        }
    }

    private static void Record(string toolName, object? args, int responseLen, TimeSpan elapsed, bool ok)
    {
        var stats = _stats.GetOrAdd(toolName, _ => new ToolStats());
        stats.Add(elapsed, responseLen, ok);
        AppendJsonl(toolName, args, responseLen, elapsed, ok);
    }

    private static void AppendJsonl(string toolName, object? args, int responseLen, TimeSpan elapsed, bool ok)
    {
        if (_logPath is null) return;
        var entry = new
        {
            ts = DateTimeOffset.UtcNow,
            tool = toolName,
            ok,
            ms = elapsed.TotalMilliseconds,
            response_len = responseLen,
            args
        };
        try
        {
            var json = JsonSerializer.Serialize(entry);
            lock (_writeLock)
            {
                File.AppendAllText(_logPath, json + "\n");
            }
        }
        catch
        {
            // best effort — never let observability break a tool call
        }
    }

    public static IReadOnlyDictionary<string, ToolStatsSnapshot> Snapshot()
    {
        return _stats.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());
    }

    public static DateTimeOffset ProcessStart => _processStart;
    public static string? LogPath => _logPath;
}

internal sealed class ToolStats
{
    private long _count;
    private long _errors;
    private long _totalMs;
    private long _maxMs;
    private long _totalRespLen;
    private DateTimeOffset _lastCalled = DateTimeOffset.MinValue;
    private readonly Lock _lock = new();

    public void Add(TimeSpan elapsed, int responseLen, bool ok)
    {
        lock (_lock)
        {
            _count++;
            if (!ok) _errors++;
            var ms = (long)elapsed.TotalMilliseconds;
            _totalMs += ms;
            if (ms > _maxMs) _maxMs = ms;
            _totalRespLen += responseLen;
            _lastCalled = DateTimeOffset.UtcNow;
        }
    }

    public ToolStatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new ToolStatsSnapshot(
                Count: _count,
                Errors: _errors,
                AvgMs: _count > 0 ? (double)_totalMs / _count : 0,
                MaxMs: _maxMs,
                AvgResponseLen: _count > 0 ? (double)_totalRespLen / _count : 0,
                LastCalledAt: _lastCalled);
        }
    }
}

public sealed record ToolStatsSnapshot(
    long Count,
    long Errors,
    double AvgMs,
    long MaxMs,
    double AvgResponseLen,
    DateTimeOffset LastCalledAt);
