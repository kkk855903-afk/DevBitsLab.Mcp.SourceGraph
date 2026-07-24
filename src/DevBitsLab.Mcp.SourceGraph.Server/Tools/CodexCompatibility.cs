using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Compatibility bridge for Codex builds that reject rich MCP tool results with
/// <c>Unexpected response type</c>. Rich results are reduced to the same single, plain text
/// content shape used by the server's universally compatible string tools.
/// </summary>
internal static class CodexCompatibility
{
    public const string EnvVarName = "SOURCEGRAPH_CODEX_COMPAT";

    /// <summary>Set once during server startup; a stdio server serves one client process.</summary>
    public static bool Enabled { get; set; }

    public static bool ShouldEnable(bool commandLineFlag)
    {
        if (commandLineFlag) return true;
        if (string.Equals(
                Environment.GetEnvironmentVariable(EnvVarName),
                "1",
                StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    public static CallToolResult NormalizeToolResult(CallToolResult result)
    {
        var content = result.Content ?? [];
        var visible = content
            .OfType<TextContentBlock>()
            .FirstOrDefault(IsUserVisible)
            ?? content.OfType<TextContentBlock>().FirstOrDefault();

        var text = visible?.Text;
        if (string.IsNullOrEmpty(text) && result.StructuredContent is { } structured)
        {
            text = structured.GetRawText();
        }

        result.Content =
        [
            new TextContentBlock
            {
                Text = text ?? string.Empty,
            },
        ];
        result.StructuredContent = null;
        result.Meta = null;
        return result;
    }

    /// <summary>
    /// A text-only tool must not advertise an output schema. Removing it keeps the downgraded
    /// result internally consistent with the MCP protocol.
    /// </summary>
    public static void RemoveOutputSchemas(IEnumerable<McpServerTool> tools)
    {
        foreach (var tool in tools)
        {
            tool.ProtocolTool.OutputSchema = null;
        }
    }

    private static bool IsUserVisible(TextContentBlock block)
    {
        var audience = block.Annotations?.Audience;
        return audience is null
            || audience.Count == 0
            || audience.Contains(Role.User);
    }
}
