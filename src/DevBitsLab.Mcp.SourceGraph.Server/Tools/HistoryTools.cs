using System.ComponentModel;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Tools that expose the <c>integrate-tests-and-history</c> data:
///   - <c>list_tests_for</c>: walk inbound <c>Tests</c> edges to find every test that exercises a symbol.
///   - <c>who_authored</c>: lookup the cached <c>symbol_history</c> row for a symbol (last commit/author/time).
///   - <c>recent_changes</c>: list symbols whose <c>last_authored_at</c> falls in a recent window.
/// </summary>
[McpServerToolType]
public static class HistoryTools
{
    [McpServerTool]
    [Description("List the test methods that exercise a production symbol. Walks inbound `Tests` edges and returns each test's location, framework, and class. Use for 'what tests cover X?' before refactoring.")]
    public static Task<string> ListTestsForAsync(
        IGraphStore store,
        [Description("Production symbol name or FQN (e.g. 'Calculator.Add', 'Sample.Domain.Calculator.Multiply').")] string symbol,
        [Description("Reserved for a future change that walks transitive callers; currently ignored.")] bool includeIndirect = false,
        [Description("Maximum results (default 50).")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_tests_for", new { symbol, includeIndirect, limit }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var tests = await store.ListTestsForAsync(top.Id, limit, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Tests targeting **{top.Fqn}** ({Format.KindWithAttrs(top)}):");
            if (tests.Count == 0)
            {
                sb.AppendLine("- (none)");
                if (includeIndirect)
                {
                    sb.AppendLine();
                    sb.AppendLine("_(includeIndirect is reserved for a future change; transitive lookup is not yet implemented.)_");
                }
                return sb.ToString();
            }
            foreach (var t in tests)
            {
                var fw = string.IsNullOrEmpty(t.Framework) ? "unknown" : t.Framework;
                sb.AppendLine($"- [{fw}] **{t.Test.Fqn}** at {Format.Location(t.Test.FilePath, t.Test.StartLine, t.Test.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Return the cached git-blame summary for a symbol: last commit sha (7-char), author, ISO-8601 authored time, and lines blamed. Use for 'who last touched X?' or 'when did X change?'.")]
    public static Task<string> WhoAuthoredAsync(
        IGraphStore store,
        HistoryOptions options,
        [Description("Symbol name or FQN.")] string symbol,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("who_authored", new { symbol }, async () =>
        {
            if (options.Disabled)
            {
                return "git history unavailable on this server (--no-history)";
            }
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var history = await store.GetSymbolHistoryAsync(top.Id, ct).ConfigureAwait(false);
            if (history is null || string.IsNullOrEmpty(history.LastCommitSha))
            {
                return $"No git history yet for '{top.Fqn}'. Either the file wasn't blamed (e.g. uncommitted) or the pipeline hasn't reached it.";
            }
            return Format.HistoryLine(history) + $" — {top.Fqn}";
        });

    [McpServerTool]
    [Description("List symbols whose last git-blame authored time falls within the last N days, optionally filtered by author substring. Use for 'what changed last week?' or 'what has Alice been working on?'.")]
    public static Task<string> RecentChangesAsync(
        IGraphStore store,
        HistoryOptions options,
        [Description("Window in days (default 7).")] int days = 7,
        [Description("Optional case-insensitive substring match against the author name (e.g. 'alice', 'jacques').")] string? author = null,
        [Description("Maximum results (default 50).")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("recent_changes", new { days, author, limit }, async () =>
        {
            if (options.Disabled)
            {
                return "git history unavailable on this server (--no-history)";
            }
            var sinceMs = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            var rows = await store.ListRecentChangesAsync(sinceMs, author, limit, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            var authorClause = string.IsNullOrEmpty(author) ? "" : $" by author~'{author}'";
            sb.AppendLine($"{rows.Count} symbol(s) changed in the last {days} day(s){authorClause}:");
            if (rows.Count == 0) return sb.ToString();
            foreach (var r in rows)
            {
                sb.AppendLine($"- {Format.HistoryLine(r.History)} — **{r.Symbol.Fqn}** at {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}");
            }
            return sb.ToString();
        });
}
