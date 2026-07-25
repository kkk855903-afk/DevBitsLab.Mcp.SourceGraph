using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
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
    private const int EvidenceLimitPerHop = 20;
    private const int MaximumReturnedEvidenceRows = 1000;
    private const int MaximumReportedProjectionFailures = 50;
    private const int MaximumQueryCharacters = 4096;
    private const int MaximumScopeFanout = 32;
    private const string ExecutionProfile = "execution";
    private const string ExecutionTerminalDefinition =
        "A terminal algorithm is reached only by binds-path, command-executes, one or more "
        + "managed calls, grpc-calls, rpc-dispatches-to, one or more server calls, "
        + "pinvoke-maps-to, an optional native-implementation hop, and one or more native "
        + "calls, in that order; its final node has "
        + "no auditable outbound calls edge.";
    private static readonly string[] ExecutionRelations =
    [
        "binds-path",
        EdgeKinds.CommandExecutes,
        EdgeKinds.Calls,
        EdgeKinds.Schedules,
        EdgeKinds.Dispatches,
        EdgeKinds.InterfaceDispatchesTo,
        EdgeKinds.HandlesEvent,
        EdgeKinds.RaisesEvent,
        EdgeKinds.EventDispatchesTo,
        EdgeKinds.SubscribesHandler,
        EdgeKinds.GrpcCalls,
        EdgeKinds.RpcDispatchesTo,
        EdgeKinds.PInvokeMapsTo,
        EdgeKinds.NativeImplementation,
    ];

    /// <summary>
    /// Source-compatible entry point retained for callers compiled against the original
    /// one-relation API. The MCP surface is registered by
    /// <see cref="TraceCallPathWithProfileAsync"/>.
    /// </summary>
    public static Task<CallToolResult> TraceCallPathAsync(
        ScopeRouter router,
        string from,
        string to,
        string? kind = null,
        int maxDepth = 8,
        int maxPaths = 10,
        int maxNodes = 1000,
        string? scope = null,
        CancellationToken ct = default) =>
        TraceCallPathWithProfileAsync(
            router,
            from,
            to,
            kind,
            profile: null,
            maxDepth,
            maxPaths,
            maxNodes,
            scope: scope,
            detail: "detail",
            ct: ct);

    [McpServerTool(
        Name = "trace_call_path",
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(TraceCallPathResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"show how execution can flow from A to B\"")]
    [Description("Trace bounded directed possible paths between indexed symbols; a returned path is not proof that every hop executes on every run. Defaults to calls edges and requires `to`. Set profile=execution to follow the ordered evidence-backed UI → command → managed call → gRPC dispatch → P/Invoke state machine. Detail output labels indexed conditional call sites with their branch condition. Execution mode may omit `to` when `from` is an exact canonical key; terminal discovery defaults to compact summary output with one shortest path per terminal, while explicit-target queries default to full detail. Exact canonical-key inputs never use fuzzy matching. Execution results disclose whether current absence claims are authoritative.")]
    public static Task<CallToolResult> TraceCallPathWithProfileAsync(
        ScopeRouter router,
        [Description("Starting symbol name, FQN, or exact canonical key")] string from,
        [Description("Destination symbol name, FQN, or exact canonical key. May be omitted only with profile=execution and an exact canonical `from`.")]
        string? to = null,
        [Description("Kebab-case edge relation to traverse (default calls)")] string? kind = null,
        [Description("Optional traversal profile. Use execution for the ordered cross-domain execution state machine; omit for one relation.")]
        string? profile = null,
        [Description("Maximum hops per path, 1-12 (default 8)")] int maxDepth = 8,
        [Description("Maximum returned paths, 1-25 (default 10)")] int maxPaths = 10,
        [Description("Maximum expanded graph nodes per scope, 1-5000 (default 1000)")] int maxNodes = 1000,
        [Description("Optional scope id, '*', or comma-separated scope ids")] string? scope = null,
        [Description("Output detail: summary | detail. Defaults to summary for terminal discovery and detail for an explicit target.")]
        string? detail = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "trace_call_path",
            new
            {
                from,
                to,
                kind,
                profile,
                maxDepth,
                maxPaths,
                maxNodes,
                scope,
                detail,
            },
            () => TraceCallPathImplAsync(
                router,
                from,
                to,
                kind,
                profile,
                maxDepth,
                maxPaths,
                maxNodes,
                scope,
                detail,
                ct));

    private static async Task<CallToolResult> TraceCallPathImplAsync(
        ScopeRouter router,
        string from,
        string? to,
        string? kind,
        string? profile,
        int maxDepth,
        int maxPaths,
        int maxNodes,
        object? scope,
        string? detail,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(from))
        {
            return DiagnosticResult.Error("trace_call_path requires a non-empty `from` symbol.");
        }
        if (from.Trim().Length > MaximumQueryCharacters
            || to?.Trim().Length > MaximumQueryCharacters)
        {
            return DiagnosticResult.Error(
                $"trace_call_path symbol queries must not exceed {MaximumQueryCharacters} characters.");
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

        var normalizedProfile = string.IsNullOrWhiteSpace(profile)
            ? "relation"
            : profile.Trim().ToLowerInvariant();
        if (normalizedProfile is not ("relation" or ExecutionProfile))
        {
            return DiagnosticResult.Error(
                "trace_call_path `profile` must be `execution` when supplied.");
        }
        var executionProfile = normalizedProfile == ExecutionProfile;
        var discoverTerminal = executionProfile
            && string.IsNullOrWhiteSpace(to);
        var detailLevel = string.IsNullOrWhiteSpace(detail)
            ? discoverTerminal ? "summary" : "detail"
            : detail.Trim().ToLowerInvariant();
        if (detailLevel is not ("summary" or "detail"))
        {
            return DiagnosticResult.Error(
                "trace_call_path `detail` must be `summary` or `detail`.");
        }
        if (!discoverTerminal && string.IsNullOrWhiteSpace(to))
        {
            return DiagnosticResult.Error(
                "trace_call_path requires a non-empty `to` symbol unless `profile=execution` discovers terminal algorithms.");
        }
        if (discoverTerminal
            && !CanonicalKeyValidator.IsValid(from.Trim()))
        {
            return DiagnosticResult.Error(
                "trace_call_path requires an exact canonical `from` key when `profile=execution` omits `to`.");
        }
        var canonicalIntentError =
            ValidateCanonicalIntent(from, "from")
            ?? (discoverTerminal ? null : ValidateCanonicalIntent(to!, "to"));
        if (canonicalIntentError is not null)
        {
            return DiagnosticResult.Error(canonicalIntentError);
        }
        if (executionProfile && !string.IsNullOrWhiteSpace(kind))
        {
            return DiagnosticResult.Error(
                "trace_call_path does not accept `kind` together with `profile=execution`; the profile uses an ordered relation state machine.");
        }

        var edgeKind = executionProfile
            ? null
            : string.IsNullOrWhiteSpace(kind)
                ? EdgeKinds.Calls
                : kind.Trim();
        if (edgeKind is not null && !KebabCaseValidator.IsValid(edgeKind))
        {
            return DiagnosticResult.Error(
                "trace_call_path `kind` must be a kebab-case edge relation such as `calls`.");
        }
        IReadOnlyList<string> relations = executionProfile
            ? ExecutionRelations
            : [edgeKind!];

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
        if (resolution.Hosts.Count > MaximumScopeFanout)
        {
            return DiagnosticResult.Error(
                $"trace_call_path resolves at most {MaximumScopeFanout} scopes per request; narrow `scope`.");
        }

        var sw = Stopwatch.StartNew();
        var perScope = await Task.WhenAll(resolution.Hosts.Select(async host =>
        {
            var executionState = executionProfile
                ? BuildExecutionState(host)
                : null;
            if (host.Status == "indexing" && !host.Ready.IsCompleted)
            {
                await host.Ready.WaitAsync(ct).ConfigureAwait(false);
                executionState = executionProfile
                    ? BuildExecutionState(host)
                    : null;
            }
            if (host.Status == "degraded")
            {
                return new TraceCallPathScopeResult(
                    host.Scope.Id,
                    Array.Empty<TraceCallPath>(),
                    Truncated: false,
                    ExpandedNodes: 0,
                    Note: $"scope is degraded: {host.StatusMessage ?? "(no message)"}",
                    ExecutionState: executionState);
            }

            try
            {
                return await TraceScopeAsync(
                    host,
                    from,
                    to,
                    edgeKind,
                    relations,
                    executionProfile,
                    discoverTerminal,
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
                var failedExecutionState = executionProfile
                    ? MarkExecutionUnstable(
                        BuildExecutionState(host),
                        "The graph query did not complete against a stable read.")
                    : null;
                return new TraceCallPathScopeResult(
                    host.Scope.Id,
                    Array.Empty<TraceCallPath>(),
                    Truncated: false,
                    ExpandedNodes: 0,
                    Note: $"scope query failed: {ex.Message}",
                    ExecutionState: failedExecutionState);
            }
        })).ConfigureAwait(false);
        sw.Stop();

        var outputScopes = detailLevel == "summary"
            ? perScope
                .Select(CompactScope)
                .ToArray()
            : perScope;
        var dto = new TraceCallPathResult(
            from,
            discoverTerminal ? null : to,
            normalizedProfile,
            detailLevel,
            discoverTerminal ? "execution-terminal" : "explicit-target",
            discoverTerminal ? ExecutionTerminalDefinition : null,
            edgeKind,
            relations,
            maxDepth,
            maxPaths,
            maxNodes,
            outputScopes);
        var pathCount = outputScopes.Sum(result => result.Paths.Count);
        var content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Text = FormatResult(dto),
            },
        };
        var linkedSymbols = new HashSet<(string ScopeId, long SymbolId)>();
        foreach (var scopeResult in outputScopes)
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

        TraceCallPathScopeResult CompactScope(
            TraceCallPathScopeResult scopeResult) =>
            scopeResult with
            {
                Paths = scopeResult.Paths
                    .Select(path => path with
                    {
                        Hops = Array.Empty<TraceCallPathHop>(),
                        HopCount = path.Hops.Count,
                    })
                    .ToArray(),
                Note = AppendNote(
                    scopeResult.Note,
                    "Summary output omits repeated hop evidence; rerun with detail=detail and an exact terminal canonical key for the full path."),
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
        string? toQuery,
        string? edgeKind,
        IReadOnlyList<string> relations,
        bool executionProfile,
        bool discoverTerminal,
        int maxDepth,
        int maxPaths,
        int maxNodes,
        CancellationToken ct)
    {
        GraphReadVersion? initialReadVersion = null;
        ExecutionProjectionStamp? initialProjectionStamp = null;
        if (executionProfile)
        {
            initialReadVersion = await host.Store.GetReadVersionAsync(ct)
                .ConfigureAwait(false);
            initialProjectionStamp = CaptureProjectionStamp(host);
        }
        var executionState = executionProfile
            ? BuildExecutionState(host)
            : null;
        IReadOnlyList<SymbolHit> targets = [];
        var sources = await ResolveSymbolsAsync(
            host.Store,
            fromQuery,
            ct).ConfigureAwait(false);
        if (sources.Count == 0)
        {
            executionState = await FinalizeExecutionStateAsync()
                .ConfigureAwait(false);
            var missingSourceNote =
                $"No source symbol matches '{fromQuery}'.";
            return new TraceCallPathScopeResult(
                host.Scope.Id,
                Array.Empty<TraceCallPath>(),
                Truncated: false,
                ExpandedNodes: 0,
                Note: AddAbsenceDisclosure(
                    missingSourceNote,
                    executionState),
                ExecutionState: executionState);
        }
        if (sources.Count > 1)
        {
            return new TraceCallPathScopeResult(
                host.Scope.Id,
                Array.Empty<TraceCallPath>(),
                Truncated: false,
                ExpandedNodes: 0,
                Note: AmbiguousSelectionNote(
                    "source",
                    fromQuery,
                    sources),
                ExecutionState: null)
            {
                Status = "ambiguous",
                PathSearchExecuted = false,
                AmbiguousRole = "source",
                Candidates = sources.Select(MapSymbol).ToArray(),
            };
        }

        targets = discoverTerminal
            ? []
            : await ResolveSymbolsAsync(
                host.Store,
                toQuery!,
                ct).ConfigureAwait(false);
        if (!discoverTerminal && targets.Count == 0)
        {
            executionState = await FinalizeExecutionStateAsync()
                .ConfigureAwait(false);
            var missingTargetNote =
                $"No destination symbol matches '{toQuery}'.";
            return new TraceCallPathScopeResult(
                host.Scope.Id,
                Array.Empty<TraceCallPath>(),
                Truncated: false,
                ExpandedNodes: 0,
                Note: AddAbsenceDisclosure(
                    missingTargetNote,
                    executionState),
                ExecutionState: executionState);
        }
        if (!discoverTerminal && targets.Count > 1)
        {
            return new TraceCallPathScopeResult(
                host.Scope.Id,
                Array.Empty<TraceCallPath>(),
                Truncated: false,
                ExpandedNodes: 0,
                Note: AmbiguousSelectionNote(
                    "destination",
                    toQuery!,
                    targets),
                ExecutionState: null)
            {
                Status = "ambiguous",
                PathSearchExecuted = false,
                AmbiguousRole = "destination",
                Candidates = targets.Select(MapSymbol).ToArray(),
            };
        }

        var targetsById = targets.ToDictionary(target => target.Id);
        var orderedSources = sources
            .OrderBy(item => item.Fqn, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToList();
        var queue = new Queue<PathState>();
        foreach (var source in orderedSources.Take(maxNodes))
        {
            queue.Enqueue(new PathState(
                source,
                new List<TraceCallPathHop>(),
                new HashSet<long> { source.Id },
                executionProfile
                    ? await InitialExecutionStageAsync(
                        host.Store,
                        source.Id,
                        ct).ConfigureAwait(false)
                    : ExecutionStage.Relation));
        }

        var paths = new List<TraceCallPath>(maxPaths);
        var emittedPathKeys = new HashSet<string>(StringComparer.Ordinal);
        var returnedEvidenceRows = 0;
        var expandedNodes = 0;
        var scheduledStates = queue.Count;
        var truncated = orderedSources.Count > maxNodes;
        var truncationReasons = new HashSet<string>(
            StringComparer.Ordinal);
        if (truncated)
        {
            truncationReasons.Add("max_nodes");
        }
        var depthReached = 0;
        var observedRelations = new HashSet<string>(
            StringComparer.Ordinal);
        var branchLimit = Math.Min(maxNodes, 1000);
        var stop = false;

        while (queue.Count > 0 && !stop)
        {
            ct.ThrowIfCancellationRequested();
            var state = queue.Dequeue();
            depthReached = Math.Max(depthReached, state.Hops.Count);
            if (discoverTerminal
                && state.Stage == ExecutionStage.NativeAlgorithm
                && !await HasAnyAuditableOutboundAsync(
                    host.Store,
                    state.Current.Id,
                    EdgeKinds.Calls,
                    ct).ConfigureAwait(false))
            {
                AddCompletedPath(state, state.Current);
                if (paths.Count >= maxPaths)
                {
                    if (queue.Count > 0)
                    {
                        MarkTruncated("max_paths");
                    }
                    break;
                }
                continue;
            }
            if (targetsById.TryGetValue(state.Current.Id, out var reachedTarget))
            {
                AddCompletedPath(state, reachedTarget);
                if (paths.Count >= maxPaths)
                {
                    if (queue.Count > 0)
                    {
                        MarkTruncated("max_paths");
                    }
                    break;
                }
                continue;
            }
            if (state.Hops.Count >= maxDepth)
            {
                if (await HasUnexploredOutboundAsync(
                    host.Store,
                    state,
                    ct).ConfigureAwait(false))
                {
                    MarkTruncated("max_depth");
                }
                continue;
            }
            if (expandedNodes >= maxNodes)
            {
                var hasUnexploredOutbound =
                    await HasUnexploredOutboundAsync(
                    host.Store,
                    state,
                    ct).ConfigureAwait(false);
                if (hasUnexploredOutbound || queue.Count > 0)
                {
                    MarkTruncated("max_nodes");
                }
                break;
            }

            expandedNodes++;
            // A fixed relation has at most one edge per target. Fetch enough extra rows to cover
            // every path-local visited target plus one unvisited sentinel, so cycle edges cannot
            // consume the branch cap or cause a false truncation report.
            var storedEdges = await ListOutboundEdgesAsync(
                host.Store,
                state.Current.Id,
                executionProfile
                    ? AllowedExecutionRelations(state.Stage)
                    : relations,
                checked(branchLimit + state.Visited.Count + 1),
                ct).ConfigureAwait(false);
            observedRelations.UnionWith(
                storedEdges.Select(edge => edge.Relation));
            var unvisitedEdges = storedEdges
                .Where(edge => !state.Visited.Contains(edge.Symbol.Id))
                .Take(branchLimit + 1)
                .ToList();
            var branchTruncated = unvisitedEdges.Count > branchLimit;
            if (branchTruncated)
            {
                MarkTruncated("branch_limit");
            }
            var visibleEdges = unvisitedEdges.Take(branchLimit).ToList();

            for (var edgeIndex = 0; edgeIndex < visibleEdges.Count; edgeIndex++)
            {
                var edge = visibleEdges[edgeIndex];
                var callee = edge.Symbol;
                var nextStage = state.Stage;
                if (executionProfile
                    && !TryAdvanceExecutionStage(
                        state.Stage,
                        edge.Relation,
                        out nextStage))
                {
                    continue;
                }

                var hop = await BuildAuditableHopAsync(
                    host.Store,
                    state.Current,
                    callee,
                    edge.Relation,
                    ct).ConfigureAwait(false);
                if (hop is null)
                {
                    // Evidence may disappear between the auditable edge query and this detail
                    // lookup during an incremental update. Never invent a fallback location.
                    continue;
                }

                var nextHops = new List<TraceCallPathHop>(state.Hops.Count + 1);
                nextHops.AddRange(state.Hops);
                nextHops.Add(hop);
                var nextVisited = new HashSet<long>(state.Visited) { callee.Id };
                var next = new PathState(
                    callee,
                    nextHops,
                    nextVisited,
                    nextStage);
                depthReached = Math.Max(depthReached, next.Hops.Count);

                if (targetsById.TryGetValue(callee.Id, out reachedTarget))
                {
                    AddCompletedPath(next, reachedTarget);
                    if (paths.Count >= maxPaths)
                    {
                        if (branchTruncated
                            || edgeIndex + 1 < visibleEdges.Count
                            || queue.Count > 0)
                        {
                            MarkTruncated("max_paths");
                        }
                        stop = true;
                        break;
                    }
                }
                else
                {
                    if (scheduledStates >= maxNodes)
                    {
                        MarkTruncated("max_nodes");
                        continue;
                    }
                    queue.Enqueue(next);
                    scheduledStates++;
                }
            }
        }

        var truncation = truncated
            ? new TraceCallPathTruncation(
                truncationReasons.Order(StringComparer.Ordinal).ToArray(),
                expandedNodes,
                maxNodes,
                depthReached,
                maxDepth,
                paths.Count,
                maxPaths,
                returnedEvidenceRows,
                MaximumReturnedEvidenceRows,
                branchLimit)
            : null;
        executionState = await FinalizeExecutionStateAsync(
                paths,
                observedRelations,
                queryTraversalComplete: !truncated)
            .ConfigureAwait(false);
        if (executionState is not null && truncated)
        {
            executionState = MarkExecutionTruncated(
                executionState,
                truncation!);
        }
        var note = paths.Count == 0
            ? executionProfile
                ? discoverTerminal
                    ? $"No terminal execution-profile path found within depth {maxDepth}."
                    : $"No execution-profile path found within depth {maxDepth}."
                : $"No `{edgeKind}` path found within depth {maxDepth}."
            : null;
        if (executionState is { AbsenceAuthoritative: false })
        {
            note = AppendNote(
                note,
                paths.Count == 0
                    ? "The relevant projections are partial, so this empty result is not an authoritative current absence."
                    : "The returned paths use persisted evidence, but partial projections can omit additional current paths.");
        }

        return new TraceCallPathScopeResult(
            host.Scope.Id,
            paths,
            truncated,
            expandedNodes,
            note,
            executionState)
        {
            Truncation = truncation,
        };

        void MarkTruncated(string reason)
        {
            truncated = true;
            truncationReasons.Add(reason);
        }

        async Task<bool> HasUnexploredOutboundAsync(
            IGraphStore store,
            PathState state,
            CancellationToken cancellationToken)
        {
            var probe = await ListOutboundEdgesAsync(
                store,
                state.Current.Id,
                executionProfile
                    ? AllowedExecutionRelations(state.Stage)
                    : relations,
                checked(state.Visited.Count + 1),
                cancellationToken).ConfigureAwait(false);
            return probe.Any(edge => !state.Visited.Contains(edge.Symbol.Id));
        }

        void AddCompletedPath(PathState state, SymbolHit target)
        {
            if (discoverTerminal
                && paths.Any(path => path.To.SymbolId == target.Id))
            {
                return;
            }
            var key = state.Hops.Count == 0
                ? $"same:{state.Current.Id}"
                : string.Join(">", state.Hops.Select(hop =>
                    $"{hop.From.SymbolId}:{hop.Relation}:{hop.To.SymbolId}"));
            if (!emittedPathKeys.Add(key)) return;
            var evidenceRows = state.Hops.Sum(hop => hop.Evidence.Count);
            if (returnedEvidenceRows + evidenceRows
                > MaximumReturnedEvidenceRows)
            {
                MarkTruncated("evidence_limit");
                return;
            }
            returnedEvidenceRows += evidenceRows;
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

        async Task<TraceCallPathExecutionState?>
            FinalizeExecutionStateAsync(
                IReadOnlyList<TraceCallPath>? observedPaths = null,
                IReadOnlySet<string>? observedRelations = null,
                bool queryTraversalComplete = false)
        {
            if (!executionProfile) return null;

            var finalProjectionStampBefore =
                CaptureProjectionStamp(host);
            var finalState = BuildExecutionState(host);
            var finalProjectionStampAfter =
                CaptureProjectionStamp(host);
            var finalReadVersion = await host.Store
                .GetReadVersionAsync(ct)
                .ConfigureAwait(false);
            var runtimeChanged =
                !ProjectionStampEquals(
                    initialProjectionStamp!,
                    finalProjectionStampBefore)
                || !ProjectionStampEquals(
                    finalProjectionStampBefore,
                    finalProjectionStampAfter);
            var reconciled = ReconcileExecutionState(
                finalState,
                initialReadVersion!.Value,
                finalReadVersion,
                runtimeChanged);
            if (discoverTerminal)
            {
                return reconciled;
            }
            if (observedPaths is { Count: > 0 })
            {
                return RefineExecutionStateForObservedPaths(
                    reconciled,
                    observedPaths);
            }
            var queryRelations = observedRelations is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(
                    observedRelations,
                    StringComparer.Ordinal);
            if (sources.Any(IsNativeSymbol)
                || targets.Any(IsNativeSymbol))
            {
                queryRelations.Add(EdgeKinds.NativeImplementation);
            }
            return queryTraversalComplete && observedRelations is not null
                ? RefineExecutionStateForObservedRelations(
                    reconciled,
                    queryRelations)
                : reconciled;
        }
    }

    private static bool IsNativeSymbol(SymbolHit symbol)
    {
        if (string.Equals(
                symbol.Kind,
                SymbolKinds.NativeExport,
                StringComparison.Ordinal))
        {
            return true;
        }
        if (symbol.CanonicalKey?.StartsWith(
                "cpp:",
                StringComparison.Ordinal) == true
            || symbol.CanonicalKey?.StartsWith(
                "c:",
                StringComparison.Ordinal) == true)
        {
            return true;
        }
        return Path.GetExtension(symbol.FilePath).ToLowerInvariant()
            is ".c" or ".cc" or ".cpp" or ".cxx" or ".c++"
                or ".h" or ".hh" or ".hpp" or ".hxx";
    }

    private static async Task<IReadOnlyList<SymbolHit>> ResolveSymbolsAsync(
        IGraphStore store,
        string query,
        CancellationToken ct)
    {
        var selection = query.Trim();
        var matches = await SymbolQueryResolver.ResolveAsync(
            store,
            selection,
            limit: 10,
            ct).ConfigureAwait(false);
        return HighestRankedMatches(selection, matches);
    }

    private static IReadOnlyList<SymbolHit> HighestRankedMatches(
        string query,
        IReadOnlyList<SymbolHit> matches)
    {
        var exactNames = matches
            .Where(hit => string.Equals(
                hit.Name,
                query,
                StringComparison.Ordinal))
            .ToArray();
        if (exactNames.Length > 0) return exactNames;

        var exactFqns = matches
            .Where(hit => string.Equals(
                hit.Fqn,
                query,
                StringComparison.Ordinal))
            .ToArray();
        if (exactFqns.Length > 0) return exactFqns;

        var suffixFqns = matches
            .Where(hit => hit.Fqn.EndsWith(
                query,
                StringComparison.Ordinal))
            .ToArray();
        if (suffixFqns.Length > 0) return suffixFqns;

        var namePrefixes = matches
            .Where(hit => hit.Name.StartsWith(
                query,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return namePrefixes.Length > 0 ? namePrefixes : matches;
    }

    private static string AmbiguousSelectionNote(
        string role,
        string query,
        IReadOnlyList<SymbolHit> candidates) =>
        $"Ambiguous {role} symbol '{query}' matched {candidates.Count} candidates. "
        + "Use one exact canonical key: "
        + string.Join(
            "; ",
            candidates.Select(candidate =>
                $"`{candidate.CanonicalKey ?? "<no-canonical-key>"}` ({candidate.Fqn})"));

    private static Task<IReadOnlyList<EdgeTraversalHit>> ListOutboundEdgesAsync(
        IGraphStore store,
        long symbolId,
        IReadOnlyList<string> relations,
        int limit,
        CancellationToken ct) =>
        relations.Count == 1
            ? store.ListAuditableOutboundEdgesAsync(
                symbolId,
                limit,
                relations[0],
                ct)
            : store.ListAuditableOutboundEdgesByKindsAsync(
                symbolId,
                relations,
                limit,
                ct);

    private static async Task<bool> HasAnyAuditableOutboundAsync(
        IGraphStore store,
        long symbolId,
        string relation,
        CancellationToken ct) =>
        (await store.ListAuditableOutboundEdgesAsync(
            symbolId,
            limit: 1,
            edgeKind: relation,
            ct: ct).ConfigureAwait(false)).Count > 0;

    private static IReadOnlyList<string> AllowedExecutionRelations(
        ExecutionStage stage) =>
        stage switch
        {
            ExecutionStage.AwaitBinding =>
                ["binds-path", EdgeKinds.HandlesEvent],
            ExecutionStage.AwaitCommand => [EdgeKinds.CommandExecutes],
            ExecutionStage.AwaitManagedCall =>
                [
                    EdgeKinds.Calls,
                    EdgeKinds.Schedules,
                    EdgeKinds.Dispatches,
                    EdgeKinds.InterfaceDispatchesTo,
                    EdgeKinds.HandlesEvent,
                    EdgeKinds.RaisesEvent,
                    EdgeKinds.EventDispatchesTo,
                    EdgeKinds.SubscribesHandler,
                ],
            ExecutionStage.ManagedClient =>
                [
                    EdgeKinds.Calls,
                    EdgeKinds.Schedules,
                    EdgeKinds.Dispatches,
                    EdgeKinds.InterfaceDispatchesTo,
                    EdgeKinds.HandlesEvent,
                    EdgeKinds.RaisesEvent,
                    EdgeKinds.EventDispatchesTo,
                    EdgeKinds.SubscribesHandler,
                    EdgeKinds.GrpcCalls,
                    EdgeKinds.PInvokeMapsTo,
                    EdgeKinds.NativeImplementation,
                ],
            ExecutionStage.AwaitRpcDispatch =>
                [EdgeKinds.RpcDispatchesTo],
            ExecutionStage.AwaitServerCall => [EdgeKinds.Calls],
            ExecutionStage.ManagedServer =>
                [EdgeKinds.Calls, EdgeKinds.PInvokeMapsTo],
            ExecutionStage.AwaitNativeCall =>
                [EdgeKinds.NativeImplementation, EdgeKinds.Calls],
            ExecutionStage.NativeAlgorithm => [EdgeKinds.Calls],
            _ => throw new InvalidOperationException(
                $"Execution relation requested for invalid stage `{stage}`."),
        };

    private static bool TryAdvanceExecutionStage(
        ExecutionStage current,
        string relation,
        out ExecutionStage next)
    {
        next = (current, relation) switch
        {
            (ExecutionStage.AwaitBinding, "binds-path") =>
                ExecutionStage.AwaitCommand,
            (ExecutionStage.AwaitBinding, EdgeKinds.HandlesEvent) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitCommand, EdgeKinds.CommandExecutes) =>
                ExecutionStage.AwaitManagedCall,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.Calls) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.Schedules) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.Dispatches) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.InterfaceDispatchesTo) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.HandlesEvent) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.RaisesEvent) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.EventDispatchesTo) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.AwaitManagedCall, EdgeKinds.SubscribesHandler) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.Calls) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.Schedules) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.Dispatches) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.InterfaceDispatchesTo) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.HandlesEvent) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.RaisesEvent) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.EventDispatchesTo) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.SubscribesHandler) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.GrpcCalls) =>
                ExecutionStage.AwaitRpcDispatch,
            (ExecutionStage.ManagedClient, EdgeKinds.PInvokeMapsTo) =>
                ExecutionStage.AwaitNativeCall,
            (ExecutionStage.ManagedClient, EdgeKinds.NativeImplementation) =>
                ExecutionStage.AwaitNativeCall,
            (ExecutionStage.AwaitRpcDispatch, EdgeKinds.RpcDispatchesTo) =>
                ExecutionStage.AwaitServerCall,
            (ExecutionStage.AwaitServerCall, EdgeKinds.Calls) =>
                ExecutionStage.ManagedServer,
            (ExecutionStage.ManagedServer, EdgeKinds.Calls) =>
                ExecutionStage.ManagedServer,
            (ExecutionStage.ManagedServer, EdgeKinds.PInvokeMapsTo) =>
                ExecutionStage.AwaitNativeCall,
            (ExecutionStage.AwaitNativeCall, EdgeKinds.NativeImplementation) =>
                ExecutionStage.AwaitNativeCall,
            (ExecutionStage.AwaitNativeCall, EdgeKinds.Calls) =>
                ExecutionStage.NativeAlgorithm,
            (ExecutionStage.NativeAlgorithm, EdgeKinds.Calls) =>
                ExecutionStage.NativeAlgorithm,
            _ => ExecutionStage.Invalid,
        };
        return next != ExecutionStage.Invalid;
    }

    private static async Task<ExecutionStage> InitialExecutionStageAsync(
        IGraphStore store,
        long symbolId,
        CancellationToken ct)
    {
        if (await HasAnyAuditableOutboundAsync(
                store,
                symbolId,
                "binds-path",
                ct).ConfigureAwait(false))
        {
            return ExecutionStage.AwaitBinding;
        }
        if (await HasAnyAuditableOutboundAsync(
                store,
                symbolId,
                EdgeKinds.CommandExecutes,
                ct).ConfigureAwait(false))
        {
            return ExecutionStage.AwaitCommand;
        }
        return ExecutionStage.ManagedClient;
    }

    private static TraceCallPathExecutionState BuildExecutionState(
        ScopeHost host)
    {
        var projections = new List<TraceCallPathProjectionState>(3);
        var failures = new List<string>();

        var scopeFailureCount =
            host.FailedProjects.Count + host.FailedFiles.Count;
        var scopeAuthoritative = string.Equals(
                host.Status,
                "ok",
                StringComparison.Ordinal)
            && scopeFailureCount == 0;
        projections.Add(new TraceCallPathProjectionState(
            "scope",
            host.Status,
            Applicable: true,
            Authoritative: scopeAuthoritative,
            RetainedLastGood: false,
            FailureCount: scopeFailureCount));
        if (!scopeAuthoritative)
        {
            failures.Add(
                $"scope: current source universe is {host.Status}"
                + (string.IsNullOrWhiteSpace(host.StatusMessage)
                    ? "."
                    : $": {host.StatusMessage}"));
            if (scopeFailureCount > 0)
            {
                failures.Add(
                    $"scope: {host.FailedProjects.Count} project failure(s) and "
                    + $"{host.FailedFiles.Count} file failure(s).");
            }
        }

        var grpc = host.GrpcLinkState;
        var grpcAuthoritative = grpc is
        {
            Status: GrpcLinkRuntimeStatus.Complete,
            RetainedLastGood: false,
            FailureCount: 0,
        };
        projections.Add(new TraceCallPathProjectionState(
            "grpc",
            grpc?.Status.ToString().ToLowerInvariant() ?? "not-run",
            Applicable: true,
            Authoritative: grpcAuthoritative,
            RetainedLastGood: grpc?.RetainedLastGood ?? false,
            FailureCount: grpc?.FailureCount ?? 1));
        if (grpc is null)
        {
            failures.Add(
                "grpc: the current gRPC projection has not run.");
        }
        else
        {
            failures.AddRange(grpc.Failures.Select(failure =>
                $"grpc:{failure.Code}: {failure.Message}"));
            if (!grpcAuthoritative && grpc.Failures.Count == 0)
            {
                failures.Add(
                    $"grpc: projection status is {grpc.Status.ToString().ToLowerInvariant()} "
                    + $"with {grpc.FailureCount} reported failure(s).");
            }
        }

        var nativeConfigured = host.Scope.Interop is not null;
        var native = host.NativeInteropState;
        var nativeAuthoritative = nativeConfigured
            && native is
            {
                Status: NativeInteropRuntimeStatus.Complete,
                RetainedLastGood: false,
                IsExportUniverseComplete: true,
                PendingStaleSymbols: 0,
                Failures.Count: 0,
            } && host.ManagedInteropInputComplete;
        projections.Add(new TraceCallPathProjectionState(
            "native-interop",
            !nativeConfigured
                ? "not-configured"
                : native?.Status.ToString().ToLowerInvariant()
                    ?? "not-started",
            Applicable: true,
            Authoritative: nativeAuthoritative,
            RetainedLastGood: native?.RetainedLastGood ?? false,
            FailureCount: !nativeConfigured
                ? 1
                : native?.Failures.Count ?? 1));
        if (!nativeConfigured)
        {
            failures.Add(
                "native-interop: this scope has no native projection configuration, "
                + "so full execution-chain absence is unavailable.");
        }
        else if (native is null)
        {
            failures.Add(
                "native-interop: the configured projection has not started.");
        }
        else
        {
            failures.AddRange(native!.Failures.Select(failure =>
                $"native-interop:{failure.Stage}/{failure.Code}: {failure.Message}"));
            if (!host.ManagedInteropInputComplete)
            {
                failures.Add(
                    "native-interop: the managed import universe is incomplete.");
            }
            if (native.PendingStaleSymbols > 0)
            {
                failures.Add(
                    $"native-interop: {native.PendingStaleSymbols} stale symbol(s) remain pending cleanup.");
            }
            if (!nativeAuthoritative
                && native.Failures.Count == 0
                && host.ManagedInteropInputComplete
                && native.PendingStaleSymbols == 0)
            {
                failures.Add(
                    $"native-interop: projection status is "
                    + $"{native.Status.ToString().ToLowerInvariant()} and is not authoritative.");
            }
        }

        var partial = projections.Any(projection =>
            projection.Applicable && !projection.Authoritative);
        var retainedLastGood = projections.Any(projection =>
            projection.RetainedLastGood);
        IReadOnlyList<string> boundedFailures = failures.Count
            <= MaximumReportedProjectionFailures
            ? failures
            : failures
                .Take(MaximumReportedProjectionFailures - 1)
                .Append(
                    $"{failures.Count - MaximumReportedProjectionFailures + 1} additional projection failure(s) omitted.")
                .ToArray();
        return new TraceCallPathExecutionState(
            partial ? "partial" : "complete",
            partial,
            AbsenceAuthoritative: !partial,
            retainedLastGood,
            projections,
            boundedFailures);
    }

    private static TraceCallPathExecutionState
        RefineExecutionStateForObservedPaths(
            TraceCallPathExecutionState current,
            IReadOnlyList<TraceCallPath> paths)
    {
        var relations = paths
            .SelectMany(path => path.Hops)
            .Select(hop => hop.Relation)
            .ToHashSet(StringComparer.Ordinal);
        return RefineExecutionStateForObservedRelations(
            current,
            relations);
    }

    private static TraceCallPathExecutionState
        RefineExecutionStateForObservedRelations(
            TraceCallPathExecutionState current,
            IReadOnlySet<string> relations)
    {
        var scope = current.Projections.First(projection =>
            string.Equals(
                projection.Name,
                "scope",
                StringComparison.Ordinal));
        var projections = current.Projections
            .Select(projection => projection.Name switch
            {
                "grpc" => projection with
                {
                    Applicable =
                        relations.Contains(EdgeKinds.GrpcCalls)
                        || relations.Contains(EdgeKinds.RpcDispatchesTo),
                },
                "native-interop" => projection with
                {
                    Applicable =
                        relations.Contains(EdgeKinds.PInvokeMapsTo)
                        || relations.Contains(EdgeKinds.NativeImplementation),
                },
                _ => projection,
            })
            .ToList();

        AddRelationProjection(
            "managed-calls",
            relations.Contains(EdgeKinds.Calls));
        AddRelationProjection(
            "task-scheduling",
            relations.Contains(EdgeKinds.Schedules));
        AddRelationProjection(
            "ui-dispatch",
            relations.Contains(EdgeKinds.Dispatches));
        AddRelationProjection(
            "interface-dispatch",
            relations.Contains(EdgeKinds.InterfaceDispatchesTo));
        var usesExternalFrameworkTrigger =
            relations.Contains(EdgeKinds.SubscribesHandler);
        var relationFailures = new List<string>();
        if (usesExternalFrameworkTrigger)
        {
            projections.Add(new TraceCallPathProjectionState(
                "event-flow",
                "partial-external-trigger",
                Applicable: true,
                Authoritative: false,
                RetainedLastGood: false,
                FailureCount: 1));
            relationFailures.Add(
                "event-flow: partial because framework-owned event trigger is external "
                + "and its occurrence cannot be proven statically.");
        }
        else
        {
            AddRelationProjection(
                "event-flow",
                relations.Overlaps(
                [
                    EdgeKinds.RaisesEvent,
                    EdgeKinds.EventDispatchesTo,
                ]));
        }
        AddRelationProjection(
            "xaml-event-flow",
            relations.Contains(EdgeKinds.HandlesEvent));
        AddRelationProjection(
            "command-flow",
            relations.Overlaps(
            [
                "binds-path",
                EdgeKinds.CommandExecutes,
            ]));

        var applicableNames = projections
            .Where(projection => projection.Applicable)
            .Select(projection => projection.Name)
            .ToHashSet(StringComparer.Ordinal);
        var failures = current.Failures
            .Where(failure =>
            {
                var separator = failure.IndexOf(':');
                var projection = separator < 0
                    ? failure
                    : failure[..separator];
                return applicableNames.Contains(projection);
            })
            .Concat(relationFailures)
            .ToArray();
        var partial = projections.Any(projection =>
            projection.Applicable && !projection.Authoritative);
        return current with
        {
            Status = partial ? "partial" : "complete",
            Partial = partial,
            AbsenceAuthoritative = !partial,
            RetainedLastGood = projections.Any(projection =>
                projection.Applicable && projection.RetainedLastGood),
            Projections = projections,
            Failures = failures,
        };

        void AddRelationProjection(string name, bool applicable)
        {
            if (!applicable)
            {
                return;
            }
            projections.Add(new TraceCallPathProjectionState(
                name,
                scope.Authoritative ? "complete" : scope.Status,
                Applicable: true,
                Authoritative: scope.Authoritative,
                RetainedLastGood: false,
                FailureCount: scope.Authoritative ? 0 : scope.FailureCount));
        }
    }

    internal static TraceCallPathExecutionState ReconcileExecutionState(
        TraceCallPathExecutionState current,
        GraphReadVersion initialReadVersion,
        GraphReadVersion finalReadVersion,
        bool runtimeStateChanged)
    {
        ArgumentNullException.ThrowIfNull(current);
        return initialReadVersion == finalReadVersion
            && !runtimeStateChanged
                ? current
                : MarkExecutionUnstable(
                    current,
                    "The graph or projection runtime changed while this path was being read.");
    }

    private static TraceCallPathExecutionState MarkExecutionUnstable(
        TraceCallPathExecutionState current,
        string failure)
    {
        var projections = current.Projections
            .Where(projection =>
                !string.Equals(
                    projection.Name,
                    "query-snapshot",
                    StringComparison.Ordinal))
            .Append(new TraceCallPathProjectionState(
                "query-snapshot",
                "changed",
                Applicable: true,
                Authoritative: false,
                RetainedLastGood: false,
                FailureCount: 1))
            .ToArray();
        var failures = current.Failures
            .Append($"query-snapshot: {failure}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<string> boundedFailures = failures.Length
            <= MaximumReportedProjectionFailures
            ? failures
            : failures
                .Take(MaximumReportedProjectionFailures - 1)
                .Append(
                    $"{failures.Length - MaximumReportedProjectionFailures + 1} additional projection failure(s) omitted.")
                .ToArray();
        return current with
        {
            Status = "partial",
            Partial = true,
            AbsenceAuthoritative = false,
            Projections = projections,
            Failures = boundedFailures,
        };
    }

    private static TraceCallPathExecutionState MarkExecutionTruncated(
        TraceCallPathExecutionState current,
        TraceCallPathTruncation truncation)
    {
        var projections = current.Projections
            .Where(projection =>
                !string.Equals(
                    projection.Name,
                    "query-bounds",
                    StringComparison.Ordinal))
            .Append(new TraceCallPathProjectionState(
                "query-bounds",
                "truncated",
                Applicable: true,
                Authoritative: false,
                RetainedLastGood: false,
                FailureCount: truncation.TruncatedBy.Count))
            .ToArray();
        var failures = current.Failures
            .Append(
                $"query-bounds: {FormatTruncationSummary(truncation)}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<string> boundedFailures = failures.Length
            <= MaximumReportedProjectionFailures
            ? failures
            : failures
                .Take(MaximumReportedProjectionFailures - 1)
                .Append(
                    $"{failures.Length - MaximumReportedProjectionFailures + 1} additional projection failure(s) omitted.")
                .ToArray();
        return current with
        {
            Status = "partial",
            Partial = true,
            AbsenceAuthoritative = false,
            Projections = projections,
            Failures = boundedFailures,
        };
    }

    private static string FormatTruncationSummary(
        TraceCallPathTruncation truncation) =>
        $"truncated_by={string.Join(",", truncation.TruncatedBy)}; "
        + $"expanded_nodes={truncation.ExpandedNodes}/{truncation.MaxNodes}; "
        + $"depth_reached={truncation.DepthReached}/{truncation.MaxDepth}; "
        + $"returned_paths={truncation.ReturnedPaths}/{truncation.MaxPaths}; "
        + "returned_evidence_rows="
        + $"{truncation.ReturnedEvidenceRows}/{truncation.MaxEvidenceRows}; "
        + $"branch_limit={truncation.BranchLimit}.";

    private static ExecutionProjectionStamp CaptureProjectionStamp(
        ScopeHost host) =>
        new(
            host.Status,
            host.StatusMessage,
            host.FailedProjects,
            host.FailedFiles,
            host.ManagedInteropInputComplete,
            host.GrpcLinkState,
            host.NativeInteropState);

    private static bool ProjectionStampEquals(
        ExecutionProjectionStamp left,
        ExecutionProjectionStamp right) =>
        string.Equals(left.ScopeStatus, right.ScopeStatus, StringComparison.Ordinal)
        && string.Equals(
            left.ScopeStatusMessage,
            right.ScopeStatusMessage,
            StringComparison.Ordinal)
        && ReferenceEquals(left.FailedProjects, right.FailedProjects)
        && ReferenceEquals(left.FailedFiles, right.FailedFiles)
        && left.ManagedInteropInputComplete
            == right.ManagedInteropInputComplete
        && ReferenceEquals(left.Grpc, right.Grpc)
        && ReferenceEquals(left.Native, right.Native);

    private static string AppendNote(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current} {addition}";

    private static string AddAbsenceDisclosure(
        string note,
        TraceCallPathExecutionState? executionState) =>
        executionState is { AbsenceAuthoritative: false }
            ? AppendNote(
                note,
                "The relevant projections are partial, so this is not an authoritative current absence.")
            : note;

    private static string? ValidateCanonicalIntent(
        string query,
        string parameterName)
    {
        var selection = query.Trim();
        var colon = selection.IndexOf(':');
        if (colon <= 0) return null;
        var scheme = selection[..colon];
        if (!CanonicalKeyValidator.EnforcedSchemes.Contains(scheme))
        {
            return null;
        }
        if (CanonicalKeyValidator.IsValid(selection)) return null;

        try
        {
            CanonicalKeyValidator.Validate(selection, parameterName);
        }
        catch (ArgumentException ex)
        {
            return $"trace_call_path `{parameterName}` canonical key is invalid: {ex.Message}";
        }
        return null;
    }

    internal static async Task<TraceCallPathHop?> BuildAuditableHopAsync(
        IGraphStore store,
        SymbolHit source,
        SymbolHit target,
        string relation,
        CancellationToken ct)
    {
        var storedEvidence = await store.ListEdgeEvidenceAsync(
            source.Id,
            target.Id,
            relation,
            EvidenceLimitPerHop + 1,
            ct).ConfigureAwait(false);
        if (storedEvidence.Count == 0) return null;

        var includedEvidence = storedEvidence
            .Take(EvidenceLimitPerHop)
            .ToList();
        return new TraceCallPathHop(
            MapSymbol(source),
            MapSymbol(target),
            relation,
            ConfidenceName(storedEvidence.Max(item => item.Confidence)),
            includedEvidence.Select(MapEvidence).ToList(),
            EvidenceTruncated: storedEvidence.Count > EvidenceLimitPerHop);
    }

    internal static TraceCallPathSymbol MapSymbol(SymbolHit hit) =>
        new(
            hit.Id,
            hit.CanonicalKey,
            hit.Fqn,
            hit.Kind,
            hit.FilePath,
            hit.StartLine,
            hit.StartCol,
            hit.EndLine,
            hit.EndCol);

    internal static TraceCallPathEvidence MapEvidence(Evidence evidence) =>
        new(
            evidence.Location.FilePath,
            evidence.Location.StartLine,
            evidence.Location.StartColumn,
            evidence.Location.EndLine,
            evidence.Location.EndColumn,
            ConfidenceName(evidence.Confidence),
            evidence.Producer,
            evidence.Metadata);

    internal static string ConfidenceName(CoreEvidenceConfidence confidence) =>
        confidence switch
        {
            CoreEvidenceConfidence.Exact => "exact",
            CoreEvidenceConfidence.Semantic => "semantic",
            _ => "inferred",
        };

    internal static CoreEvidenceConfidence ConfidenceValue(string confidence) =>
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
        var singleAmbiguous = result.Scopes.Count == 1
            && string.Equals(
                result.Scopes[0].Status,
                "ambiguous",
                StringComparison.Ordinal);
        sb.Append("trace_call_path `")
          .Append(result.FromQuery);
        if (result.ToQuery is null)
        {
            sb.Append("` → terminal native algorithm: ");
        }
        else
        {
            sb.Append("` → `")
              .Append(result.ToQuery)
              .Append("`: ");
        }
        if (singleAmbiguous)
        {
            sb.AppendLine("ambiguous; path search not executed");
        }
        else
        {
            sb
              .Append(pathCount)
              .Append(" path")
              .Append(pathCount == 1 ? "" : "s")
              .Append(result.Profile == ExecutionProfile
                  ? " via execution profile"
                  : $" via `{result.EdgeKind}`")
              .AppendLine();
        }
        if (result.TerminalDefinition is not null)
        {
            sb.Append("terminal definition: ")
              .AppendLine(result.TerminalDefinition);
        }
        sb.Append("detail: ").AppendLine(result.Detail);

        foreach (var scope in result.Scopes)
        {
            if (result.Scopes.Count > 1)
            {
                sb.AppendLine();
                sb.Append("### scope: `").Append(scope.ScopeId).AppendLine("`");
            }
            if (!singleAmbiguous
                && string.Equals(
                    scope.Status,
                    "ambiguous",
                    StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine("status: ambiguous; path search not executed");
            }
            if (scope.ExecutionState is not null)
            {
                sb.AppendLine();
                sb.Append("execution projection: ")
                  .Append(scope.ExecutionState.Status)
                  .Append("; absence authoritative: ")
                  .AppendLine(scope.ExecutionState.AbsenceAuthoritative
                      ? "yes"
                      : "no");
                if (scope.ExecutionState.RetainedLastGood)
                {
                    sb.AppendLine(
                        "note: at least one relation projection is retained from the last complete index.");
                }
                foreach (var failure in scope.ExecutionState.Failures)
                {
                    sb.Append("projection gap: ")
                      .AppendLine(failure);
                }
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
                sb.Append("selection: from `")
                  .Append(path.From.CanonicalKey ?? "<no-canonical-key>")
                  .Append("`; to `")
                  .Append(path.To.CanonicalKey ?? "<no-canonical-key>")
                  .AppendLine("`");
                if (path.Hops.Count == 0)
                {
                    if (path.HopCount > 0)
                    {
                        sb.Append("- `")
                          .Append(path.From.Fqn)
                          .Append("` → `")
                          .Append(path.To.Fqn)
                          .Append("` (")
                          .Append(path.HopCount)
                          .AppendLine(" hops; evidence omitted in summary)");
                    }
                    else
                    {
                        sb.Append("- `")
                          .Append(path.From.Fqn)
                          .AppendLine("` (source equals destination)");
                    }
                    continue;
                }
                for (var hopIndex = 0; hopIndex < path.Hops.Count; hopIndex++)
                {
                    var hop = path.Hops[hopIndex];
                    var isConditional = hop.Evidence.Any(evidence =>
                        evidence.Metadata?.TryGetValue(
                            "control_flow",
                            out var controlFlow) == true
                        && string.Equals(
                            controlFlow,
                            "conditional",
                            StringComparison.Ordinal));
                    sb.Append(hopIndex + 1).Append(". `")
                      .Append(hop.From.Fqn)
                      .Append("` -[")
                      .Append(hop.Relation)
                      .Append("]-> `")
                      .Append(hop.To.Fqn)
                      .Append("` [")
                      .Append(hop.Confidence)
                      .Append(isConditional ? "; conditional" : "")
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
                        if (evidence.Metadata?.TryGetValue(
                                "condition",
                                out var condition) == true)
                        {
                            evidence.Metadata.TryGetValue(
                                "branch",
                                out var branch);
                            sb.Append("     condition")
                              .Append(string.IsNullOrWhiteSpace(branch)
                                  ? ""
                                  : $" ({branch})")
                              .Append(": `")
                              .Append(condition.Replace('`', '\''))
                              .AppendLine(
                                  "` — this hop is conditional, not guaranteed normal flow.");
                        }
                    }
                }
            }
            if (scope.Truncated)
            {
                sb.AppendLine();
                if (scope.Truncation is null)
                {
                    sb.AppendLine("note: traversal was truncated.");
                }
                else
                {
                    sb.Append("truncated_by: ")
                      .AppendLine(string.Join(
                          ", ",
                          scope.Truncation.TruncatedBy));
                    sb.Append("expanded_nodes: ")
                      .Append(scope.Truncation.ExpandedNodes)
                      .Append('/')
                      .AppendLine(scope.Truncation.MaxNodes.ToString());
                    sb.Append("depth_reached: ")
                      .Append(scope.Truncation.DepthReached)
                      .Append('/')
                      .AppendLine(scope.Truncation.MaxDepth.ToString());
                    sb.Append("returned_paths: ")
                      .Append(scope.Truncation.ReturnedPaths)
                      .Append('/')
                      .AppendLine(scope.Truncation.MaxPaths.ToString());
                    sb.Append("returned_evidence_rows: ")
                      .Append(scope.Truncation.ReturnedEvidenceRows)
                      .Append('/')
                      .AppendLine(
                          scope.Truncation.MaxEvidenceRows.ToString());
                    sb.Append("branch_limit: ")
                      .AppendLine(scope.Truncation.BranchLimit.ToString());
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private sealed record PathState(
        SymbolHit Current,
        List<TraceCallPathHop> Hops,
        HashSet<long> Visited,
        ExecutionStage Stage);

    private enum ExecutionStage
    {
        Invalid,
        Relation,
        AwaitBinding,
        AwaitCommand,
        AwaitManagedCall,
        ManagedClient,
        AwaitRpcDispatch,
        AwaitServerCall,
        ManagedServer,
        AwaitNativeCall,
        NativeAlgorithm,
    }

    private sealed record ExecutionProjectionStamp(
        string ScopeStatus,
        string? ScopeStatusMessage,
        object FailedProjects,
        object FailedFiles,
        bool ManagedInteropInputComplete,
        GrpcLinkRuntimeState? Grpc,
        NativeInteropRuntimeState? Native);
}
