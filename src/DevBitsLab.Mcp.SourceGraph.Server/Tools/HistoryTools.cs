using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Resources;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Protocol;
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

    // Diagnostic short-circuits route through DevBitsLab.Mcp.SourceGraph.Server.Tools.Output.DiagnosticResult.Build,
    // shared with GraphTools, ScopeTools, and ScopedExecution.

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListTestsForResult))]
    [ToolTrigger("\"what tests cover X?\" — call before refactoring")]
    [Description("List the test methods that exercise a production symbol. Walks inbound `Tests` edges and returns each test's location, framework, and class.")]
    public static Task<CallToolResult> ListTestsForAsync(
        ScopeRouter router,
        [Description("Production symbol exact canonical key, name, or FQN (e.g. 'Calculator.Add', 'Sample.Domain.Calculator.Multiply').")] string symbol,
        [Description("Reserved for a future change that walks transitive callers; currently ignored.")] bool includeIndirect = false,
        [Description("Maximum results (default 50).")] int limit = 50,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_tests_for", new { symbol, includeIndirect, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var hits = await SymbolQueryResolver.ResolveAsync(
                    host.Store,
                    symbol,
                    limit: 5,
                    ct).ConfigureAwait(false);
                if (hits.Count == 0) return DiagnosticResult.Build($"No matches for '{symbol}'.");
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
                    return BuildListTestsForResult(
                        prose: sb.ToString(),
                        target: top,
                        tests: Array.Empty<TestForHit>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
                if (tests.Count >= 2)
                {
                    var tableRows = new List<IReadOnlyList<string>>(tests.Count);
                    foreach (var t in tests)
                    {
                        var fw = string.IsNullOrEmpty(t.Framework) ? "unknown" : t.Framework;
                        tableRows.Add(new[]
                        {
                            fw,
                            $"**{t.Test.Fqn}**",
                            Format.Location(t.Test.FilePath, t.Test.StartLine, t.Test.StartCol),
                        });
                    }
                    Format.AppendTable(sb, new[] { "Framework", "Test", "Location" }, tableRows);
                }
                else
                {
                    foreach (var t in tests)
                    {
                        var fw = string.IsNullOrEmpty(t.Framework) ? "unknown" : t.Framework;
                        sb.AppendLine($"- [{fw}] **{t.Test.Fqn}** at {Format.Location(t.Test.FilePath, t.Test.StartLine, t.Test.StartCol)}");
                    }
                }
                return BuildListTestsForResult(
                    prose: sb.ToString(),
                    target: top,
                    tests: tests,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildListTestsForResult(
        string prose,
        SymbolHit target,
        IReadOnlyList<TestForHit> tests,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + tests.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var t in tests)
        {
            var fw = string.IsNullOrEmpty(t.Framework) ? "unknown" : t.Framework;
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(t.Test.Id),
                Name = t.Test.Fqn,
                Title = $"[{fw}] {t.Test.Fqn}",
                Description = $"test — {Format.Location(t.Test.FilePath, t.Test.StartLine, t.Test.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("tests", tests.Count.ToString())));

        var structuredTests = tests
            .Select(t => new ListTestsForRow(
                TestSymbolId: t.Test.Id,
                Framework: string.IsNullOrEmpty(t.Framework) ? "unknown" : t.Framework,
                TestFqn: t.Test.Fqn,
                FilePath: t.Test.FilePath,
                Line: t.Test.StartLine,
                Column: t.Test.StartCol))
            .ToList();
        var dto = new ListTestsForResult(
            TargetSymbolId: target.Id,
            TargetFqn: target.Fqn,
            TargetKind: target.Kind,
            Tests: structuredTests);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListTestsForResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(WhoAuthoredResult))]
    [ToolTrigger("\"who last touched X?\" or \"when did X change?\"")]
    [Description("Return the cached git-blame summary for a symbol: last commit sha (7-char), author, ISO-8601 authored time, and lines blamed.")]
    public static Task<CallToolResult> WhoAuthoredAsync(
        ScopeRouter router,
        HistoryOptions options,
        [Description("Exact canonical key, symbol name, or FQN.")] string symbol,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("who_authored", new { symbol, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (options.Disabled)
                {
                    return DiagnosticResult.Error("git history unavailable on this server (--no-history)");
                }
                var hits = await SymbolQueryResolver.ResolveAsync(
                    host.Store,
                    symbol,
                    limit: 5,
                    ct).ConfigureAwait(false);
                var top = hits.FirstOrDefault(hit => IsAllowedHistoryPath(host, hit.FilePath));
                if (top is null) return DiagnosticResult.Build($"No matches for '{symbol}'.");
                var history = await host.Store.GetSymbolHistoryAsync(top.Id, ct).ConfigureAwait(false);
                if (history is null || string.IsNullOrEmpty(history.LastCommitSha))
                {
                    // No-history path still ships structured payload with null git fields so agents
                    // can branch on the absence rather than parse the prose.
                    return BuildWhoAuthoredResult(
                        prose: $"No git history yet for '{top.Fqn}'. Either the file wasn't blamed (e.g. uncommitted) or the pipeline hasn't reached it.",
                        target: top,
                        history: history,
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
                var line = Format.HistoryLine(history) + $" — {top.Fqn}";
                return BuildWhoAuthoredResult(
                    prose: line,
                    target: top,
                    history: history,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildWhoAuthoredResult(
        string prose,
        SymbolHit target,
        DevBitsLab.Mcp.SourceGraph.Core.SymbolHistory? history,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 3)
        {
            new TextContentBlock { Text = prose },
            // Singleton link to the resolved target so card-rendering UIs surface the symbol's
            // graph card alongside the prose.
            new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(target.Id),
                Name = target.Fqn,
                Title = target.Fqn,
                Description = $"{target.Kind} — {Format.Location(target.FilePath, target.StartLine, target.StartCol)}",
                MimeType = "text/markdown",
            },
        };

        content.Add(AudienceMetadata.Build(scopeId, elapsedMs));

        // Render the 7-char short SHA and ISO-8601 authored timestamp on the wire so consumers
        // don't have to redo string formatting.
        var sha = history?.LastCommitSha is { Length: > 0 } s ? s[..Math.Min(7, s.Length)] : null;
        var authoredAtIso = history?.LastAuthoredAt is { } t ? t.ToString("o") : null;
        var dto = new WhoAuthoredResult(
            SymbolId: target.Id,
            Fqn: target.Fqn,
            Author: history?.LastAuthor,
            Sha: sha,
            AuthoredAt: authoredAtIso,
            BlamedLines: history?.LineCount);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.WhoAuthoredResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(RecentChangesResult))]
    [ToolTrigger("\"what changed last week?\" or \"what has Alice been working on?\"")]
    [Description("List symbols whose last git-blame authored time falls within the last N days, optionally filtered by author substring.")]
    public static Task<CallToolResult> RecentChangesAsync(
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (options.Disabled)
                {
                    return DiagnosticResult.Error("git history unavailable on this server (--no-history)");
                }
                var sinceMs = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
                var rows = (await host.Store.ListRecentChangesAsync(sinceMs, author, limit, ct).ConfigureAwait(false))
                    .Where(row => IsAllowedHistoryPath(host, row.Symbol.FilePath))
                    .ToList();
                var sb = new StringBuilder();
                var authorClause = string.IsNullOrEmpty(author) ? "" : $" by author~'{author}'";
                var symbolNoun = rows.Count == 1 ? "symbol" : "symbols";
                sb.AppendLine($"{rows.Count} {symbolNoun} changed in the last {days} day(s){authorClause}:");
                if (rows.Count >= 2)
                {
                    var tableRows = new List<IReadOnlyList<string>>(rows.Count);
                    foreach (var r in rows)
                    {
                        var when = r.History.LastAuthoredAt is { } t ? t.ToString("yyyy-MM-dd") : "?";
                        var who = r.History.LastAuthor ?? "(unknown)";
                        var sha = r.History.LastCommitSha is { Length: > 0 } s ? s[..Math.Min(7, s.Length)] : "(none)";
                        tableRows.Add(new[]
                        {
                            $"{when} ({sha})",
                            who,
                            $"**{r.Symbol.Fqn}**",
                            Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol),
                        });
                    }
                    Format.AppendTable(sb, new[] { "When", "Author", "Symbol", "Location" }, tableRows);
                }
                else if (rows.Count == 1)
                {
                    foreach (var r in rows)
                    {
                        sb.AppendLine($"- {Format.HistoryLine(r.History)} — **{r.Symbol.Fqn}** at {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}");
                    }
                }
                return BuildRecentChangesResult(
                    prose: sb.ToString(),
                    days: days,
                    author: author,
                    rows: rows,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildRecentChangesResult(
        string prose,
        int days,
        string? author,
        IReadOnlyList<RecentChangeHit> rows,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + rows.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var r in rows)
        {
            var when = r.History.LastAuthoredAt is { } t ? t.ToString("yyyy-MM-dd") : "?";
            var who = r.History.LastAuthor ?? "(unknown)";
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(r.Symbol.Id),
                Name = r.Symbol.Fqn,
                Title = $"{when} {who}: {r.Symbol.Fqn}",
                Description = $"{r.Symbol.Kind} — {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("days", days.ToString()),
            ("changes", rows.Count.ToString())));

        var structuredRows = rows
            .Select(r =>
            {
                var sha = r.History.LastCommitSha is { Length: > 0 } s ? s[..Math.Min(7, s.Length)] : null;
                var authoredAtIso = r.History.LastAuthoredAt is { } t ? t.ToString("o") : null;
                return new RecentChangesRow(
                    SymbolId: r.Symbol.Id,
                    Fqn: r.Symbol.Fqn,
                    Kind: r.Symbol.Kind,
                    FilePath: r.Symbol.FilePath,
                    Line: r.Symbol.StartLine,
                    Column: r.Symbol.StartCol,
                    CommitSha: sha,
                    Author: r.History.LastAuthor,
                    AuthoredAt: authoredAtIso);
            })
            .ToList();
        var dto = new RecentChangesResult(
            Days: days,
            Author: string.IsNullOrEmpty(author) ? null : author,
            Changes: structuredRows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.RecentChangesResult),
        };
    }

    private static bool IsAllowedHistoryPath(ScopeHost host, string? path)
    {
        try
        {
            return !new ScopePathPolicy(
                    Path.GetFullPath(host.Scope.Root),
                    host.Scope.ProjectSet.Exclude)
                .IsExcluded(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}
