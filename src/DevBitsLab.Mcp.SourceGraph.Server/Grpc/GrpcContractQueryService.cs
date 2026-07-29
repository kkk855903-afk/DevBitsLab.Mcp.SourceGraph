using DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Core = DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Server.Grpc;

/// <summary>
/// Evidence-first read service for the gRPC MCP tools. Selection is exact-canonical only and
/// traversal follows persisted edge orientation; this service never reconstructs relationships
/// from names.
/// </summary>
public sealed class GrpcContractQueryService
{
    public const int MaximumQueryCharacters = 4096;
    public const int MaximumContractFacts = 10_000;
    public const int MaximumSymbols = 100_000;
    public const int MaximumRelationsPerRpc = 64;
    public const int MaximumEvidencePerRelation = 8;

    private const int AnnotationPageSize = 1_000;
    private const string StoredOrientation =
        "managed-source-to-proto-rpc-target";
    private const string BaselinePolicy =
        "first-complete-successful-observation-per-exact-canonical-key";

    public async Task<GrpcTraceScopeResult> TraceAsync(
        string scopeId,
        string scopeStatus,
        IGraphStore store,
        GrpcLinkRuntimeState? runtimeState,
        string query,
        int relationLimit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        relationLimit = Math.Clamp(
            relationLimit,
            1,
            MaximumRelationsPerRpc);

        var retainedSnapshot =
            runtimeState?.Status != GrpcLinkRuntimeStatus.Complete
            && runtimeState?.RetainedLastGood == true;
        if (runtimeState?.Status != GrpcLinkRuntimeStatus.Complete
            && !retainedSnapshot)
        {
            return TraceRuntimeUnavailable(
                scopeId,
                scopeStatus,
                query,
                runtimeState);
        }

        try
        {
            var snapshot = await ReadCurrentSnapshotAsync(store, ct)
                .ConfigureAwait(false);
            var selection = await ResolveTraceSelectionAsync(
                    store,
                    snapshot,
                    query,
                    ct)
                .ConfigureAwait(false);
            if (selection.Rpcs.Count == 0)
            {
                var runtimeFailures = retainedSnapshot
                    ? RuntimeFailures(runtimeState)
                    : [];
                var failures = runtimeFailures
                    .Append(
                        new GrpcToolFailureRow(
                            "selection",
                            selection.FailureCode
                                ?? "rpc-not-found",
                            selection.FailureMessage
                                ?? "No evidence-backed RPC relationship matched the exact canonical key.",
                            selection.CanonicalKey))
                    .ToArray();
                return new GrpcTraceScopeResult(
                    scopeId,
                    scopeStatus,
                    retainedSnapshot ? "partial" : "not_found",
                    Partial: retainedSnapshot,
                    RetainedLastGood: retainedSnapshot,
                    selection.Status,
                    selection.CanonicalKey,
                    [],
                    TotalRpcCount: 0,
                    TotalClientCount: 0,
                    TotalServerCount: 0,
                    failures,
                    TotalFailureCount: retainedSnapshot
                        ? Math.Max(
                            failures.Length,
                            SaturatingAdd(
                                runtimeState!.FailureCount,
                                1))
                        : 1,
                    Truncated:
                        (runtimeState?.OmittedFailures ?? 0) > 0,
                    OmittedCount:
                        runtimeState?.OmittedFailures ?? 0,
                    OmittedEvidenceCount: 0,
                    LinkCoverage: runtimeState?.Coverage);
            }

            var rows = new List<GrpcRpcTraceRow>();
            var totalClients = 0;
            var totalServers = 0;
            var omitted = 0;
            var omittedEvidence = 0;
            foreach (var rpc in selection.Rpcs
                .OrderBy(item => item.Fact.SymbolCanonicalKey, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var clients = await ReadRelationsAsync(
                        store,
                        rpc,
                        EdgeKinds.GrpcCalls,
                        relationLimit,
                        ct)
                    .ConfigureAwait(false);
                var servers = await ReadRelationsAsync(
                        store,
                        rpc,
                        EdgeKinds.ImplementsRpc,
                        relationLimit,
                        ct)
                    .ConfigureAwait(false);
                totalClients = SaturatingAdd(
                    totalClients,
                    clients.TotalCountLowerBound);
                totalServers = SaturatingAdd(
                    totalServers,
                    servers.TotalCountLowerBound);
                omitted = SaturatingAdd(
                    omitted,
                    clients.OmittedCount + servers.OmittedCount);
                omittedEvidence = SaturatingAdd(
                    omittedEvidence,
                    clients.OmittedEvidenceCount
                    + servers.OmittedEvidenceCount);
                var contract = rpc.Fact.Rpc
                    ?? throw new InvalidOperationException(
                        "A selected proto RPC had no RPC contract.");
                rows.Add(new GrpcRpcTraceRow(
                    rpc.Fact.SymbolCanonicalKey,
                    rpc.Fact.FullName,
                    contract.ServiceFullName,
                    contract.InputType,
                    contract.OutputType,
                    contract.ClientStreaming,
                    contract.ServerStreaming,
                    StoredOrientation,
                    [CurrentContractEvidence(rpc)],
                    EvidenceOmittedCount: 0,
                    clients.Rows,
                    clients.TotalCountLowerBound,
                    servers.Rows,
                    servers.TotalCountLowerBound,
                    Truncated:
                        clients.OmittedCount > 0
                        || servers.OmittedCount > 0,
                    OmittedCount:
                        clients.OmittedCount + servers.OmittedCount));
            }

            var retainedFailures = retainedSnapshot
                ? RuntimeFailures(runtimeState)
                : [];
            var runtimeOmitted = retainedSnapshot
                ? runtimeState!.OmittedFailures
                : 0;
            omitted = SaturatingAdd(omitted, runtimeOmitted);
            return new GrpcTraceScopeResult(
                scopeId,
                scopeStatus,
                retainedSnapshot || omitted > 0 ? "partial" : "ok",
                Partial: retainedSnapshot || omitted > 0,
                RetainedLastGood: retainedSnapshot,
                selection.Status,
                selection.CanonicalKey,
                rows,
                rows.Count,
                totalClients,
                totalServers,
                retainedFailures,
                TotalFailureCount: retainedSnapshot
                    ? Math.Max(
                        retainedFailures.Count,
                        runtimeState!.FailureCount)
                    : 0,
                Truncated: omitted > 0,
                OmittedCount: omitted,
                OmittedEvidenceCount: omittedEvidence,
                LinkCoverage: runtimeState?.Coverage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GrpcTraceScopeResult(
                scopeId,
                scopeStatus,
                "partial",
                Partial: true,
                RetainedLastGood:
                    runtimeState?.RetainedLastGood ?? false,
                SelectionStatus: "unknown",
                SelectionCanonicalKey: query,
                Rpcs: [],
                TotalRpcCount: 0,
                TotalClientCount: 0,
                TotalServerCount: 0,
                Failures:
                [
                    new GrpcToolFailureRow(
                        "query",
                        "grpc-query-incomplete",
                        Bound(ex.Message, 512),
                        query),
                ],
                TotalFailureCount: 1,
                Truncated: false,
                OmittedCount: 0,
                OmittedEvidenceCount: 0,
                LinkCoverage: runtimeState?.Coverage);
        }
    }

    public async Task<GrpcContractCheckScopeResult> CheckAsync(
        string scopeId,
        string scopeStatus,
        IGraphStore store,
        GrpcLinkRuntimeState? runtimeState,
        string? symbol,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var retainedPartialSnapshot =
            runtimeState?.Status != GrpcLinkRuntimeStatus.Complete
            && runtimeState?.RetainedLastGood == true;
        if (runtimeState?.Status != GrpcLinkRuntimeStatus.Complete
            && !retainedPartialSnapshot)
        {
            return CheckRuntimeUnavailable(
                scopeId,
                scopeStatus,
                runtimeState);
        }

        try
        {
            var snapshot = await ReadCurrentSnapshotAsync(store, ct)
                .ConfigureAwait(false);
            var selected = SelectContracts(snapshot, symbol);
            if (selected.Failure is not null)
            {
                return new GrpcContractCheckScopeResult(
                    scopeId,
                    scopeStatus,
                    "not_found",
                    Partial: retainedPartialSnapshot,
                    RetainedLastGood: retainedPartialSnapshot,
                    BaselinePolicy,
                    TotalContractCount: 0,
                    Findings: [],
                    TotalFindingCount: 0,
                    Failures: retainedPartialSnapshot
                        ? RuntimeFailures(runtimeState).Append(selected.Failure).ToArray()
                        : [selected.Failure],
                    TotalFailureCount: retainedPartialSnapshot
                        ? Math.Max(
                            RuntimeFailures(runtimeState).Count + 1,
                            runtimeState!.FailureCount + 1)
                        : 1,
                    Truncated: retainedPartialSnapshot
                        && runtimeState!.OmittedFailures > 0,
                    OmittedCount: retainedPartialSnapshot
                        ? runtimeState!.OmittedFailures
                        : 0,
                    OmittedEvidenceCount: 0,
                    LinkCoverage: runtimeState?.Coverage);
            }

            var baselines = await ReadBaselinesAsync(store, ct)
                .ConfigureAwait(false);
            var findings = new List<GrpcContractFindingRow>();
            foreach (var current in selected.Facts)
            {
                ct.ThrowIfCancellationRequested();
                if (baselines.TryGetValue(
                        current.Fact.SymbolCanonicalKey,
                        out var baseline))
                {
                    AddChangeFindings(current, baseline, findings);
                }
                // A partial projection can prove links that were published, but cannot prove
                // that a managed implementation is absent from the unprocessed portion.
                if (current.Fact.Kind == ProtoContractKind.Rpc
                    && !retainedPartialSnapshot)
                {
                    await AddMissingImplementationFindingAsync(
                            store,
                            current,
                            findings,
                            ct)
                        .ConfigureAwait(false);
                }
            }

            var selectedKeys = selected.Facts
                .Select(item => item.Fact.SymbolCanonicalKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var failure in runtimeState!.Failures
                .Where(failure =>
                    failure.Code == "grpc-signature-mismatch"
                    && failure.ProtoCanonicalKey is not null
                    && failure.GeneratedRole is not null
                    && selectedKeys.Contains(failure.ProtoCanonicalKey))
                .OrderBy(failure => failure.ProtoCanonicalKey, StringComparer.Ordinal)
                .ThenBy(failure => failure.SymbolCanonicalKey, StringComparer.Ordinal))
            {
                var current = snapshot.Facts[failure.ProtoCanonicalKey!];
                var managed = await TryGetExactSymbolAsync(
                        store,
                        snapshot.SymbolKeys,
                        failure.SymbolCanonicalKey,
                        ct)
                    .ConfigureAwait(false);
                if (managed is null)
                {
                    continue;
                }
                findings.Add(CreateSignatureMismatchFinding(
                    current,
                    managed,
                    failure));
            }

            findings = findings
                .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.ProtoSymbol, StringComparer.Ordinal)
                .ThenBy(finding => finding.ManagedSymbol, StringComparer.Ordinal)
                .ToList();
            var failures = retainedPartialSnapshot
                ? RuntimeFailures(runtimeState).ToList()
                : [];
            var omitted = runtimeState.OmittedFailures;
            if (!retainedPartialSnapshot && omitted > 0)
            {
                failures.Add(new GrpcToolFailureRow(
                    "linker",
                    "grpc-link-failures-omitted",
                    $"The linker omitted {runtimeState.OmittedFailures} bounded diagnostic(s); additional proven signature mismatches may be absent.",
                    null));
            }

            return new GrpcContractCheckScopeResult(
                scopeId,
                scopeStatus,
                retainedPartialSnapshot || omitted > 0 ? "partial" : "ok",
                Partial: retainedPartialSnapshot || omitted > 0,
                RetainedLastGood: retainedPartialSnapshot,
                BaselinePolicy,
                selected.Facts.Count,
                findings,
                findings.Count,
                failures,
                retainedPartialSnapshot
                    ? Math.Max(failures.Count, runtimeState.FailureCount)
                    : failures.Count,
                Truncated: omitted > 0,
                OmittedCount: omitted,
                OmittedEvidenceCount: 0,
                LinkCoverage: runtimeState.Coverage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GrpcContractCheckScopeResult(
                scopeId,
                scopeStatus,
                "partial",
                Partial: true,
                RetainedLastGood:
                    runtimeState?.RetainedLastGood ?? false,
                BaselinePolicy,
                TotalContractCount: 0,
                Findings: [],
                TotalFindingCount: 0,
                Failures:
                [
                    new GrpcToolFailureRow(
                        "query",
                        "grpc-contract-check-incomplete",
                        Bound(ex.Message, 512),
                        symbol),
                ],
                TotalFailureCount: 1,
                Truncated: false,
                OmittedCount: 0,
                OmittedEvidenceCount: 0,
                LinkCoverage: runtimeState?.Coverage);
        }
    }

    private static async Task<CurrentSnapshot> ReadCurrentSnapshotAsync(
        IGraphStore store,
        CancellationToken ct)
    {
        var rows = new List<StoredAnnotationRow>();
        long afterId = 0;
        while (true)
        {
            var page = await store.ListAnnotationsByFlavorAsync(
                    ProtoContractAnnotations.Flavor,
                    afterId,
                    AnnotationPageSize,
                    ct)
                .ConfigureAwait(false);
            if (page.Count == 0) break;
            if (rows.Count + page.Count > MaximumContractFacts)
            {
                throw new InvalidOperationException(
                    $"The current protobuf contract universe exceeds {MaximumContractFacts} facts.");
            }
            foreach (var row in page)
            {
                if (row.AnnotationId <= afterId)
                {
                    throw new InvalidOperationException(
                        "The proto-contract annotation cursor did not advance.");
                }
                rows.Add(row);
                afterId = row.AnnotationId;
            }
            if (page.Count < AnnotationPageSize) break;
        }

        var symbolKeys = await store.GetAllSymbolKeysAsync(ct)
            .ConfigureAwait(false);
        if (symbolKeys.Count > MaximumSymbols)
        {
            throw new InvalidOperationException(
                $"The symbol universe exceeds the {MaximumSymbols}-row gRPC query limit.");
        }

        var facts = new Dictionary<string, CurrentFactRow>(
            StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ArgsJson is null)
            {
                throw new InvalidOperationException(
                    $"Proto contract `{row.SymbolCanonicalKey}` has no payload.");
            }
            var fact = ProtoContractPayloadCodec.Decode(row.ArgsJson);
            if (fact.Status != ProtoContractStatus.Complete)
            {
                throw new InvalidOperationException(
                    $"Proto contract `{fact.SymbolCanonicalKey}` is incomplete.");
            }
            if (!string.Equals(
                    fact.SymbolCanonicalKey,
                    row.SymbolCanonicalKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A proto annotation key does not match its strict payload.");
            }
            var symbol = await store.GetSymbolByIdAsync(row.SymbolId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Proto contract `{fact.SymbolCanonicalKey}` has no declaration.");
            if (!string.Equals(
                    symbol.CanonicalKey,
                    fact.SymbolCanonicalKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    symbol.FilePath,
                    row.FilePath,
                    PathComparison))
            {
                throw new InvalidOperationException(
                    $"Proto contract `{fact.SymbolCanonicalKey}` has inconsistent source evidence.");
            }
            if (!facts.TryAdd(
                    fact.SymbolCanonicalKey,
                    new CurrentFactRow(fact, row, symbol)))
            {
                throw new InvalidOperationException(
                    $"Proto contract `{fact.SymbolCanonicalKey}` is duplicated.");
            }
        }

        return new CurrentSnapshot(symbolKeys, facts);
    }

    private static async Task<TraceSelection> ResolveTraceSelectionAsync(
        IGraphStore store,
        CurrentSnapshot snapshot,
        string query,
        CancellationToken ct)
    {
        var canonicalKey = query.Trim();
        if (snapshot.Facts.TryGetValue(canonicalKey, out var proto))
        {
            if (proto.Fact.Kind != ProtoContractKind.Rpc)
            {
                return TraceSelection.None(
                    "not_rpc",
                    canonicalKey,
                    "selection-not-rpc",
                    "The exact protobuf symbol is not an RPC declaration.");
            }
            return new TraceSelection(
                "selected_proto_rpc",
                canonicalKey,
                [proto],
                null,
                null);
        }
        if (!canonicalKey.StartsWith("csharp:", StringComparison.Ordinal))
        {
            return TraceSelection.None(
                "not_found",
                canonicalKey,
                "exact-canonical-key-required",
                "trace_rpc requires an exact `proto:R:` RPC key or exact `csharp:` managed symbol key.");
        }

        var managed = await TryGetExactSymbolAsync(
                store,
                snapshot.SymbolKeys,
                canonicalKey,
                ct)
            .ConfigureAwait(false);
        if (managed is null)
        {
            return TraceSelection.None(
                "not_found",
                canonicalKey,
                "managed-symbol-not-found",
                "The exact managed canonical key was not found.");
        }

        var targets = new Dictionary<string, CurrentFactRow>(
            StringComparer.Ordinal);
        foreach (var relation in new[]
        {
            EdgeKinds.GrpcCalls,
            EdgeKinds.ImplementsRpc,
        })
        {
            var outbound = await store.ListAuditableOutboundEdgesAsync(
                    managed.Id,
                    MaximumRelationsPerRpc + 1,
                    relation,
                    ct)
                .ConfigureAwait(false);
            if (outbound.Count > MaximumRelationsPerRpc)
            {
                return TraceSelection.None(
                    "unknown",
                    canonicalKey,
                    "managed-rpc-edge-limit",
                    $"The managed symbol exceeds the {MaximumRelationsPerRpc}-RPC traversal limit.");
            }
            foreach (var edge in outbound)
            {
                if (edge.Symbol.CanonicalKey is { } targetKey
                    && snapshot.Facts.TryGetValue(targetKey, out var target)
                    && target.Fact.Kind == ProtoContractKind.Rpc)
                {
                    targets[targetKey] = target;
                }
            }
        }
        return targets.Count == 0
            ? TraceSelection.None(
                "not_found",
                canonicalKey,
                "stored-rpc-edge-not-found",
                "The exact managed symbol has no evidence-backed outgoing `grpc-calls` or `implements-rpc` edge.")
            : new TraceSelection(
                "selected_managed_symbol",
                canonicalKey,
                targets.Values
                    .OrderBy(
                        item => item.Fact.SymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ToArray(),
                null,
                null);
    }

    private static async Task<RelationSelection> ReadRelationsAsync(
        IGraphStore store,
        CurrentFactRow rpc,
        string relation,
        int limit,
        CancellationToken ct)
    {
        var inbound = await store.ListAuditableInboundEdgesAsync(
                rpc.Symbol.Id,
                limit + 1,
                relation,
                ct)
            .ConfigureAwait(false);
        var truncatedRows = inbound.Count > limit;
        var selected = inbound
            .Take(limit)
            .Where(edge => edge.Symbol.CanonicalKey is not null)
            .OrderBy(
                edge => edge.Symbol.CanonicalKey,
                StringComparer.Ordinal)
            .ToArray();
        var rows = new List<GrpcManagedRpcRelationRow>(selected.Length);
        var omittedEvidence = 0;
        foreach (var edge in selected)
        {
            var evidence = await store.ListEdgeEvidenceAsync(
                    edge.Symbol.Id,
                    rpc.Symbol.Id,
                    relation,
                    MaximumEvidencePerRelation + 1,
                    ct)
                .ConfigureAwait(false);
            if (evidence.Count == 0)
            {
                continue;
            }
            var evidenceTruncated =
                evidence.Count > MaximumEvidencePerRelation;
            if (evidenceTruncated)
            {
                omittedEvidence++;
            }
            rows.Add(new GrpcManagedRpcRelationRow(
                relation,
                edge.Symbol.CanonicalKey!,
                edge.Symbol.Name,
                edge.Symbol.Kind,
                edge.Symbol.CanonicalKey!,
                rpc.Fact.SymbolCanonicalKey,
                StoredOrientation,
                "reverse-inbound-from-proto-rpc",
                evidence
                    .Take(MaximumEvidencePerRelation)
                    .OrderBy(
                        item => item.Location.FilePath,
                        PathComparer)
                    .ThenBy(item => item.Location.StartLine)
                    .ThenBy(item => item.Location.StartColumn)
                    .Select(StoredEvidence)
                    .ToArray(),
                evidence.Count,
                evidenceTruncated,
                evidenceTruncated ? 1 : 0));
        }

        return new RelationSelection(
            rows,
            inbound.Count,
            truncatedRows ? 1 : 0,
            omittedEvidence);
    }

    private static ContractSelection SelectContracts(
        CurrentSnapshot snapshot,
        string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return new ContractSelection(
                snapshot.Facts.Values
                    .Where(item =>
                        item.Fact.Kind is ProtoContractKind.Field
                            or ProtoContractKind.Rpc)
                    .OrderBy(
                        item => item.Fact.SymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ToArray(),
                null);
        }
        var key = symbol.Trim();
        if (!snapshot.Facts.TryGetValue(key, out var fact)
            || fact.Fact.Kind is not (
                ProtoContractKind.Field or ProtoContractKind.Rpc))
        {
            return new ContractSelection(
                [],
                new GrpcToolFailureRow(
                    "selection",
                    "proto-contract-not-found",
                    "The exact canonical key does not select a current protobuf RPC or field contract.",
                    key));
        }
        return new ContractSelection([fact], null);
    }

    private static async Task<IReadOnlyDictionary<string, BaselineFactRow>>
        ReadBaselinesAsync(
            IGraphStore store,
            CancellationToken ct)
    {
        var rows = await store.ListGrpcContractBaselinesAsync(
                MaximumContractFacts,
                ct)
            .ConfigureAwait(false);
        var result = new Dictionary<string, BaselineFactRow>(
            StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var fact = ProtoContractPayloadCodec.Decode(row.ContractJson);
            if (fact.Status != ProtoContractStatus.Complete
                || !string.Equals(
                    fact.SymbolCanonicalKey,
                    row.SymbolCanonicalKey,
                    StringComparison.Ordinal)
                || !result.TryAdd(
                    row.SymbolCanonicalKey,
                    new BaselineFactRow(fact, row)))
            {
                throw new InvalidOperationException(
                    $"Persisted gRPC baseline `{row.SymbolCanonicalKey}` is inconsistent.");
            }
        }
        return result;
    }

    private static void AddChangeFindings(
        CurrentFactRow current,
        BaselineFactRow baseline,
        ICollection<GrpcContractFindingRow> findings)
    {
        if (current.Fact.Kind == ProtoContractKind.Field
            && baseline.Fact.Kind == ProtoContractKind.Field
            && current.Fact.Field is { } currentField
            && baseline.Fact.Field is { } baselineField
            && currentField.Number != baselineField.Number)
        {
            findings.Add(new GrpcContractFindingRow(
                "Grpc002",
                "diagnoses-contract",
                "field_number_changed",
                "error",
                "semantic",
                $"Protobuf field number changed from {baselineField.Number} to {currentField.Number}.",
                current.Fact.SymbolCanonicalKey,
                ManagedSymbol: null,
                GeneratedRole: null,
                BaselineProvenance: BaselinePolicy,
                Details: new SortedDictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["baseline_number"] =
                        baselineField.Number.ToString(),
                    ["current_number"] =
                        currentField.Number.ToString(),
                },
                CurrentEvidence: [CurrentContractEvidence(current)],
                BaselineEvidence: [BaselineEvidence(baseline)],
                EvidenceOmittedCount: 0));
        }

        if (current.Fact.Kind == ProtoContractKind.Rpc
            && baseline.Fact.Kind == ProtoContractKind.Rpc
            && current.Fact.Rpc is { } currentRpc
            && baseline.Fact.Rpc is { } baselineRpc
            && (currentRpc.ClientStreaming
                    != baselineRpc.ClientStreaming
                || currentRpc.ServerStreaming
                    != baselineRpc.ServerStreaming))
        {
            findings.Add(new GrpcContractFindingRow(
                "Grpc003",
                "diagnoses-contract",
                "streaming_changed",
                "error",
                "semantic",
                "Protobuf RPC client/server streaming shape changed from the persisted prior baseline.",
                current.Fact.SymbolCanonicalKey,
                ManagedSymbol: null,
                GeneratedRole: null,
                BaselineProvenance: BaselinePolicy,
                Details: new SortedDictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["baseline_client_streaming"] =
                        Bool(baselineRpc.ClientStreaming),
                    ["baseline_server_streaming"] =
                        Bool(baselineRpc.ServerStreaming),
                    ["current_client_streaming"] =
                        Bool(currentRpc.ClientStreaming),
                    ["current_server_streaming"] =
                        Bool(currentRpc.ServerStreaming),
                },
                CurrentEvidence: [CurrentContractEvidence(current)],
                BaselineEvidence: [BaselineEvidence(baseline)],
                EvidenceOmittedCount: 0));
        }
    }

    private static async Task AddMissingImplementationFindingAsync(
        IGraphStore store,
        CurrentFactRow current,
        ICollection<GrpcContractFindingRow> findings,
        CancellationToken ct)
    {
        var implementations =
            await store.ListAuditableInboundEdgesAsync(
                    current.Symbol.Id,
                    limit: 1,
                    EdgeKinds.ImplementsRpc,
                    ct)
                .ConfigureAwait(false);
        if (implementations.Count > 0)
        {
            return;
        }

        findings.Add(new GrpcContractFindingRow(
            "Grpc001",
            "diagnoses-contract",
            "rpc_without_implementation",
            "warning",
            "semantic",
            "No evidence-backed `implements-rpc` edge targets this RPC in the complete current scope.",
            current.Fact.SymbolCanonicalKey,
            ManagedSymbol: null,
            GeneratedRole: "server",
            BaselineProvenance:
                "not-applicable-current-state-rule",
            Details: new SortedDictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["stored_orientation"] = StoredOrientation,
                ["scope_completeness"] = "complete",
            },
            CurrentEvidence: [CurrentContractEvidence(current)],
            BaselineEvidence: [],
            EvidenceOmittedCount: 0));
    }

    private static GrpcContractFindingRow CreateSignatureMismatchFinding(
        CurrentFactRow current,
        SymbolHit managed,
        GrpcLinkFailure failure)
    {
        var rpc = current.Fact.Rpc
            ?? throw new InvalidOperationException(
                "A signature mismatch targeted a non-RPC contract.");
        return new GrpcContractFindingRow(
            "Grpc004",
            "diagnoses-contract",
            "generated_signature_mismatch",
            "error",
            "semantic",
            failure.Message,
            current.Fact.SymbolCanonicalKey,
            managed.CanonicalKey,
            failure.GeneratedRole,
            BaselineProvenance:
                "not-applicable-current-state-rule",
            Details: new SortedDictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["actual_signature"] =
                    Bound(managed.Signature ?? string.Empty, 512),
                ["client_streaming"] = Bool(rpc.ClientStreaming),
                ["input_type"] = rpc.InputType,
                ["output_type"] = rpc.OutputType,
                ["server_streaming"] = Bool(rpc.ServerStreaming),
            },
            CurrentEvidence:
            [
                CurrentContractEvidence(current),
                DeclarationEvidence(managed),
            ],
            BaselineEvidence: [],
            EvidenceOmittedCount: 0);
    }

    private static async Task<SymbolHit?> TryGetExactSymbolAsync(
        IGraphStore store,
        IReadOnlyList<SymbolKeyRow> keys,
        string? canonicalKey,
        CancellationToken ct)
    {
        if (canonicalKey is null) return null;
        var key = keys.SingleOrDefault(item =>
            string.Equals(
                item.CanonicalKey,
                canonicalKey,
                StringComparison.Ordinal));
        return key is null
            ? null
            : await store.GetSymbolByIdAsync(key.Id, ct)
                .ConfigureAwait(false);
    }

    private static GrpcToolEvidenceRow CurrentContractEvidence(
        CurrentFactRow row) =>
        new(
            row.Row.FileId,
            row.Row.FilePath,
            Math.Max(1, row.Symbol.StartLine),
            Math.Max(1, row.Symbol.StartCol),
            Math.Max(1, row.Symbol.EndLine),
            Math.Max(1, row.Symbol.EndCol),
            "semantic",
            "protobuf-descriptor",
            new SortedDictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["contract_kind"] =
                    row.Fact.Kind.ToString().ToLowerInvariant(),
                ["contract_status"] = "complete",
            },
            MetadataOmittedCount: 0,
            ObservedAtUnixMs: null);

    private static GrpcToolEvidenceRow BaselineEvidence(
        BaselineFactRow row) =>
        new(
            ProducingFileId: null,
            row.Row.FilePath,
            row.Row.StartLine,
            row.Row.StartColumn,
            row.Row.EndLine,
            row.Row.EndColumn,
            "semantic",
            "grpc-contract-baseline-v1",
            new SortedDictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["baseline_policy"] = BaselinePolicy,
                ["contract_kind"] =
                    row.Fact.Kind.ToString().ToLowerInvariant(),
            },
            MetadataOmittedCount: 0,
            row.Row.ObservedAtUnixMs);

    private static GrpcToolEvidenceRow DeclarationEvidence(
        SymbolHit symbol) =>
        new(
            ProducingFileId: null,
            symbol.FilePath,
            Math.Max(1, symbol.StartLine),
            Math.Max(1, symbol.StartCol),
            Math.Max(1, symbol.EndLine),
            Math.Max(1, symbol.EndCol),
            "semantic",
            "roslyn-declaration",
            new SortedDictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["canonical_key"] = symbol.CanonicalKey ?? string.Empty,
                ["kind"] = symbol.Kind,
            },
            MetadataOmittedCount: 0,
            ObservedAtUnixMs: null);

    private static GrpcToolEvidenceRow StoredEvidence(
        Core.Evidence evidence) =>
        new(
            evidence.ProducingFileId,
            evidence.Location.FilePath,
            evidence.Location.StartLine,
            evidence.Location.StartColumn,
            evidence.Location.EndLine,
            evidence.Location.EndColumn,
            Confidence(evidence.Confidence),
            evidence.Producer,
            evidence.Metadata,
            MetadataOmittedCount: 0,
            ObservedAtUnixMs: null);

    private static GrpcTraceScopeResult TraceRuntimeUnavailable(
        string scopeId,
        string scopeStatus,
        string query,
        GrpcLinkRuntimeState? state)
    {
        var failures = RuntimeFailures(state);
        return new GrpcTraceScopeResult(
            scopeId,
            scopeStatus,
            "partial",
            Partial: true,
            RetainedLastGood: state?.RetainedLastGood ?? false,
            SelectionStatus: "unknown",
            SelectionCanonicalKey: query,
            Rpcs: [],
            TotalRpcCount: 0,
            TotalClientCount: 0,
            TotalServerCount: 0,
            failures,
            TotalFailureCount:
                state is null
                    ? failures.Count
                    : Math.Max(failures.Count, state.FailureCount),
            Truncated: (state?.OmittedFailures ?? 0) > 0,
            OmittedCount: state?.OmittedFailures ?? 0,
            OmittedEvidenceCount: 0,
            LinkCoverage: state?.Coverage);
    }

    private static GrpcContractCheckScopeResult CheckRuntimeUnavailable(
        string scopeId,
        string scopeStatus,
        GrpcLinkRuntimeState? state)
    {
        var failures = RuntimeFailures(state);
        return new GrpcContractCheckScopeResult(
            scopeId,
            scopeStatus,
            "partial",
            Partial: true,
            RetainedLastGood: state?.RetainedLastGood ?? false,
            BaselinePolicy,
            TotalContractCount: 0,
            Findings: [],
            TotalFindingCount: 0,
            failures,
            TotalFailureCount:
                state is null
                    ? failures.Count
                    : Math.Max(failures.Count, state.FailureCount),
            Truncated: (state?.OmittedFailures ?? 0) > 0,
            OmittedCount: state?.OmittedFailures ?? 0,
            OmittedEvidenceCount: 0,
            LinkCoverage: state?.Coverage);
    }

    private static IReadOnlyList<GrpcToolFailureRow> RuntimeFailures(
        GrpcLinkRuntimeState? state)
    {
        if (state is null)
        {
            return
            [
                new GrpcToolFailureRow(
                    "linker",
                    "grpc-link-state-unavailable",
                    "The gRPC projection has not completed for this scope.",
                    null),
            ];
        }
        var rows = state.Failures
            .Select(failure => new GrpcToolFailureRow(
                "linker",
                failure.Code,
                Bound(failure.Message, 512),
                failure.SymbolCanonicalKey))
            .ToArray();
        return rows.Length > 0
            ? rows
            :
            [
                new GrpcToolFailureRow(
                    "linker",
                    "grpc-link-state-partial",
                    "The latest gRPC projection was incomplete; last-good data was retained.",
                    null),
            ];
    }

    private static string Confidence(Core.EvidenceConfidence value) =>
        value switch
        {
            Core.EvidenceConfidence.Inferred => "inferred",
            Core.EvidenceConfidence.Semantic => "semantic",
            Core.EvidenceConfidence.Exact => "exact",
            _ => "unknown",
        };

    private static string Bool(bool value) => value ? "true" : "false";

    private static int SaturatingAdd(int left, int right)
    {
        var total = (long)left + right;
        return total >= int.MaxValue
            ? int.MaxValue
            : (int)total;
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum
            ? value
            : value[..maximum];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record CurrentSnapshot(
        IReadOnlyList<SymbolKeyRow> SymbolKeys,
        IReadOnlyDictionary<string, CurrentFactRow> Facts);

    private sealed record CurrentFactRow(
        ProtoContractFact Fact,
        StoredAnnotationRow Row,
        SymbolHit Symbol);

    private sealed record BaselineFactRow(
        ProtoContractFact Fact,
        GrpcContractBaselineRow Row);

    private sealed record TraceSelection(
        string Status,
        string? CanonicalKey,
        IReadOnlyList<CurrentFactRow> Rpcs,
        string? FailureCode,
        string? FailureMessage)
    {
        public static TraceSelection None(
            string status,
            string? canonicalKey,
            string code,
            string message) =>
            new(status, canonicalKey, [], code, message);
    }

    private sealed record RelationSelection(
        IReadOnlyList<GrpcManagedRpcRelationRow> Rows,
        int TotalCountLowerBound,
        int OmittedCount,
        int OmittedEvidenceCount);

    private sealed record ContractSelection(
        IReadOnlyList<CurrentFactRow> Facts,
        GrpcToolFailureRow? Failure);
}
