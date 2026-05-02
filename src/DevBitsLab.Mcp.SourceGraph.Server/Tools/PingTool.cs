using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

[McpServerToolType]
public static class PingTool
{
    [McpServerTool]
    [Description("Returns 'pong' along with the server time. Useful to verify the source-graph MCP server is reachable.")]
    public static string Ping() =>
        $"pong @ {DateTimeOffset.UtcNow:O}";
}
