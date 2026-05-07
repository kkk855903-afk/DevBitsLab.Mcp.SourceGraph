using System;
using System.Threading.Tasks;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Plugin contract for adding additional MCP tools to the server. Tool names a plugin registers
/// are mandatory-prefixed with the plugin's <see cref="Prefix"/>: a plugin with
/// <c>Prefix = "mediatr"</c> registering a tool named <c>"find_handlers"</c> ends up exposing
/// <c>"mediatr.find_handlers"</c> over MCP. Built-in tools keep unprefixed names.
/// </summary>
public interface IMcpToolPlugin
{
    /// <summary>
    /// Mandatory non-empty prefix. Lowercase letters/digits/hyphens/underscores; '.' is reserved
    /// as the separator. The host validates this before invoking <see cref="RegisterAsync"/>.
    /// </summary>
    string Prefix { get; }

    /// <summary>
    /// Invoked once at MCP host startup. The plugin calls
    /// <see cref="IToolRegistry.AddTool(string, string, System.Delegate)"/> for each tool it
    /// wants to expose. The host throws if two plugins try to register the same final tool name
    /// (after prefixing); first registration wins.
    /// </summary>
    Task RegisterAsync(IToolRegistry registry);
}

/// <summary>
/// Registry abstraction tool plugins use to add tools. Wraps the underlying MCP server builder so
/// plugins don't take a binding on the MCP SDK directly.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Add a new tool. The final tool name exposed over MCP is
    /// <c>$"{plugin.Prefix}.{toolName}"</c>; the host enforces the prefix and rejects collisions.
    /// </summary>
    /// <param name="toolName">
    ///     Short tool name (without the prefix). Letters/digits/underscores. Will be combined with
    ///     the plugin's <see cref="IMcpToolPlugin.Prefix"/> to produce the wire-level name.
    /// </param>
    /// <param name="description">Human-readable description used by the MCP client to render the tool.</param>
    /// <param name="handler">Delegate implementing the tool body. Standard MCP SDK rules apply for parameter binding.</param>
    void AddTool(string toolName, string description, Delegate handler);

    /// <summary>
    /// Add a new tool that advertises a natural-language trigger phrase. The host appends a
    /// <c>Use when: &lt;trigger&gt;</c> line to the tool's effective description before
    /// registering it with the underlying MCP server, so the connected model sees the trigger
    /// alongside the tool's signature in the catalog. Use this overload when the tool answers
    /// a recognisable question shape (e.g. <c>"\"who handles MediatR request X?\""</c>); use the
    /// 3-arg <see cref="AddTool(string, string, Delegate)"/> for diagnostic / utility tools.
    /// </summary>
    /// <remarks>
    /// This method was added in SDK 1.1.0. The 3-arg <see cref="AddTool(string, string, Delegate)"/>
    /// remains on the interface unchanged, so plugins compiled against SDK 1.0.0 keep working
    /// without recompilation.
    /// </remarks>
    /// <param name="toolName">Short tool name (without the prefix).</param>
    /// <param name="description">Human-readable description used by the MCP client to render the tool.</param>
    /// <param name="handler">Delegate implementing the tool body.</param>
    /// <param name="trigger">
    ///     Natural-language question phrase the tool answers (typically wrapped in quotes,
    ///     e.g. <c>"\"who handles MediatR request X?\""</c>). Required, non-empty.
    /// </param>
    void AddTool(string toolName, string description, Delegate handler, string trigger);
}

/// <summary>
/// Optional assembly-level marker the plugin host uses for fast plugin discovery without scanning
/// every public type. Assemblies that carry <see cref="McpToolPluginAttribute"/> are still loaded
/// for their <see cref="ILanguageIndexer"/> / <see cref="ICodeAnalyzer"/> implementations even
/// when no <see cref="IMcpToolPlugin"/> is present; the attribute simply hints the host's
/// instantiation pass.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class McpToolPluginAttribute : Attribute
{
    /// <summary>
    /// Mandatory tool prefix the plugin's <see cref="IMcpToolPlugin"/> implementation will use.
    /// Allows the host to surface the prefix in <c>plugins info</c> output before instantiating
    /// the type, which is useful for diagnosing prefix collisions before the plugin even loads.
    /// Settable both positionally and via the <c>Prefix = "..."</c> named argument so the
    /// canonical attribute usage works either way.
    /// </summary>
    public string Prefix { get; set; }

    public McpToolPluginAttribute() : this("") { }

    public McpToolPluginAttribute(string prefix)
    {
        Prefix = prefix;
    }
}
