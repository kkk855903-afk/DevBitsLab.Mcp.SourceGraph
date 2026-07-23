using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Observability;

/// <summary>
/// Tracks tool-call activity. Each tool wraps its body in <see cref="TrackAsync"/>; the helper
/// records per-tool counters in memory and appends a metadata-only JSONL line to
/// <c>usage.jsonl</c> next to the graph database. Request values are never persisted because
/// symbol queries, paths, and SQL parameters may contain patient or proprietary data.
/// Inspectable at runtime via the <c>usage_stats</c> MCP tool.
/// </summary>
public static class ToolMetrics
{
    private static readonly ConcurrentDictionary<string, ToolStats> _stats = new();
    private static readonly ConcurrentDictionary<string, long> _scopeCounts = new();
    private static readonly Lock _writeLock = new();
    private static string? _logPath;
    private static readonly DateTimeOffset _processStart = DateTimeOffset.UtcNow;

    /// <summary>Configures the JSONL output path. Call once at process start.</summary>
    public static void Configure(string? logPath)
    {
        _logPath = logPath;
        if (logPath is null) return;
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public static async Task<string> TrackAsync(string toolName, object? args, Func<Task<string>> body)
    {
        // StartActivity returns null when no OTel listener is attached, so the using-block is a
        // no-op outside an instrumented host. Activity name follows OTel semconv "rpc-like" shape.
        using var activity = Telemetry.ActivitySource.StartActivity(
            $"mcp.tool {toolName}", ActivityKind.Server);
        activity?.SetTag("mcp.tool.name", toolName);
        activity?.SetTag("mcp.tool.scope", ExtractScope(args));

        var sw = Stopwatch.StartNew();
        var ok = true;
        var result = string.Empty;
        try
        {
            result = await body().ConfigureAwait(false);
            // The leaf is presentation, not payload — `result` (the local) stays unbranded so
            // the `finally` block records true response size, while the caller gets the branded
            // version. See openspec/changes/add-leaf-brand-mark/design.md (Decision 3).
            return LeafFormatter.Brand(result);
        }
        catch (Exception ex)
        {
            ok = false;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            throw;
        }
        finally
        {
            sw.Stop();
            var responseBytes = System.Text.Encoding.UTF8.GetByteCount(result);
            activity?.SetTag("mcp.tool.response_bytes", responseBytes);
            Record(toolName, args, result.Length, responseBytes, sw.Elapsed, ok);
        }
    }

    public static string TrackSync(string toolName, object? args, Func<string> body)
    {
        using var activity = Telemetry.ActivitySource.StartActivity(
            $"mcp.tool {toolName}", ActivityKind.Server);
        activity?.SetTag("mcp.tool.name", toolName);
        activity?.SetTag("mcp.tool.scope", ExtractScope(args));

        var sw = Stopwatch.StartNew();
        var ok = true;
        var result = string.Empty;
        try
        {
            result = body();
            return LeafFormatter.Brand(result);
        }
        catch (Exception ex)
        {
            ok = false;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            throw;
        }
        finally
        {
            sw.Stop();
            var responseBytes = System.Text.Encoding.UTF8.GetByteCount(result);
            activity?.SetTag("mcp.tool.response_bytes", responseBytes);
            Record(toolName, args, result.Length, responseBytes, sw.Elapsed, ok);
        }
    }

    /// <summary>
    /// Multi-content overload: tools that return <see cref="IReadOnlyList{ContentBlock}"/> route
    /// here. The leaf brand mark is applied to the first user-visible <see cref="TextContentBlock"/>
    /// (audience-restricted blocks are skipped). Telemetry counts the total text length across all
    /// blocks so usage_stats / OTel response_size remain meaningful.
    /// </summary>
    public static async Task<IReadOnlyList<ContentBlock>> TrackAsync(
        string toolName, object? args, Func<Task<IReadOnlyList<ContentBlock>>> body)
    {
        using var activity = Telemetry.ActivitySource.StartActivity(
            $"mcp.tool {toolName}", ActivityKind.Server);
        activity?.SetTag("mcp.tool.name", toolName);
        activity?.SetTag("mcp.tool.scope", ExtractScope(args));

        var sw = Stopwatch.StartNew();
        var ok = true;
        IReadOnlyList<ContentBlock> result = Array.Empty<ContentBlock>();
        try
        {
            result = await body().ConfigureAwait(false);
            return LeafFormatter.BrandFirstText(result);
        }
        catch (Exception ex)
        {
            ok = false;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            throw;
        }
        finally
        {
            sw.Stop();
            var (textLen, byteLen) = MeasureContent(result);
            activity?.SetTag("mcp.tool.response_bytes", byteLen);
            Record(toolName, args, textLen, byteLen, sw.Elapsed, ok);
        }
    }

    /// <summary>
    /// Rich-result overload: tools that need <see cref="CallToolResult.StructuredContent"/> or
    /// <see cref="CallToolResult.IsError"/> route here. Same leaf-branding rule as the
    /// content-list overload (the first user-visible <see cref="TextContentBlock"/> is prefixed
    /// with the brand mark), and same telemetry — measured on the **unbranded** payload so the
    /// recorded response size is comparable to the string and content-list overloads. When
    /// <see cref="CallToolResult.IsError"/> is true the call records as ok=false so dashboards
    /// surface tool-reported errors alongside thrown exceptions.
    ///
    /// No runtime guard against anonymous types — both <see cref="CallToolResult.StructuredContent"/>
    /// (<see cref="System.Text.Json.JsonElement"/>?) and <see cref="CallToolResult.Meta"/>
    /// (<see cref="System.Text.Json.Nodes.JsonObject"/>?) are typed strictly enough that the SDK's
    /// source-gen <c>JsonContext</c> can't reject them at wire time; the C# compiler enforces the
    /// shape at assignment instead.
    /// </summary>
    public static async Task<CallToolResult> TrackAsync(
        string toolName, object? args, Func<Task<CallToolResult>> body)
    {
        using var activity = Telemetry.ActivitySource.StartActivity(
            $"mcp.tool {toolName}", ActivityKind.Server);
        activity?.SetTag("mcp.tool.name", toolName);
        activity?.SetTag("mcp.tool.scope", ExtractScope(args));

        var sw = Stopwatch.StartNew();
        var ok = true;
        CallToolResult result = new() { Content = Array.Empty<ContentBlock>() };
        // Snapshots of the unbranded shape captured before LeafFormatter.BrandFirstText mutates
        // result.Content in place. Telemetry measures these so the recorded byte count reflects
        // payload size, not branded prose — consistent with the string and IReadOnlyList<ContentBlock>
        // overloads which also measure unbranded.
        IReadOnlyList<ContentBlock> unbrandedContent = Array.Empty<ContentBlock>();
        JsonElement? unbrandedStructured = null;
        try
        {
            result = await body().ConfigureAwait(false);
            unbrandedContent = result.Content as IReadOnlyList<ContentBlock>
                ?? (result.Content is null ? Array.Empty<ContentBlock>() : (IReadOnlyList<ContentBlock>)result.Content.ToArray());
            unbrandedStructured = result.StructuredContent;
            LeafFormatter.BrandFirstText(result);
            return result;
        }
        catch (Exception ex)
        {
            ok = false;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            throw;
        }
        finally
        {
            sw.Stop();
            var (textLen, byteLen) = MeasureContent(unbrandedContent, unbrandedStructured);
            activity?.SetTag("mcp.tool.response_bytes", byteLen);
            // CallToolResult with IsError=true should still record as ok=false in telemetry so
            // dashboards see tool-reported errors alongside thrown exceptions.
            var effectiveOk = ok && (result.IsError != true);
            Record(toolName, args, textLen, byteLen, sw.Elapsed, effectiveOk);
        }
    }

    /// <summary>
    /// Sum the size of the user-renderable surface of a tool result: text-block contents,
    /// resource-link metadata (uri, name, title, description), and — when present — the raw JSON
    /// of <see cref="CallToolResult.StructuredContent"/>. Captures the bulk of what the SDK
    /// serialises for a real <c>tools/call</c> response, so <c>mcp.tool.response_bytes</c> stays
    /// representative when tools emit multi-content blocks or structured payloads. Other
    /// envelope-level fields (annotations, meta, isError) are small enough to skip.
    /// </summary>
    private static (int TextLen, int ByteCount) MeasureContent(
        IReadOnlyList<ContentBlock> content,
        JsonElement? structuredContent = null)
    {
        var len = 0;
        var bytes = 0;
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContentBlock t when !string.IsNullOrEmpty(t.Text):
                    len += t.Text.Length;
                    bytes += Encoding.UTF8.GetByteCount(t.Text);
                    break;
                case ResourceLinkBlock r:
                    // Sum every textual field a client renders for a resource link card.
                    AddString(ref len, ref bytes, r.Uri);
                    AddString(ref len, ref bytes, r.Name);
                    AddString(ref len, ref bytes, r.Title);
                    AddString(ref len, ref bytes, r.Description);
                    break;
            }
        }
        if (structuredContent.HasValue)
        {
            var raw = structuredContent.Value.GetRawText();
            len += raw.Length;
            bytes += Encoding.UTF8.GetByteCount(raw);
        }
        return (len, bytes);
    }

    private static void AddString(ref int len, ref int bytes, string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        len += s.Length;
        bytes += Encoding.UTF8.GetByteCount(s);
    }

    private static void Record(string toolName, object? args, int responseLen, int responseBytes, TimeSpan elapsed, bool ok)
    {
        var stats = _stats.GetOrAdd(toolName, _ => new ToolStats());
        stats.Add(elapsed, responseLen, ok);
        // Best-effort scope counter: pull `scope` off the args bag so usage_stats can show a
        // per-scope breakdown. Reflection-light: only fires for anonymous arg objects that
        // contain a `scope` property, which is the convention every scope-aware tool follows.
        var scopeName = ExtractScope(args);
        if (scopeName is not null)
        {
            _scopeCounts.AddOrUpdate(scopeName, 1, (_, c) => c + 1);
        }
        AppendJsonl(toolName, args, responseLen, elapsed, ok, scopeName);

        // OpenTelemetry signals — emitted unconditionally; the underlying instruments are no-ops
        // when no MeterListener is attached. Tags use OTel semconv-flavoured naming so the data
        // lights up in standard dashboards without a custom processor.
        var tags = scopeName is null
            ? new TagList { { "mcp.tool", toolName }, { "mcp.tool.ok", ok } }
            : new TagList { { "mcp.tool", toolName }, { "mcp.tool.ok", ok }, { "mcp.tool.scope", scopeName } };
        Telemetry.ToolCalls.Add(1, tags);
        if (!ok)
        {
            Telemetry.ToolErrors.Add(1, tags);
        }
        Telemetry.ToolDurationMs.Record(elapsed.TotalMilliseconds, tags);
        Telemetry.ToolResponseBytes.Record(responseBytes, tags);
    }

    private static string? ExtractScope(object? args)
    {
        if (args is null) return null;
        var prop = args.GetType().GetProperty("scope");
        if (prop is null) return null;
        var value = prop.GetValue(args);
        return value switch
        {
            string s when !string.IsNullOrEmpty(s) => s,
            _ => null,
        };
    }

    private static void AppendJsonl(string toolName, object? args, int responseLen, TimeSpan elapsed, bool ok, string? scope)
    {
        if (_logPath is null) return;
        // Measure the serialized request shape in memory, then discard it. Never persist argument
        // values: queries, file hints, paths, authors, and SQL bindings may contain patient data.
        // File I/O exceptions are absorbed by the broader catch below — observability is
        // best-effort and must never break the wrapped tool call.
        int requestLen;
        try
        {
            requestLen = args is null
                ? 0
                : JsonSerializer.SerializeToElement(args).GetRawText().Length;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            requestLen = 0;
        }
        var entry = new
        {
            ts = DateTimeOffset.UtcNow,
            tool = toolName,
            ok,
            ms = elapsed.TotalMilliseconds,
            request_len = requestLen,
            response_len = responseLen,
            scope,
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

    /// <summary>
    /// Per-scope call counts captured from the <c>scope</c> arg of every wrapped tool. Empty when
    /// no tool has been called with an explicit <c>scope</c> argument yet. Surfaced by
    /// <c>usage_stats</c> so users can see which scopes are getting exercised.
    /// </summary>
    public static IReadOnlyDictionary<string, long> ScopeSnapshot()
    {
        return _scopeCounts.ToDictionary(kv => kv.Key, kv => kv.Value);
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
