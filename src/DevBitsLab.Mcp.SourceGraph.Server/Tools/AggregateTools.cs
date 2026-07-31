using System.ComponentModel;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>Compact, structured query aggregation for latency-sensitive MCP clients.</summary>
[McpServerToolType]
public static class AggregateTools
{
    private const string ScopeDescription =
        "Optional scope id, '*', or comma-separated scope ids. Omit to use the default scope.";

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ResolveAndReferencesResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"resolve a symbol and return its references in one request\"")]
    [Description("Resolve one symbol and return its definition plus references in a single MCP round-trip. Ambiguous queries return candidates and never choose a target. The detailed data is emitted once in structured content; prose is a compact status line.")]
    public static Task<CallToolResult> ResolveAndReferencesAsync(
        ScopeRouter router,
        [Description("Symbol name, FQN, or canonical key.")] string symbol,
        [Description("Maximum references (1-500, default 50).")] int limit = 50,
        [Description("Include references from generated files.")] bool includeGenerated = false,
        [Description("Optional file-path hint used during resolution.")] string? fileHint = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "resolve_and_references",
            new { symbol, limit, includeGenerated, fileHint, scope },
            () => ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (limit is < 1 or > 500)
                {
                    return DiagnosticResult.Error(
                        "resolve_and_references `limit` must be between 1 and 500.");
                }
                var dto = await BuildResolveAndReferencesAsync(
                    host,
                    symbol,
                    limit,
                    includeGenerated,
                    fileHint,
                    ct).ConfigureAwait(false);
                return BuildResult(
                    StatusLine(dto.Status, symbol, dto.References.Count),
                    dto,
                    ToolOutputJsonContext.Default.ResolveAndReferencesResult,
                    host.Scope.Id,
                    sw.ElapsedMilliseconds);
            }, ct));

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(SymbolOverviewResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"give me the definition, members, callers, and implementations of this symbol\"")]
    [Description("Return a compact symbol overview containing definition, direct members, callers, and implementations in one MCP round-trip. Ambiguous queries return candidates and never choose a target.")]
    public static Task<CallToolResult> SymbolOverviewAsync(
        ScopeRouter router,
        [Description("Symbol name, FQN, or canonical key.")] string symbol,
        [Description("Maximum rows per category (1-200, default 20).")] int limit = 20,
        [Description("Optional file-path hint used during resolution.")] string? fileHint = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "symbol_overview",
            new { symbol, limit, fileHint, scope },
            () => ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (limit is < 1 or > 200)
                {
                    return DiagnosticResult.Error(
                        "symbol_overview `limit` must be between 1 and 200.");
                }
                var dto = await BuildSymbolOverviewAsync(
                    host,
                    symbol,
                    limit,
                    fileHint,
                    ct).ConfigureAwait(false);
                var count = dto.Members.Count + dto.Callers.Count + dto.Implementations.Count;
                return BuildResult(
                    StatusLine(dto.Status, symbol, count),
                    dto,
                    ToolOutputJsonContext.Default.SymbolOverviewResult,
                    host.Scope.Id,
                    sw.ElapsedMilliseconds);
            }, ct));

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(BatchQueryResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"run several symbol overviews or resolve-and-reference queries at once\"")]
    [Description("Run 1-20 independent aggregate queries in one MCP round-trip. operation must be resolve_and_references or symbol_overview. Results preserve input order and isolate not-found/ambiguous status per item.")]
    public static Task<CallToolResult> BatchQueryAsync(
        ScopeRouter router,
        [Description("Independent aggregate requests (1-20).")] IReadOnlyList<BatchQueryRequest> requests,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "batch_query",
            new { requests, scope },
            () => ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (requests is null || requests.Count is < 1 or > 20)
                {
                    return DiagnosticResult.Error(
                        "batch_query `requests` must contain between 1 and 20 items.");
                }

                var results = new List<BatchQueryItemResult>(requests.Count);
                foreach (var request in requests)
                {
                    if (string.IsNullOrWhiteSpace(request.Symbol))
                    {
                        return DiagnosticResult.Error(
                            "batch_query request symbols must be non-empty.");
                    }
                    var operation = request.Operation?.Trim().ToLowerInvariant();
                    if (operation is not ("resolve_and_references" or "symbol_overview"))
                    {
                        return DiagnosticResult.Error(
                            "batch_query operation must be resolve_and_references or symbol_overview.");
                    }

                    if (operation == "resolve_and_references")
                    {
                        if (request.Limit is < 1 or > 500)
                        {
                            return DiagnosticResult.Error(
                                "batch_query resolve_and_references limit must be between 1 and 500.");
                        }
                        var resolved = await BuildResolveAndReferencesAsync(
                            host,
                            request.Symbol,
                            request.Limit,
                            request.IncludeGenerated,
                            request.FileHint,
                            ct).ConfigureAwait(false);
                        results.Add(new BatchQueryItemResult(
                            operation,
                            request.Symbol,
                            resolved.Status,
                            resolved,
                            null));
                    }
                    else
                    {
                        if (request.Limit is < 1 or > 200)
                        {
                            return DiagnosticResult.Error(
                                "batch_query symbol_overview limit must be between 1 and 200.");
                        }
                        var overview = await BuildSymbolOverviewAsync(
                            host,
                            request.Symbol,
                            request.Limit,
                            request.FileHint,
                            ct).ConfigureAwait(false);
                        results.Add(new BatchQueryItemResult(
                            operation,
                            request.Symbol,
                            overview.Status,
                            null,
                            overview));
                    }
                }

                var dto = new BatchQueryResult(results);
                return BuildResult(
                    $"batch_query: {results.Count} completed; "
                    + $"{results.Count(result => result.Status == "ok")} resolved.",
                    dto,
                    ToolOutputJsonContext.Default.BatchQueryResult,
                    host.Scope.Id,
                    sw.ElapsedMilliseconds);
            }, ct));

    private static async Task<ResolveAndReferencesResult> BuildResolveAndReferencesAsync(
        ScopeHost host,
        string query,
        int limit,
        bool includeGenerated,
        string? fileHint,
        CancellationToken ct)
    {
        var (status, candidates, selected) = await ResolveAsync(
            host.Store,
            query,
            fileHint,
            ct).ConfigureAwait(false);
        if (selected is null)
        {
            return new ResolveAndReferencesResult(
                query,
                status,
                candidates.Select(MapSymbol).ToList(),
                null,
                false,
                Array.Empty<AggregateReference>());
        }

        var references = await host.Store.FindReferencesAsync(
            selected.Id,
            includeGenerated,
            checked(limit + 1),
            ct).ConfigureAwait(false);
        var truncated = references.Count > limit;
        return new ResolveAndReferencesResult(
            query,
            "ok",
            candidates.Select(MapSymbol).ToList(),
            MapSymbol(selected),
            truncated,
            references.Take(limit).Select(reference => new AggregateReference(
                GraphTools.RefKindLabel(reference.Kind),
                reference.FilePath,
                reference.Line,
                reference.Col,
                reference.IsGenerated)).ToList());
    }

    private static async Task<SymbolOverviewResult> BuildSymbolOverviewAsync(
        ScopeHost host,
        string query,
        int limit,
        string? fileHint,
        CancellationToken ct)
    {
        var (status, candidates, selected) = await ResolveAsync(
            host.Store,
            query,
            fileHint,
            ct).ConfigureAwait(false);
        if (selected is null)
        {
            return new SymbolOverviewResult(
                query,
                status,
                candidates.Select(MapSymbol).ToList(),
                null,
                false,
                Array.Empty<AggregateSymbol>(),
                Array.Empty<AggregateRelation>(),
                Array.Empty<AggregateSymbol>());
        }

        var members = await host.Store.ListMembersAsync(
            selected.Id,
            accessibilityFilter: null,
            checked(limit + 1),
            ct).ConfigureAwait(false);
        var callers = await EvidenceTraversal.LoadInboundAsync(
            host.Store,
            selected,
            limit,
            edgeKind: "calls",
            ct).ConfigureAwait(false);
        var implementations = await host.Store.ListImplementationsAsync(
            selected.Id,
            checked(limit + 1),
            ct).ConfigureAwait(false);
        var truncated = members.Count > limit
            || callers.Truncated
            || implementations.Count > limit;

        return new SymbolOverviewResult(
            query,
            "ok",
            candidates.Select(MapSymbol).ToList(),
            MapSymbol(selected),
            truncated,
            members.Take(limit).Select(MapSymbol).ToList(),
            callers.Relations.Select(relation => new AggregateRelation(
                MapSymbol(relation.Source),
                relation.Hop.Relation,
                relation.Hop.Confidence,
                relation.Hop.Evidence,
                relation.Hop.EvidenceTruncated)).ToList(),
            implementations.Take(limit).Select(MapSymbol).ToList());
    }

    private static async Task<(string Status, IReadOnlyList<SymbolHit> Candidates, SymbolHit? Selected)>
        ResolveAsync(
            IGraphStore store,
            string query,
            string? fileHint,
            CancellationToken ct)
    {
        var candidates = await store.FindSymbolsAsync(
            query,
            fileHint,
            limit: 6,
            ct).ConfigureAwait(false);
        if (candidates.Count == 0) return ("not_found", candidates, null);
        if (candidates.Count == 1) return ("ok", candidates, candidates[0]);

        var exact = candidates.Where(candidate =>
                string.Equals(candidate.CanonicalKey, query, StringComparison.Ordinal)
                || string.Equals(candidate.Fqn, query, StringComparison.Ordinal)
                || string.Equals(candidate.Name, query, StringComparison.Ordinal))
            .ToList();
        return exact.Count == 1
            ? ("ok", candidates, exact[0])
            : ("ambiguous", candidates, null);
    }

    private static AggregateSymbol MapSymbol(SymbolHit symbol) => new(
        symbol.Id,
        symbol.CanonicalKey,
        symbol.Fqn,
        symbol.Kind,
        symbol.FilePath,
        symbol.StartLine,
        symbol.StartCol,
        symbol.EndLine,
        symbol.EndCol,
        string.IsNullOrEmpty(symbol.Signature) ? null : symbol.Signature);

    private static string StatusLine(string status, string query, int count) =>
        status switch
        {
            "ok" => $"{query}: resolved; {count} related rows.",
            "ambiguous" => $"{query}: ambiguous; inspect structured candidates.",
            _ => $"{query}: not found.",
        };

    private static CallToolResult BuildResult<T>(
        string prose,
        T dto,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string scopeId,
        long elapsedMs) =>
        new()
        {
            Content =
            [
                new TextContentBlock { Text = prose },
                AudienceMetadata.Build(scopeId, elapsedMs),
            ],
            StructuredContent = JsonSerializer.SerializeToElement(dto, typeInfo),
        };
}
