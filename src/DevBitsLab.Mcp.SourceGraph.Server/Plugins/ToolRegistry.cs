using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Concrete <see cref="IToolRegistry"/> handed to plugin <see cref="IMcpToolPlugin.RegisterAsync"/>
/// invocations. Wraps an <see cref="IMcpServerBuilder"/> via <c>WithTools(IEnumerable&lt;McpServerTool&gt;)</c>:
/// every plugin tool registered here ends up exposed over MCP exactly like the built-in tools, but
/// with a mandatory <c>{prefix}.{name}</c> wire-level name. Tool-name collisions (the same final
/// name from two plugins, or a plugin trying to shadow a built-in) are blocked at registration
/// with a clear error.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly string _prefix;
    private readonly HashSet<string> _claimedNames;
    private readonly List<McpServerTool> _registered = new();
    private readonly PluginRecord _ownerRecord;

    public ToolRegistry(string prefix, HashSet<string> claimedNames, PluginRecord ownerRecord)
    {
        _prefix = prefix;
        _claimedNames = claimedNames;
        _ownerRecord = ownerRecord;
    }

    /// <summary>Tools the plugin has registered so far. Returned to the host so it can call
    /// <see cref="McpServerBuilderExtensions.WithTools(IMcpServerBuilder, IEnumerable{McpServerTool})"/>
    /// in one shot after all plugins have run.</summary>
    public IReadOnlyList<McpServerTool> RegisteredTools => _registered;

    /// <inheritdoc />
    public void AddTool(string toolName, string description, Delegate handler)
        => AddToolCore(toolName, description, handler, trigger: null);

    /// <inheritdoc />
    public void AddTool(string toolName, string description, Delegate handler, string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            throw new ArgumentException("Trigger must be non-empty when calling the trigger-bearing overload.", nameof(trigger));
        }
        AddToolCore(toolName, description, handler, trigger);
    }

    private void AddToolCore(string toolName, string description, Delegate handler, string? trigger)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            throw new ArgumentException("Tool name must be non-empty.", nameof(toolName));
        }
        if (toolName.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Tool name `{toolName}` must not contain '.'; the prefix is added automatically by the host.", nameof(toolName));
        }
        var fullName = $"{_prefix}.{toolName}";
        if (!_claimedNames.Add(fullName))
        {
            throw new InvalidOperationException(
                $"Tool name `{fullName}` is already registered. Plugin tool names must be globally unique after prefixing.");
        }

        var tool = McpServerTool.Create(handler, new McpServerToolCreateOptions
        {
            Name = fullName,
            Description = ToolDescriptionFormatter.AppendTrigger(description, trigger),
        });
        _registered.Add(tool);
        _ownerRecord.RegisteredToolNames.Add(fullName);
    }
}
