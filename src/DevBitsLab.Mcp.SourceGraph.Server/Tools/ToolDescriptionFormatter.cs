using System.Reflection;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Single source of truth for the <c>Use when: &lt;trigger&gt;</c> append shared between two
/// registration paths: the static <c>WithToolsFromAssembly()</c> sweep (built-in tools — applied
/// post-hoc once tools are resolved from DI) and <see cref="Plugins.ToolRegistry"/> (plugin tools
/// — applied at <c>McpServerTool.Create</c> time).
/// </summary>
internal static class ToolDescriptionFormatter
{
    private const string UseWhenSeparator = "\n\nUse when: ";

    public static string AppendTrigger(string description, string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return description;
        return string.IsNullOrEmpty(description)
            ? $"Use when: {trigger.Trim()}"
            : $"{description}{UseWhenSeparator}{trigger.Trim()}";
    }

    /// <summary>
    /// Apply every <see cref="ToolTriggerAttribute"/> declared on the methods backing
    /// <paramref name="tools"/> by mutating the protocol-level <see cref="ModelContextProtocol.Protocol.Tool.Description"/>
    /// in place. The SDK reads this on every <c>tools/list</c> response, so the rewrite is visible
    /// without rebuilding the tool collection.
    /// </summary>
    public static void ApplyTriggersFromAttributes(IEnumerable<McpServerTool> tools)
    {
        foreach (var tool in tools)
        {
            // McpServerTool.Metadata is documented as carrying the source MethodInfo as the first
            // entry for tools created from a method. For tools created from a raw delegate (plugin
            // path) it may be absent; that's fine — those tools handle triggers via ToolRegistry.
            var method = tool.Metadata?.OfType<MethodInfo>().FirstOrDefault();
            var trigger = method?.GetCustomAttribute<ToolTriggerAttribute>()?.Trigger;
            if (string.IsNullOrWhiteSpace(trigger)) continue;

            var current = tool.ProtocolTool.Description ?? string.Empty;
            // Idempotency: if the trigger line is already present (e.g. the registration sweep ran
            // twice), don't keep stacking copies.
            var line = $"Use when: {trigger.Trim()}";
            if (current.EndsWith(line, StringComparison.Ordinal)) continue;

            tool.ProtocolTool.Description = AppendTrigger(current, trigger);
        }
    }
}
