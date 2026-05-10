using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// MCP tools for inspecting the scope registry. Pairs with the optional <c>scope</c> parameter
/// every other query tool gained.
/// </summary>
[McpServerToolType]
public static class ScopeTools
{
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListScopesResult))]
    [ToolTrigger("\"what scopes are configured?\" — call before passing the `scope` parameter to other tools, or after a 'no default_scope' error")]
    [Description("List every registered scope: id, name, root directory, project count, last-indexed timestamp, status (ok | degraded | indexing), and isolation flag. Pair with the optional `scope` parameter on every other tool.")]
    public static Task<CallToolResult> ListScopesAsync(ScopeRouter router) =>
        // Body is sync but tracking goes through the async overload that knows how to brand-mark
        // the first user-visible TextContentBlock and serialise StructuredContent. Wrap in
        // Task.FromResult so the existing TrackAsync(CallToolResult) overload picks it up; no need
        // to introduce a sync-CallToolResult sibling for this one tool. The SDK's tool-name
        // derivation strips the `Async` suffix, so the wire-level tool name remains `list_scopes`
        // — matches every other converted tool's naming convention while keeping the contract
        // visible to C# callers.
        ToolMetrics.TrackAsync("list_scopes", null, () => Task.FromResult(BuildListScopesResultFromRouter(router)));

    private static CallToolResult BuildListScopesResultFromRouter(ScopeRouter router)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hosts = router.All();
        if (hosts.Count == 0)
        {
            // Empty-registry diagnostic still ships through the structured shape so consumers that
            // depend on `outputSchema` can probe `.scopes.length === 0` instead of parsing prose.
            return BuildListScopesResult(
                prose: "No scopes registered. Run `sourcegraph-mcp init-scopes` to scaffold .sourcegraph.json, or pass --solution to register a single-scope default.",
                hosts: Array.Empty<ScopeHost>(),
                defaultScope: router.DefaultScope,
                elapsedMs: sw.ElapsedMilliseconds);
        }

        var sb = new StringBuilder();
        var scopeNoun = hosts.Count == 1 ? "scope" : "scopes";
        sb.AppendLine($"{hosts.Count} {scopeNoun} registered:");
        sb.AppendLine();
        sb.AppendLine("| Id | Name | Status | Isolated | Projects | Last indexed | Root |");
        sb.AppendLine("|----|------|--------|---------:|---------:|--------------|------|");
        foreach (var host in hosts)
        {
            var scope = host.Scope;
            var projectCount = ProjectCount(scope.ProjectSet);
            var lastIndexed = host.LastIndexedAt == DateTimeOffset.MinValue
                ? "_never_"
                : host.LastIndexedAt.ToString("yyyy-MM-dd HH:mm:ss UTC");
            var statusCell = host.Status == "degraded" && !string.IsNullOrEmpty(host.StatusMessage)
                ? $"degraded ({host.StatusMessage})"
                : host.Status;
            sb.AppendLine($"| `{scope.Id}` | {scope.Name} | {statusCell} | {(scope.Isolated ? "yes" : "no")} | {projectCount} | {lastIndexed} | `{scope.Root}` |");
        }
        if (router.DefaultScope is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"_default_scope: `{router.DefaultScope}`_");
        }

        return BuildListScopesResult(
            prose: sb.ToString(),
            hosts: hosts,
            defaultScope: router.DefaultScope,
            elapsedMs: sw.ElapsedMilliseconds);
    }

    private static CallToolResult BuildListScopesResult(
        string prose,
        IReadOnlyList<ScopeHost> hosts,
        string? defaultScope,
        long elapsedMs)
    {
        // Per spec: no resource link per row — scopes don't have a graph:// URI scheme yet.
        // scopeId = null because list_scopes IS the scope listing (no single resolved scope).
        var content = new List<ContentBlock>(capacity: 2)
        {
            new TextContentBlock { Text = prose },
            AudienceMetadata.Build(
                scopeId: null,
                latencyMs: elapsedMs,
                ("scopes", hosts.Count.ToString())),
        };

        var rows = hosts
            .Select(host => new ListScopesRow(
                Id: host.Scope.Id,
                Name: host.Scope.Name,
                Root: host.Scope.Root,
                Status: host.Status,
                StatusMessage: string.IsNullOrEmpty(host.StatusMessage) ? null : host.StatusMessage,
                Isolated: host.Scope.Isolated,
                LastIndexedAt: host.LastIndexedAt == DateTimeOffset.MinValue
                    ? null
                    : host.LastIndexedAt.ToString("o"),
                ProjectCount: ProjectCount(host.Scope.ProjectSet)))
            .ToList();
        var dto = new ListScopesResult(DefaultScope: defaultScope, Scopes: rows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListScopesResult),
        };
    }

    private static int ProjectCount(Core.ScopeProjectSet projectSet) => projectSet switch
    {
        Core.ScopeProjectSet.Solutions s => s.Items.Count,
        Core.ScopeProjectSet.Projects p => p.Items.Count,
        Core.ScopeProjectSet.Paths g => g.Globs.Count,
        _ => 0,
    };
}
