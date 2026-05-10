using System.Diagnostics;
using System.Text.Json;

namespace DevBitsLab.Mcp.SourceGraph.Server.Observability;

/// <summary>
/// Persistent log of heal events emitted by the server. Each call to <see cref="Append"/>
/// writes one JSONL line to the configured path AND increments
/// <see cref="Telemetry.HealFired"/> with structured tags. Mirrors the
/// <c>usage.jsonl</c> + tool-counter pattern that <see cref="ToolMetrics"/> uses for tool calls.
///
/// Best-effort: write failures are swallowed silently so observability never breaks a heal action.
/// The Counter is independent of the log — it fires even when <see cref="Configure"/> wasn't called
/// or when <see cref="Configure"/> was called with <c>null</c>.
/// </summary>
public static class HealLog
{
    private static readonly Lock _writeLock = new();
    private static string? _logPath;

    /// <summary>Configures the JSONL output path. Call once at process start.</summary>
    public static void Configure(string? logPath)
    {
        _logPath = logPath;
        if (!string.IsNullOrEmpty(logPath))
        {
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Records one heal event. Writes a JSONL line of shape
    /// <c>{"ts":"…","kind":"…","scope":"…","ok":true|false,"ms":…,"details":"…"}</c> when
    /// <see cref="Configure"/> was called with a non-null path; always increments the
    /// <see cref="Telemetry.HealFired"/> Counter with tags <c>kind</c>, <c>scope</c>, <c>ok</c>.
    /// </summary>
    public static void Append(string kind, string scope, bool ok, double ms, string? details = null)
    {
        // Counter fires unconditionally — Add is bounded-cost when no listener subscribes.
        var tags = new TagList
        {
            { "kind", kind },
            { "scope", scope },
            { "ok", ok },
        };
        Telemetry.HealFired.Add(1, tags);

        if (_logPath is null) return;
        var entry = new
        {
            ts = DateTimeOffset.UtcNow,
            kind,
            scope,
            ok,
            ms,
            details,
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
            // best effort — never let observability break a heal action
        }
    }

    public static string? LogPath => _logPath;
}
