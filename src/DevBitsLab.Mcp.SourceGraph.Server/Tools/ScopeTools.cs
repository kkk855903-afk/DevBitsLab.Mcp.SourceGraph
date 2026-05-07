using System.ComponentModel;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// MCP tools for inspecting the scope registry. Pairs with the optional <c>scope</c> parameter
/// every other query tool gained.
/// </summary>
[McpServerToolType]
public static class ScopeTools
{
    [McpServerTool]
    [ToolTrigger("\"what scopes are configured?\" — call before passing the `scope` parameter to other tools, or after a 'no default_scope' error")]
    [Description("List every registered scope: id, name, root directory, project count, last-indexed timestamp, status (ok | degraded | indexing), and isolation flag. Pair with the optional `scope` parameter on every other tool.")]
    public static string ListScopes(ScopeRouter router) =>
        ToolMetrics.TrackSync("list_scopes", null, () =>
        {
            var hosts = router.All();
            if (hosts.Count == 0)
            {
                return "No scopes registered. Run `sourcegraph-mcp init-scopes` to scaffold .sourcegraph.json, or pass --solution to register a single-scope default.";
            }
            var sb = new StringBuilder();
            sb.AppendLine($"{hosts.Count} scope(s) registered:");
            sb.AppendLine();
            sb.AppendLine("| Id | Name | Status | Isolated | Projects | Last indexed | Root |");
            sb.AppendLine("|----|------|--------|---------:|---------:|--------------|------|");
            foreach (var host in hosts)
            {
                var scope = host.Scope;
                var projectCount = scope.ProjectSet switch
                {
                    Core.ScopeProjectSet.Solutions s => s.Items.Count,
                    Core.ScopeProjectSet.Projects p => p.Items.Count,
                    Core.ScopeProjectSet.Paths g => g.Globs.Count,
                    _ => 0,
                };
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
            return sb.ToString();
        });
}
