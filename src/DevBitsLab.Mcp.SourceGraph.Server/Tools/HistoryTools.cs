using System.ComponentModel;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
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
    private const string ScopeDescription =
        "Optional scope id, the literal '*' for all non-isolated scopes, or a comma-separated list of ids " +
        "(e.g. 'frontend,backend'). Omit to use `default_scope` from .sourcegraph.json. Call `list_scopes` to discover.";

    [McpServerTool]
    [ToolTrigger("\"what tests cover X?\" — call before refactoring")]
    [Description("List the test methods that exercise a production symbol. Walks inbound `Tests` edges and returns each test's location, framework, and class.")]
    public static Task<string> ListTestsForAsync(
        ScopeRouter router,
        [Description("Production symbol name or FQN (e.g. 'Calculator.Add', 'Sample.Domain.Calculator.Multiply').")] string symbol,
        [Description("Reserved for a future change that walks transitive callers; currently ignored.")] bool includeIndirect = false,
        [Description("Maximum results (default 50).")] int limit = 50,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_tests_for", new { symbol, includeIndirect, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
                var top = hits[0];
                var tests = await host.Store.ListTestsForAsync(top.Id, limit, ct).ConfigureAwait(false);
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
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"who last touched X?\" or \"when did X change?\"")]
    [Description("Return the cached git-blame summary for a symbol: last commit sha (7-char), author, ISO-8601 authored time, and lines blamed.")]
    public static Task<string> WhoAuthoredAsync(
        ScopeRouter router,
        HistoryOptions options,
        [Description("Symbol name or FQN.")] string symbol,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("who_authored", new { symbol, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                if (options.Disabled)
                {
                    return "git history unavailable on this server (--no-history)";
                }
                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
                var top = hits[0];
                var history = await host.Store.GetSymbolHistoryAsync(top.Id, ct).ConfigureAwait(false);
                if (history is null || string.IsNullOrEmpty(history.LastCommitSha))
                {
                    return $"No git history yet for '{top.Fqn}'. Either the file wasn't blamed (e.g. uncommitted) or the pipeline hasn't reached it.";
                }
                return Format.HistoryLine(history) + $" — {top.Fqn}";
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"what changed last week?\" or \"what has Alice been working on?\"")]
    [Description("List symbols whose last git-blame authored time falls within the last N days, optionally filtered by author substring.")]
    public static Task<string> RecentChangesAsync(
        ScopeRouter router,
        HistoryOptions options,
        [Description("Window in days (default 7).")] int days = 7,
        [Description("Optional case-insensitive substring match against the author name (e.g. 'alice', 'jacques').")] string? author = null,
        [Description("Maximum results (default 50).")] int limit = 50,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("recent_changes", new { days, author, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                if (options.Disabled)
                {
                    return "git history unavailable on this server (--no-history)";
                }
                var sinceMs = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
                var rows = await host.Store.ListRecentChangesAsync(sinceMs, author, limit, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                var authorClause = string.IsNullOrEmpty(author) ? "" : $" by author~'{author}'";
                sb.AppendLine($"{rows.Count} symbol(s) changed in the last {days} day(s){authorClause}:");
                if (rows.Count == 0) return sb.ToString();
                foreach (var r in rows)
                {
                    sb.AppendLine($"- {Format.HistoryLine(r.History)} — **{r.Symbol.Fqn}** at {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}");
                }
                return sb.ToString();
            }, ct));
}
