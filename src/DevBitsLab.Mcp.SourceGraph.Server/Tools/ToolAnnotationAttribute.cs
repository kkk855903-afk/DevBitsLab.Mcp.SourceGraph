namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Declares MCP-spec tool annotations on a method-decorated <c>[McpServerTool]</c>. The SDK's
/// <c>McpServerToolAttribute</c> doesn't expose these directly today, so we apply them post-build
/// via <see cref="ToolDescriptionFormatter.ApplyAnnotationsFromAttributes"/> by mutating each
/// tool's <c>ProtocolTool.Annotations</c>.
///
/// <para>
/// Spec hints (per the MCP spec):
/// <list type="bullet">
///   <item><c>DestructiveHint = true</c>: irreversible operations (e.g. delete files). Hosts that
///         honour MCP annotations require explicit user confirmation.</item>
///   <item><c>IdempotentHint = true</c>: re-running the operation has the same effect as running
///         it once.</item>
///   <item><c>ReadOnlyHint = true</c>: the operation does not modify any state. Most query tools.</item>
/// </list>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAnnotationAttribute : Attribute
{
    /// <summary>True for irreversible operations (delete-on-disk, drop-table, etc.).</summary>
    public bool DestructiveHint { get; init; }

    /// <summary>True when re-running the operation has the same effect as running it once.</summary>
    public bool IdempotentHint { get; init; }

    /// <summary>True when the operation does not modify any state.</summary>
    public bool ReadOnlyHint { get; init; }
}
