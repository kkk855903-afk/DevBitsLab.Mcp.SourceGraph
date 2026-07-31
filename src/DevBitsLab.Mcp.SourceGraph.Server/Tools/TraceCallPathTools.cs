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
        "A terminal algorithm is reached through optional UI/command entry edges, one or more "
        + "managed calls or interface dispatches, an optional gRPC dispatch stage, "
        + "pinvoke-maps-to, a native implementation or call edge, and zero or more native "
        + "calls, in that order; its final node has "
        + "no auditable outbound calls edge.";
    private static readonly string[] ExecutionRelations =
    [
        "binds-path",
        EdgeKinds.CommandExecutes,
        EdgeKinds.Calls,
        EdgeKinds.InterfaceDispatchesTo,
        EdgeKinds.HandlesEvent,
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
            from: from,
            to: to,
            fromId: null,
            toId: null,
            kind: kind,
            profile: null,
            maxDepth: maxDepth,
            maxPaths: maxPaths,
            maxNodes: maxNodes,
            scope: scope,
            ct: ct);

    [McpServerTool(
        Name = "trace_call_path",
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(TraceCallPathResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"show how execution can flow from A to B\"")]
    [Description("Trace bounded directed paths between indexed symbols. Defaults to calls edges and requires `to`. Set profile=execution to follow the ordered evidence-backed UI → command → managed call → gRPC dispatch → P/Invoke state machine. Execution mode may omit `to` when `from` is an exact canonical key; it then returns only proven terminal native algorithms. Exact canonical-key inputs never use fuzzy matching. Every hop includes occurrence evidence, and execution results disclose whether current absence claims are authoritative.")]
    public static Task<CallToolResult> TraceCallPathWithProfileAsync(
        ScopeRouter router,
        [Description("Starting symbol name, FQN, or exact canonical key. Mutually exclusive with fromId.")] string? from = null,
        [Description("Destination symbol name, FQN, or exact canonical key. May be omitted only with profile=execution and an exact canonical `from`.")]
        string? to = null,
        [Description("Exact starting symbol id returned by resolve_symbol. Mutually exclusive with from.")]
        long? fromId = null,
        [Description("Exact destination symbol id returned by resolve_symbol. Mutually exclusive with to.")]
        long? toId = null,
        [Description("Kebab-case edge relation to traverse (default calls)")] string? kind = null,
        [Description("Optional traversal profile. Use execution for the ordered cross-domain execution state machine; omit for one relation.")]
        string? profile = null,
        [Description("Maximum hops per path, 1-12 (default 8)")] int maxDepth = 8,
        [Description("Maximum returned paths, 1-25 (default 10)")] int maxPaths = 10,
        [Description("Maximum expanded graph nodes per scope, 1-5000 (default 1000)")] int maxNodes = 1000,
        [Description("Optional scope id, '*', or comma-separated scope ids")] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "trace_call_path",
            new { from, to, fromId, toId, kind, profile, maxDepth, maxPaths, maxNodes, scope },
            () => TraceCallPathImplAsync(
                router,
                from,
                to,
                fromId,
                toId,
                kind,
                profile,
                maxDepth,
                maxPaths,
                maxNodes,
                scope,
                ct));

    private static async Task<CallToolResult> TraceCallPathImplAsync(
        ScopeRouter router,
        string? from,
        string? to,
        long? fromId,
        long? toId,
        string? kind,
        string? profile,
        int maxDepth,
        int maxPaths,
        int maxNodes,
        object? scope,
        CancellationToken ct)
    {
        var hasFrom = !string.IsNullOrWhiteSpace(from);
        if (hasFrom == (fromId is not null))
        {
            return DiagnosticResult.Error(
                "trace_call_path requires exactly one of `from` or `fromId`.");
        }
        if (from?.Trim().Length > MaximumQueryCharacters
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
            && string.IsNullOrWhiteSpace(to)
            && toId is null;
        var hasTo = !string.IsNullOrWhiteSpace(to);
        if (!discoverTerminal && hasTo == (toId is not null))
        {
            return DiagnosticResult.Error(
                "trace_call_path requires a non-empty `to` or positive `toId` (exactly one) unless profile=execution discovers terminal algorithms.");
        }
        if (discoverTerminal
            && fromId is null
            && !CanonicalKeyValidator.IsValid(from!.Trim()))
        {
            return DiagnosticResult.Error(
                "trace_call_path requires an exact canonical `from` key when `profile=execution` omits `to`.");
        }
        var canonicalIntentError =
            (hasFrom ? ValidateCanonicalIntent(from!, "from") : null)
            ?? (discoverTerminal || !hasTo ? null : ValidateCanonicalIntent(to!, "to"));
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
                    fromId,
                    toId,
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

        var dto = new TraceCallPathResult(
            from?.Trim() ?? $"#{fromId}",
            discoverTerminal ? null : (to?.Trim() ?? $"#{toId}"),
            fromId,
            discoverTerminal ? null : toId,
            normalizedProfile,
            discoverTerminal ? "execution-terminal" : "explicit-target",
            discoverTerminal ? ExecutionTerminalDefinition : null,
            edgeKind,
            relations,
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
        string? fromQuery,
        string? toQuery,
        long? fromId,
        long? toId,
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
        var sourceResolution = await SymbolResolver.ResolveAsync(
            host.Store,
            fromQuery,
            fromId,
            fileHint: null,
            candidateLimit: 10,
            ct).ConfigureAwait(false);
        if (sourceResolution.Selected is null)
        {
            executionState = await FinalizeExecutionStateAsync()
                .ConfigureAwait(false);
            var missingSourceNote =
                ResolutionNote("source", fromQuery, fromId, sourceResolution);
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

        IReadOnlyList<SymbolHit> sources = [sourceResolution.Selected!];
        var targetResolution = discoverTerminal
            ? null
            : await SymbolResolver.ResolveAsync(
                host.Store,
                toQuery,
                toId,
                fileHint: null,
                candidateLimit: 10,
                ct).ConfigureAwait(false);
        if (!discoverTerminal && targetResolution!.Selected is null)
        {
            executionState = await FinalizeExecutionStateAsync()
                .ConfigureAwait(false);
            var missingTargetNote =
                ResolutionNote("destination", toQuery, toId, targetResolution);
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

        IReadOnlyList<SymbolHit> targets = discoverTerminal
            ? []
            : [targetResolution!.Selected!];

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
        var branchLimit = Math.Min(maxNodes, 1000);
        var stop = false;

        while (queue.Count > 0 && !stop)
        {
            ct.ThrowIfCancellationRequested();
            var state = queue.Dequeue();
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
                    truncated |= queue.Count > 0;
                    break;
                }
                continue;
            }
            if (targetsById.TryGetValue(state.Current.Id, out var reachedTarget))
            {
                AddCompletedPath(state, reachedTarget);
                if (paths.Count >= maxPaths)
                {
                    truncated |= queue.Count > 0;
                    break;
                }
                continue;
            }
            if (state.Hops.Count >= maxDepth)
            {
                truncated |= await HasUnexploredOutboundAsync(
                    host.Store,
                    state,
                    ct).ConfigureAwait(false);
                continue;
            }
            if (expandedNodes >= maxNodes)
            {
                truncated |= await HasUnexploredOutboundAsync(
                    host.Store,
                    state,
                    ct).ConfigureAwait(false);
                truncated |= queue.Count > 0;
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
            var unvisitedEdges = storedEdges
                .Where(edge => !state.Visited.Contains(edge.Symbol.Id))
                .Take(branchLimit + 1)
                .ToList();
            var branchTruncated = unvisitedEdges.Count > branchLimit;
            if (branchTruncated) truncated = true;
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

                if (targetsById.TryGetValue(callee.Id, out reachedTarget))
                {
                    AddCompletedPath(next, reachedTarget);
                    if (paths.Count >= maxPaths)
                    {
                        truncated |= branchTruncated
                            || edgeIndex + 1 < visibleEdges.Count
                            || queue.Count > 0;
                        stop = true;
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

        executionState = await FinalizeExecutionStateAsync()
            .ConfigureAwait(false);
        if (executionState is not null && truncated)
        {
            executionState = MarkExecutionTruncated(executionState);
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
            executionState);

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
            var key = state.Hops.Count == 0
                ? $"same:{state.Current.Id}"
                : string.Join(">", state.Hops.Select(hop =>
                    $"{hop.From.SymbolId}:{hop.Relation}:{hop.To.SymbolId}"));
            if (!emittedPathKeys.Add(key)) return;
            var evidenceRows = state.Hops.Sum(hop => hop.Evidence.Count);
            if (returnedEvidenceRows + evidenceRows
                > MaximumReturnedEvidenceRows)
            {
                truncated = true;
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
            FinalizeExecutionStateAsync()
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
            return ReconcileExecutionState(
                finalState,
                initialReadVersion!.Value,
                finalReadVersion,
                runtimeChanged);
        }

        static string ResolutionNote(
            string endpoint,
            string? query,
            long? id,
            SymbolResolution resolution)
        {
            if (resolution.Status == "ambiguous")
            {
                var candidates = string.Join(
                    ", ",
                    resolution.Candidates.Select(candidate =>
                        $"{candidate.Fqn} (id={candidate.Id}, key={candidate.CanonicalKey ?? "missing"})"));
                return $"Ambiguous {endpoint} symbol; no default was selected. Candidates: {candidates}.";
            }
            return $"No {endpoint} symbol matches '{query?.Trim() ?? $"#{id}"}'.";
        }
    }

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
            ExecutionStage.AwaitManagedCall => [EdgeKinds.Calls],
            ExecutionStage.ManagedClient =>
                [
                    EdgeKinds.Calls,
                    EdgeKinds.InterfaceDispatchesTo,
                    EdgeKinds.GrpcCalls,
                    EdgeKinds.PInvokeMapsTo,
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
            (ExecutionStage.ManagedClient, EdgeKinds.Calls) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.InterfaceDispatchesTo) =>
                ExecutionStage.ManagedClient,
            (ExecutionStage.ManagedClient, EdgeKinds.GrpcCalls) =>
                ExecutionStage.AwaitRpcDispatch,
            (ExecutionStage.ManagedClient, EdgeKinds.PInvokeMapsTo) =>
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
                ExecutionStage.NativeAlgorithm,
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
                ct).ConfigureAwait(false)
            || await HasAnyAuditableOutboundAsync(
                store,
                symbolId,
                EdgeKinds.HandlesEvent,
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
        TraceCallPathExecutionState current)
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
                FailureCount: 1))
            .ToArray();
        var failures = current.Failures
            .Append(
                "query-bounds: traversal stopped at a configured depth, path, node, branch, or evidence bound.")
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
        sb
          .Append(pathCount)
          .Append(" path")
          .Append(pathCount == 1 ? "" : "s")
          .Append(result.Profile == ExecutionProfile
              ? " via execution profile"
              : $" via `{result.EdgeKind}`")
          .AppendLine();
        if (result.TerminalDefinition is not null)
        {
            sb.Append("terminal definition: ")
              .AppendLine(result.TerminalDefinition);
        }

        foreach (var scope in result.Scopes)
        {
            if (result.Scopes.Count > 1)
            {
                sb.AppendLine();
                sb.Append("### scope: `").Append(scope.ScopeId).AppendLine("`");
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
