using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Resources;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Evidence-preserving bounded path traversal for the MedInteropLens Phase 1 contract.
/// </summary>
[McpServerToolType]
public static class TraceCallPathTools
{
    private const int EvidenceLimitPerHop = 100;

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(TraceCallPathResult))]
    [ToolTrigger("\"show how execution can flow from A to B\"")]
    [Description("Trace bounded directed paths between two indexed symbols. Defaults to calls edges and returns every hop with source/target symbols, relation, confidence, and occurrence-level file/line evidence. Uses cycle detection plus explicit depth/path/node caps.")]
    public static Task<CallToolResult> TraceCallPathAsync(
        ScopeRouter router,
        [Description("Starting symbol name or FQN")] string from,
        [Description("Destination symbol name or FQN")] string to,
        [Description("Kebab-case edge relation to traverse (default calls)")] string? kind = null,
        [Description("Maximum hops per path, 1-12 (default 8)")] int maxDepth = 8,
        [Description("Maximum returned paths, 1-25 (default 10)")] int maxPaths = 10,
        [Description("Maximum expanded graph nodes per scope, 1-5000 (default 1000)")] int maxNodes = 1000,
        [Description("Optional scope id, '*', or comma-separated scope ids")] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "trace_call_path",
            new { from, to, kind, maxDepth, maxPaths, maxNodes, scope },
            () => TraceCallPathImplAsync(
                router,
                from,
                to,
                kind,
                maxDepth,
                maxPaths,
                maxNodes,
                scope,
                ct));

    private static async Task<CallToolResult> TraceCallPathImplAsync(
        ScopeRouter router,
        string from,
        string to,
        string? kind,
        int maxDepth,
        int maxPaths,
        int maxNodes,
        object? scope,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return DiagnosticResult.Error("trace_call_path requires non-empty `from` and `to` symbols.");
        }
        if (maxDepth is < 1 or > 12)
        {
            return DiagnosticResult.Error("trace_call_path `maxDepth` must be between 1 and 12.");
        }
        if (maxPaths is < 1 or > 25)
        {
            return DiagnosticResult.Error("trace_call_path `maxPaths` must be between 1 and 25.");
        }
        if (maxNodes is < 1 or > 5000)
        {
            return DiagnosticResult.Error("trace_call_path `maxNodes` must be between 1 and 5000.");
        }

        var edgeKind = string.IsNullOrWhiteSpace(kind) ? EdgeKinds.Calls : kind.Trim();
        if (!KebabCaseValidator.IsValid(edgeKind))
        {
            return DiagnosticResult.Error(
                "trace_call_path `kind` must be a kebab-case edge relation such as `calls`.");
        }

        ScopeResolution resolution;
        try
        {
            resolution = router.Resolve(scope);
        }
        catch (ArgumentException ex)
        {
            return DiagnosticResult.Error(ex.Message);
        }
        if (resolution.IsError) return DiagnosticResult.Error(resolution.ErrorMessage!);

        var sw = Stopwatch.StartNew();
        var perScope = await Task.WhenAll(resolution.Hosts.Select(async host =>
        {
            if (host.Status == "indexing" && !host.Ready.IsCompleted)
            {
                await host.Ready.WaitAsync(ct).ConfigureAwait(false);
            }
            if (host.Status == "degraded")
            {
                return new TraceCallPathScopeResult(
                    host.Scope.Id,
                    Array.Empty<TraceCallPath>(),
                    Truncated: false,
                    ExpandedNodes: 0,
                    Note: $"scope is degraded: {host.StatusMessage ?? "(no message)"}");
            }

            try
            {
                return await TraceScopeAsync(
                    host,
                    from,
                    to,
                    edgeKind,
                    maxDepth,
                    maxPaths,
                    maxNodes,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new TraceCallPathScopeResult(
                    host.Scope.Id,
                    Array.Empty<TraceCallPath>(),
                    Truncated: false,
                    ExpandedNodes: 0,
                    Note: $"scope query failed: {ex.Message}");
            }
        })).ConfigureAwait(false);
        sw.Stop();

        var dto = new TraceCallPathResult(
            from,
            to,
            edgeKind,
            maxDepth,
            maxPaths,
            maxNodes,
            perScope);
        var pathCount = perScope.Sum(result => result.Paths.Count);
        var content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Text = FormatResult(dto),
            },
        };
        var linkedSymbols = new HashSet<(string ScopeId, long SymbolId)>();
        foreach (var scopeResult in perScope)
        {
            foreach (var path in scopeResult.Paths)
            {
                AddSymbolLink(scopeResult.ScopeId, path.From);
                AddSymbolLink(scopeResult.ScopeId, path.To);
                foreach (var hop in path.Hops)
                {
                    AddSymbolLink(scopeResult.ScopeId, hop.From);
                    AddSymbolLink(scopeResult.ScopeId, hop.To);
                }
            }
        }
        content.Add(AudienceMetadata.Build(
            scopeId: scope?.ToString(),
            latencyMs: sw.ElapsedMilliseconds,
            ("paths", pathCount.ToString()),
            ("scopes", perScope.Length.ToString())));

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.TraceCallPathResult),
        };

        void AddSymbolLink(string scopeId, TraceCallPathSymbol symbol)
        {
            if (!linkedSymbols.Add((scopeId, symbol.SymbolId))) return;
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(symbol.SymbolId),
                Name = perScope.Length == 1 ? symbol.Fqn : $"{scopeId}: {symbol.Fqn}",
                Title = symbol.Fqn,
                Description =
                    $"{symbol.Kind} — {Format.Location(symbol.FilePath, symbol.Line, symbol.Column)}",
                MimeType = "text/markdown",
            });
        }
    }

    private static async Task<TraceCallPathScopeResult> TraceScopeAsync(
        ScopeHost host,
        string fromQuery,
        string toQuery,
        string edgeKind,
        int maxDepth,
        int maxPaths,
        int maxNodes,
        CancellationToken ct)
    {
        var sources = await host.Store.FindSymbolsAsync(
            fromQuery,
            limit: 10,
            ct: ct).ConfigureAwait(false);
        if (sources.Count == 0)
        {
            return new TraceCallPathScopeResult(
                host.Scope.Id,
                Array.Empty<TraceCallPath>(),
                Truncated: false,
                ExpandedNodes: 0,
                Note: $"No source symbol matches '{fromQuery}'.");
        }

        var targets = await host.Store.FindSymbolsAsync(
            toQuery,
            limit: 10,
            ct: ct).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return new TraceCallPathScopeResult(
                host.Scope.Id,
                Array.Empty<TraceCallPath>(),
                Truncated: false,
                ExpandedNodes: 0,
                Note: $"No destination symbol matches '{toQuery}'.");
        }

        var targetsById = targets.ToDictionary(target => target.Id);
        var queue = new Queue<PathState>();
        foreach (var source in sources.OrderBy(item => item.Fqn, StringComparer.Ordinal))
        {
            queue.Enqueue(new PathState(
                source,
                new List<TraceCallPathHop>(),
                new HashSet<long> { source.Id }));
        }

        var paths = new List<TraceCallPath>(maxPaths);
        var emittedPathKeys = new HashSet<string>(StringComparer.Ordinal);
        var expandedNodes = 0;
        var scheduledStates = queue.Count;
        var truncated = false;
        var branchLimit = Math.Min(maxNodes, 1000);

        while (queue.Count > 0 && paths.Count < maxPaths)
        {
            ct.ThrowIfCancellationRequested();
            var state = queue.Dequeue();
            if (targetsById.TryGetValue(state.Current.Id, out var reachedTarget))
            {
                AddCompletedPath(state, reachedTarget);
                continue;
            }
            if (state.Hops.Count >= maxDepth)
            {
                truncated = true;
                continue;
            }
            if (expandedNodes >= maxNodes)
            {
                truncated = true;
                break;
            }

            expandedNodes++;
            var callees = await host.Store.ListCalleesAsync(
                state.Current.Id,
                branchLimit,
                edgeKind,
                ct).ConfigureAwait(false);
            if (callees.Count == branchLimit) truncated = true;

            foreach (var callee in callees
                         .OrderBy(item => item.Fqn, StringComparer.Ordinal)
                         .ThenBy(item => item.Id))
            {
                if (state.Visited.Contains(callee.Id)) continue;

                var storedEvidence = await host.Store.ListEdgeEvidenceAsync(
                    state.Current.Id,
                    callee.Id,
                    edgeKind,
                    EvidenceLimitPerHop,
                    ct).ConfigureAwait(false);
                if (storedEvidence.Count == 0)
                {
                    // V13's invariant requires evidence for every logical edge. Skipping a
                    // malformed raw row is safer than inventing a location in an evidence tool.
                    continue;
                }

                var hopEvidence = storedEvidence
                    .Select(MapEvidence)
                    .ToList();
                var hopConfidence = storedEvidence.Max(item => item.Confidence);
                var hop = new TraceCallPathHop(
                    MapSymbol(state.Current),
                    MapSymbol(callee),
                    edgeKind,
                    ConfidenceName(hopConfidence),
                    hopEvidence,
                    EvidenceTruncated: storedEvidence.Count == EvidenceLimitPerHop);
                var nextHops = new List<TraceCallPathHop>(state.Hops.Count + 1);
                nextHops.AddRange(state.Hops);
                nextHops.Add(hop);
                var nextVisited = new HashSet<long>(state.Visited) { callee.Id };
                var next = new PathState(callee, nextHops, nextVisited);

                if (targetsById.TryGetValue(callee.Id, out reachedTarget))
                {
                    AddCompletedPath(next, reachedTarget);
                    if (paths.Count >= maxPaths)
                    {
                        truncated = true;
                        break;
                    }
                }
                else
                {
                    if (scheduledStates >= maxNodes)
                    {
                        truncated = true;
                        continue;
                    }
                    queue.Enqueue(next);
                    scheduledStates++;
                }
            }
        }
        if (queue.Count > 0 && paths.Count >= maxPaths) truncated = true;

        return new TraceCallPathScopeResult(
            host.Scope.Id,
            paths,
            truncated,
            expandedNodes,
            paths.Count == 0
                ? $"No `{edgeKind}` path found within depth {maxDepth}."
                : null);

        void AddCompletedPath(PathState state, SymbolHit target)
        {
            var key = state.Hops.Count == 0
                ? $"same:{state.Current.Id}"
                : string.Join(">", state.Hops.Select(hop =>
                    $"{hop.From.SymbolId}:{hop.Relation}:{hop.To.SymbolId}"));
            if (!emittedPathKeys.Add(key)) return;
            var source = state.Hops.Count == 0
                ? MapSymbol(state.Current)
                : state.Hops[0].From;
            var confidence = state.Hops.Count == 0
                ? ConfidenceName(CoreEvidenceConfidence.Exact)
                : ConfidenceName(state.Hops.Min(hop => ConfidenceValue(hop.Confidence)));
            paths.Add(new TraceCallPath(
                source,
                MapSymbol(target),
                confidence,
                state.Hops));
        }
    }

    private static TraceCallPathSymbol MapSymbol(SymbolHit hit) =>
        new(
            hit.Id,
            hit.Fqn,
            hit.Kind,
            hit.FilePath,
            hit.StartLine,
            hit.StartCol);

    private static TraceCallPathEvidence MapEvidence(Evidence evidence) =>
        new(
            evidence.Location.FilePath,
            evidence.Location.StartLine,
            evidence.Location.StartColumn,
            evidence.Location.EndLine,
            evidence.Location.EndColumn,
            ConfidenceName(evidence.Confidence),
            evidence.Producer,
            evidence.Metadata);

    private static string ConfidenceName(CoreEvidenceConfidence confidence) =>
        confidence switch
        {
            CoreEvidenceConfidence.Exact => "exact",
            CoreEvidenceConfidence.Semantic => "semantic",
            _ => "inferred",
        };

    private static CoreEvidenceConfidence ConfidenceValue(string confidence) =>
        confidence switch
        {
            "exact" => CoreEvidenceConfidence.Exact,
            "semantic" => CoreEvidenceConfidence.Semantic,
            _ => CoreEvidenceConfidence.Inferred,
        };

    private static string FormatResult(TraceCallPathResult result)
    {
        var sb = new StringBuilder();
        var pathCount = result.Scopes.Sum(scope => scope.Paths.Count);
        sb.Append("trace_call_path `")
          .Append(result.FromQuery)
          .Append("` → `")
          .Append(result.ToQuery)
          .Append("`: ")
          .Append(pathCount)
          .Append(" path")
          .Append(pathCount == 1 ? "" : "s")
          .Append(" via `")
          .Append(result.EdgeKind)
          .AppendLine("`");

        foreach (var scope in result.Scopes)
        {
            if (result.Scopes.Count > 1)
            {
                sb.AppendLine();
                sb.Append("### scope: `").Append(scope.ScopeId).AppendLine("`");
            }
            if (!string.IsNullOrEmpty(scope.Note))
            {
                sb.AppendLine();
                sb.Append("note: ").AppendLine(scope.Note);
            }
            for (var pathIndex = 0; pathIndex < scope.Paths.Count; pathIndex++)
            {
                var path = scope.Paths[pathIndex];
                sb.AppendLine();
                sb.Append("Path ").Append(pathIndex + 1)
                  .Append(" [").Append(path.Confidence).AppendLine("]");
                if (path.Hops.Count == 0)
                {
                    sb.Append("- `").Append(path.From.Fqn).AppendLine("` (source equals destination)");
                    continue;
                }
                for (var hopIndex = 0; hopIndex < path.Hops.Count; hopIndex++)
                {
                    var hop = path.Hops[hopIndex];
                    sb.Append(hopIndex + 1).Append(". `")
                      .Append(hop.From.Fqn)
                      .Append("` -[")
                      .Append(hop.Relation)
                      .Append("]-> `")
                      .Append(hop.To.Fqn)
                      .Append("` [")
                      .Append(hop.Confidence)
                      .AppendLine("]");
                    foreach (var evidence in hop.Evidence)
                    {
                        sb.Append("   - ")
                          .Append(Format.Location(
                              evidence.FilePath,
                              evidence.StartLine,
                              evidence.StartColumn))
                          .Append(" → ")
                          .Append(evidence.EndLine)
                          .Append(':')
                          .Append(evidence.EndColumn)
                          .Append(" [")
                          .Append(evidence.Confidence)
                          .Append(", ")
                          .Append(evidence.Producer)
                          .AppendLine("]");
                    }
                }
            }
            if (scope.Truncated)
            {
                sb.AppendLine();
                sb.AppendLine("note: traversal was truncated by a configured path/node/evidence cap.");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private sealed record PathState(
        SymbolHit Current,
        List<TraceCallPathHop> Hops,
        HashSet<long> Visited);
}
