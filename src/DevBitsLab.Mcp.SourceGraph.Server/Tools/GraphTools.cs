using System.ComponentModel;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

[McpServerToolType]
public static class GraphTools
{
    [McpServerTool]
    [Description("Find the definition of a symbol by name or fully-qualified name. Returns location, kind, and signature for each match. Use for 'where is X defined?'.")]
    public static Task<string> FindDefinitionAsync(
        IGraphStore store,
        [Description("Symbol name (e.g. 'Calculator', 'Divide') or FQN suffix (e.g. 'Calculator.Add', 'Sample.Domain.Calculator')")] string symbol,
        [Description("Optional substring to narrow the search to specific file paths")] string? fileHint = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_definition", new { symbol, fileHint }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, fileHint, limit: 25, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No definition found for '{symbol}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {hits.Count} match(es) for '{symbol}':");
            sb.AppendLine();
            foreach (var h in hits)
            {
                sb.AppendLine($"- **{h.Fqn}** ({KindLabel(h.Kind)})");
                sb.AppendLine($"  - {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
                if (!string.IsNullOrEmpty(h.Signature)) sb.AppendLine($"  - `{h.Signature}`");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Find every place that references a symbol. Resolves the symbol by name/FQN, then lists each call site or type-use as file:line. Use for 'who uses X?' or 'who calls X?'.")]
    public static Task<string> FindReferencesAsync(
        IGraphStore store,
        [Description("Symbol name or FQN, same matching rules as find_definition")] string symbol,
        [Description("Maximum number of references to return (default 200)")] int limit = 200,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_references", new { symbol, limit }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";

            var sb = new StringBuilder();
            if (hits.Count > 1)
            {
                sb.AppendLine($"Multiple symbols match '{symbol}'; reporting references for the top match. Other matches: {string.Join(", ", hits.Skip(1).Select(h => h.Fqn))}");
                sb.AppendLine();
            }
            var top = hits[0];
            var refs = await store.FindReferencesAsync(top.Id, limit, ct).ConfigureAwait(false);
            sb.AppendLine($"References to **{top.Fqn}** ({KindLabel(top.Kind)}):");
            sb.AppendLine($"- definition: {Format.Location(top.FilePath, top.StartLine, top.StartCol)}");
            if (refs.Count == 0)
            {
                sb.AppendLine("- no other references in the graph");
                return sb.ToString();
            }
            sb.AppendLine();
            sb.AppendLine($"{refs.Count} reference(s):");
            foreach (var r in refs)
            {
                sb.AppendLine($"- {RefKindLabel(r.Kind)} at {Format.Location(r.FilePath, r.Line, r.Col)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List every symbol declared in a file (classes, methods, properties, etc.). Use for 'what's in this file?' to skip a Read.")]
    public static Task<string> ListSymbolsInFileAsync(
        IGraphStore store,
        [Description("Absolute path or path suffix that uniquely identifies the file")] string path,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_symbols_in_file", new { path }, async () =>
        {
            var hits = await store.ListSymbolsInFileAsync(path, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No indexed symbols found in '{path}'. The file may not be part of an indexed solution, or may not exist.";

            var sb = new StringBuilder();
            sb.AppendLine($"{hits.Count} symbol(s) in {hits[0].FilePath}:");
            foreach (var h in hits)
            {
                sb.AppendLine($"- L{h.StartLine}: **{h.Name}** ({KindLabel(h.Kind)}) — {h.Fqn}");
                if (!string.IsNullOrEmpty(h.Signature)) sb.AppendLine($"    `{h.Signature}`");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List the named methods/properties that call into a target symbol. Use for impact-of-change or trace-the-callers analysis.")]
    public static Task<string> ListCallersAsync(
        IGraphStore store,
        [Description("Target symbol name or FQN")] string symbol,
        [Description("Maximum number of callers to return (default 50)")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callers", new { symbol, limit }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var callers = await store.ListCallersAsync(top.Id, limit, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Callers of **{top.Fqn}** ({KindLabel(top.Kind)}):");
            if (callers.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
            foreach (var c in callers)
            {
                sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List every named method/property the target symbol calls. Inverse of list_callers.")]
    public static Task<string> ListCalleesAsync(
        IGraphStore store,
        [Description("Source symbol name or FQN")] string symbol,
        [Description("Maximum number of callees to return (default 50)")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callees", new { symbol, limit }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var callees = await store.ListCalleesAsync(top.Id, limit, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Callees of **{top.Fqn}** ({KindLabel(top.Kind)}):");
            if (callees.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
            foreach (var c in callees)
            {
                sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Free-text search for symbols by partial name, FQN, or signature using FTS5. Use this when you only have a fragment ('Calc', 'Greet', 'Async').")]
    public static Task<string> SearchSymbolsAsync(
        IGraphStore store,
        [Description("Search query (a few characters, words, or substring of name/FQN/signature)")] string query,
        [Description("Optional kind filter: class|method|property|field|interface|namespace|...")] string? kind = null,
        [Description("Maximum results (default 25)")] int topK = 25,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("search_symbols", new { query, kind, topK }, async () =>
        {
            SymbolKind? kindFilter = string.IsNullOrEmpty(kind)
                ? null
                : Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var k) ? k : null;

            var hits = await store.SearchSymbolsAsync(query, kindFilter, topK, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbols match '{query}'.";
            var sb = new StringBuilder();
            sb.AppendLine($"{hits.Count} match(es) for '{query}':");
            foreach (var h in hits)
            {
                sb.AppendLine($"- **{h.Fqn}** ({KindLabel(h.Kind)}) at {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Get the immediate graph neighborhood of a symbol: callers, callees, and inheritance/implements edges. Use to orient yourself around a symbol before diving in.")]
    public static Task<string> NeighborhoodAsync(
        IGraphStore store,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Max items per category (default 20)")] int perCategory = 20,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("neighborhood", new { symbol, perCategory }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var callers = await store.ListCallersAsync(top.Id, perCategory, ct).ConfigureAwait(false);
            var callees = await store.ListCalleesAsync(top.Id, perCategory, ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.AppendLine($"Neighborhood of **{top.Fqn}** ({KindLabel(top.Kind)})");
            sb.AppendLine($"definition: {Format.Location(top.FilePath, top.StartLine, top.StartCol)}");
            sb.AppendLine();
            sb.AppendLine($"### Callers ({callers.Count})");
            foreach (var c in callers) sb.AppendLine($"- {c.Fqn} — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
            if (callers.Count == 0) sb.AppendLine("- (none)");
            sb.AppendLine();
            sb.AppendLine($"### Callees ({callees.Count})");
            foreach (var c in callees) sb.AppendLine($"- {c.Fqn} — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
            if (callees.Count == 0) sb.AppendLine("- (none)");
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Summarize a namespace or directory: lists the most-referenced symbols (highest in-degree) so you know what to read first. Use 'Sample.Domain' or a path fragment.")]
    public static Task<string> ModuleSummaryAsync(
        IGraphStore store,
        [Description("Namespace (e.g. 'Sample.Domain') or path-substring that identifies the module")] string namespaceOrPath,
        [Description("Top-K most-referenced symbols to return (default 25)")] int topK = 25,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("module_summary", new { namespaceOrPath, topK }, async () =>
        {
            var rows = await store.ModuleSummaryAsync(namespaceOrPath, topK, ct).ConfigureAwait(false);
            if (rows.Count == 0) return $"No symbols matched module '{namespaceOrPath}'.";
            var sb = new StringBuilder();
            sb.AppendLine($"Top {rows.Count} symbol(s) in '{namespaceOrPath}' (by inbound calls):");
            foreach (var row in rows)
            {
                sb.AppendLine($"- in-deg {row.InDegree,3} — **{row.Symbol.Fqn}** ({KindLabel(row.Symbol.Kind)}) at {Format.Location(row.Symbol.FilePath, row.Symbol.StartLine, row.Symbol.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Compute the transitive set of upstream callers for a symbol (impact of changing it). Walks the call graph backward up to maxDepth.")]
    public static Task<string> ImpactOfChangeAsync(
        IGraphStore store,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Maximum traversal depth (default 4)")] int maxDepth = 4,
        [Description("Maximum results (default 100)")] int limit = 100,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("impact_of_change", new { symbol, maxDepth, limit }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var rows = await store.ImpactOfChangeAsync(top.Id, maxDepth, limit, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Upstream impact of **{top.Fqn}** ({KindLabel(top.Kind)}) up to depth {maxDepth}:");
            if (rows.Count == 0) { sb.AppendLine("- (no upstream callers found in graph)"); return sb.ToString(); }
            foreach (var r in rows)
            {
                sb.AppendLine($"- d{r.Depth}: **{r.Symbol.Fqn}** ({KindLabel(r.Symbol.Kind)}) at {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Print summary counts (files, symbols, references, edges) for the current graph database. Use to confirm the index is populated.")]
    public static Task<string> GraphStatsAsync(IGraphStore store, CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("graph_stats", null, async () =>
        {
            var s = await store.GetStatsAsync(ct).ConfigureAwait(false);
            return $"files={s.FileCount} symbols={s.SymbolCount} references={s.ReferenceCount} edges={s.EdgeCount}";
        });

    [McpServerTool]
    [Description("Show how often each MCP tool was called this server-process session: count, errors, avg/max latency, avg response size, last-called time. Use this to verify the agent is actually using the source-graph tools (vs grep+read fallback). Persistent log of every call is at usage.jsonl next to graph.db.")]
    public static string UsageStats() =>
        ToolMetrics.TrackSync("usage_stats", null, () =>
        {
            var snap = ToolMetrics.Snapshot();
            var sb = new StringBuilder();
            var uptime = DateTimeOffset.UtcNow - ToolMetrics.ProcessStart;
            sb.AppendLine($"sourcegraph-mcp uptime: {uptime:hh\\:mm\\:ss} (since {ToolMetrics.ProcessStart:HH:mm:ss UTC})");
            if (snap.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("No tool calls recorded yet.");
                return sb.ToString();
            }
            sb.AppendLine();
            sb.AppendLine("| Tool | Calls | Errors | Avg ms | Max ms | Avg resp | Last |");
            sb.AppendLine("|------|------:|-------:|-------:|-------:|---------:|------|");
            foreach (var (name, s) in snap.OrderByDescending(kv => kv.Value.Count))
            {
                var lastAgo = (DateTimeOffset.UtcNow - s.LastCalledAt).TotalSeconds;
                sb.AppendLine($"| `{name}` | {s.Count} | {s.Errors} | {s.AvgMs:F1} | {s.MaxMs} | {(int)s.AvgResponseLen}B | {lastAgo:F0}s ago |");
            }
            if (!string.IsNullOrEmpty(ToolMetrics.LogPath))
            {
                sb.AppendLine();
                sb.AppendLine($"_Persistent JSONL log: `{ToolMetrics.LogPath}`_");
            }
            return sb.ToString();
        });

    private static string KindLabel(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace => "namespace",
        SymbolKind.Class => "class",
        SymbolKind.Struct => "struct",
        SymbolKind.Interface => "interface",
        SymbolKind.Enum => "enum",
        SymbolKind.EnumMember => "enum member",
        SymbolKind.Delegate => "delegate",
        SymbolKind.Method => "method",
        SymbolKind.Constructor => "ctor",
        SymbolKind.Property => "property",
        SymbolKind.Field => "field",
        SymbolKind.Event => "event",
        SymbolKind.Local => "local",
        SymbolKind.Parameter => "parameter",
        SymbolKind.TypeParameter => "type parameter",
        _ => "?",
    };

    private static string RefKindLabel(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Definition => "def",
        ReferenceKind.Reference => "ref",
        ReferenceKind.Call => "call",
        ReferenceKind.Implements => "impl",
        ReferenceKind.Inherits => "inherit",
        _ => "?",
    };
}

internal static class Format
{
    public static string Location(string path, int line, int col) => $"{path}:{line}:{col}";
}
