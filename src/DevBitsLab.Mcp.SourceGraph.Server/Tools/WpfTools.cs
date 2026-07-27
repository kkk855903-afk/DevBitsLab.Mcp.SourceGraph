using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Resources;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Read-only, evidence-first WPF queries over XAML binding and resource graph facts.
/// </summary>
[McpServerToolType]
public static class WpfTools
{
    private const string BindsPath = "binds-path";
    private const string BindsTo = "binds-to";
    private const string UsesResource = "uses-resource";
    private const string AppliesStyle = "applies-style";
    private const string CommandExecutes = "command-executes";
    private const string ResolvedBindingEdgeReason = "resolved-by-indexed-binding-edge";
    private const string ResolvedLegacyBindingEdgeReason = "resolved-by-legacy-binding-edge";
    private const string ResolvedResourceEdgeReason = "resolved-by-indexed-resource-edge";
    private const int MaxLimit = 200;
    private const int MaxEvidencePerMatch = 20;
    private const int MaxProbeRows = 2_000;
    private const int MaxTypedScopeFanout = 200;
    private const int OutputBudgetSafetyMargin = 256;

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(TraceBindingResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"trace this WPF binding\", \"where does this XAML binding resolve?\", \"why is this binding missing?\"")]
    [Description(
        "Trace a WPF data binding from a XAML element and/or binding path to its canonical target. " +
        "Returns resolved `binds-path` (and legacy `binds-to` fallback) edges together with " +
        "xaml-binding-finding/outcome rows for missing, ambiguous, incomplete, or unknown bindings. " +
        "Every match includes stored occurrence evidence; ambiguous element queries are returned " +
        "as candidates and are never guessed.")]
    public static Task<CallToolResult> TraceBindingAsync(
        ScopeRouter router,
        [Description("Optional XAML element name, FQN, or canonical key. Multiple matches return `ambiguous` without choosing one.")] string? element = null,
        [Description("Optional binding path or final member name, for example `User.Name` or `Name`.")] string? binding = null,
        [Description("Optional scope id, '*', or comma-separated scope ids.")] string? scope = null,
        [Description("Maximum result rows, 1-200 (default 50).")] int limit = 50,
        [Description("Output detail: summary | locations | evidence | audit.")] string detail = "summary",
        [Description("Override whether occurrence evidence is included.")] bool? includeEvidence = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "trace_binding",
            new { element, binding, scope, limit, detail, includeEvidence },
            () => TraceAsync(router, element, binding, scope, limit, detail, includeEvidence, isCommand: false, ct));

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(TraceCommandResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"trace this WPF command\", \"where does this Command binding resolve?\", \"why is this command missing?\"")]
    [Description(
        "Trace a WPF Command binding from a XAML element and/or command name to its canonical " +
        "ICommand-like property target. Returns resolved command-flavoured `binds-path` (and " +
        "legacy `binds-to` fallback) edges together with xaml-command-finding/outcome rows. " +
        "Every match includes stored occurrence evidence; ambiguous element queries are returned " +
        "as candidates and are never guessed.")]
    public static Task<CallToolResult> TraceCommandAsync(
        ScopeRouter router,
        [Description("Optional XAML element name, FQN, or canonical key. Multiple matches return `ambiguous` without choosing one.")] string? element = null,
        [Description("Optional command binding path or command member name, for example `SaveCommand`.")] string? command = null,
        [Description("Optional scope id, '*', or comma-separated scope ids.")] string? scope = null,
        [Description("Maximum result rows, 1-200 (default 50).")] int limit = 50,
        [Description("Output detail: summary | locations | evidence | audit.")] string detail = "summary",
        [Description("Override whether occurrence evidence is included.")] bool? includeEvidence = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "trace_command",
            new { element, command, scope, limit, detail, includeEvidence },
            () => TraceAsync(router, element, command, scope, limit, detail, includeEvidence, isCommand: true, ct));

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(CheckResourcesResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger("\"check WPF resources\", \"find missing StaticResource keys\", \"where does this XAML resource resolve?\"")]
    [Description(
        "Audit WPF resource references. Combines resolved `uses-resource` / `applies-style` edges with " +
        "xaml-resource-finding/outcome annotations and reports resolved, missing, ambiguous, " +
        "incomplete, unsupported, or unknown status plus canonical source/target identities and exact " +
        "occurrence evidence. Optional file and key filters are exact/suffix-safe and never " +
        "substitute a similarly named resource.")]
    public static Task<CallToolResult> CheckResourcesAsync(
        ScopeRouter router,
        [Description("Optional XAML file path or unique path suffix.")] string? file = null,
        [Description("Optional exact, case-sensitive XAML resource key.")] string? key = null,
        [Description("Optional scope id, '*', or comma-separated scope ids.")] string? scope = null,
        [Description("Maximum result rows, 1-200 (default 50).")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "check_resources",
            new { file, key, scope, limit },
            () => CheckResourcesImplAsync(router, file, key, scope, limit, ct));

    private static Task<CallToolResult> TraceAsync(
        ScopeRouter router,
        string? element,
        string? member,
        object? scope,
        int limit,
        string detail,
        bool? includeEvidence,
        bool isCommand,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(element) && string.IsNullOrWhiteSpace(member))
        {
            return Task.FromResult(DiagnosticResult.Error(
                isCommand
                    ? "trace_command requires at least one of `element` or `command`."
                    : "trace_binding requires at least one of `element` or `binding`."));
        }
        if (limit is < 1 or > MaxLimit)
        {
            return Task.FromResult(DiagnosticResult.Error(
                $"{(isCommand ? "trace_command" : "trace_binding")} `limit` must be between 1 and {MaxLimit}."));
        }
        if (detail is not ("summary" or "locations" or "evidence" or "audit"))
        {
            return Task.FromResult(DiagnosticResult.Error(
                "detail must be one of summary, locations, evidence, or audit."));
        }
        var emitEvidence = includeEvidence ?? detail is "evidence" or "audit";

        var normalizedElement = NormalizeFilter(element);
        var normalizedMember = NormalizeFilter(member);
        var sw = Stopwatch.StartNew();
        return ScopedExecution.RunAsync(
            router,
            scope,
            (host, hostIndex, hostCount) => TraceScopeAsync(
                host,
                normalizedElement,
                normalizedMember,
                SharedRowLimit(limit, hostIndex, hostCount),
                isCommand,
                emitEvidence,
                ct),
            scoped => MergeTraceResults(
                scoped,
                normalizedElement,
                normalizedMember,
                isCommand,
                sw.ElapsedMilliseconds),
            ct,
            maxHosts: MaxTypedScopeFanout);
    }

    private static Task<CallToolResult> CheckResourcesImplAsync(
        ScopeRouter router,
        string? file,
        string? key,
        object? scope,
        int limit,
        CancellationToken ct)
    {
        if (limit is < 1 or > MaxLimit)
        {
            return Task.FromResult(DiagnosticResult.Error(
                $"check_resources `limit` must be between 1 and {MaxLimit}."));
        }

        var normalizedFile = NormalizeFilter(file);
        var normalizedKey = NormalizeFilter(key);
        var sw = Stopwatch.StartNew();
        return ScopedExecution.RunAsync(
            router,
            scope,
            (host, hostIndex, hostCount) => CheckResourcesScopeAsync(
                host,
                normalizedFile,
                normalizedKey,
                SharedRowLimit(limit, hostIndex, hostCount),
                ct),
            scoped => MergeResourceResults(
                scoped,
                normalizedFile,
                normalizedKey,
                sw.ElapsedMilliseconds),
            ct,
            maxHosts: MaxTypedScopeFanout);
    }

    private static async Task<CallToolResult> TraceScopeAsync(
        ScopeHost host,
        string? elementQuery,
        string? memberQuery,
        int limit,
        bool isCommand,
        bool includeEvidence,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var element = await ResolveElementAsync(
            host.Store,
            host.Scope.Id,
            elementQuery,
            ct).ConfigureAwait(false);
        if (element.Status is "ambiguous" or "not-found")
        {
            var candidates = element.Candidates.Take(limit).ToList();
            var candidateOmittedCount = Math.Max(0, element.Candidates.Count - candidates.Count);
            var candidatesTruncated = element.CandidatesTruncated || candidateOmittedCount > 0;
            sw.Stop();
            return BuildTraceResult(
                host.Scope.Id,
                host.Status,
                isCommand,
                elementQuery,
                memberQuery,
                element.Status,
                element.Status,
                candidates,
                Array.Empty<WpfTraceMatch>(),
                candidatesTruncated,
                candidateOmittedCount,
                note: CombineNotes(
                    PartialScopeNote(host),
                    element.Note,
                    candidatesTruncated
                        ? "Candidates were bounded by the shared query limit; narrow the element filter for a complete slice."
                        : null),
                scopes: null,
                elapsedMs: sw.ElapsedMilliseconds);
        }

        var probeLimit = ProbeLimit(limit);
        var pending = new List<PendingTrace>();
        var sourceKey = element.Symbol?.CanonicalKey;
        var edges = await host.Store.FindDataBindingsAsync(
            targetCanonicalKey: null,
            sourceCanonicalKey: sourceKey,
            pathContains: memberQuery,
            modeExact: null,
            converterExact: null,
            limit: probeLimit,
            ct: ct).ConfigureAwait(false);

        foreach (var edge in edges)
        {
            var payload = ParsePayload(edge.PayloadJson);
            if (payload.IsCommand != isCommand) continue;
            var path = payload.Path ?? payload.Command;
            if (string.IsNullOrEmpty(path) || !MemberMatches(path, memberQuery)) continue;
            pending.Add(new PendingTrace(
                edge.Source,
                edge.Target,
                BindsPath,
                path,
                "resolved",
                StableReason(payload.Reason, "resolved", BindsPath),
                payload.Raw,
                Array.Empty<WpfResolutionCandidate>(),
                AnnotationEvidence: null));
        }

        var annotationScan = await LoadTraceAnnotationsAsync(
            host.Store,
            element.Symbol,
            memberQuery,
            isCommand,
            probeLimit,
            ct).ConfigureAwait(false);
        pending.AddRange(annotationScan.Rows);

        // Legacy indexes may carry a direct binds-to relation rather than binds-path. Only pay
        // the global enumeration cost when the primary relation produced no resolved match.
        var hasResolvedPrimary = pending.Any(row =>
            row.Relation == BindsPath && row.Status == "resolved");
        var fallbackTruncated = false;
        if (!hasResolvedPrimary)
        {
            var fallback = await LoadBindsToFallbackAsync(
                host.Store,
                element.Symbol,
                memberQuery,
                isCommand,
                probeLimit,
                ct).ConfigureAwait(false);
            pending.AddRange(fallback.Rows);
            fallbackTruncated = fallback.Truncated;
        }

        var ordered = pending
            .GroupBy(TraceDedupKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(row => row.Source.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Source.StartLine)
            .ThenBy(row => row.Source.StartCol)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .ThenBy(row => row.Status, StringComparer.Ordinal)
            .ToList();

        var queryTruncated = edges.Count >= probeLimit
            || annotationScan.Truncated
            || fallbackTruncated
            || ordered.Count > limit;
        var requested = ordered.Take(limit).ToList();
        var matches = new List<WpfTraceMatch>(requested.Count);
        foreach (var row in requested)
        {
            var materialized = await MaterializeTraceAsync(
                host.Store,
                host.Scope.Id,
                row,
                isCommand,
                includeEvidence,
                ct).ConfigureAwait(false);
            if (materialized is not null) matches.Add(materialized);
        }

        var omittedCount = Math.Max(0, ordered.Count - requested.Count);
        var truncated = queryTruncated;
        var status = AggregateStatus(matches.Select(match => match.Status), element.Status);
        var note = CombineNotes(
            PartialScopeNote(host),
            matches.Count == 0
                ? $"No {(isCommand ? "command" : "binding")} occurrence matched the supplied query."
                : null,
            truncated
                ? "Results were bounded by the query limit, scan cap, or output-size budget; narrow the element/path filter for a complete slice."
                : null);

        sw.Stop();
        return BuildTraceResult(
            host.Scope.Id,
            host.Status,
            isCommand,
            elementQuery,
            memberQuery,
            status,
            element.Status,
            element.Candidates,
            matches,
            truncated,
            omittedCount,
            note,
            scopes: null,
            elapsedMs: sw.ElapsedMilliseconds);
    }

    private static async Task<CallToolResult> CheckResourcesScopeAsync(
        ScopeHost host,
        string? fileQuery,
        string? keyQuery,
        int limit,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probeLimit = ProbeLimit(limit);
        var pending = new List<PendingResource>();

        var sourceScan = await LoadXamlSymbolsAsync(
            host.Store,
            fileQuery,
            IsXamlResourceReferenceSource,
            ct).ConfigureAwait(false);
        var edgeProbeTruncated = false;
        foreach (var source in sourceScan.Symbols)
        {
            if (pending.Count >= MaxProbeRows)
            {
                edgeProbeTruncated = true;
                break;
            }

            foreach (var relation in new[] { UsesResource, AppliesStyle })
            {
                var perRelationLimit = Math.Min(MaxProbeRows, Math.Max(100, probeLimit));
                var outgoing = await host.Store.ListAuditableOutboundEdgesAsync(
                    source.Id,
                    limit: perRelationLimit,
                    edgeKind: relation,
                    ct: ct).ConfigureAwait(false);
                if (outgoing.Count >= perRelationLimit)
                {
                    edgeProbeTruncated = true;
                }

                foreach (var edge in outgoing)
                {
                    var payload = ParsePayload(edge.PayloadJson);
                    if (string.IsNullOrEmpty(payload.Key)
                        || !ExactMatches(payload.Key, keyQuery))
                    {
                        continue;
                    }
                    pending.Add(new PendingResource(
                        source,
                        edge.Symbol,
                        edge.Relation,
                        payload.Key,
                        "resolved",
                        StableReason(payload.Reason, "resolved", relation),
                        payload.ResourceLookup,
                        payload.Raw,
                        Array.Empty<WpfResolutionCandidate>(),
                        AnnotationEvidence: null));
                    if (pending.Count >= MaxProbeRows)
                    {
                        edgeProbeTruncated = true;
                        break;
                    }
                }
                if (pending.Count >= MaxProbeRows) break;
            }
        }

        var annotationScan = await LoadResourceAnnotationsAsync(
            host.Store,
            fileQuery,
            keyQuery,
            sourceScan.Symbols,
            probeLimit,
            ct).ConfigureAwait(false);
        pending.AddRange(annotationScan.Rows);

        var ordered = pending
            .GroupBy(ResourceDedupKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(row => row.Source.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Source.StartLine)
            .ThenBy(row => row.Source.StartCol)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ThenBy(row => row.Status, StringComparer.Ordinal)
            .ToList();

        var queryTruncated = sourceScan.Truncated
            || edgeProbeTruncated
            || annotationScan.Truncated
            || ordered.Count > limit;
        var requested = ordered.Take(limit).ToList();
        var resources = new List<WpfResourceCheck>(requested.Count);
        foreach (var row in requested)
        {
            var materialized = await MaterializeResourceAsync(
                host.Store,
                host.Scope.Id,
                row,
                ct).ConfigureAwait(false);
            if (materialized is not null) resources.Add(materialized);
        }

        var omittedCount = Math.Max(0, ordered.Count - requested.Count);
        var truncated = queryTruncated;
        var status = AggregateStatus(resources.Select(item => item.Status), "unrestricted");
        var note = CombineNotes(
            PartialScopeNote(host),
            resources.Count == 0
                ? "No uses-resource/applies-style edge or XAML resource finding/outcome matched the supplied filters."
                : null,
            truncated
                ? "Results were bounded by the query limit, scan cap, or output-size budget; narrow the file/key filter for a complete slice."
                : null);

        sw.Stop();
        return BuildResourceResult(
            host.Scope.Id,
            host.Status,
            fileQuery,
            keyQuery,
            status,
            resources,
            truncated,
            omittedCount,
            note,
            scopes: null,
            elapsedMs: sw.ElapsedMilliseconds);
    }

    private static async Task<ElementResolution> ResolveElementAsync(
        IGraphStore store,
        string scopeId,
        string? query,
        CancellationToken ct)
    {
        if (query is null)
        {
            return new ElementResolution(
                "unrestricted",
                Symbol: null,
                Array.Empty<WpfSymbolIdentity>(),
                CandidatesTruncated: false,
                Note: null);
        }

        if (CanonicalKeyValidator.IsValid(query))
        {
            var keys = await store.GetAllSymbolKeysAsync(ct).ConfigureAwait(false);
            var exact = keys
                .Where(row => string.Equals(row.CanonicalKey, query, StringComparison.Ordinal))
                .ToList();
            if (exact.Count == 0)
            {
                return new ElementResolution(
                    "not-found",
                    Symbol: null,
                    Array.Empty<WpfSymbolIdentity>(),
                    CandidatesTruncated: false,
                    Note: $"No XAML element has canonical key '{query}'.");
            }

            var symbol = await store.GetSymbolByIdAsync(exact[0].Id, ct).ConfigureAwait(false);
            if (symbol is null || !IsXamlHost(symbol))
            {
                return new ElementResolution(
                    "not-found",
                    Symbol: null,
                    Array.Empty<WpfSymbolIdentity>(),
                    CandidatesTruncated: false,
                    Note: $"Canonical key '{query}' does not identify a XAML element.");
            }
            return new ElementResolution(
                "resolved",
                symbol,
                Array.Empty<WpfSymbolIdentity>(),
                CandidatesTruncated: false,
                Note: null);
        }

        const int candidateLimit = 20;
        const int candidateProbeLimit = candidateLimit + 1;
        var elementHits = await store.SearchSymbolsAsync(
            query,
            kindFilter: "xaml-element",
            limit: candidateProbeLimit,
            ct: ct).ConfigureAwait(false);
        var viewHits = await store.SearchSymbolsAsync(
            query,
            kindFilter: "xaml-view",
            limit: candidateProbeLimit,
            ct: ct).ConfigureAwait(false);
        var candidateScanTruncated = elementHits.Count >= candidateProbeLimit
            || viewHits.Count >= candidateProbeLimit;
        var hits = elementHits
            .Concat(viewHits)
            .Where(IsXamlHost)
            .GroupBy(hit => hit.Id)
            .Select(group => group.First())
            .OrderBy(hit => CandidateRank(hit, query))
            .ThenBy(hit => hit.Fqn.Length)
            .ThenBy(hit => hit.Fqn, StringComparer.Ordinal)
            .ToList();
        var exactHits = hits.Where(hit =>
                string.Equals(hit.Name, query, StringComparison.Ordinal)
                || string.Equals(hit.Fqn, query, StringComparison.Ordinal)
                || string.Equals(hit.CanonicalKey, query, StringComparison.Ordinal))
            .ToList();
        var candidates = exactHits.Count > 0 ? exactHits : hits;
        if (candidates.Count == 0)
        {
            return new ElementResolution(
                "not-found",
                Symbol: null,
                Array.Empty<WpfSymbolIdentity>(),
                CandidatesTruncated: false,
                Note: $"No XAML element matches '{query}'.");
        }
        if (candidates.Count == 1 && !candidateScanTruncated)
        {
            return new ElementResolution(
                "resolved",
                candidates[0],
                Array.Empty<WpfSymbolIdentity>(),
                CandidatesTruncated: false,
                Note: null);
        }

        var mapped = candidates
            .Take(candidateLimit)
            .Select(symbol => MapSymbol(symbol, scopeId))
            .ToList();
        var truncated = candidateScanTruncated || candidates.Count > candidateLimit;
        return new ElementResolution(
            "ambiguous",
            Symbol: null,
            mapped,
            CandidatesTruncated: truncated,
            Note: $"{mapped.Count}{(truncated ? "+" : "")} XAML elements match '{query}'; provide a canonical key or a more specific FQN.");
    }

    private static async Task<TraceScan> LoadTraceAnnotationsAsync(
        IGraphStore store,
        SymbolHit? element,
        string? memberQuery,
        bool isCommand,
        int probeLimit,
        CancellationToken ct)
    {
        var flavors = isCommand
            ? new HashSet<string>(StringComparer.Ordinal)
            {
                "xaml-command-finding",
                "xaml-command-outcome",
            }
            : new HashSet<string>(StringComparer.Ordinal)
            {
                "xaml-binding-finding",
                "xaml-binding-outcome",
            };
        var symbols = new List<SymbolHit>();
        var truncated = false;
        if (element is not null)
        {
            symbols.Add(element);
        }
        else
        {
            var names = isCommand
                ? new[]
                {
                    ("Command成员不存在", "xaml-command-finding"),
                    ("Command解析结果", "xaml-command-outcome"),
                }
                : new[]
                {
                    ("Binding成员不存在", "xaml-binding-finding"),
                    ("Binding解析结果", "xaml-binding-outcome"),
                };
            foreach (var (name, flavor) in names)
            {
                var found = await store.FindByAnnotationAsync(
                    name,
                    flavor,
                    argSubstring: null,
                    kindFilter: null,
                    limit: probeLimit,
                    ct: ct).ConfigureAwait(false);
                symbols.AddRange(found);
                if (found.Count >= probeLimit) truncated = true;
            }
        }

        var rows = new List<PendingTrace>();
        foreach (var source in symbols.GroupBy(symbol => symbol.Id).Select(group => group.First()))
        {
            var annotations = await store.GetAnnotationsForSymbolAsync(source.Id, ct).ConfigureAwait(false);
            foreach (var annotation in annotations)
            {
                if (!flavors.Contains(annotation.Flavor)) continue;
                var parsed = ParseOutcome(annotation.ArgsJson);
                var status = NormalizeStatus(parsed.Status);
                var path = parsed.Path;
                if (path is null)
                {
                    if (memberQuery is not null || status != "unsupported") continue;
                    path = "(unsupported-binding-form)";
                }
                else if (!MemberMatches(path, memberQuery))
                {
                    continue;
                }
                rows.Add(new PendingTrace(
                    source,
                    Target: null,
                    Relation: annotation.Flavor,
                    Path: path,
                    Status: status,
                    Reason: StableReason(parsed.Reason, status, annotation.Flavor),
                    Payload: null,
                    Candidates: parsed.Candidates,
                    AnnotationEvidence: parsed.Evidence));
                if (rows.Count >= MaxProbeRows)
                {
                    return new TraceScan(rows, Truncated: true);
                }
            }
        }
        return new TraceScan(rows, truncated);
    }

    private static async Task<TraceScan> LoadBindsToFallbackAsync(
        IGraphStore store,
        SymbolHit? element,
        string? memberQuery,
        bool isCommand,
        int probeLimit,
        CancellationToken ct)
    {
        IReadOnlyList<SymbolHit> sources;
        var truncated = false;
        if (element is not null)
        {
            sources = new[] { element };
        }
        else
        {
            var scan = await LoadXamlSymbolsAsync(
                store,
                fileQuery: null,
                IsXamlHost,
                ct).ConfigureAwait(false);
            sources = scan.Symbols;
            truncated = scan.Truncated;
        }

        var rows = new List<PendingTrace>();
        foreach (var source in sources)
        {
            var outgoing = await store.ListAuditableOutboundEdgesAsync(
                source.Id,
                limit: probeLimit,
                edgeKind: BindsTo,
                ct: ct).ConfigureAwait(false);
            if (outgoing.Count >= probeLimit) truncated = true;
            foreach (var edge in outgoing)
            {
                var payload = ParsePayload(edge.PayloadJson);
                if (payload.IsCommand != isCommand) continue;
                var path = payload.Path ?? payload.Command;
                if (string.IsNullOrEmpty(path) || !MemberMatches(path, memberQuery)) continue;
                rows.Add(new PendingTrace(
                    source,
                    edge.Symbol,
                    BindsTo,
                    path,
                    "resolved",
                    StableReason(payload.Reason, "resolved", BindsTo),
                    payload.Raw,
                    Array.Empty<WpfResolutionCandidate>(),
                    AnnotationEvidence: null));
                if (rows.Count >= MaxProbeRows)
                {
                    return new TraceScan(rows, Truncated: true);
                }
            }
        }
        return new TraceScan(rows, truncated);
    }

    private static async Task<ResourceScan> LoadResourceAnnotationsAsync(
        IGraphStore store,
        string? fileQuery,
        string? keyQuery,
        IReadOnlyList<SymbolHit> fileSymbols,
        int probeLimit,
        CancellationToken ct)
    {
        var flavors = new HashSet<string>(StringComparer.Ordinal)
        {
            "xaml-resource-finding",
            "xaml-resource-outcome",
        };
        var symbols = new List<SymbolHit>();
        var truncated = false;
        if (fileQuery is not null)
        {
            symbols.AddRange(fileSymbols);
        }
        else
        {
            var specs = new[]
            {
                ("Resource不存在", "xaml-resource-finding"),
                ("Resource解析结果", "xaml-resource-outcome"),
            };
            foreach (var (name, flavor) in specs)
            {
                var found = await store.FindByAnnotationAsync(
                    name,
                    flavor,
                    argSubstring: null,
                    kindFilter: null,
                    limit: probeLimit,
                    ct: ct).ConfigureAwait(false);
                symbols.AddRange(found);
                if (found.Count >= probeLimit) truncated = true;
            }
        }

        var rows = new List<PendingResource>();
        foreach (var source in symbols.GroupBy(symbol => symbol.Id).Select(group => group.First()))
        {
            if (!FileMatches(source.FilePath, fileQuery)) continue;
            var annotations = await store.GetAnnotationsForSymbolAsync(source.Id, ct).ConfigureAwait(false);
            foreach (var annotation in annotations)
            {
                if (!flavors.Contains(annotation.Flavor)) continue;
                var parsed = ParseOutcome(annotation.ArgsJson);
                if (parsed.Key is null || !ExactMatches(parsed.Key, keyQuery)) continue;
                rows.Add(new PendingResource(
                    source,
                    Target: null,
                    Relation: annotation.Flavor,
                    Key: parsed.Key,
                    Status: NormalizeStatus(parsed.Status),
                    Reason: StableReason(
                        parsed.Reason,
                        NormalizeStatus(parsed.Status),
                        annotation.Flavor),
                    ResourceLookup: parsed.ResourceLookup,
                    Payload: null,
                    Candidates: parsed.Candidates,
                    AnnotationEvidence: parsed.Evidence));
                if (rows.Count >= MaxProbeRows)
                {
                    return new ResourceScan(rows, Truncated: true);
                }
            }
        }
        return new ResourceScan(rows, truncated);
    }

    private static async Task<XamlSymbolScan> LoadXamlSymbolsAsync(
        IGraphStore store,
        string? fileQuery,
        Func<SymbolHit, bool> symbolFilter,
        CancellationToken ct)
    {
        var symbols = new List<SymbolHit>();
        if (fileQuery is not null)
        {
            // Stored paths use the host platform's separator, while MCP callers commonly send
            // repository-style forward slashes. Resolve the suffix against the file catalog
            // first, then query with the exact stored path.
            var files = await store.GetAllFilesAsync(ct).ConfigureAwait(false);
            foreach (var file in files.Where(row => FileMatches(row.Path, fileQuery)))
            {
                symbols.AddRange(
                    await store.ListSymbolsInFileAsync(file.Path, ct).ConfigureAwait(false));
            }
        }
        else
        {
            var files = await store.GetAllFilesAsync(ct).ConfigureAwait(false);
            foreach (var file in files
                         .Where(row => row.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase))
            {
                symbols.AddRange(await store.ListSymbolsInFileAsync(file.Path, ct).ConfigureAwait(false));
                if (symbols.Count >= MaxProbeRows)
                {
                    return new XamlSymbolScan(
                        symbols.Where(symbolFilter).Take(MaxProbeRows).ToList(),
                        Truncated: true);
                }
            }
        }

        var hosts = symbols
            .Where(symbolFilter)
            .Where(symbol => FileMatches(symbol.FilePath, fileQuery))
            .GroupBy(symbol => symbol.Id)
            .Select(group => group.First())
            .Take(MaxProbeRows)
            .ToList();
        return new XamlSymbolScan(hosts, symbols.Count > MaxProbeRows);
    }

    private static async Task<WpfTraceMatch?> MaterializeTraceAsync(
        IGraphStore store,
        string scopeId,
        PendingTrace row,
        bool includeCommandExecutions,
        bool includeEvidence,
        CancellationToken ct)
    {
        IReadOnlyList<WpfOccurrenceEvidence> proof;
        var evidenceTruncated = false;
        if (row.AnnotationEvidence is not null)
        {
            proof = new[] { row.AnnotationEvidence };
        }
        else if (row.Target is not null)
        {
            var stored = await store.ListEdgeEvidenceAsync(
                row.Source.Id,
                row.Target.Id,
                row.Relation,
                limit: MaxEvidencePerMatch + 1,
                ct: ct).ConfigureAwait(false);
            if (stored.Count == 0) return null;
            proof = stored.Take(MaxEvidencePerMatch).Select(MapEvidence).ToList();
            evidenceTruncated = stored.Count > MaxEvidencePerMatch;
        }
        else
        {
            return null;
        }

        var commandExecutions = includeCommandExecutions && row.Target is not null
            ? await LoadCommandExecutionsAsync(
                store,
                scopeId,
                row.Target.Id,
                includeEvidence,
                ct).ConfigureAwait(false)
            : Array.Empty<WpfCommandExecution>();
        return new WpfTraceMatch(
            scopeId,
            row.Relation,
            row.Path,
            row.Status,
            row.Reason,
            MapSymbol(row.Source, scopeId),
            row.Target is null ? null : MapSymbol(row.Target, scopeId),
            StrongestConfidence(proof),
            includeEvidence ? proof : Array.Empty<WpfOccurrenceEvidence>(),
            evidenceTruncated,
            row.Candidates)
        {
            CommandExecutions = commandExecutions,
        };
    }

    private static async Task<IReadOnlyList<WpfCommandExecution>> LoadCommandExecutionsAsync(
        IGraphStore store,
        string scopeId,
        long commandPropertyId,
        bool includeEvidence,
        CancellationToken ct)
    {
        var hits = await store.ListAuditableOutboundEdgesByKindsAsync(
            commandPropertyId,
            new[] { CommandExecutes },
            MaxEvidencePerMatch + 1,
            ct).ConfigureAwait(false);
        var executions = new List<WpfCommandExecution>(
            Math.Min(hits.Count, MaxEvidencePerMatch));
        foreach (var hit in hits.Take(MaxEvidencePerMatch))
        {
            var stored = await store.ListEdgeEvidenceAsync(
                commandPropertyId,
                hit.Symbol.Id,
                CommandExecutes,
                MaxEvidencePerMatch + 1,
                ct).ConfigureAwait(false);
            if (stored.Count == 0) continue;
            var evidence = stored.Take(MaxEvidencePerMatch).Select(MapEvidence).ToArray();
            executions.Add(new WpfCommandExecution(
                CommandExecutes,
                MapSymbol(hit.Symbol, scopeId),
                StrongestConfidence(evidence),
                includeEvidence ? evidence : Array.Empty<WpfOccurrenceEvidence>(),
                stored.Count > MaxEvidencePerMatch));
        }
        return executions;
    }

    private static async Task<WpfResourceCheck?> MaterializeResourceAsync(
        IGraphStore store,
        string scopeId,
        PendingResource row,
        CancellationToken ct)
    {
        IReadOnlyList<WpfOccurrenceEvidence> evidence;
        var evidenceTruncated = false;
        if (row.AnnotationEvidence is not null)
        {
            evidence = new[] { row.AnnotationEvidence };
        }
        else if (row.Target is not null)
        {
            var stored = await store.ListEdgeEvidenceAsync(
                row.Source.Id,
                row.Target.Id,
                row.Relation,
                limit: MaxEvidencePerMatch + 1,
                ct: ct).ConfigureAwait(false);
            if (stored.Count == 0) return null;
            evidence = stored.Take(MaxEvidencePerMatch).Select(MapEvidence).ToList();
            evidenceTruncated = stored.Count > MaxEvidencePerMatch;
        }
        else
        {
            return null;
        }

        return new WpfResourceCheck(
            scopeId,
            row.Key,
            row.Relation,
            row.Status,
            row.Reason,
            row.ResourceLookup,
            MapSymbol(row.Source, scopeId),
            row.Target is null ? null : MapSymbol(row.Target, scopeId),
            StrongestConfidence(evidence),
            evidence,
            evidenceTruncated,
            row.Candidates);
    }

    private static CallToolResult BuildTraceResult(
        string scopeId,
        string scopeStatus,
        bool isCommand,
        string? elementQuery,
        string? memberQuery,
        string status,
        string elementStatus,
        IReadOnlyList<WpfSymbolIdentity> candidates,
        IReadOnlyList<WpfTraceMatch> matches,
        bool truncated,
        int omittedCount,
        string? note,
        IReadOnlyList<WpfScopeSummary>? scopes,
        long elapsedMs)
    {
        elementQuery = BoundOutputText(elementQuery);
        memberQuery = BoundOutputText(memberQuery);
        note = BoundOutputText(note);
        var keptCandidates = candidates.ToList();
        var keptMatches = matches.ToList();
        var evidenceTruncated = keptMatches.Any(match => match.EvidenceTruncated);
        truncated |= evidenceTruncated;
        var scopeRows = scopes?.ToList()
            ?? new List<WpfScopeSummary>
            {
                new(
                    scopeId,
                    scopeStatus,
                    status,
                    IsPartial(scopeStatus, truncated),
                    truncated,
                    omittedCount,
                    note),
            };
        var includeScopeProse = true;
        var result = CreateTraceResult(
            scopeId,
            scopeStatus,
            isCommand,
            elementQuery,
            memberQuery,
            status,
            elementStatus,
            keptCandidates,
            keptMatches,
            truncated,
            omittedCount,
            note,
            scopeRows,
            includeScopeProse,
            elapsedMs);

        while (SerializedLength(result) > EffectiveOutputBudget)
        {
            if (includeScopeProse && scopeRows.Count > 1)
            {
                // Structured scope summaries remain authoritative. Avoid duplicating every scope
                // block in prose when that duplication alone would exceed the MCP response cap.
                includeScopeProse = false;
            }
            else if (CompactScopeNotes(scopeRows))
            {
                truncated = true;
                note = BoundOutputText(CombineNotes(
                    note,
                    "Per-scope diagnostic notes were omitted to keep the response within the shared output budget."));
            }
            else
            {
                var evidenceIndex = keptMatches.FindLastIndex(match =>
                    match.Evidence.Any(item => item.Metadata is not null));
                if (evidenceIndex >= 0)
                {
                    var match = keptMatches[evidenceIndex];
                    var evidence = match.Evidence.ToList();
                    var metadataIndex = evidence.FindLastIndex(item => item.Metadata is not null);
                    evidence[metadataIndex] = evidence[metadataIndex] with { Metadata = null };
                    keptMatches[evidenceIndex] = match with
                    {
                        Evidence = evidence,
                        EvidenceTruncated = true,
                    };
                    truncated = true;
                    MarkScopeTruncated(scopeRows, match.ScopeId);
                }
                else
                {
                    evidenceIndex = keptMatches.FindLastIndex(match => match.Evidence.Count > 1);
                    if (evidenceIndex >= 0)
                    {
                        var match = keptMatches[evidenceIndex];
                        keptMatches[evidenceIndex] = match with
                        {
                            Evidence = match.Evidence.Take(match.Evidence.Count - 1).ToList(),
                            EvidenceTruncated = true,
                        };
                        truncated = true;
                        MarkScopeTruncated(scopeRows, match.ScopeId);
                    }
                    else if (keptMatches.Count > 0)
                    {
                        var removed = keptMatches[^1];
                        keptMatches.RemoveAt(keptMatches.Count - 1);
                        omittedCount++;
                        truncated = true;
                        MarkScopeOmitted(scopeRows, removed.ScopeId);
                    }
                    else if (keptCandidates.Count > 0)
                    {
                        var removed = keptCandidates[^1];
                        keptCandidates.RemoveAt(keptCandidates.Count - 1);
                        omittedCount++;
                        truncated = true;
                        MarkScopeOmitted(scopeRows, removed.ScopeId);
                    }
                    else
                    {
                        return OutputBudgetFailure(isCommand ? "trace_command" : "trace_binding");
                    }
                }
            }

            result = CreateTraceResult(
                scopeId,
                scopeStatus,
                isCommand,
                elementQuery,
                memberQuery,
                status,
                elementStatus,
                keptCandidates,
                keptMatches,
                truncated,
                omittedCount,
                note,
                scopeRows,
                includeScopeProse,
                elapsedMs);
        }
        return result;
    }

    private static CallToolResult CreateTraceResult(
        string scopeId,
        string scopeStatus,
        bool isCommand,
        string? elementQuery,
        string? memberQuery,
        string status,
        string elementStatus,
        IReadOnlyList<WpfSymbolIdentity> candidates,
        IReadOnlyList<WpfTraceMatch> matches,
        bool truncated,
        int omittedCount,
        string? note,
        IReadOnlyList<WpfScopeSummary> scopes,
        bool includeScopeProse,
        long elapsedMs)
    {
        var sb = new StringBuilder();
        sb.Append(isCommand ? "trace_command" : "trace_binding")
            .Append(": status=`").Append(status).Append("`, matches=").Append(matches.Count)
            .Append(", scope_status=`").Append(scopeStatus)
            .Append("`, partial=").Append(scopes.Any(row => row.Partial) ? "true" : "false")
            .Append(", truncated=").Append(truncated ? "true" : "false")
            .Append(", omitted=").AppendLine(omittedCount.ToString());
        if (note is not null) sb.Append("note: ").AppendLine(note);
        if (includeScopeProse)
        {
            foreach (var scope in scopes)
            {
                sb.Append("scope `").Append(scope.ScopeId).Append("`: scope_status=`")
                    .Append(scope.ScopeStatus).Append("`, status=`").Append(scope.Status)
                    .Append(", partial=").Append(scope.Partial ? "true" : "false")
                    .Append(", truncated=").Append(scope.Truncated ? "true" : "false")
                    .Append(", omitted=").AppendLine(scope.OmittedCount.ToString());
            }
        }
        foreach (var candidate in candidates)
        {
            sb.Append("- [scope `").Append(candidate.ScopeId).Append("`] candidate: `")
                .Append(candidate.CanonicalKey ?? candidate.Fqn)
                .Append("` — ").Append(candidate.FilePath).Append(':')
                .Append(candidate.Line).Append(':').AppendLine(candidate.Column.ToString());
        }
        foreach (var match in matches)
        {
            sb.Append("- [scope `").Append(match.ScopeId).Append("`] [")
                .Append(match.Status).Append("] `")
                .Append(match.Source.CanonicalKey ?? match.Source.Fqn)
                .Append("` --").Append(match.Relation).Append('(').Append(match.Path).Append(")--> ");
            sb.AppendLine(match.Target is null
                ? "(no canonical target)"
                : $"`{match.Target.CanonicalKey ?? match.Target.Fqn}`");
            sb.Append("  reason: ").AppendLine(match.Reason);
            foreach (var occurrence in match.Evidence)
            {
                sb.Append("  evidence: ").Append(occurrence.FilePath).Append(':')
                    .Append(occurrence.StartLine).Append(':').Append(occurrence.StartColumn)
                    .Append(" [").Append(occurrence.Confidence).Append(", ")
                    .Append(occurrence.Producer).AppendLine("]");
            }
        }

        var content = BuildContent(
            sb.ToString(),
            scopeId,
            elapsedMs,
            matches.SelectMany(match =>
                match.Target is null
                    ? new[] { match.Source }
                    : new[] { match.Source, match.Target }),
            ("status", status),
            ("matches", matches.Count.ToString()),
            ("scope_status", scopeStatus),
            ("omitted_size", omittedCount.ToString()));

        var partial = scopes.Any(row => row.Partial);
        if (isCommand)
        {
            var dto = new TraceCommandResult(
                status,
                scopeId,
                scopeStatus,
                note,
                elementQuery,
                memberQuery,
                elementStatus,
                candidates,
                partial,
                truncated,
                omittedCount,
                matches,
                scopes);
            return new CallToolResult
            {
                Content = content,
                StructuredContent = JsonSerializer.SerializeToElement(
                    dto,
                    ToolOutputJsonContext.Default.TraceCommandResult),
            };
        }
        else
        {
            var dto = new TraceBindingResult(
                status,
                scopeId,
                scopeStatus,
                note,
                elementQuery,
                memberQuery,
                elementStatus,
                candidates,
                partial,
                truncated,
                omittedCount,
                matches,
                scopes);
            return new CallToolResult
            {
                Content = content,
                StructuredContent = JsonSerializer.SerializeToElement(
                    dto,
                    ToolOutputJsonContext.Default.TraceBindingResult),
            };
        }
    }

    private static CallToolResult BuildResourceResult(
        string scopeId,
        string scopeStatus,
        string? fileQuery,
        string? keyQuery,
        string status,
        IReadOnlyList<WpfResourceCheck> resources,
        bool truncated,
        int omittedCount,
        string? note,
        IReadOnlyList<WpfScopeSummary>? scopes,
        long elapsedMs)
    {
        fileQuery = BoundOutputText(fileQuery);
        keyQuery = BoundOutputText(keyQuery);
        note = BoundOutputText(note);
        var keptResources = resources.ToList();
        truncated |= keptResources.Any(resource => resource.EvidenceTruncated);
        var scopeRows = scopes?.ToList()
            ?? new List<WpfScopeSummary>
            {
                new(
                    scopeId,
                    scopeStatus,
                    status,
                    IsPartial(scopeStatus, truncated),
                    truncated,
                    omittedCount,
                    note),
            };
        var includeScopeProse = true;
        var result = CreateResourceResult(
            scopeId,
            scopeStatus,
            fileQuery,
            keyQuery,
            status,
            keptResources,
            truncated,
            omittedCount,
            note,
            scopeRows,
            includeScopeProse,
            elapsedMs);

        while (SerializedLength(result) > EffectiveOutputBudget)
        {
            if (includeScopeProse && scopeRows.Count > 1)
            {
                includeScopeProse = false;
            }
            else if (CompactScopeNotes(scopeRows))
            {
                truncated = true;
                note = BoundOutputText(CombineNotes(
                    note,
                    "Per-scope diagnostic notes were omitted to keep the response within the shared output budget."));
            }
            else
            {
                var evidenceIndex = keptResources.FindLastIndex(resource =>
                    resource.Evidence.Any(item => item.Metadata is not null));
                if (evidenceIndex >= 0)
                {
                    var resource = keptResources[evidenceIndex];
                    var evidence = resource.Evidence.ToList();
                    var metadataIndex = evidence.FindLastIndex(item => item.Metadata is not null);
                    evidence[metadataIndex] = evidence[metadataIndex] with { Metadata = null };
                    keptResources[evidenceIndex] = resource with
                    {
                        Evidence = evidence,
                        EvidenceTruncated = true,
                    };
                    truncated = true;
                    MarkScopeTruncated(scopeRows, resource.ScopeId);
                }
                else
                {
                    evidenceIndex = keptResources.FindLastIndex(resource =>
                        resource.Evidence.Count > 1);
                    if (evidenceIndex >= 0)
                    {
                        var resource = keptResources[evidenceIndex];
                        keptResources[evidenceIndex] = resource with
                        {
                            Evidence = resource.Evidence.Take(resource.Evidence.Count - 1).ToList(),
                            EvidenceTruncated = true,
                        };
                        truncated = true;
                        MarkScopeTruncated(scopeRows, resource.ScopeId);
                    }
                    else if (keptResources.Count > 0)
                    {
                        var removed = keptResources[^1];
                        keptResources.RemoveAt(keptResources.Count - 1);
                        omittedCount++;
                        truncated = true;
                        MarkScopeOmitted(scopeRows, removed.ScopeId);
                    }
                    else
                    {
                        return OutputBudgetFailure("check_resources");
                    }
                }
            }

            result = CreateResourceResult(
                scopeId,
                scopeStatus,
                fileQuery,
                keyQuery,
                status,
                keptResources,
                truncated,
                omittedCount,
                note,
                scopeRows,
                includeScopeProse,
                elapsedMs);
        }
        return result;
    }

    private static CallToolResult CreateResourceResult(
        string scopeId,
        string scopeStatus,
        string? fileQuery,
        string? keyQuery,
        string status,
        IReadOnlyList<WpfResourceCheck> resources,
        bool truncated,
        int omittedCount,
        string? note,
        IReadOnlyList<WpfScopeSummary> scopes,
        bool includeScopeProse,
        long elapsedMs)
    {
        var sb = new StringBuilder();
        sb.Append("check_resources: status=`").Append(status)
            .Append("`, resources=").Append(resources.Count)
            .Append(", scope_status=`").Append(scopeStatus)
            .Append("`, partial=").Append(scopes.Any(row => row.Partial) ? "true" : "false")
            .Append(", truncated=").Append(truncated ? "true" : "false")
            .Append(", omitted=").AppendLine(omittedCount.ToString());
        if (note is not null) sb.Append("note: ").AppendLine(note);
        if (includeScopeProse)
        {
            foreach (var scope in scopes)
            {
                sb.Append("scope `").Append(scope.ScopeId).Append("`: scope_status=`")
                    .Append(scope.ScopeStatus).Append("`, status=`").Append(scope.Status)
                    .Append(", partial=").Append(scope.Partial ? "true" : "false")
                    .Append(", truncated=").Append(scope.Truncated ? "true" : "false")
                    .Append(", omitted=").AppendLine(scope.OmittedCount.ToString());
            }
        }
        foreach (var resource in resources)
        {
            sb.Append("- [scope `").Append(resource.ScopeId).Append("`] [")
                .Append(resource.Status).Append("] relation=`")
                .Append(resource.Relation).Append("`, key=`")
                .Append(resource.Key).Append("` at `")
                .Append(resource.Source.CanonicalKey ?? resource.Source.Fqn).Append("` -> ");
            sb.AppendLine(resource.Target is null
                ? "(no canonical target)"
                : $"`{resource.Target.CanonicalKey ?? resource.Target.Fqn}`");
            sb.Append("  reason: ").AppendLine(resource.Reason);
            foreach (var occurrence in resource.Evidence)
            {
                sb.Append("  evidence: ").Append(occurrence.FilePath).Append(':')
                    .Append(occurrence.StartLine).Append(':').Append(occurrence.StartColumn)
                    .Append(" [").Append(occurrence.Confidence).Append(", ")
                    .Append(occurrence.Producer).AppendLine("]");
            }
        }

        var dto = new CheckResourcesResult(
            status,
            scopeId,
            scopeStatus,
            note,
            fileQuery,
            keyQuery,
            scopes.Any(row => row.Partial),
            truncated,
            omittedCount,
            resources,
            scopes);
        return new CallToolResult
        {
            Content = BuildContent(
                sb.ToString(),
                scopeId,
                elapsedMs,
                resources.SelectMany(resource =>
                    resource.Target is null
                        ? new[] { resource.Source }
                        : new[] { resource.Source, resource.Target }),
                ("status", status),
                ("resources", resources.Count.ToString()),
                ("scope_status", scopeStatus),
                ("omitted_size", omittedCount.ToString())),
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.CheckResourcesResult),
        };
    }

    private static List<ContentBlock> BuildContent(
        string prose,
        string scopeId,
        long elapsedMs,
        IEnumerable<WpfSymbolIdentity> symbols,
        params (string Key, string Value)[] metadata)
    {
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = prose },
        };
        foreach (var symbol in symbols
                     .GroupBy(item => (item.ScopeId, item.SymbolId))
                     .Select(group => group.First()))
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(symbol.SymbolId),
                Name = symbol.Fqn,
                Title = symbol.Fqn,
                Description = $"{symbol.Kind} — {symbol.FilePath}:{symbol.Line}:{symbol.Column}",
                MimeType = "text/markdown",
            });
        }
        content.Add(AudienceMetadata.Build(scopeId, elapsedMs, metadata));
        return content;
    }

    private static CallToolResult MergeTraceResults(
        IReadOnlyList<ScopedCallToolResult> perScope,
        string? elementQuery,
        string? memberQuery,
        bool isCommand,
        long elapsedMs)
    {
        var candidates = new List<WpfSymbolIdentity>();
        var matches = new List<WpfTraceMatch>();
        var scopes = new List<WpfScopeSummary>(perScope.Count);
        var elementStatuses = new List<string>(perScope.Count);
        foreach (var scoped in perScope)
        {
            if (scoped.Result.StructuredContent is not { } structured)
            {
                scopes.Add(new WpfScopeSummary(
                    scoped.ScopeId,
                    scoped.ScopeStatus,
                    "error",
                    Partial: true,
                    Truncated: false,
                    OmittedCount: 0,
                    Note: BoundOutputText(DiagnosticText(scoped.Result))));
                elementStatuses.Add("error");
                continue;
            }

            if (isCommand)
            {
                var dto = structured.Deserialize(
                    ToolOutputJsonContext.Default.TraceCommandResult)
                    ?? throw new InvalidOperationException(
                        $"trace_command scope '{scoped.ScopeId}' returned an empty structured payload.");
                candidates.AddRange(dto.Candidates.Select(candidate =>
                    candidate with { ScopeId = scoped.ScopeId }));
                matches.AddRange(dto.Matches.Select(match =>
                    ScopeTraceMatch(match, scoped.ScopeId)));
                scopes.Add(ScopeSummary(scoped, dto.Status, dto.Partial, dto.Truncated,
                    dto.OmittedCount, dto.Note, dto.Scopes));
                elementStatuses.Add(dto.ElementStatus);
            }
            else
            {
                var dto = structured.Deserialize(
                    ToolOutputJsonContext.Default.TraceBindingResult)
                    ?? throw new InvalidOperationException(
                        $"trace_binding scope '{scoped.ScopeId}' returned an empty structured payload.");
                candidates.AddRange(dto.Candidates.Select(candidate =>
                    candidate with { ScopeId = scoped.ScopeId }));
                matches.AddRange(dto.Matches.Select(match =>
                    ScopeTraceMatch(match, scoped.ScopeId)));
                scopes.Add(ScopeSummary(scoped, dto.Status, dto.Partial, dto.Truncated,
                    dto.OmittedCount, dto.Note, dto.Scopes));
                elementStatuses.Add(dto.ElementStatus);
            }
        }

        var scopeId = perScope.Count == 1 ? perScope[0].ScopeId : "*";
        var status = AggregateMultiScopeStatus(scopes.Select(item => item.Status));
        return BuildTraceResult(
            scopeId,
            AggregateMultiScopeStatus(scopes.Select(item => item.ScopeStatus)),
            isCommand,
            elementQuery,
            memberQuery,
            status,
            AggregateMultiScopeStatus(elementStatuses),
            candidates,
            matches,
            scopes.Any(item => item.Truncated),
            SaturatingSum(scopes.Select(item => item.OmittedCount)),
            "Multi-scope result; inspect `scopes` and each row's `scope_id` for provenance and completeness.",
            scopes,
            elapsedMs);
    }

    private static CallToolResult MergeResourceResults(
        IReadOnlyList<ScopedCallToolResult> perScope,
        string? fileQuery,
        string? keyQuery,
        long elapsedMs)
    {
        var resources = new List<WpfResourceCheck>();
        var scopes = new List<WpfScopeSummary>(perScope.Count);
        foreach (var scoped in perScope)
        {
            if (scoped.Result.StructuredContent is not { } structured)
            {
                scopes.Add(new WpfScopeSummary(
                    scoped.ScopeId,
                    scoped.ScopeStatus,
                    "error",
                    Partial: true,
                    Truncated: false,
                    OmittedCount: 0,
                    Note: BoundOutputText(DiagnosticText(scoped.Result))));
                continue;
            }

            var dto = structured.Deserialize(
                ToolOutputJsonContext.Default.CheckResourcesResult)
                ?? throw new InvalidOperationException(
                    $"check_resources scope '{scoped.ScopeId}' returned an empty structured payload.");
            resources.AddRange(dto.Resources.Select(resource =>
                ScopeResource(resource, scoped.ScopeId)));
            scopes.Add(ScopeSummary(scoped, dto.Status, dto.Partial, dto.Truncated,
                dto.OmittedCount, dto.Note, dto.Scopes));
        }

        var scopeId = perScope.Count == 1 ? perScope[0].ScopeId : "*";
        return BuildResourceResult(
            scopeId,
            AggregateMultiScopeStatus(scopes.Select(item => item.ScopeStatus)),
            fileQuery,
            keyQuery,
            AggregateMultiScopeStatus(scopes.Select(item => item.Status)),
            resources,
            scopes.Any(item => item.Truncated),
            SaturatingSum(scopes.Select(item => item.OmittedCount)),
            "Multi-scope result; inspect `scopes` and each row's `scope_id` for provenance and completeness.",
            scopes,
            elapsedMs);
    }

    private static WpfScopeSummary ScopeSummary(
        ScopedCallToolResult scoped,
        string status,
        bool partial,
        bool truncated,
        int omittedCount,
        string? note,
        IReadOnlyList<WpfScopeSummary> emittedScopes)
    {
        var emitted = emittedScopes.FirstOrDefault();
        return new WpfScopeSummary(
            scoped.ScopeId,
            scoped.ScopeStatus,
            status,
            partial || emitted?.Partial == true,
            truncated || emitted?.Truncated == true,
            Math.Max(omittedCount, emitted?.OmittedCount ?? 0),
            BoundOutputText(CombineNotes(note, emitted?.Note, scoped.ScopeStatusMessage)));
    }

    private static WpfTraceMatch ScopeTraceMatch(WpfTraceMatch match, string scopeId) =>
        match with
        {
            ScopeId = scopeId,
            Source = match.Source with { ScopeId = scopeId },
            Target = match.Target is null ? null : match.Target with { ScopeId = scopeId },
        };

    private static WpfResourceCheck ScopeResource(WpfResourceCheck resource, string scopeId) =>
        resource with
        {
            ScopeId = scopeId,
            Source = resource.Source with { ScopeId = scopeId },
            Target = resource.Target is null ? null : resource.Target with { ScopeId = scopeId },
        };

    private static string DiagnosticText(CallToolResult result) =>
        result.Content?
            .OfType<TextContentBlock>()
            .Select(block => block.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
        ?? "scope query failed without a diagnostic message";

    private static int SerializedLength(CallToolResult result) =>
        JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions).Length;

    private static int EffectiveOutputBudget =>
        OutputBudget.DefaultBudgetChars - OutputBudgetSafetyMargin;

    private static bool CompactScopeNotes(List<WpfScopeSummary> scopes)
    {
        var changed = false;
        for (var index = 0; index < scopes.Count; index++)
        {
            var scope = scopes[index];
            if (scope.Note is null) continue;
            scopes[index] = scope with
            {
                Partial = true,
                Truncated = true,
                Note = null,
            };
            changed = true;
        }
        return changed;
    }

    private static CallToolResult OutputBudgetFailure(string toolName) =>
        DiagnosticResult.Error(
            $"{toolName} could not represent the selected scopes within the shared "
            + $"{OutputBudget.DefaultBudgetChars}-character output budget; "
            + "provide a narrower scope list.");

    private static void MarkScopeTruncated(List<WpfScopeSummary> scopes, string scopeId)
    {
        var index = scopes.FindIndex(scope =>
            string.Equals(scope.ScopeId, scopeId, StringComparison.Ordinal));
        if (index < 0) return;
        var scope = scopes[index];
        scopes[index] = scope with
        {
            Partial = true,
            Truncated = true,
            Note = BoundOutputText(CombineNotes(
                scope.Note,
                "Evidence was reduced to keep the response within the shared output budget.")),
        };
    }

    private static void MarkScopeOmitted(List<WpfScopeSummary> scopes, string scopeId)
    {
        var index = scopes.FindIndex(scope =>
            string.Equals(scope.ScopeId, scopeId, StringComparison.Ordinal));
        if (index < 0) return;
        var scope = scopes[index];
        scopes[index] = scope with
        {
            Partial = true,
            Truncated = true,
            OmittedCount = scope.OmittedCount == int.MaxValue
                ? int.MaxValue
                : scope.OmittedCount + 1,
            Note = BoundOutputText(CombineNotes(
                scope.Note,
                "Rows were omitted to keep the response within the shared output budget.")),
        };
    }

    private static int SaturatingSum(IEnumerable<int> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            total += value;
            if (total >= int.MaxValue) return int.MaxValue;
        }
        return (int)total;
    }

    private static string AggregateMultiScopeStatus(IEnumerable<string> statuses)
    {
        var distinct = statuses
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return distinct.Count switch
        {
            0 => "unknown",
            1 => distinct[0],
            _ => "mixed",
        };
    }

    private static bool IsPartial(string scopeStatus, bool truncated) =>
        truncated || scopeStatus is "partial" or "degraded" or "indexing";

    private static string? BoundOutputText(string? value, int maxLength = 2_048)
    {
        if (value is null || value.Length <= maxLength) return value;
        return value[..(maxLength - 1)] + "…";
    }

    private static ParsedPayload ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ParsedPayload(null, null, null, null, false, json);
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var path = GetString(root, "path");
            var command = GetString(root, "command");
            return new ParsedPayload(
                path,
                command,
                GetString(root, "resolution-reason") ?? GetString(root, "reason"),
                GetString(root, "resource-lookup"),
                !string.IsNullOrEmpty(command),
                json)
            {
                Key = GetString(root, "key"),
            };
        }
        catch (JsonException)
        {
            return new ParsedPayload(null, null, null, null, false, json);
        }
    }

    private static ParsedOutcome ParseOutcome(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ParsedOutcome.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var evidence = ParseAnnotationEvidence(root);
            return new ParsedOutcome(
                GetString(root, "status"),
                GetString(root, "reason"),
                GetString(root, "path"),
                GetString(root, "key"),
                GetString(root, "resourceLookup"),
                ParseCandidates(root),
                evidence);
        }
        catch (JsonException)
        {
            return ParsedOutcome.Empty;
        }
    }

    private static WpfOccurrenceEvidence? ParseAnnotationEvidence(JsonElement root)
    {
        var file = GetString(root, "file");
        var producer = GetString(root, "producer");
        var confidence = GetString(root, "confidence");
        if (file is null
            || producer is null
            || confidence is null
            || !TryGetInt(root, "startLine", out var startLine)
            || !TryGetInt(root, "startColumn", out var startColumn)
            || !TryGetInt(root, "endLine", out var endLine)
            || !TryGetInt(root, "endColumn", out var endColumn))
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        AddMetadata("status");
        AddMetadata("reason");
        AddMetadata("kind");
        AddMetadata("path");
        AddMetadata("key");
        AddMetadata("resourceLookup");
        AddMetadata("code");
        return new WpfOccurrenceEvidence(
            file,
            startLine,
            startColumn,
            endLine,
            endColumn,
            confidence,
            producer,
            metadata.Count == 0 ? null : metadata);

        void AddMetadata(string propertyName)
        {
            var value = GetString(root, propertyName);
            if (value is not null) metadata[propertyName] = value;
        }
    }

    private static IReadOnlyList<WpfResolutionCandidate> ParseCandidates(JsonElement root)
    {
        if (!TryGetProperty(root, "candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<WpfResolutionCandidate>();
        }

        var result = new List<WpfResolutionCandidate>();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.String)
            {
                var display = candidate.GetString();
                result.Add(new WpfResolutionCandidate(
                    display is not null && CanonicalKeyValidator.IsValid(display) ? display : null,
                    display,
                    FilePath: null,
                    Line: null,
                    Column: null));
            }
            else if (candidate.ValueKind == JsonValueKind.Object)
            {
                result.Add(new WpfResolutionCandidate(
                    GetString(candidate, "canonicalKey"),
                    GetString(candidate, "display"),
                    GetString(candidate, "file"),
                    GetNullableInt(candidate, "line"),
                    GetNullableInt(candidate, "column")));
            }
        }
        return result;
    }

    private static WpfOccurrenceEvidence MapEvidence(Evidence evidence) =>
        new(
            evidence.Location.FilePath,
            evidence.Location.StartLine,
            evidence.Location.StartColumn,
            evidence.Location.EndLine,
            evidence.Location.EndColumn,
            TraceCallPathTools.ConfidenceName(evidence.Confidence),
            evidence.Producer,
            evidence.Metadata);

    private static WpfSymbolIdentity MapSymbol(SymbolHit symbol, string scopeId) =>
        new(
            symbol.Id,
            symbol.CanonicalKey,
            symbol.Name,
            symbol.Fqn,
            symbol.Kind,
            symbol.FilePath,
            symbol.StartLine,
            symbol.StartCol,
            scopeId);

    private static string StrongestConfidence(IReadOnlyList<WpfOccurrenceEvidence> evidence)
    {
        if (evidence.Any(item => item.Confidence == "exact")) return "exact";
        if (evidence.Any(item => item.Confidence == "semantic")) return "semantic";
        if (evidence.Any(item => item.Confidence == "inferred")) return "inferred";
        return "unknown";
    }

    private static string AggregateStatus(IEnumerable<string> statuses, string elementStatus)
    {
        if (elementStatus is "ambiguous" or "not-found") return elementStatus;
        var distinct = statuses.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return "not-found";
        if (distinct.Count == 1) return distinct[0];
        return "matched";
    }

    private static string NormalizeStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "resolved" => "resolved",
            "missing" => "missing",
            "ambiguous" => "ambiguous",
            "incomplete" => "incomplete",
            "unsupported" => "unsupported",
            _ => "unknown",
        };

    private static string? PartialScopeNote(ScopeHost host) =>
        host.Status == "partial"
            ? $"Scope is partial; results may be incomplete. {host.StatusMessage ?? "(no failure detail)"}"
            : null;

    private static string? CombineNotes(params string?[] notes)
    {
        var present = notes.Where(note => !string.IsNullOrWhiteSpace(note)).ToList();
        return present.Count == 0 ? null : string.Join(" ", present);
    }

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int SharedRowLimit(int limit, int hostIndex, int hostCount)
    {
        var quotient = limit / hostCount;
        return quotient + (hostIndex < limit % hostCount ? 1 : 0);
    }

    private static int ProbeLimit(int limit) =>
        Math.Min(MaxProbeRows, Math.Max(100, Math.Max(limit + 1, limit * 4)));

    private static int CandidateRank(SymbolHit candidate, string query)
    {
        if (string.Equals(candidate.Name, query, StringComparison.Ordinal)) return 1;
        if (string.Equals(candidate.Fqn, query, StringComparison.Ordinal)) return 2;
        if (candidate.Fqn.EndsWith(query, StringComparison.Ordinal)) return 3;
        if (candidate.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static string StableReason(string? reason, string status, string relation)
    {
        if (!string.IsNullOrWhiteSpace(reason)) return reason.Trim();
        if (status == "resolved")
        {
            return relation switch
            {
                BindsPath => ResolvedBindingEdgeReason,
                BindsTo => ResolvedLegacyBindingEdgeReason,
                UsesResource or AppliesStyle => ResolvedResourceEdgeReason,
                _ => $"resolved-by-indexed-{relation}",
            };
        }
        return $"{relation}-{status}";
    }

    private static bool IsXamlHost(SymbolHit symbol) =>
        symbol.Kind is "xaml-element" or "xaml-view";

    private static bool IsXamlResourceReferenceSource(SymbolHit symbol) =>
        symbol.Kind is "xaml-element"
            or "xaml-view"
            or "xaml-resource"
            or "xaml-style"
            or "xaml-template";

    private static bool ExactMatches(string value, string? query) =>
        query is null || string.Equals(value, query, StringComparison.Ordinal);

    private static bool MemberMatches(string value, string? query)
    {
        if (query is null) return true;
        if (string.Equals(value, query, StringComparison.Ordinal)) return true;
        var lastDot = value.LastIndexOf('.');
        return lastDot >= 0
            && string.Equals(value[(lastDot + 1)..], query, StringComparison.Ordinal);
    }

    private static bool FileMatches(string path, string? query)
    {
        if (query is null) return true;
        var normalizedPath = NormalizePathForComparison(path).TrimEnd('/');
        var normalizedQuery = NormalizePathForComparison(query).Trim('/');
        if (normalizedQuery.Length == 0) return false;
        if (string.Equals(
                normalizedPath.TrimStart('/'),
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return normalizedPath.EndsWith(
            "/" + normalizedQuery,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathForComparison(string path) =>
        path.Replace('\\', '/');

    private static string TraceDedupKey(PendingTrace row) =>
        $"{row.Source.Id}|{row.Target?.Id ?? 0}|{row.Relation}|{row.Path}|{row.Status}|{row.AnnotationEvidence?.FilePath}|{row.AnnotationEvidence?.StartLine}|{row.AnnotationEvidence?.StartColumn}";

    private static string ResourceDedupKey(PendingResource row) =>
        $"{row.Source.Id}|{row.Target?.Id ?? 0}|{row.Relation}|{row.Key}|{row.Status}|{row.AnnotationEvidence?.FilePath}|{row.AnnotationEvidence?.StartLine}|{row.AnnotationEvidence?.StartColumn}";

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetNullableInt(JsonElement element, string propertyName) =>
        TryGetInt(element, propertyName, out var value) ? value : null;

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return TryGetProperty(element, propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value))
        {
            return true;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private sealed record ElementResolution(
        string Status,
        SymbolHit? Symbol,
        IReadOnlyList<WpfSymbolIdentity> Candidates,
        bool CandidatesTruncated,
        string? Note);

    private sealed record PendingTrace(
        SymbolHit Source,
        SymbolHit? Target,
        string Relation,
        string Path,
        string Status,
        string Reason,
        string? Payload,
        IReadOnlyList<WpfResolutionCandidate> Candidates,
        WpfOccurrenceEvidence? AnnotationEvidence);

    private sealed record PendingResource(
        SymbolHit Source,
        SymbolHit? Target,
        string Relation,
        string Key,
        string Status,
        string Reason,
        string? ResourceLookup,
        string? Payload,
        IReadOnlyList<WpfResolutionCandidate> Candidates,
        WpfOccurrenceEvidence? AnnotationEvidence);

    private sealed record TraceScan(IReadOnlyList<PendingTrace> Rows, bool Truncated);
    private sealed record ResourceScan(IReadOnlyList<PendingResource> Rows, bool Truncated);
    private sealed record XamlSymbolScan(IReadOnlyList<SymbolHit> Symbols, bool Truncated);

    private sealed record ParsedPayload(
        string? Path,
        string? Command,
        string? Reason,
        string? ResourceLookup,
        bool IsCommand,
        string? Raw)
    {
        public string? Key { get; init; }
    }

    private sealed record ParsedOutcome(
        string? Status,
        string? Reason,
        string? Path,
        string? Key,
        string? ResourceLookup,
        IReadOnlyList<WpfResolutionCandidate> Candidates,
        WpfOccurrenceEvidence? Evidence)
    {
        public static ParsedOutcome Empty { get; } = new(
            Status: null,
            Reason: null,
            Path: null,
            Key: null,
            ResourceLookup: null,
            Array.Empty<WpfResolutionCandidate>(),
            Evidence: null);
    }
}
