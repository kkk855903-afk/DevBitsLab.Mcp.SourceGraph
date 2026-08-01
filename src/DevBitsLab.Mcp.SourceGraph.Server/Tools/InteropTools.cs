using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>Read-only, persisted managed/native boundary queries.</summary>
[McpServerToolType]
public static class InteropTools
{
    private const int MaximumSymbolCharacters = 4096;
    private const int MaximumScopeFanout = 16;
    private const int OutputBudgetSafetyMargin = 512;
    private const int AggregateReserveCharacters = 8_000;
    private const int MinimumScopeBudgetCharacters = 1_800;

    [McpServerTool(
        Name = "match_pinvoke",
        UseStructuredContent = true,
        OutputSchemaType = typeof(MatchPInvokeResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger(
        "\"match this P/Invoke\", \"which native export does this DllImport call?\", "
        + "\"resolve this LibraryImport boundary\"")]
    [Description(
        "Match one uniquely selected managed DllImport, LibraryImport, or native export to "
        + "persisted interop boundaries. Exact canonical keys take precedence; zero or multiple declarations "
        + "are reported without guessing. Returns explicit target, status, candidate count, "
        + "reasons, and stored evidence for every selected scope.")]
    public static Task<CallToolResult> MatchPInvokeAsync(
        ScopeRouter router,
        [Description("Managed import or native export name, FQN, or exact canonical key.")] string symbol,
        [Description("Optional scope id, '*', or comma-separated scope ids.")] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "match_pinvoke",
            new { symbol, scope },
            () => RunAsync(
                router,
                symbol,
                scope,
                InteropQuerySelectionMode.ManagedOrNativeBoundary,
                includeFindings: false,
                "match_pinvoke",
                ct));

    [McpServerTool(
        Name = "analyze_native_boundary",
        UseStructuredContent = true,
        OutputSchemaType = typeof(AnalyzeNativeBoundaryResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger(
        "\"analyze this native boundary\", \"check this P/Invoke ABI\", "
        + "\"show interop risks and evidence\"")]
    [Description(
        "Analyze one uniquely selected managed import or native export using persisted Phase 2 "
        + "boundary facts. A unique native export returns every related managed boundary in "
        + "stable order. Findings are limited to Interop001 and Interop003-Interop006; "
        + "Interop002 struct compatibility is not returned.")]
    public static Task<CallToolResult> AnalyzeNativeBoundaryAsync(
        ScopeRouter router,
        [Description("Managed import or native export name, FQN, or exact canonical key.")] string symbol,
        [Description("Optional scope id, '*', or comma-separated scope ids.")] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "analyze_native_boundary",
            new { symbol, scope },
            () => RunAsync(
                router,
                symbol,
                scope,
                InteropQuerySelectionMode.ManagedOrNativeBoundary,
                includeFindings: true,
                "analyze_native_boundary",
                ct));

    private static async Task<CallToolResult> RunAsync(
        ScopeRouter router,
        string symbol,
        object? scope,
        InteropQuerySelectionMode selectionMode,
        bool includeFindings,
        string toolName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return DiagnosticResult.Error($"{toolName} requires a non-empty `symbol`.");
        }

        var query = symbol.Trim();
        if (query.Length > MaximumSymbolCharacters)
        {
            return DiagnosticResult.Error(
                $"{toolName} `symbol` must not exceed "
                + $"{MaximumSymbolCharacters} characters.");
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

        var stopwatch = Stopwatch.StartNew();
        var routed = await ScopedExecution.RunAsync(
                router,
                scope,
                (host, _, hostCount) => QueryScopeAsync(
                    host,
                    query,
                    selectionMode,
                    includeFindings,
                    SharedScopeBudget(hostCount),
                    cancellationToken),
                scoped => BuildAggregateResult(
                    scoped,
                    query,
                    includeFindings,
                    toolName,
                    stopwatch.ElapsedMilliseconds),
                cancellationToken,
                maxHosts: MaximumScopeFanout)
            .ConfigureAwait(false);

        // Typed ScopedExecution invokes the merge callback only for multi-host calls. Normalize
        // its single-host pass-through (including a degraded diagnostic) into the same declared
        // object-root output shape.
        if (!resolution.IsError
            && resolution.Hosts.Count == 1)
        {
            var host = resolution.Hosts[0];
            return BuildAggregateResult(
                [
                    new ScopedCallToolResult(
                        host.Scope.Id,
                        host.Status,
                        host.StatusMessage,
                        routed),
                ],
                query,
                includeFindings,
                toolName,
                stopwatch.ElapsedMilliseconds);
        }

        return routed;
    }

    private static async Task<CallToolResult> QueryScopeAsync(
        ScopeHost host,
        string query,
        InteropQuerySelectionMode selectionMode,
        bool includeFindings,
        int scopeBudget,
        CancellationToken cancellationToken)
    {
        if (host.NativeInteropState is null)
        {
            var discovery = NativeInteropDiscovery.Discover(host.Scope.Root);
            var notConfigured = ScopeFailure(
                host.Scope.Id,
                query,
                host.Status,
                status: "not_configured",
                code: "interop-not-configured",
                "This scope has no native interop configuration."
                + discovery.ToDiagnostic());
            return ScopeCall(
                InteropQueryBudget.Apply(notConfigured, scopeBudget).Result,
                isError: false);
        }

        var state = CurrentStateForQuery(host);
        try
        {
            var bounded = await new InteropQueryService().QueryAsync(
                    host.Scope.Id,
                    host.Store,
                    state,
                    query,
                    selectionMode,
                    includeFindings,
                    cancellationToken)
                .ConfigureAwait(false);
            bounded = InteropQueryBudget.Apply(bounded.Result, scopeBudget);
            return ScopeCall(bounded.Result, isError: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = ScopeFailure(
                host.Scope.Id,
                query,
                host.Status,
                status: "error",
                code: "interop-query-failed",
                $"The persisted interop query failed ({ex.GetType().Name}).");
            return ScopeCall(
                InteropQueryBudget.Apply(failure, scopeBudget).Result,
                isError: true);
        }
    }

    private static NativeInteropRuntimeState CurrentStateForQuery(ScopeHost host)
    {
        var state = host.NativeInteropState
            ?? throw new InvalidOperationException(
                "An interop-configured scope has no runtime state.");
        if (host.Status == "ok" && host.ManagedInteropInputComplete)
        {
            return state;
        }

        var failure = new NativeInteropRuntimeFailure(
            "scope",
            host.ManagedInteropInputComplete
                ? "scope-not-complete"
                : "managed-import-universe-incomplete",
            host.ManagedInteropInputComplete
                ? $"Scope status is {host.Status}; current interop conclusions are unavailable."
                : "The managed import universe is incomplete; current interop conclusions "
                  + "are unavailable.");
        return state with
        {
            Status = NativeInteropRuntimeStatus.Partial,
            RetainedLastGood =
                state.RetainedLastGood || state.LastSuccessfulAt is not null,
            IsExportUniverseComplete = false,
            Failures = state.Failures
                .Append(failure)
                .OrderBy(item => item.Stage, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Message, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static CallToolResult ScopeCall(
        InteropScopeQueryResult scope,
        bool isError) =>
        new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"Scope `{Markdown(scope.ScopeId)}`: {scope.Status}.",
                },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(
                scope,
                InteropQueryJsonContext.Default.InteropScopeQueryResult),
            IsError = isError ? true : null,
        };

    internal static CallToolResult BuildAggregateResult(
        IReadOnlyList<ScopedCallToolResult> perScope,
        string query,
        bool includeFindings,
        string toolName,
        long elapsedMilliseconds)
    {
        var scopes = perScope
            .Select(scoped => ReadScope(scoped, query))
            .OrderBy(scope => scope.ScopeId, StringComparer.Ordinal)
            .ToArray();
        var anyError = perScope.Any(scoped => scoped.Result.IsError == true);
        return BuildBoundedAggregate(
            scopes,
            query,
            includeFindings,
            toolName,
            elapsedMilliseconds,
            anyError);
    }

    private static InteropScopeQueryResult ReadScope(
        ScopedCallToolResult scoped,
        string query)
    {
        if (scoped.Result.StructuredContent is { } structured)
        {
            try
            {
                var parsed = structured.Deserialize(
                    InteropQueryJsonContext.Default.InteropScopeQueryResult);
                if (parsed is not null)
                {
                    return parsed with
                    {
                        ScopeId = scoped.ScopeId,
                        ScopeStatus = scoped.ScopeStatus,
                    };
                }
            }
            catch (JsonException)
            {
            }
        }

        return ScopeFailure(
            scoped.ScopeId,
            query,
            scoped.ScopeStatus,
            status: "error",
            code: "scope-unavailable",
            Bound(
                DiagnosticText(scoped.Result),
                512));
    }

    internal static CallToolResult BuildBoundedAggregate(
        IReadOnlyList<InteropScopeQueryResult> inputScopes,
        string query,
        bool includeFindings,
        string toolName,
        long elapsedMilliseconds,
        bool isError)
    {
        var scopes = inputScopes.ToArray();
        var compactProse = false;
        var proseOmittedCount = 0;

        CallToolResult Build() => CreateAggregate(
            scopes,
            query,
            includeFindings,
            toolName,
            elapsedMilliseconds,
            isError,
            compactProse,
            proseOmittedCount);

        var result = Build();
        if (SerializedLength(result) <= EffectiveOutputBudget)
        {
            return result;
        }

        foreach (var perScopeBudget in new[] { 4_000, 3_000, 2_400, 1_800, 1_500 })
        {
            var reduced = new InteropScopeQueryResult[scopes.Length];
            var allScopesFit = true;
            for (var index = 0; index < scopes.Length; index++)
            {
                try
                {
                    reduced[index] = InteropQueryBudget.Apply(
                        scopes[index],
                        perScopeBudget).Result;
                }
                catch (InvalidOperationException)
                {
                    // A scope's required status/partial/failure core may itself exceed this
                    // intermediate share. Keep the prior representation and continue to the
                    // aggregate core compaction below rather than losing the scope block.
                    allScopesFit = false;
                    break;
                }
            }
            if (!allScopesFit)
            {
                break;
            }

            scopes = reduced;
            result = Build();
            if (SerializedLength(result) <= EffectiveOutputBudget)
            {
                return result;
            }
        }

        compactProse = true;
        proseOmittedCount = scopes.Length;
        result = Build();
        if (SerializedLength(result) <= EffectiveOutputBudget)
        {
            return result;
        }

        // Last-resort aggregate reduction keeps every selected scope's status, partial marker,
        // totals, and first failure. It never changes an ambiguous selection into a match.
        scopes = scopes.Select(CompactScopeCore).ToArray();
        result = Build();
        if (SerializedLength(result) <= EffectiveOutputBudget)
        {
            return result;
        }

        throw new InvalidOperationException(
            $"{toolName} could not preserve {scopes.Length} scope summaries within "
            + $"the {OutputBudget.DefaultBudgetChars}-character output budget.");
    }

    private static CallToolResult CreateAggregate(
        IReadOnlyList<InteropScopeQueryResult> scopes,
        string query,
        bool includeFindings,
        string toolName,
        long elapsedMilliseconds,
        bool isError,
        bool compactProse,
        int proseOmittedCount)
    {
        var status = AggregateStatus(scopes.Select(scope => scope.Status));
        var totalSelectionCandidates = SaturatingSum(
            scopes.Select(scope => scope.TotalSelectionCandidateCount));
        var totalMatches = SaturatingSum(
            scopes.Select(scope => scope.TotalMatchCount));
        var totalFindings = SaturatingSum(
            scopes.Select(scope => scope.TotalFindingCount));
        var totalFailures = SaturatingSum(
            scopes.Select(scope => scope.TotalFailureCount));
        var omitted = SaturatingAdd(
            SaturatingSum(scopes.Select(scope => scope.OmittedCount)),
            proseOmittedCount);
        var omittedEvidence = SaturatingSum(
            scopes.Select(scope => scope.OmittedEvidenceCount));
        var truncated =
            proseOmittedCount > 0 || scopes.Any(scope => scope.Truncated);

        JsonElement structured;
        if (includeFindings)
        {
            var dto = new AnalyzeNativeBoundaryResult(
                query,
                status,
                scopes,
                totalSelectionCandidates,
                totalMatches,
                totalFindings,
                totalFailures,
                truncated,
                omitted,
                omittedEvidence,
                proseOmittedCount);
            structured = JsonSerializer.SerializeToElement(
                dto,
                InteropToolJsonContext.Default.AnalyzeNativeBoundaryResult);
        }
        else
        {
            var dto = new MatchPInvokeResult(
                query,
                status,
                scopes,
                totalSelectionCandidates,
                totalMatches,
                totalFailures,
                truncated,
                omitted,
                omittedEvidence,
                proseOmittedCount);
            structured = JsonSerializer.SerializeToElement(
                dto,
                InteropToolJsonContext.Default.MatchPInvokeResult);
        }

        var prose = RenderMarkdown(
            toolName,
            query,
            scopes,
            includeFindings,
            compactProse,
            omitted);
        var scopeLabel = scopes.Count == 1 ? scopes[0].ScopeId : "*";
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = prose },
                AudienceMetadata.Build(
                    scopeLabel,
                    elapsedMilliseconds,
                    ("scopes", scopes.Count.ToString()),
                    ("matches", totalMatches.ToString()),
                    ("findings", totalFindings.ToString()),
                    ("truncated", truncated ? "true" : "false"),
                    ("omitted", omitted.ToString())),
            ],
            StructuredContent = structured,
            IsError = isError ? true : null,
        };
    }

    private static string RenderMarkdown(
        string toolName,
        string query,
        IReadOnlyList<InteropScopeQueryResult> scopes,
        bool includeFindings,
        bool compact,
        int omittedCount)
    {
        var builder = new StringBuilder();
        builder.Append(toolName == "match_pinvoke"
                ? "## P/Invoke match"
                : "## Native boundary analysis")
            .AppendLine()
            .AppendLine()
            .Append("Query: `")
            .Append(Markdown(Bound(query, 256)))
            .AppendLine("`");

        foreach (var scope in scopes)
        {
            builder.AppendLine()
                .Append("### Scope `")
                .Append(Markdown(scope.ScopeId))
                .AppendLine("`")
                .Append("- Status: **")
                .Append(scope.Status)
                .Append("**; runtime=")
                .Append(scope.ScopeStatus)
                .Append("; partial=")
                .Append(scope.Partial ? "true" : "false")
                .Append("; matches=")
                .Append(scope.TotalMatchCount);
            if (includeFindings)
            {
                builder.Append("; findings=").Append(scope.TotalFindingCount);
            }
            builder.AppendLine();

            if (compact)
            {
                if (scope.Failures.Count > 0)
                {
                    builder.Append("- Failure: ")
                        .Append(scope.Failures[0].Code)
                        .AppendLine();
                }
                continue;
            }

            var match = scope.Matches.FirstOrDefault();
            if (match is not null)
            {
                builder.Append("- Boundary: `")
                    .Append(Markdown(Bound(match.ManagedSymbol, 256)))
                    .Append("` → ")
                    .Append(match.NativeSymbol is null
                        ? "_no current native symbol_"
                        : $"`{Markdown(Bound(match.NativeSymbol, 256))}`")
                    .Append("; relation=")
                    .Append(match.Relation)
                    .Append("; status=")
                    .Append(match.Status)
                    .Append("; candidates=")
                    .Append(match.CandidateCount)
                    .AppendLine();
                var evidence = match.Evidence.FirstOrDefault();
                if (evidence is not null)
                {
                    builder.Append("- Evidence: `")
                        .Append(Markdown(Bound(evidence.FilePath, 256)))
                        .Append(':')
                        .Append(evidence.StartLine)
                        .Append(':')
                        .Append(evidence.StartColumn)
                        .Append("` (")
                        .Append(evidence.Confidence)
                        .Append(", ")
                        .Append(Markdown(Bound(evidence.Producer, 96)))
                        .AppendLine(")");
                }
                else if (match.Reasons.Count > 0)
                {
                    builder.Append("- Reason: ")
                        .AppendLine(Markdown(Bound(match.Reasons[0], 256)));
                }
            }
            else if (scope.SelectionCandidates.Count > 0)
            {
                builder.Append("- Selection: ")
                    .Append(scope.SelectionStatus)
                    .Append(" — ")
                    .AppendJoin(
                        ", ",
                        scope.SelectionCandidates
                            .Take(2)
                            .Select(candidate =>
                                $"`{Markdown(Bound(candidate.CanonicalKey, 160))}`"))
                    .AppendLine();
            }

            if (includeFindings && scope.Findings.Count > 0)
            {
                builder.Append("- Findings: ")
                    .AppendJoin(
                        ", ",
                        scope.Findings
                            .Take(4)
                            .Select(finding =>
                                $"{finding.RuleId} ({finding.Severity}, {finding.Relation})"))
                    .AppendLine();
            }
            if (scope.Failures.Count > 0)
            {
                builder.Append("- Failure: ")
                    .Append(scope.Failures[0].Code)
                    .Append(" — ")
                    .AppendLine(Markdown(Bound(
                        scope.Failures[0].Message,
                        256)));
            }
            if (scope.Truncated)
            {
                builder.Append("- Truncated: omitted ")
                    .Append(scope.OmittedCount)
                    .Append(" item(s), including ")
                    .Append(scope.OmittedEvidenceCount)
                    .AppendLine(" evidence row(s).");
            }
        }

        if (omittedCount > 0)
        {
            builder.AppendLine()
                .Append("_Response truncated deterministically; omitted_count=")
                .Append(omittedCount)
                .AppendLine("._");
        }
        return builder.ToString();
    }

    private static InteropScopeQueryResult ScopeFailure(
        string scopeId,
        string query,
        string scopeStatus,
        string status,
        string code,
        string message) =>
        new(
            scopeId,
            query,
            scopeStatus,
            status,
            Partial: true,
            RetainedLastGood: false,
            SelectionStatus: "unknown",
            SelectionCandidates: [],
            TotalSelectionCandidateCount: 0,
            Matches: [],
            TotalMatchCount: 0,
            Findings: [],
            TotalFindingCount: 0,
            Failures:
            [
                new InteropQueryFailureRow(
                    "scope",
                    code,
                    message),
            ],
            TotalFailureCount: 1,
            Truncated: false,
            OmittedCount: 0,
            OmittedEvidenceCount: 0,
            OmittedReasonCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);

    private static InteropScopeQueryResult CompactScopeCore(
        InteropScopeQueryResult scope)
    {
        var additionalEvidence = scope.Matches.Sum(match =>
            match.Evidence.Count + match.EvidenceOmittedCount);
        additionalEvidence = SaturatingAdd(
            additionalEvidence,
            scope.Findings.Sum(finding =>
                finding.Evidence.Count + finding.EvidenceOmittedCount));
        var additionalReasons = scope.Matches.Sum(match =>
            match.Reasons.Count + match.ReasonOmittedCount);
        var additionalMetadata = scope.Matches
            .SelectMany(match => match.Evidence)
            .Sum(evidence =>
                (evidence.Metadata?.Count ?? 0)
                + evidence.MetadataOmittedCount);
        additionalMetadata = SaturatingAdd(
            additionalMetadata,
            scope.Findings
                .SelectMany(finding => finding.Evidence)
                .Sum(evidence =>
                    (evidence.Metadata?.Count ?? 0)
                    + evidence.MetadataOmittedCount));
        var additionalRows = scope.SelectionCandidates.Count
            + scope.Matches.Count
            + scope.Findings.Count;
        var additionalOmitted = SaturatingAdd(additionalRows, additionalEvidence);
        additionalOmitted = SaturatingAdd(additionalOmitted, additionalReasons);
        additionalOmitted = SaturatingAdd(additionalOmitted, additionalMetadata);
        return scope with
        {
            SelectionCandidates = [],
            Matches = [],
            Findings = [],
            Failures = scope.Failures.Take(1).ToArray(),
            Truncated = true,
            OmittedCount = SaturatingAdd(
                scope.OmittedCount,
                additionalOmitted),
            OmittedEvidenceCount = SaturatingAdd(
                scope.OmittedEvidenceCount,
                additionalEvidence),
            OmittedReasonCount = SaturatingAdd(
                scope.OmittedReasonCount,
                additionalReasons),
            OmittedMetadataCount = SaturatingAdd(
                scope.OmittedMetadataCount,
                additionalMetadata),
        };
    }

    private static int SharedScopeBudget(int hostCount)
    {
        var available =
            OutputBudget.DefaultBudgetChars
            - OutputBudgetSafetyMargin
            - AggregateReserveCharacters;
        return Math.Max(
            MinimumScopeBudgetCharacters,
            available / Math.Max(1, hostCount));
    }

    private static int SerializedLength(CallToolResult result) =>
        JsonSerializer.Serialize(
            result,
            McpJsonUtilities.DefaultOptions).Length;

    private static int EffectiveOutputBudget =>
        OutputBudget.DefaultBudgetChars - OutputBudgetSafetyMargin;

    private static string DiagnosticText(CallToolResult result) =>
        result.Content?
            .OfType<TextContentBlock>()
            .Select(block => block.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
        ?? "Scope query failed without a diagnostic message.";

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        var distinct = statuses
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(status => status, StringComparer.Ordinal)
            .ToArray();
        return distinct.Length switch
        {
            0 => "unknown",
            1 => distinct[0],
            _ => "mixed",
        };
    }

    private static int SaturatingSum(IEnumerable<int> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total = SaturatingAdd(total, value);
        }
        return total;
    }

    private static int SaturatingAdd(int left, int right)
    {
        var sum = (long)left + right;
        return sum >= int.MaxValue ? int.MaxValue : (int)sum;
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string Markdown(string value) =>
        value
            .Replace('`', '\'')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
