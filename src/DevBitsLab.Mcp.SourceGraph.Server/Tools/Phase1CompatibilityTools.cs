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

    [McpServerTool(
        UseStructuredContent = true,
        OutputSchemaType = typeof(SearchSymbolsResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"search indexed code for a name or signature fragment\"")]
    [Description(
        "Search indexed code symbols by partial name, qualified name, or signature using the local FTS index. " +
        "Compatibility name for search_symbols; returns the same evidence-backed symbol locations.")]
    public static Task<CallToolResult> SearchCodeAsync(
        ScopeRouter router,
        [Description("Name, qualified-name, or signature fragment")] string query,
        [Description("Optional kebab-case symbol kind filter")] string? kind = null,
        [Description("Maximum results (default 25)")] int topK = 25,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        GraphTools.SearchSymbolsAsync(
            router,
            query,
            kind,
            topK,
            scope,
            ct);

    [McpServerTool(
        UseStructuredContent = true,
        OutputSchemaType = typeof(SearchSymbolsResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"find an indexed symbol\"")]
    [Description(
        "Find indexed symbols by name, qualified name, or signature fragment. " +
        "Compatibility name for search_symbols; ambiguous matches are returned rather than guessed.")]
    public static Task<CallToolResult> FindSymbolAsync(
        ScopeRouter router,
        [Description("Symbol name, qualified name, or signature fragment")] string query,
        [Description("Optional kebab-case symbol kind filter")] string? kind = null,
        [Description("Maximum results (default 25)")] int topK = 25,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        GraphTools.SearchSymbolsAsync(
            router,
            query,
            kind,
            topK,
            scope,
            ct);

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindReferencesResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
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
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
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
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
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

    /// <summary>
    /// Source-compatible entry point for the original Phase 1 alias signature.
    /// </summary>
    public static Task<CallToolResult> TraceCallAsync(
        ScopeRouter router,
        string from,
        string to,
        string? kind = null,
        int maxDepth = 8,
        int maxPaths = 10,
        int maxNodes = 1000,
        string? scope = null,
        CancellationToken ct = default) =>
        TraceCallWithProfileAsync(
            router,
            from,
            to,
            kind,
            profile: null,
            maxDepth,
            maxPaths,
            maxNodes,
            scope,
            ct);

    [McpServerTool(
        Name = "trace_call",
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(TraceCallPathResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"trace a call or execution path from A to B\"")]
    [Description(
        "Trace bounded evidence-backed paths between two indexed symbols. Compatibility name for " +
        "trace_call_path; defaults to calls. Set profile=execution for the evidence-backed " +
        "UI-to-native execution relation whitelist and projection-completeness disclosure.")]
    public static Task<CallToolResult> TraceCallWithProfileAsync(
        ScopeRouter router,
        [Description("Starting symbol name or qualified name")] string from,
        [Description("Destination symbol name or qualified name")] string to,
        [Description("Kebab-case edge relation to traverse (default calls)")] string? kind = null,
        [Description("Optional traversal profile; use execution for cross-domain execution flow")]
        string? profile = null,
        [Description("Maximum hops per path, 1-12 (default 8)")] int maxDepth = 8,
        [Description("Maximum returned paths, 1-25 (default 10)")] int maxPaths = 10,
        [Description("Maximum expanded graph nodes per scope, 1-5000 (default 1000)")] int maxNodes = 1000,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        TraceCallPathTools.TraceCallPathWithProfileAsync(
            router,
            from,
            to,
            kind,
            profile,
            maxDepth,
            maxPaths,
            maxNodes,
            scope,
            ct);

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ImpactOfChangeResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
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
