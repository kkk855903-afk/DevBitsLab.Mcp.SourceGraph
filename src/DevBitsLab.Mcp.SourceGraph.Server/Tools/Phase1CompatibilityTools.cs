using System.ComponentModel;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Compatibility entry points for the Phase 1 MedInteropLens tool contract.
/// The established source-graph tools remain available under their original names.
/// </summary>
[McpServerToolType]
public static class Phase1CompatibilityTools
{
    private const string ScopeDescription =
        "Optional scope id, the literal '*' for all non-isolated scopes, or a comma-separated list of ids. " +
        "Omit to use `default_scope` from .sourcegraph.json.";

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindReferencesResult))]
    [ToolTrigger("\"find references to X\"")]
    [Description("Find each indexed reference occurrence for a symbol. Compatibility name for find_references; returns the same file, line, column, and reference-kind evidence.")]
    public static Task<CallToolResult> FindReferenceAsync(
        ScopeRouter router,
        [Description("Symbol name or FQN, same matching rules as find_definition")] string symbol,
        [Description("Maximum number of references to return (default 50)")] int limit = 50,
        [Description("Include references from source-generated files (default false)")] bool includeGenerated = false,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        GraphTools.FindReferencesAsync(router, symbol, limit, includeGenerated, scope, ct);

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListCallersResult))]
    [ToolTrigger("\"find callers of X\"")]
    [Description("Find evidence-backed inbound relations to a target. Compatibility name for list_callers; each row includes canonical source/target identities, the actual relation and confidence, plus stored occurrence file/range evidence.")]
    public static Task<CallToolResult> FindCallersAsync(
        ScopeRouter router,
        [Description("Target symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk (default calls); pass all to walk every indexed edge kind")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        GraphTools.ListCallersAsync(router, symbol, limit, kind, scope, ct);

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListCalleesResult))]
    [ToolTrigger("\"find callees of X\"")]
    [Description("Find evidence-backed outbound relations from a source. Compatibility name for list_callees; each row includes canonical source/target identities, the actual relation and confidence, plus stored occurrence file/range evidence.")]
    public static Task<CallToolResult> FindCalleesAsync(
        ScopeRouter router,
        [Description("Source symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk (default calls); pass all to walk every indexed edge kind")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        GraphTools.ListCalleesAsync(router, symbol, limit, kind, scope, ct);

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ImpactOfChangeResult))]
    [ToolTrigger("\"analyze the impact of changing X\"")]
    [Description("Compute bounded evidence-backed upstream impact. Compatibility name for impact_of_change; every row includes its BFS predecessor and a source-to-target path whose hops carry real occurrence evidence.")]
    public static Task<CallToolResult> ImpactAnalysisAsync(
        ScopeRouter router,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Maximum traversal depth (default 4)")] int maxDepth = 4,
        [Description("Maximum results (default 100)")] int limit = 100,
        [Description("Edge kind to walk (default calls); pass all to walk every indexed edge kind")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        GraphTools.ImpactOfChangeAsync(router, symbol, maxDepth, limit, kind, scope, progress, ct);
}
