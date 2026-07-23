using System.Reflection;
using ModelContextProtocol.Protocol;
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
    public const string EnvVarName = "SOURCEGRAPH_NO_TOOL_TRIGGERS";

    /// <summary>
    /// When true, <see cref="AppendTrigger"/> and <see cref="ApplyTriggersFromAttributes"/> become
    /// no-ops — the <c>Use when:</c> line is omitted from every tool description. Set once at
    /// process start by <c>Program.cs</c> from the <c>--no-tool-triggers</c> CLI flag or
    /// <c>SOURCEGRAPH_NO_TOOL_TRIGGERS=1</c> env var. Not intended to flip mid-session.
    /// </summary>
    public static bool Suppressed { get; set; }

    private const string UseWhenSeparator = "\n\nUse when: ";

    public static string AppendTrigger(string description, string? trigger)
    {
        if (Suppressed) return description;
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
        if (Suppressed) return;
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

    /// <summary>
    /// Apply every <see cref="ToolAnnotationAttribute"/> declared on the methods backing
    /// <paramref name="tools"/> by mutating each tool's <see cref="ModelContextProtocol.Protocol.Tool.Annotations"/>
    /// in place. Spec-aware MCP clients use these hints to require explicit user confirmation
    /// before invoking destructive tools (e.g. <c>embeddings_remove</c>).
    /// </summary>
    public static void ApplyAnnotationsFromAttributes(IEnumerable<McpServerTool> tools)
    {
        foreach (var tool in tools)
        {
            var method = tool.Metadata?.OfType<MethodInfo>().FirstOrDefault();
            var attr = method?.GetCustomAttribute<ToolAnnotationAttribute>();
            if (attr is null) continue;

            tool.ProtocolTool.Annotations ??= new ToolAnnotations();
            tool.ProtocolTool.Annotations.DestructiveHint = attr.DestructiveHint;
            tool.ProtocolTool.Annotations.IdempotentHint = attr.IdempotentHint;
            tool.ProtocolTool.Annotations.ReadOnlyHint = attr.ReadOnlyHint;
        }
    }
}
