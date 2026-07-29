using System.ComponentModel;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Grpc;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>Read-only, exact-selection tools for persisted protobuf/gRPC relationships.</summary>
[McpServerToolType]
public static class GrpcTools
{
    private const int MaximumScopeFanout = 16;
    private const int MaximumIncompleteRpcPageSize = 100;
    private const int OutputBudgetSafetyMargin = 256;
    private static readonly GrpcContractQueryService QueryService = new();

    [McpServerTool(
        UseStructuredContent = true,
        OutputSchemaType = typeof(TraceRpcResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger(
        "\"trace this RPC\", \"find gRPC callers and implementation\", "
        + "\"trace_rpc\"")]
    [Description(
        "Trace one exact protobuf RPC canonical key (`proto:R:...`) or one exact managed "
        + "canonical key (`csharp:...`). Returns only persisted evidence-backed `grpc-calls` "
        + "clients and `implements-rpc` servers. Both relations are stored managed→proto; the "
        + "tool explicitly performs reverse/inbound traversal from the proto RPC. No name-only "
        + "matching is performed.")]
    public static Task<CallToolResult> TraceRpcAsync(
        ScopeRouter router,
        [Description(
            "Exact `proto:R:` RPC canonical key or exact `csharp:` managed canonical key.")]
        string rpc,
        [Description(
            "Optional scope id, '*', or comma-separated scope ids (maximum 16 scopes).")]
        string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "trace_rpc",
            new { rpc, scope },
            () => TraceRpcImplAsync(router, rpc, scope, ct));

    [McpServerTool(
        UseStructuredContent = true,
        OutputSchemaType = typeof(CheckProtoContractResult))]
    [ToolAnnotation(ReadOnlyHint = true, IdempotentHint = true)]
    [ToolTrigger(
        "\"check protobuf compatibility\", \"check gRPC contract\", "
        + "\"check_proto_contract\"")]
    [Description(
        "Check current complete protobuf contracts for RPCs without an evidence-backed server "
        + "implementation, uniquely proven generated client/server signature mismatches, field "
        + "number changes, and streaming changes. Change rules compare against the persisted "
        + "first complete successful observation for the same exact canonical key; the first "
        + "observation is a baseline and never a change finding. Incomplete inputs retain the "
        + "last-good baseline and produce no speculative findings.")]
    public static Task<CallToolResult> CheckProtoContractAsync(
        ScopeRouter router,
        [Description(
            "Optional exact `proto:R:` RPC or `proto:F:` field canonical key. Omit to check all current RPC and field contracts.")]
        string? symbol = null,
        [Description(
            "Optional scope id, '*', or comma-separated scope ids (maximum 16 scopes).")]
        string? scope = null,
        [Description(
            "Filter incomplete RPC details by missing generated side: any, client, server, or both (default any).")]
        string missing = "any",
        [Description(
            "Zero-based per-scope offset into the filtered incomplete RPC detail list (default 0).")]
        int incompleteOffset = 0,
        [Description(
            "Maximum incomplete RPC details per scope (default 20, maximum 100).")]
        int incompleteLimit = 20,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync(
            "check_proto_contract",
            new
            {
                symbol,
                scope,
                missing,
                incompleteOffset,
                incompleteLimit,
            },
            () => CheckProtoContractImplAsync(
                router,
                symbol,
                scope,
                missing,
                incompleteOffset,
                incompleteLimit,
                ct));

    private static Task<CallToolResult> TraceRpcImplAsync(
        ScopeRouter router,
        string rpc,
        object? scope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (string.IsNullOrWhiteSpace(rpc))
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    "trace_rpc requires a non-empty exact canonical key."));
        }
        var query = rpc.Trim();
        if (query.Length > GrpcContractQueryService.MaximumQueryCharacters)
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    $"trace_rpc canonical keys must not exceed {GrpcContractQueryService.MaximumQueryCharacters} characters."));
        }

        return ScopedExecution.RunAsync(
            router,
            scope,
            async (host, _, hostCount) =>
            {
                var relationLimit = Math.Max(
                    1,
                    GrpcContractQueryService.MaximumRelationsPerRpc
                    / Math.Max(1, hostCount));
                var result = await QueryService.TraceAsync(
                        host.Scope.Id,
                        host.Status,
                        host.Store,
                        host.GrpcLinkState,
                        query,
                        relationLimit,
                        ct)
                    .ConfigureAwait(false);
                return BuildBoundedTrace(
                    query,
                    [result],
                    isError: HasCaughtQueryFailure(result.Failures));
            },
            perScope => MergeTrace(query, perScope),
            ct,
            maxHosts: MaximumScopeFanout);
    }

    private static Task<CallToolResult> CheckProtoContractImplAsync(
        ScopeRouter router,
        string? symbol,
        object? scope,
        string missing,
        int incompleteOffset,
        int incompleteLimit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(router);
        var selection = string.IsNullOrWhiteSpace(symbol)
            ? null
            : symbol.Trim();
        if (selection?.Length
            > GrpcContractQueryService.MaximumQueryCharacters)
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    $"check_proto_contract canonical keys must not exceed {GrpcContractQueryService.MaximumQueryCharacters} characters."));
        }
        var missingFilter = NormalizeMissingFilter(missing);
        if (missingFilter is null)
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    "check_proto_contract missing must be one of: any, client, server, both."));
        }
        if (incompleteOffset < 0)
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    "check_proto_contract incompleteOffset must be zero or greater."));
        }
        if (incompleteLimit is < 1 or > MaximumIncompleteRpcPageSize)
        {
            return Task.FromResult(
                DiagnosticResult.Error(
                    $"check_proto_contract incompleteLimit must be between 1 and {MaximumIncompleteRpcPageSize}."));
        }

        return ScopedExecution.RunAsync(
            router,
            scope,
            async (host, _, _) =>
            {
                var result = await QueryService.CheckAsync(
                         host.Scope.Id,
                        host.Status,
                        host.Store,
                        host.GrpcLinkState,
                        selection,
                         ct)
                     .ConfigureAwait(false);
                result = PageIncompleteRpcDetails(
                    result,
                    missingFilter,
                    incompleteOffset,
                    incompleteLimit);
                return BuildBoundedCheck(
                    selection,
                    [result],
                    isError: HasCaughtQueryFailure(result.Failures));
            },
            perScope => MergeCheck(selection, perScope),
            ct,
            maxHosts: MaximumScopeFanout);
    }

    private static CallToolResult MergeTrace(
        string query,
        IReadOnlyList<ScopedCallToolResult> perScope)
    {
        var scopes = perScope
            .Select(scoped =>
                ReadTraceScope(scoped, query))
            .OrderBy(scope => scope.ScopeId, StringComparer.Ordinal)
            .ToArray();
        return BuildBoundedTrace(
            query,
            scopes,
            perScope.Any(item => item.Result.IsError == true));
    }

    private static CallToolResult MergeCheck(
        string? symbol,
        IReadOnlyList<ScopedCallToolResult> perScope)
    {
        var scopes = perScope
            .Select(scoped => ReadCheckScope(scoped))
            .OrderBy(scope => scope.ScopeId, StringComparer.Ordinal)
            .ToArray();
        return BuildBoundedCheck(
            symbol,
            scopes,
            perScope.Any(item => item.Result.IsError == true));
    }

    internal static CallToolResult BuildBoundedTraceForTests(
        string query,
        IReadOnlyList<GrpcTraceScopeResult> scopes) =>
        BuildBoundedTrace(query, scopes);

    internal static CallToolResult BuildBoundedCheckForTests(
        string? symbol,
        IReadOnlyList<GrpcContractCheckScopeResult> scopes) =>
        BuildBoundedCheck(symbol, scopes);

    private static CallToolResult BuildBoundedTrace(
        string query,
        IReadOnlyList<GrpcTraceScopeResult> rawScopes,
        bool isError = false)
    {
        foreach (var limits in ReductionLimits.Stages)
        {
            var scopes = rawScopes
                .OrderBy(scope => scope.ScopeId, StringComparer.Ordinal)
                .Select(scope => LimitTraceScope(scope, limits))
                .ToArray();
            var dto = new TraceRpcResult(
                Bound(query, 1024),
                AggregateStatus(scopes.Select(scope => scope.Status)),
                scopes,
                scopes.Length,
                SaturatingSum(scopes.Select(scope => scope.TotalRpcCount)),
                SaturatingSum(scopes.Select(scope => scope.TotalClientCount)),
                SaturatingSum(scopes.Select(scope => scope.TotalServerCount)),
                SaturatingSum(scopes.Select(scope => scope.TotalFailureCount)),
                scopes.Any(scope => scope.Partial),
                scopes.Any(scope => scope.Truncated),
                SaturatingSum(scopes.Select(scope => scope.OmittedCount)),
                SaturatingSum(
                    scopes.Select(scope => scope.OmittedEvidenceCount)));
            var result = CreateTraceResult(dto, isError);
            if (SerializedLength(result) <= EffectiveOutputBudget)
            {
                return result;
            }
        }
        throw new InvalidOperationException(
            "trace_rpc could not preserve its required per-scope core within the 50K-character output budget.");
    }

    private static CallToolResult BuildBoundedCheck(
        string? symbol,
        IReadOnlyList<GrpcContractCheckScopeResult> rawScopes,
        bool isError = false)
    {
        foreach (var limits in ReductionLimits.Stages)
        {
            var scopes = rawScopes
                .OrderBy(scope => scope.ScopeId, StringComparer.Ordinal)
                .Select(scope => LimitCheckScope(scope, limits))
                .ToArray();
            var dto = new CheckProtoContractResult(
                symbol is null ? null : Bound(symbol, 1024),
                AggregateStatus(scopes.Select(scope => scope.Status)),
                scopes,
                scopes.Length,
                SaturatingSum(
                    scopes.Select(scope => scope.TotalContractCount)),
                SaturatingSum(
                    scopes.Select(scope => scope.TotalFindingCount)),
                SaturatingSum(
                    scopes.Select(scope => scope.TotalFailureCount)),
                scopes.Any(scope => scope.Partial),
                scopes.Any(scope => scope.Truncated),
                SaturatingSum(scopes.Select(scope => scope.OmittedCount)),
                SaturatingSum(
                    scopes.Select(scope => scope.OmittedEvidenceCount)));
            var result = CreateCheckResult(dto, isError);
            if (SerializedLength(result) <= EffectiveOutputBudget)
            {
                return result;
            }
        }
        throw new InvalidOperationException(
            "check_proto_contract could not preserve its required per-scope core within the 50K-character output budget.");
    }

    private static CallToolResult CreateTraceResult(
        TraceRpcResult dto,
        bool isError)
    {
        var prose = new System.Text.StringBuilder()
            .Append($"trace_rpc: status=`{dto.Status}`, scopes={dto.TotalScopeCount}, ")
            .Append($"rpcs={dto.TotalRpcCount}, clients={dto.TotalClientCount}, ")
            .Append($"servers={dto.TotalServerCount}, partial={Bool(dto.Partial)}, ")
            .Append($"truncated={Bool(dto.Truncated)}, omitted={dto.OmittedCount}");
        AppendIncompleteRpcSummary(prose, dto.Scopes);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = prose.ToString() }],
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.TraceRpcResult),
            IsError = isError ? true : null,
        };
    }

    private static CallToolResult CreateCheckResult(
        CheckProtoContractResult dto,
        bool isError)
    {
        var prose = new System.Text.StringBuilder()
            .Append($"check_proto_contract: status=`{dto.Status}`, scopes={dto.TotalScopeCount}, ")
            .Append($"contracts={dto.TotalContractCount}, findings={dto.TotalFindingCount}, ")
            .Append("relation=`diagnoses-contract`, ")
            .Append($"partial={Bool(dto.Partial)}, truncated={Bool(dto.Truncated)}, ")
            .Append($"omitted={dto.OmittedCount}");
        AppendIncompleteRpcSummary(prose, dto.Scopes);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = prose.ToString() }],
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.CheckProtoContractResult),
            IsError = isError ? true : null,
        };
    }

    private static void AppendIncompleteRpcSummary<TScope>(
        System.Text.StringBuilder prose,
        IReadOnlyList<TScope> scopes)
    {
        const int maximumRows = 20;
        var rows = scopes
            .Select(scope => scope switch
            {
                GrpcTraceScopeResult trace =>
                    (trace.ScopeId, trace.LinkCoverage),
                GrpcContractCheckScopeResult check =>
                    (check.ScopeId, check.LinkCoverage),
                _ => (string.Empty, (GrpcLinkCoverage?)null),
            })
            .Where(item => item.Item2 is not null)
            .SelectMany(item => item.Item2!.IncompleteRpcs.Select(detail =>
                (ScopeId: item.Item1, Detail: detail)))
            .OrderBy(item => item.ScopeId, StringComparer.Ordinal)
            .ThenBy(
                item => item.Detail.RpcCanonicalKey,
                StringComparer.Ordinal)
            .ToArray();
        var omittedByCoverage = scopes
            .Select(scope => scope switch
            {
                GrpcTraceScopeResult trace =>
                    trace.LinkCoverage?.OmittedIncompleteRpcDetails ?? 0,
                GrpcContractCheckScopeResult check =>
                    check.LinkCoverage?.OmittedIncompleteRpcDetails ?? 0,
                _ => 0,
            })
            .Sum();
        var coverages = scopes
            .Select(scope => scope switch
            {
                GrpcTraceScopeResult trace => trace.LinkCoverage,
                GrpcContractCheckScopeResult check => check.LinkCoverage,
                _ => null,
            })
            .Where(coverage => coverage is not null)
            .Cast<GrpcLinkCoverage>()
            .ToArray();
        if (coverages.Length == 0)
        {
            return;
        }

        prose.AppendLine()
            .Append("incomplete_rpc_summary: total=")
            .Append(SaturatingSum(coverages.Select(
                coverage => coverage.IncompleteRpcContracts)))
            .Append(", missing_client=")
            .Append(SaturatingSum(coverages.Select(
                coverage => coverage.MissingGeneratedClients)))
            .Append(", missing_server=")
            .Append(SaturatingSum(coverages.Select(
                coverage => coverage.MissingGeneratedServers)))
            .Append(", detail_total=")
            .Append(SaturatingSum(coverages.Select(
                coverage => coverage.IncompleteRpcDetailTotal)))
            .Append(", detail_returned=")
            .Append(rows.Length)
            .AppendLine();
        if (rows.Length == 0 && omittedByCoverage == 0)
        {
            return;
        }
        prose.AppendLine("incomplete_rpcs:");
        foreach (var row in rows.Take(maximumRows))
        {
            prose.Append("- scope=`")
                .Append(row.ScopeId)
                .Append("`, rpc=`")
                .Append(row.Detail.RpcCanonicalKey)
                .Append("`, missing_client=")
                .Append(Bool(row.Detail.MissingGeneratedClient))
                .Append(", missing_server=")
                .Append(Bool(row.Detail.MissingGeneratedServer))
                .AppendLine();
        }
        var omitted = omittedByCoverage
            + Math.Max(0, rows.Length - maximumRows);
        if (omitted > 0)
        {
            prose.Append("- omitted_incomplete_rpc_details=")
                .Append(omitted)
                .AppendLine();
        }
    }

    private static GrpcTraceScopeResult LimitTraceScope(
        GrpcTraceScopeResult scope,
        ReductionLimits limits)
    {
        var originalEvidence = CountTraceEvidence(scope);
        var rpcs = scope.Rpcs
            .Take(limits.Rpcs)
            .Select(rpc => LimitRpc(rpc, limits))
            .ToArray();
        var failures = LimitFailures(scope.Failures, scope.Partial, limits);
        var keptEvidence = rpcs.Sum(CountRpcEvidence);
        var omittedEvidence = Math.Max(
            scope.OmittedEvidenceCount,
            originalEvidence - keptEvidence);
        var omittedRows =
            Math.Max(0, scope.Rpcs.Count - rpcs.Length)
            + rpcs.Sum(rpc => rpc.OmittedCount)
            + Math.Max(0, scope.Failures.Count - failures.Count);
        var omitted = Math.Max(
            scope.OmittedCount,
            SaturatingAdd(omittedRows, omittedEvidence));
        return scope with
        {
            ScopeId = Bound(scope.ScopeId, 64),
            SelectionCanonicalKey =
                scope.SelectionCanonicalKey is null
                    ? null
                    : Bound(scope.SelectionCanonicalKey, 1024),
            Rpcs = rpcs,
            Failures = failures,
            Partial = scope.Partial || omitted > 0,
            Truncated = scope.Truncated || omitted > 0,
            OmittedCount = omitted,
            OmittedEvidenceCount = omittedEvidence,
        };
    }

    private static GrpcRpcTraceRow LimitRpc(
        GrpcRpcTraceRow rpc,
        ReductionLimits limits)
    {
        var clients = rpc.Clients
            .Take(limits.RelationsPerRpc)
            .Select(row => LimitRelation(row, limits))
            .ToArray();
        var servers = rpc.Servers
            .Take(limits.RelationsPerRpc)
            .Select(row => LimitRelation(row, limits))
            .ToArray();
        var evidence = LimitEvidence(rpc.Evidence, limits);
        var omitted =
            Math.Max(0, rpc.Clients.Count - clients.Length)
            + Math.Max(0, rpc.Servers.Count - servers.Length);
        omitted = SaturatingAdd(
            omitted,
            clients.Sum(row => row.EvidenceOmittedCount)
            + servers.Sum(row => row.EvidenceOmittedCount));
        return rpc with
        {
            CanonicalKey = Bound(rpc.CanonicalKey, 1024),
            FullName = Bound(rpc.FullName, 512),
            ServiceFullName = Bound(rpc.ServiceFullName, 512),
            InputType = Bound(rpc.InputType, 512),
            OutputType = Bound(rpc.OutputType, 512),
            Evidence = evidence.Rows,
            EvidenceOmittedCount = Math.Max(
                rpc.EvidenceOmittedCount,
                evidence.Omitted),
            Clients = clients,
            Servers = servers,
            Truncated = rpc.Truncated || omitted > 0,
            OmittedCount = Math.Max(rpc.OmittedCount, omitted),
        };
    }

    private static GrpcManagedRpcRelationRow LimitRelation(
        GrpcManagedRpcRelationRow row,
        ReductionLimits limits)
    {
        var evidence = LimitEvidence(row.Evidence, limits);
        return row with
        {
            ManagedSymbol = Bound(row.ManagedSymbol, 1024),
            ManagedName = Bound(row.ManagedName, 256),
            StoredSource = Bound(row.StoredSource, 1024),
            StoredTarget = Bound(row.StoredTarget, 1024),
            Evidence = evidence.Rows,
            EvidenceTruncated =
                row.EvidenceTruncated || evidence.Omitted > 0,
            EvidenceOmittedCount = Math.Max(
                row.EvidenceOmittedCount,
                evidence.Omitted),
        };
    }

    private static GrpcContractCheckScopeResult LimitCheckScope(
        GrpcContractCheckScopeResult scope,
        ReductionLimits limits)
    {
        var originalEvidence = scope.Findings.Sum(finding =>
            finding.CurrentEvidence.Count
            + finding.BaselineEvidence.Count
            + finding.EvidenceOmittedCount);
        var findings = scope.Findings
            .Take(limits.Findings)
            .Select(finding => LimitFinding(finding, limits))
            .ToArray();
        var failures = LimitFailures(scope.Failures, scope.Partial, limits);
        var keptEvidence = findings.Sum(finding =>
            finding.CurrentEvidence.Count
            + finding.BaselineEvidence.Count);
        var omittedEvidence = Math.Max(
            scope.OmittedEvidenceCount,
            originalEvidence - keptEvidence);
        var omitted =
            Math.Max(0, scope.Findings.Count - findings.Length)
            + Math.Max(0, scope.Failures.Count - failures.Count);
        omitted = SaturatingAdd(omitted, omittedEvidence);
        omitted = Math.Max(scope.OmittedCount, omitted);
        var coverage = LimitIncompleteRpcCoverage(
            scope.LinkCoverage,
            limits.Rpcs);
        return scope with
        {
            ScopeId = Bound(scope.ScopeId, 64),
            Findings = findings,
            Failures = failures,
            Partial = scope.Partial || omitted > 0,
            Truncated = scope.Truncated || omitted > 0,
            OmittedCount = omitted,
            OmittedEvidenceCount = omittedEvidence,
            LinkCoverage = coverage,
        };
    }

    private static GrpcContractCheckScopeResult PageIncompleteRpcDetails(
        GrpcContractCheckScopeResult scope,
        string missingFilter,
        int offset,
        int limit)
    {
        if (scope.LinkCoverage is not { } coverage)
        {
            return scope;
        }

        var filtered = coverage.IncompleteRpcs
            .Where(detail => missingFilter switch
            {
                "client" => detail.MissingGeneratedClient,
                "server" => detail.MissingGeneratedServer,
                "both" => detail.MissingGeneratedClient
                    && detail.MissingGeneratedServer,
                _ => true,
            })
            .ToArray();
        var page = filtered
            .Skip(offset)
            .Take(limit)
            .ToArray();
        var nextOffset = offset + page.Length;
        return scope with
        {
            LinkCoverage = coverage with
            {
                IncompleteRpcs = page,
                IncompleteRpcDetailTotal = filtered.Length,
                IncompleteRpcDetailOffset = offset,
                IncompleteRpcDetailLimit = limit,
                IncompleteRpcDetailReturned = page.Length,
                IncompleteRpcDetailHasMore = nextOffset < filtered.Length,
                IncompleteRpcDetailNextOffset =
                    nextOffset < filtered.Length ? nextOffset : null,
                IncompleteRpcMissingFilter = missingFilter,
            },
        };
    }

    private static GrpcLinkCoverage? LimitIncompleteRpcCoverage(
        GrpcLinkCoverage? coverage,
        int limit)
    {
        if (coverage is null || coverage.IncompleteRpcs.Count <= limit)
        {
            return coverage;
        }

        var rows = coverage.IncompleteRpcs.Take(limit).ToArray();
        var nextOffset = coverage.IncompleteRpcDetailOffset + rows.Length;
        return coverage with
        {
            IncompleteRpcs = rows,
            IncompleteRpcDetailReturned = rows.Length,
            IncompleteRpcDetailHasMore =
                nextOffset < coverage.IncompleteRpcDetailTotal,
            IncompleteRpcDetailNextOffset =
                nextOffset < coverage.IncompleteRpcDetailTotal
                    ? nextOffset
                    : null,
        };
    }

    private static string? NormalizeMissingFilter(string? missing)
    {
        var value = string.IsNullOrWhiteSpace(missing)
            ? "any"
            : missing.Trim().ToLowerInvariant();
        return value is "any" or "client" or "server" or "both"
            ? value
            : null;
    }

    private static GrpcContractFindingRow LimitFinding(
        GrpcContractFindingRow finding,
        ReductionLimits limits)
    {
        var current = LimitEvidence(finding.CurrentEvidence, limits);
        var baseline = LimitEvidence(finding.BaselineEvidence, limits);
        var omitted = finding.EvidenceOmittedCount;
        omitted = SaturatingAdd(omitted, current.Omitted);
        omitted = SaturatingAdd(omitted, baseline.Omitted);
        return finding with
        {
            Message = Bound(finding.Message, 512),
            ProtoSymbol = Bound(finding.ProtoSymbol, 1024),
            ManagedSymbol =
                finding.ManagedSymbol is null
                    ? null
                    : Bound(finding.ManagedSymbol, 1024),
            Details = limits.IncludeMetadata
                ? LimitMetadata(finding.Details)
                : null,
            CurrentEvidence = current.Rows,
            BaselineEvidence = baseline.Rows,
            EvidenceOmittedCount = omitted,
        };
    }

    private static (
        IReadOnlyList<GrpcToolEvidenceRow> Rows,
        int Omitted) LimitEvidence(
        IReadOnlyList<GrpcToolEvidenceRow> evidence,
        ReductionLimits limits)
    {
        var rows = evidence
            .Take(limits.EvidencePerItem)
            .Select(item => item with
            {
                FilePath = Bound(item.FilePath, 512),
                Producer = Bound(item.Producer, 96),
                Metadata = limits.IncludeMetadata
                    ? LimitMetadata(item.Metadata)
                    : null,
                MetadataOmittedCount = SaturatingAdd(
                    item.MetadataOmittedCount,
                    limits.IncludeMetadata
                        ? Math.Max(
                            0,
                            (item.Metadata?.Count ?? 0)
                            - (LimitMetadata(item.Metadata)?.Count ?? 0))
                        : item.Metadata?.Count ?? 0),
            })
            .ToArray();
        return (rows, Math.Max(0, evidence.Count - rows.Length));
    }

    private static IReadOnlyDictionary<string, string>? LimitMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        return metadata
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(16)
            .ToDictionary(
                pair => Bound(pair.Key, 96),
                pair => Bound(pair.Value, 256),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<GrpcToolFailureRow> LimitFailures(
        IReadOnlyList<GrpcToolFailureRow> failures,
        bool partial,
        ReductionLimits limits)
    {
        var count = partial && failures.Count > 0
            ? Math.Max(1, limits.Failures)
            : limits.Failures;
        return failures
            .Take(count)
            .Select(failure => failure with
            {
                Message = Bound(failure.Message, 512),
                SymbolCanonicalKey =
                    failure.SymbolCanonicalKey is null
                        ? null
                        : Bound(failure.SymbolCanonicalKey, 1024),
            })
            .ToArray();
    }

    private static GrpcTraceScopeResult ReadTraceScope(
        ScopedCallToolResult scoped,
        string query)
    {
        if (scoped.Result.StructuredContent is { } structured)
        {
            try
            {
                var dto = structured.Deserialize(
                    ToolOutputJsonContext.Default.TraceRpcResult);
                if (dto?.Scopes.Count > 0)
                {
                    return dto.Scopes[0] with
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
        return new GrpcTraceScopeResult(
            scoped.ScopeId,
            scoped.ScopeStatus,
            "error",
            Partial: true,
            RetainedLastGood: false,
            SelectionStatus: "unknown",
            SelectionCanonicalKey: query,
            Rpcs: [],
            TotalRpcCount: 0,
            TotalClientCount: 0,
            TotalServerCount: 0,
            Failures:
            [
                new GrpcToolFailureRow(
                    "scope",
                    "scope-unavailable",
                    DiagnosticText(scoped.Result),
                    query),
            ],
            TotalFailureCount: 1,
            Truncated: false,
            OmittedCount: 0,
            OmittedEvidenceCount: 0);
    }

    private static GrpcContractCheckScopeResult ReadCheckScope(
        ScopedCallToolResult scoped)
    {
        if (scoped.Result.StructuredContent is { } structured)
        {
            try
            {
                var dto = structured.Deserialize(
                    ToolOutputJsonContext.Default.CheckProtoContractResult);
                if (dto?.Scopes.Count > 0)
                {
                    return dto.Scopes[0] with
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
        return new GrpcContractCheckScopeResult(
            scoped.ScopeId,
            scoped.ScopeStatus,
            "error",
            Partial: true,
            RetainedLastGood: false,
            BaselinePolicy:
                "first-complete-successful-observation-per-exact-canonical-key",
            TotalContractCount: 0,
            Findings: [],
            TotalFindingCount: 0,
            Failures:
            [
                new GrpcToolFailureRow(
                    "scope",
                    "scope-unavailable",
                    DiagnosticText(scoped.Result),
                    null),
            ],
            TotalFailureCount: 1,
            Truncated: false,
            OmittedCount: 0,
            OmittedEvidenceCount: 0);
    }

    private static int CountTraceEvidence(GrpcTraceScopeResult scope) =>
        scope.Rpcs.Sum(CountRpcEvidence)
        + scope.OmittedEvidenceCount;

    private static int CountRpcEvidence(GrpcRpcTraceRow rpc) =>
        rpc.Evidence.Count
        + rpc.Clients.Sum(row => row.Evidence.Count)
        + rpc.Servers.Sum(row => row.Evidence.Count);

    private static string DiagnosticText(CallToolResult result) =>
        Bound(
            result.Content?
                .OfType<TextContentBlock>()
                .Select(block => block.Text)
                .FirstOrDefault(text =>
                    !string.IsNullOrWhiteSpace(text))
            ?? "Scope query failed without a diagnostic message.",
            512);

    private static bool HasCaughtQueryFailure(
        IReadOnlyList<GrpcToolFailureRow> failures) =>
        failures.Any(failure =>
            string.Equals(
                failure.Phase,
                "query",
                StringComparison.Ordinal));

    private static int SerializedLength(CallToolResult result) =>
        JsonSerializer.Serialize(
            result,
            McpJsonUtilities.DefaultOptions).Length;

    private static int EffectiveOutputBudget =>
        OutputBudget.DefaultBudgetChars - OutputBudgetSafetyMargin;

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        var distinct = statuses
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
        return sum >= int.MaxValue
            ? int.MaxValue
            : (int)sum;
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum
            ? value
            : value[..maximum];

    private static string Bool(bool value) =>
        value ? "true" : "false";

    private sealed record ReductionLimits(
        int Rpcs,
        int RelationsPerRpc,
        int Findings,
        int EvidencePerItem,
        int Failures,
        bool IncludeMetadata)
    {
        public static IReadOnlyList<ReductionLimits> Stages { get; } =
        [
            new(64, 64, 128, 8, 32, true),
            new(16, 16, 64, 4, 16, false),
            new(4, 4, 16, 2, 8, false),
            new(1, 1, 4, 1, 2, false),
            new(0, 0, 0, 0, 1, false),
        ];
    }
}
