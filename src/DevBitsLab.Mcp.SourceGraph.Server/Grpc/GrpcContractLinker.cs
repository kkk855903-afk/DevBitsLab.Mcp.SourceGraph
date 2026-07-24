using DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Core = DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Server.Grpc;

public enum GrpcLinkRuntimeStatus
{
    Complete,
    Partial,
}

public sealed record GrpcLinkFailure(
    string Code,
    string Message,
    string? SymbolCanonicalKey,
    string? ProtoCanonicalKey = null,
    string? GeneratedRole = null);

public sealed record GrpcLinkRuntimeState(
    GrpcLinkRuntimeStatus Status,
    int ProtoContracts,
    int ClientLinks,
    int ServerLinks,
    bool RetainedLastGood,
    int FailureCount,
    int OmittedFailures,
    IReadOnlyList<GrpcLinkFailure> Failures);

public sealed record GrpcLinkProjectionResult(GrpcLinkRuntimeState State);

/// <summary>
/// Rebuilds the evidence-backed C# ↔ protobuf gRPC projection. The linker deliberately consumes
/// only strict <c>proto-contract</c> payloads and Roslyn edges that already have occurrence-level
/// evidence. Generated-member names are never sufficient on their own: the generated service
/// container, descriptor field shape, request/response types, streaming signature, and a unique
/// proto contract must all agree before an edge is published. The graph does not currently retain
/// field-initializer constants, so descriptor association is explicitly published at semantic
/// rather than exact confidence.
/// </summary>
public sealed class GrpcContractLinker
{
    internal const string Producer = "grpc-contract-linker-v1";
    internal const int MaximumAnnotations = 10_000;
    internal const int MaximumSymbols = 100_000;
    internal const int MaximumCandidates = 10_000;
    internal const int MaximumInboundEdges = 1_000;

    private const int AnnotationPageSize = 1_000;
    private const int MaximumFailures = 64;
    private const int MaximumFailureMessageCharacters = 512;

    private readonly IGraphStore _store;

    public GrpcContractLinker(IGraphStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<GrpcLinkProjectionResult> RunAsync(
        bool sourceUniverseComplete,
        CancellationToken ct = default)
    {
        var failures = new FailureCollector();
        if (!sourceUniverseComplete)
        {
            failures.Add(
                "grpc-input-incomplete",
                "The managed/protobuf indexing pass was incomplete; persisted gRPC projection evidence was left unchanged.");
            return await PartialAsync(
                    failures,
                    protoContracts: 0,
                    ct)
                .ConfigureAwait(false);
        }

        try
        {
            var snapshot = await ReadSnapshotAsync(failures, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                return await PartialAsync(
                        failures,
                        protoContracts: 0,
                        ct)
                    .ConfigureAwait(false);
            }

            var candidate = await BuildCandidateAsync(
                    snapshot,
                    failures,
                    ct)
                .ConfigureAwait(false);
            if (candidate is null)
            {
                return await PartialAsync(
                        failures,
                        snapshot.Rpcs.Count,
                        ct)
                    .ConfigureAwait(false);
            }

            await EnsureBaselinesAsync(snapshot.Facts, ct)
                .ConfigureAwait(false);
            await PublishAsync(candidate.Edges, ct)
                .ConfigureAwait(false);
            return new GrpcLinkProjectionResult(
                new GrpcLinkRuntimeState(
                    GrpcLinkRuntimeStatus.Complete,
                    snapshot.Rpcs.Count,
                    candidate.ClientLinks,
                    candidate.ServerLinks,
                    RetainedLastGood: false,
                    failures.TotalCount,
                    failures.OmittedCount,
                    failures.Items));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(
                "grpc-projection-failed",
                $"The gRPC projection could not be rebuilt: {ex.Message}");
            return await PartialAsync(
                    failures,
                    protoContracts: 0,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<Snapshot?> ReadSnapshotAsync(
        FailureCollector failures,
        CancellationToken ct)
    {
        var rows = new List<StoredAnnotationRow>();
        long afterId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _store.ListAnnotationsByFlavorAsync(
                    ProtoContractAnnotations.Flavor,
                    afterId,
                    AnnotationPageSize,
                    ct)
                .ConfigureAwait(false);
            if (page.Count == 0) break;
            if (rows.Count + page.Count > MaximumAnnotations)
            {
                failures.Add(
                    "grpc-annotation-limit",
                    $"The proto-contract annotation universe exceeds the {MaximumAnnotations}-row limit.");
                return null;
            }
            foreach (var row in page)
            {
                if (row.AnnotationId <= afterId)
                {
                    failures.Add(
                        "grpc-annotation-order",
                        "The proto-contract annotation cursor did not advance.",
                        row.SymbolCanonicalKey);
                    return null;
                }
                rows.Add(row);
                afterId = row.AnnotationId;
            }
            if (page.Count < AnnotationPageSize) break;
        }

        var symbolKeys = await _store.GetAllSymbolKeysAsync(ct)
            .ConfigureAwait(false);
        if (symbolKeys.Count > MaximumSymbols)
        {
            failures.Add(
                "grpc-symbol-limit",
                $"The graph contains more than the {MaximumSymbols}-symbol projection limit.");
            return null;
        }

        var files = await _store.GetAllFilesAsync(ct).ConfigureAwait(false);
        if (files.Count > MaximumSymbols)
        {
            failures.Add(
                "grpc-file-limit",
                $"The graph contains more than the {MaximumSymbols}-file projection limit.");
            return null;
        }
        var protoFilePaths = files
            .Select(file => file.Path)
            .Where(path => string.Equals(
                Path.GetExtension(path),
                ".proto",
                StringComparison.OrdinalIgnoreCase))
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();
        if (protoFilePaths.Length > MaximumAnnotations)
        {
            failures.Add(
                "grpc-proto-file-limit",
                $"The indexed protobuf file universe exceeds the {MaximumAnnotations}-file limit.");
            return null;
        }
        var indexedFiles = files
            .Select(file => file.Path)
            .ToHashSet(PathComparer);
        var protoFiles = protoFilePaths.ToHashSet(PathComparer);

        var factsByKey = new Dictionary<string, ProtoFactRow>(
            StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(
                    row.Flavor,
                    ProtoContractAnnotations.Flavor,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "grpc-flavor-mismatch",
                    "A selected annotation did not have the exact proto-contract flavor.",
                    row.SymbolCanonicalKey);
                return null;
            }
            if (row.ArgsJson is null)
            {
                failures.Add(
                    "grpc-payload-missing",
                    "A proto-contract annotation had no payload.",
                    row.SymbolCanonicalKey);
                return null;
            }

            ProtoContractFact fact;
            try
            {
                fact = ProtoContractPayloadCodec.Decode(row.ArgsJson);
            }
            catch (ProtoContractPayloadException ex)
            {
                failures.Add(
                    "grpc-payload-malformed",
                    ex.Message,
                    row.SymbolCanonicalKey);
                return null;
            }
            if (fact.Status != ProtoContractStatus.Complete)
            {
                failures.Add(
                    "grpc-contract-partial",
                    "A protobuf contract fact is partial; prior gRPC links were retained.",
                    fact.SymbolCanonicalKey);
                return null;
            }
            if (!ValidateAnnotationIdentity(row, fact, failures))
            {
                return null;
            }
            if (!indexedFiles.Contains(row.FilePath)
                || !protoFiles.Contains(row.FilePath))
            {
                failures.Add(
                    "grpc-contract-file-missing",
                    "A protobuf contract annotation refers to a non-proto or unindexed file.",
                    fact.SymbolCanonicalKey);
                return null;
            }

            var symbol = await _store.GetSymbolByIdAsync(row.SymbolId, ct)
                .ConfigureAwait(false);
            if (symbol is null || !ValidateSymbolIdentity(row, fact, symbol, failures))
            {
                return null;
            }
            if (!factsByKey.TryAdd(
                    fact.SymbolCanonicalKey,
                    new ProtoFactRow(fact, row, symbol)))
            {
                failures.Add(
                    "grpc-contract-duplicate",
                    "A protobuf contract canonical key has more than one annotation.",
                    fact.SymbolCanonicalKey);
                return null;
            }
        }

        var protoDeclarationKeys = symbolKeys
            .Where(symbol => IsProtoContractCanonicalKey(symbol.CanonicalKey))
            .Select(symbol => symbol.CanonicalKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        foreach (var key in protoDeclarationKeys)
        {
            if (!factsByKey.ContainsKey(key))
            {
                failures.Add(
                    "grpc-contract-fact-missing",
                    "A protobuf declaration has no strict proto-contract fact.",
                    key);
                return null;
            }
        }

        var messages = factsByKey.Values
            .Where(row => row.Fact.Kind == ProtoContractKind.Message)
            .ToDictionary(
                row => row.Fact.FullName,
                row => row,
                StringComparer.Ordinal);
        var rpcs = factsByKey.Values
            .Where(row => row.Fact.Kind == ProtoContractKind.Rpc)
            .OrderBy(row => row.Fact.SymbolCanonicalKey, StringComparer.Ordinal)
            .ToArray();
        foreach (var rpc in rpcs)
        {
            if (rpc.Fact.Rpc is null
                || !messages.ContainsKey(rpc.Fact.Rpc.InputType)
                || !messages.ContainsKey(rpc.Fact.Rpc.OutputType))
            {
                failures.Add(
                    "grpc-rpc-message-missing",
                    "An RPC request or response message has no unique complete contract fact.",
                    rpc.Fact.SymbolCanonicalKey);
                return null;
            }
        }

        return new Snapshot(
            symbolKeys,
            messages,
            rpcs,
            factsByKey.Values
                .OrderBy(
                    fact => fact.Fact.SymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ToArray());
    }

    private async Task<CandidateProjection?> BuildCandidateAsync(
        Snapshot snapshot,
        FailureCollector failures,
        CancellationToken ct)
    {
        if (snapshot.Rpcs.Count == 0)
        {
            return new CandidateProjection([], 0, 0);
        }

        var requiredKeys = BuildRelevantGeneratedKeySet(snapshot.SymbolKeys);
        var relevantKeys = snapshot.SymbolKeys
            .Where(row => requiredKeys.Contains(row.CanonicalKey))
            .OrderBy(row => row.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        if (relevantKeys.Length > MaximumCandidates)
        {
            failures.Add(
                "grpc-candidate-limit",
                $"Generated gRPC candidates exceed the {MaximumCandidates}-symbol limit.");
            return null;
        }

        var symbols = new Dictionary<string, SymbolHit>(StringComparer.Ordinal);
        foreach (var key in relevantKeys)
        {
            ct.ThrowIfCancellationRequested();
            var hit = await _store.GetSymbolByIdAsync(key.Id, ct)
                .ConfigureAwait(false);
            if (hit is not null
                && string.Equals(
                    hit.CanonicalKey,
                    key.CanonicalKey,
                    StringComparison.Ordinal))
            {
                symbols[key.CanonicalKey] = hit;
            }
        }

        var methods = symbols.Values
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .Select(symbol => GeneratedMethod.TryCreate(symbol))
            .Where(method => method is not null)
            .Select(method => method!)
            .OrderBy(method => method.Symbol.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        if (methods.Length > MaximumCandidates)
        {
            failures.Add(
                "grpc-method-limit",
                $"Generated gRPC methods exceed the {MaximumCandidates}-candidate limit.");
            return null;
        }

        var facts = new List<DerivedEdge>();
        foreach (var method in methods)
        {
            ct.ThrowIfCancellationRequested();
            var possibleContracts = snapshot.Rpcs
                .Where(rpc => CouldNameMatch(method, rpc.Fact))
                .ToArray();
            if (possibleContracts.Length == 0)
            {
                continue;
            }
            if (!HasGeneratedContainerEvidence(method, symbols))
            {
                failures.Add(
                    "grpc-generated-evidence-missing",
                    "A possible gRPC member lacked the generated service container or __ServiceName evidence.",
                    method.Symbol.CanonicalKey);
                continue;
            }
            var descriptorAssociatedContracts = possibleContracts
                .Where(rpc => HasDescriptorContractAssociation(
                    method,
                    rpc,
                    snapshot.Messages,
                    symbols))
                .ToArray();
            var matchingContracts = possibleContracts
                .Where(rpc => MatchesContract(
                    method,
                    rpc,
                    snapshot.Messages,
                    symbols))
                .ToArray();
            if (matchingContracts.Length != 1)
            {
                failures.Add(
                    matchingContracts.Length == 0
                        ? "grpc-signature-mismatch"
                        : "grpc-contract-ambiguous",
                    matchingContracts.Length == 0
                        ? "The generated gRPC member's descriptor shape, request/response, or streaming signature did not match a proto RPC."
                        : "The generated gRPC member matched more than one proto RPC contract.",
                    method.Symbol.CanonicalKey,
                    matchingContracts.Length == 0
                        && descriptorAssociatedContracts.Length == 1
                            ? descriptorAssociatedContracts[0]
                                .Fact.SymbolCanonicalKey
                            : null,
                    matchingContracts.Length == 0
                        && descriptorAssociatedContracts.Length == 1
                            ? ToRoleToken(method.Role)
                            : null);
                continue;
            }

            var contract = matchingContracts[0];
            var rpc = contract.Fact.Rpc!;
            var requestShape = ToGeneratedTypeShape(
                snapshot.Messages[rpc.InputType].Fact);
            var responseShape = ToGeneratedTypeShape(
                snapshot.Messages[rpc.OutputType].Fact);
            var inboundKind = method.Role == GeneratedMethodRole.Client
                ? EdgeKinds.Calls
                : EdgeKinds.OverridesMember;
            var inbound = await _store.ListAuditableInboundEdgesAsync(
                    method.Symbol.Id,
                    MaximumInboundEdges + 1,
                    inboundKind,
                    ct)
                .ConfigureAwait(false);
            if (inbound.Count > MaximumInboundEdges)
            {
                failures.Add(
                    "grpc-inbound-limit",
                    $"A generated gRPC member has more than {MaximumInboundEdges} evidence-backed inbound edges.",
                    method.Symbol.CanonicalKey);
                return null;
            }

            foreach (var source in inbound
                .Select(edge => edge.Symbol)
                .Where(source => IsEligibleSource(
                    method,
                    source,
                    rpc,
                    requestShape,
                    responseShape))
                .OrderBy(source => source.CanonicalKey, StringComparer.Ordinal))
            {
                if (source.CanonicalKey is null) continue;
                var inboundEvidence = await _store.ListEdgeEvidenceAsync(
                        source.Id,
                        method.Symbol.Id,
                        inboundKind,
                        MaximumInboundEdges,
                        ct)
                    .ConfigureAwait(false);
                if (inboundEvidence.Count >= MaximumInboundEdges)
                {
                    failures.Add(
                        "grpc-inbound-evidence-limit",
                        $"One managed gRPC relationship has at least {MaximumInboundEdges} evidence occurrences; the candidate was retained rather than truncated.",
                        source.CanonicalKey);
                    return null;
                }

                var roslynEvidence = inboundEvidence
                    .Where(evidence =>
                        string.Equals(
                            evidence.Producer,
                            "roslyn",
                            StringComparison.Ordinal)
                        && PathsEqual(
                            evidence.Location.FilePath,
                            source.FilePath))
                    .OrderBy(
                        evidence => evidence.Location.FilePath,
                        PathComparer)
                    .ThenBy(evidence => evidence.Location.StartLine)
                    .ThenBy(evidence => evidence.Location.StartColumn)
                    .ThenBy(evidence => evidence.Location.EndLine)
                    .ThenBy(evidence => evidence.Location.EndColumn)
                    .ToArray();
                if (roslynEvidence.Length == 0)
                {
                    failures.Add(
                        "grpc-roslyn-evidence-missing",
                        "An inbound generated-member edge had no occurrence owned by the Roslyn indexer in the source method's file.",
                        source.CanonicalKey);
                    continue;
                }

                var derivedEdges = roslynEvidence
                    .SelectMany(evidence =>
                        CreateDerivedEdges(
                            method,
                            source,
                            contract,
                            requestShape,
                            responseShape,
                            evidence))
                    .ToArray();
                if (facts.Count + derivedEdges.Length > MaximumCandidates)
                {
                    failures.Add(
                        "grpc-evidence-limit",
                        $"The derived gRPC projection exceeds the {MaximumCandidates}-managed-evidence limit.");
                    return null;
                }
                facts.AddRange(derivedEdges);
            }
        }

        var logicalGroups = facts
            .GroupBy(
                fact => new
                {
                    fact.Fact.SourceCanonicalKey,
                    fact.Fact.TargetCanonicalKey,
                    fact.Fact.Kind,
                })
            .OrderBy(
                group => group.Key.SourceCanonicalKey,
                StringComparer.Ordinal)
            .ThenBy(
                group => group.Key.TargetCanonicalKey,
                StringComparer.Ordinal)
            .ThenBy(group => group.Key.Kind, StringComparer.Ordinal)
            .ToArray();
        if (facts.Count + logicalGroups.Length > MaximumCandidates)
        {
            failures.Add(
                "grpc-evidence-limit",
                $"The derived gRPC projection exceeds the {MaximumCandidates}-total-evidence limit.");
            return null;
        }

        var projection = new List<ProducerEdgeEvidenceFact>(
            facts.Count + logicalGroups.Length);
        foreach (var group in logicalGroups)
        {
            var ordered = group
                .OrderBy(
                    item => item.GeneratedMemberCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(
                    item => item.Fact.Evidence.Location.FilePath,
                    PathComparer)
                .ThenBy(item => item.Fact.Evidence.Location.StartLine)
                .ThenBy(item => item.Fact.Evidence.Location.StartColumn)
                .ToArray();
            projection.AddRange(ordered.Select(item => item.Fact));
            projection.Add(CreateProtoEvidence(ordered[0]));
        }

        var orderedProjection = projection
            .OrderBy(
                item => item.Evidence.Location.FilePath,
                PathComparer)
            .ThenBy(
                item => item.SourceCanonicalKey,
                StringComparer.Ordinal)
            .ThenBy(
                item => item.TargetCanonicalKey,
                StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Evidence.Location.StartLine)
            .ThenBy(item => item.Evidence.Location.StartColumn)
            .ToArray();
        return new CandidateProjection(
            orderedProjection,
            logicalGroups.Count(group =>
                group.Key.Kind == EdgeKinds.GrpcCalls),
            logicalGroups.Count(group =>
                group.Key.Kind == EdgeKinds.ImplementsRpc));
    }

    private async Task PublishAsync(
        IReadOnlyList<ProducerEdgeEvidenceFact> edges,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _store.ReplaceProducerEdgeEvidenceProjectionAsync(
                Producer,
                edges,
                ct)
            .ConfigureAwait(false);
    }

    private async Task EnsureBaselinesAsync(
        IReadOnlyList<ProtoFactRow> facts,
        CancellationToken ct)
    {
        var baselines = facts
            .OrderBy(
                fact => fact.Fact.SymbolCanonicalKey,
                StringComparer.Ordinal)
            .Select(fact => new GrpcContractBaselineFact(
                fact.Fact.SymbolCanonicalKey,
                fact.Row.ArgsJson
                    ?? throw new InvalidOperationException(
                        "A validated proto contract had no payload."),
                fact.Row.FilePath,
                Math.Max(1, fact.Symbol.StartLine),
                Math.Max(1, fact.Symbol.StartCol),
                Math.Max(1, fact.Symbol.EndLine),
                Math.Max(1, fact.Symbol.EndCol)))
            .ToArray();
        await _store.EnsureGrpcContractBaselinesAsync(baselines, ct)
            .ConfigureAwait(false);
    }

    private static bool ValidateAnnotationIdentity(
        StoredAnnotationRow row,
        ProtoContractFact fact,
        FailureCollector failures)
    {
        var expected = fact.Kind switch
        {
            ProtoContractKind.Message =>
                (Name: "ProtoMessageContract",
                    FullName: "protobuf.contract.v1.message"),
            ProtoContractKind.Field =>
                (Name: "ProtoFieldContract",
                    FullName: "protobuf.contract.v1.field"),
            ProtoContractKind.Rpc =>
                (Name: "ProtoRpcContract",
                    FullName: "protobuf.contract.v1.rpc"),
            _ => throw new ArgumentOutOfRangeException(nameof(fact)),
        };
        if (!string.Equals(
                row.SymbolCanonicalKey,
                fact.SymbolCanonicalKey,
                StringComparison.Ordinal)
            || !string.Equals(row.Name, expected.Name, StringComparison.Ordinal)
            || !string.Equals(
                row.FullName,
                expected.FullName,
                StringComparison.Ordinal)
            || row.AttributeSymbolId is not null)
        {
            failures.Add(
                "grpc-annotation-identity",
                "A proto-contract annotation row does not match its strict payload identity.",
                fact.SymbolCanonicalKey);
            return false;
        }
        return true;
    }

    private static bool ValidateSymbolIdentity(
        StoredAnnotationRow row,
        ProtoContractFact fact,
        SymbolHit symbol,
        FailureCollector failures)
    {
        var expectedKind = fact.Kind switch
        {
            ProtoContractKind.Message => SymbolKinds.Message,
            ProtoContractKind.Field => SymbolKinds.ProtoField,
            ProtoContractKind.Rpc => SymbolKinds.Rpc,
            _ => throw new ArgumentOutOfRangeException(nameof(fact)),
        };
        if (!string.Equals(
                symbol.CanonicalKey,
                fact.SymbolCanonicalKey,
                StringComparison.Ordinal)
            || !string.Equals(symbol.Fqn, fact.FullName, StringComparison.Ordinal)
            || !string.Equals(symbol.Kind, expectedKind, StringComparison.Ordinal)
            || !PathsEqual(symbol.FilePath, row.FilePath))
        {
            failures.Add(
                "grpc-symbol-identity",
                "A protobuf declaration row does not match its strict contract fact.",
                fact.SymbolCanonicalKey);
            return false;
        }
        return true;
    }

    private static bool HasGeneratedContainerEvidence(
        GeneratedMethod method,
        IReadOnlyDictionary<string, SymbolHit> symbols)
    {
        if (!symbols.TryGetValue(method.OuterTypeKey, out var outer)
            || outer.Kind != SymbolKinds.Class
            || !symbols.TryGetValue(method.ContainerTypeKey, out var container)
            || container.Kind != SymbolKinds.Class
            || !symbols.TryGetValue(method.ServiceNameFieldKey, out var serviceName)
            || serviceName.Kind != SymbolKinds.Field)
        {
            return false;
        }
        return HasModifiers(serviceName.Modifiers, "static", "readonly")
            && SignatureEndsWith(
                serviceName.Signature,
                "string __ServiceName");
    }

    private static bool CouldNameMatch(
        GeneratedMethod method,
        ProtoContractFact fact)
    {
        var rpc = fact.Rpc;
        if (rpc is null
            || !string.Equals(
                LastSegment(rpc.ServiceFullName),
                method.ServiceName,
                StringComparison.Ordinal))
        {
            return false;
        }
        var rpcName = LastSegment(fact.FullName);
        if (method.Role == GeneratedMethodRole.Server)
        {
            return string.Equals(
                method.MethodName,
                rpcName,
                StringComparison.Ordinal);
        }
        return string.Equals(
                method.MethodName,
                rpcName,
                StringComparison.Ordinal)
            || string.Equals(
                method.MethodName,
                rpcName + "Async",
                StringComparison.Ordinal);
    }

    private static bool MatchesContract(
        GeneratedMethod method,
        ProtoFactRow contract,
        IReadOnlyDictionary<string, ProtoFactRow> messages,
        IReadOnlyDictionary<string, SymbolHit> symbols)
    {
        var rpc = contract.Fact.Rpc;
        if (rpc is null
            || !messages.TryGetValue(rpc.InputType, out var request)
            || !messages.TryGetValue(rpc.OutputType, out var response))
        {
            return false;
        }
        var rpcName = LastSegment(contract.Fact.FullName);
        var methodFieldKey =
            $"csharp:F:{method.OuterTypeName}.__Method_{rpcName}";
        if (!symbols.TryGetValue(methodFieldKey, out var descriptor)
            || descriptor.Kind != SymbolKinds.Field
            || !HasModifiers(descriptor.Modifiers, "static", "readonly"))
        {
            return false;
        }

        var requestShape = ToGeneratedTypeShape(request.Fact);
        var responseShape = ToGeneratedTypeShape(response.Fact);
        if (!SignatureEndsWith(
                descriptor.Signature,
                $"Method<{requestShape}, {responseShape}> __Method_{rpcName}"))
        {
            return false;
        }
        if (!string.Equals(
                method.MethodName,
                rpcName,
                StringComparison.Ordinal)
            && !(method.Role == GeneratedMethodRole.Client
                && !rpc.ClientStreaming
                && !rpc.ServerStreaming
                && string.Equals(
                    method.MethodName,
                    rpcName + "Async",
                    StringComparison.Ordinal)))
        {
            return false;
        }

        return method.Role == GeneratedMethodRole.Client
            ? MatchesClientSignature(
                method,
                rpc,
                requestShape,
                responseShape,
                isUnaryAsyncOverload: string.Equals(
                    method.MethodName,
                    rpcName + "Async",
                    StringComparison.Ordinal))
            : MatchesServerBaseSignature(
                method.Symbol,
                rpc,
                requestShape,
                responseShape,
                requireOverride: false);
    }

    private static bool HasDescriptorContractAssociation(
        GeneratedMethod method,
        ProtoFactRow contract,
        IReadOnlyDictionary<string, ProtoFactRow> messages,
        IReadOnlyDictionary<string, SymbolHit> symbols)
    {
        var rpc = contract.Fact.Rpc;
        if (rpc is null
            || !messages.TryGetValue(rpc.InputType, out var request)
            || !messages.TryGetValue(rpc.OutputType, out var response))
        {
            return false;
        }
        var rpcName = LastSegment(contract.Fact.FullName);
        var methodFieldKey =
            $"csharp:F:{method.OuterTypeName}.__Method_{rpcName}";
        if (!symbols.TryGetValue(methodFieldKey, out var descriptor)
            || descriptor.Kind != SymbolKinds.Field
            || !HasModifiers(descriptor.Modifiers, "static", "readonly"))
        {
            return false;
        }
        return SignatureEndsWith(
            descriptor.Signature,
            $"Method<{ToGeneratedTypeShape(request.Fact)}, {ToGeneratedTypeShape(response.Fact)}> __Method_{rpcName}");
    }

    private static bool MatchesClientSignature(
        GeneratedMethod method,
        ProtoRpcContract rpc,
        string requestShape,
        string responseShape,
        bool isUnaryAsyncOverload)
    {
        if (!HasModifiers(method.Symbol.Modifiers, "virtual"))
        {
            return false;
        }
        var parameters = method.ParameterTypes;
        if (!rpc.ClientStreaming
            && (parameters.Count == 0
                || !TypeMatches(parameters[0], requestShape)
                || !IsClientCallTail(parameters.Skip(1).ToArray())))
        {
            return false;
        }
        if (rpc.ClientStreaming && !IsClientCallTail(parameters))
        {
            return false;
        }

        var expectedReturn = (rpc.ClientStreaming, rpc.ServerStreaming) switch
        {
            (false, false) when isUnaryAsyncOverload =>
                $"AsyncUnaryCall<{responseShape}>",
            (false, false) => responseShape,
            (false, true) => $"AsyncServerStreamingCall<{responseShape}>",
            (true, false) =>
                $"AsyncClientStreamingCall<{requestShape}, {responseShape}>",
            (true, true) =>
                $"AsyncDuplexStreamingCall<{requestShape}, {responseShape}>",
        };
        return ReturnTypeMatches(
            method.Symbol.Signature,
            method.MethodName,
            expectedReturn);
    }

    private static bool MatchesServerBaseSignature(
        SymbolHit symbol,
        ProtoRpcContract rpc,
        string requestShape,
        string responseShape,
        bool requireOverride)
    {
        if (requireOverride)
        {
            if (!HasModifiers(symbol.Modifiers, "override")) return false;
        }
        else if (!HasAnyModifier(symbol.Modifiers, "virtual", "abstract"))
        {
            return false;
        }
        if (symbol.CanonicalKey is null
            || !TryParseMethodKey(
                symbol.CanonicalKey,
                out _,
                out var methodName,
                out var parameters))
        {
            return false;
        }

        var expectedParameters = (rpc.ClientStreaming, rpc.ServerStreaming)
            switch
            {
                (false, false) =>
                new[] { requestShape, "Grpc.Core.ServerCallContext" },
                (false, true) =>
                [
                    requestShape,
                    $"Grpc.Core.IServerStreamWriter{{{responseShape}}}",
                    "Grpc.Core.ServerCallContext",
                ],
                (true, false) =>
                [
                    $"Grpc.Core.IAsyncStreamReader{{{requestShape}}}",
                    "Grpc.Core.ServerCallContext",
                ],
                (true, true) =>
                [
                    $"Grpc.Core.IAsyncStreamReader{{{requestShape}}}",
                    $"Grpc.Core.IServerStreamWriter{{{responseShape}}}",
                    "Grpc.Core.ServerCallContext",
                ],
            };
        if (!ParametersMatch(parameters, expectedParameters))
        {
            return false;
        }

        var expectedReturn = rpc.ServerStreaming
            ? "Task"
            : $"Task<{responseShape}>";
        return ReturnTypeMatches(
            symbol.Signature,
            methodName,
            expectedReturn);
    }

    private static bool IsEligibleSource(
        GeneratedMethod method,
        SymbolHit source,
        ProtoRpcContract rpc,
        string requestShape,
        string responseShape)
    {
        if (source.Kind != SymbolKinds.Method
            || source.CanonicalKey is null
            || source.CanonicalKey.StartsWith(
                $"csharp:M:{method.OuterTypeName}.",
                StringComparison.Ordinal))
        {
            return false;
        }
        if (method.Role == GeneratedMethodRole.Client)
        {
            return true;
        }

        return MatchesServerBaseSignature(
            source,
            rpc,
            requestShape,
            responseShape,
            requireOverride: true);
    }

    private static IReadOnlyList<DerivedEdge> CreateDerivedEdges(
        GeneratedMethod method,
        SymbolHit source,
        ProtoFactRow contract,
        string requestShape,
        string responseShape,
        Core.Evidence upstreamEvidence)
    {
        var rpc = contract.Fact.Rpc
            ?? throw new InvalidOperationException(
                "A selected gRPC contract had no RPC variant.");
        var relation = method.Role == GeneratedMethodRole.Client
            ? EdgeKinds.GrpcCalls
            : EdgeKinds.ImplementsRpc;
        var metadata = new SortedDictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["client_streaming"] = rpc.ClientStreaming ? "true" : "false",
            ["descriptor_proof"] = "structural-signature-only",
            ["generated_member"] = method.Symbol.CanonicalKey!,
            ["input_type"] = rpc.InputType,
            ["match"] = method.Role == GeneratedMethodRole.Client
                ? "roslyn-call-to-generated-client"
                : "roslyn-override-to-generated-base",
            ["output_type"] = rpc.OutputType,
            ["request_shape"] = requestShape,
            ["response_shape"] = responseShape,
            ["rpc"] = contract.Fact.FullName,
            ["server_streaming"] = rpc.ServerStreaming ? "true" : "false",
            ["service"] = rpc.ServiceFullName,
        };
        var evidenceMetadata = new SortedDictionary<string, string>(
            metadata,
            StringComparer.Ordinal)
        {
            ["evidence_role"] = method.Role == GeneratedMethodRole.Client
                ? "managed-call"
                : "managed-override",
            ["upstream_confidence"] = ToConfidenceToken(
                upstreamEvidence.Confidence),
            ["upstream_producer"] = upstreamEvidence.Producer,
        };
        var auditEdge = new DerivedEdge(
            new ProducerEdgeEvidenceFact(
                source.CanonicalKey!,
                contract.Fact.SymbolCanonicalKey,
                relation,
                metadata,
                new FileEvidenceFact(
                    upstreamEvidence.Location,
                    Core.EvidenceConfidence.Semantic,
                    Producer,
                    evidenceMetadata)),
            method.Symbol.CanonicalKey!,
            contract);
        if (method.Role == GeneratedMethodRole.Client)
        {
            return [auditEdge];
        }

        var dispatchMetadata = new SortedDictionary<string, string>(
            metadata,
            StringComparer.Ordinal)
        {
            ["match"] = "proto-dispatch-to-managed-override",
        };
        var dispatchEvidenceMetadata =
            new SortedDictionary<string, string>(
                evidenceMetadata,
                StringComparer.Ordinal)
            {
                ["match"] = "proto-dispatch-to-managed-override",
            };
        var dispatchEdge = new DerivedEdge(
            new ProducerEdgeEvidenceFact(
                contract.Fact.SymbolCanonicalKey,
                source.CanonicalKey!,
                EdgeKinds.RpcDispatchesTo,
                dispatchMetadata,
                new FileEvidenceFact(
                    upstreamEvidence.Location,
                    Core.EvidenceConfidence.Semantic,
                    Producer,
                    dispatchEvidenceMetadata)),
            method.Symbol.CanonicalKey!,
            contract);
        return [auditEdge, dispatchEdge];
    }

    private static ProducerEdgeEvidenceFact CreateProtoEvidence(
        DerivedEdge representative)
    {
        var evidenceMetadata = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        if (representative.Fact.Metadata is not null)
        {
            foreach (var pair in representative.Fact.Metadata)
            {
                evidenceMetadata[pair.Key] = pair.Value;
            }
        }
        evidenceMetadata["evidence_role"] = "proto-contract";

        var target = representative.Contract.Symbol;
        var location = new Core.SourceLocation(
            representative.Contract.Row.FilePath,
            Math.Max(1, target.StartLine),
            Math.Max(1, target.StartCol),
            Math.Max(1, target.EndLine),
            Math.Max(1, target.EndCol));
        return new ProducerEdgeEvidenceFact(
            representative.Fact.SourceCanonicalKey,
            representative.Fact.TargetCanonicalKey,
            representative.Fact.Kind,
            representative.Fact.Metadata,
            new FileEvidenceFact(
                location,
                Core.EvidenceConfidence.Semantic,
                Producer,
                evidenceMetadata));
    }

    private static string ToConfidenceToken(
        Core.EvidenceConfidence confidence) =>
        confidence switch
        {
            Core.EvidenceConfidence.Inferred => "inferred",
            Core.EvidenceConfidence.Semantic => "semantic",
            Core.EvidenceConfidence.Exact => "exact",
            _ => throw new ArgumentOutOfRangeException(nameof(confidence)),
        };

    private static string ToRoleToken(GeneratedMethodRole role) =>
        role switch
        {
            GeneratedMethodRole.Client => "client",
            GeneratedMethodRole.Server => "server",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private static bool IsClientCallTail(
        IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1
            && TypeMatches(parameters[0], "Grpc.Core.CallOptions"))
        {
            return true;
        }
        return parameters.Count == 3
            && TypeMatches(parameters[0], "Grpc.Core.Metadata")
            && TypeMatches(
                parameters[1],
                "System.Nullable{System.DateTime}")
            && TypeMatches(
                parameters[2],
                "System.Threading.CancellationToken");
    }

    private static bool ParametersMatch(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected)
    {
        if (actual.Count != expected.Count) return false;
        for (var index = 0; index < actual.Count; index++)
        {
            if (!TypeMatches(actual[index], expected[index])) return false;
        }
        return true;
    }

    private static bool TypeMatches(string actual, string expectedShape)
    {
        var normalized = actual.EndsWith("@", StringComparison.Ordinal)
            ? actual[..^1]
            : actual;
        return string.Equals(
                normalized,
                expectedShape,
                StringComparison.Ordinal)
            || normalized.EndsWith(
                "." + expectedShape,
                StringComparison.Ordinal);
    }

    private static bool ReturnTypeMatches(
        string? signature,
        string methodName,
        string expectedType)
    {
        if (signature is null) return false;
        var marker = " " + methodName + "(";
        var markerIndex = signature.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0) return false;
        var prefix = signature[..markerIndex].TrimEnd();
        return string.Equals(prefix, expectedType, StringComparison.Ordinal)
            || prefix.EndsWith(
                " " + expectedType,
                StringComparison.Ordinal);
    }

    private static bool SignatureEndsWith(
        string? signature,
        string suffix) =>
        signature is not null
        && (string.Equals(signature, suffix, StringComparison.Ordinal)
            || signature.EndsWith(
                " " + suffix,
                StringComparison.Ordinal));

    private static bool HasModifiers(
        string? modifiers,
        params string[] expected) =>
        expected.All(value => HasAnyModifier(modifiers, value));

    private static bool HasAnyModifier(
        string? modifiers,
        params string[] expected)
    {
        if (modifiers is null) return false;
        var values = modifiers.Split(',');
        return expected.Any(value =>
            values.Contains(value, StringComparer.Ordinal));
    }

    private static string ToGeneratedTypeShape(ProtoContractFact message)
    {
        var relative = message.Package.Length == 0
            ? message.FullName
            : message.FullName[(message.Package.Length + 1)..];
        var segments = relative.Split('.');
        return segments.Length == 1
            ? segments[0]
            : segments[0]
                + ".Types."
                + string.Join(".Types.", segments.Skip(1));
    }

    private static HashSet<string> BuildRelevantGeneratedKeySet(
        IReadOnlyList<SymbolKeyRow> symbolKeys)
    {
        var available = symbolKeys
            .Select(row => row.CanonicalKey)
            .ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in symbolKeys)
        {
            var key = row.CanonicalKey;
            if (key.StartsWith("csharp:F:", StringComparison.Ordinal)
                && (key.EndsWith(".__ServiceName", StringComparison.Ordinal)
                    || key.Contains(".__Method_", StringComparison.Ordinal)))
            {
                result.Add(key);
                continue;
            }
            if (!key.StartsWith("csharp:M:", StringComparison.Ordinal)
                || !TryParseMethodKey(
                    key,
                    out var container,
                    out _,
                    out _))
            {
                continue;
            }
            var separator = container.LastIndexOf('.');
            if (separator <= 0) continue;
            var inner = container[(separator + 1)..];
            if (!inner.EndsWith("Client", StringComparison.Ordinal)
                && !inner.EndsWith("Base", StringComparison.Ordinal))
            {
                continue;
            }
            var outer = container[..separator];
            result.Add(key);
            result.Add("csharp:T:" + container);
            result.Add("csharp:T:" + outer);
            result.Add("csharp:F:" + outer + ".__ServiceName");
        }
        result.IntersectWith(available);
        return result;
    }

    private static bool IsProtoContractCanonicalKey(string key) =>
        key.StartsWith("proto:M:", StringComparison.Ordinal)
        || key.StartsWith("proto:F:", StringComparison.Ordinal)
        || key.StartsWith("proto:R:", StringComparison.Ordinal);

    private static string LastSegment(string value)
    {
        var separator = value.LastIndexOf('.');
        return separator < 0 ? value : value[(separator + 1)..];
    }

    private static bool TryParseMethodKey(
        string canonicalKey,
        out string containerName,
        out string methodName,
        out IReadOnlyList<string> parameterTypes)
    {
        containerName = string.Empty;
        methodName = string.Empty;
        parameterTypes = Array.Empty<string>();
        const string prefix = "csharp:M:";
        if (!canonicalKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var body = canonicalKey[prefix.Length..];
        var parameterStart = body.IndexOf('(');
        var memberPart = parameterStart < 0
            ? body
            : body[..parameterStart];
        var separator = memberPart.LastIndexOf('.');
        if (separator <= 0 || separator == memberPart.Length - 1)
        {
            return false;
        }
        containerName = memberPart[..separator];
        methodName = memberPart[(separator + 1)..];
        if (parameterStart < 0)
        {
            return true;
        }
        if (!body.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }
        var payload = body[(parameterStart + 1)..^1];
        parameterTypes = SplitDocumentationParameters(payload);
        return true;
    }

    private static IReadOnlyList<string> SplitDocumentationParameters(
        string payload)
    {
        if (payload.Length == 0) return Array.Empty<string>();
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < payload.Length; index++)
        {
            switch (payload[index])
            {
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(payload[start..index]);
                    start = index + 1;
                    break;
            }
        }
        result.Add(payload[start..]);
        return result;
    }

    private static bool PathsEqual(string left, string right) =>
        PathComparer.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private async Task<GrpcLinkProjectionResult> PartialAsync(
        FailureCollector failures,
        int protoContracts,
        CancellationToken ct)
    {
        var retainedLastGood = false;
        try
        {
            retainedLastGood = await _store.HasEdgeEvidenceByProducerAsync(
                    Producer,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(
                "grpc-last-good-probe-failed",
                $"Persisted gRPC projection evidence could not be checked: {ex.Message}");
        }

        return new GrpcLinkProjectionResult(
            new GrpcLinkRuntimeState(
                GrpcLinkRuntimeStatus.Partial,
                protoContracts,
                ClientLinks: 0,
                ServerLinks: 0,
                RetainedLastGood: retainedLastGood,
                failures.TotalCount,
                failures.OmittedCount,
                failures.Items));
    }

    private sealed record Snapshot(
        IReadOnlyList<SymbolKeyRow> SymbolKeys,
        IReadOnlyDictionary<string, ProtoFactRow> Messages,
        IReadOnlyList<ProtoFactRow> Rpcs,
        IReadOnlyList<ProtoFactRow> Facts);

    private sealed record ProtoFactRow(
        ProtoContractFact Fact,
        StoredAnnotationRow Row,
        SymbolHit Symbol);

    private sealed record DerivedEdge(
        ProducerEdgeEvidenceFact Fact,
        string GeneratedMemberCanonicalKey,
        ProtoFactRow Contract);

    private sealed record CandidateProjection(
        IReadOnlyList<ProducerEdgeEvidenceFact> Edges,
        int ClientLinks,
        int ServerLinks);

    private enum GeneratedMethodRole
    {
        Client,
        Server,
    }

    private sealed record GeneratedMethod(
        SymbolHit Symbol,
        GeneratedMethodRole Role,
        string OuterTypeName,
        string OuterTypeKey,
        string ContainerTypeKey,
        string ServiceNameFieldKey,
        string ServiceName,
        string MethodName,
        IReadOnlyList<string> ParameterTypes)
    {
        public static GeneratedMethod? TryCreate(SymbolHit symbol)
        {
            if (symbol.CanonicalKey is null
                || !TryParseMethodKey(
                    symbol.CanonicalKey,
                    out var containerName,
                    out var methodName,
                    out var parameters))
            {
                return null;
            }
            var separator = containerName.LastIndexOf('.');
            if (separator <= 0) return null;
            var innerName = containerName[(separator + 1)..];
            GeneratedMethodRole role;
            string serviceName;
            if (innerName.EndsWith("Client", StringComparison.Ordinal))
            {
                role = GeneratedMethodRole.Client;
                serviceName = innerName[..^"Client".Length];
            }
            else if (innerName.EndsWith("Base", StringComparison.Ordinal))
            {
                role = GeneratedMethodRole.Server;
                serviceName = innerName[..^"Base".Length];
            }
            else
            {
                return null;
            }
            var outerName = containerName[..separator];
            if (!string.Equals(
                    LastSegment(outerName),
                    serviceName,
                    StringComparison.Ordinal))
            {
                return null;
            }
            return new GeneratedMethod(
                symbol,
                role,
                outerName,
                "csharp:T:" + outerName,
                "csharp:T:" + containerName,
                "csharp:F:" + outerName + ".__ServiceName",
                serviceName,
                methodName,
                parameters);
        }
    }

    private sealed class FailureCollector
    {
        private readonly List<GrpcLinkFailure> _items = [];

        public int TotalCount { get; private set; }
        public int OmittedCount => TotalCount - _items.Count;
        public IReadOnlyList<GrpcLinkFailure> Items => _items;

        public void Add(
            string code,
            string message,
            string? symbolCanonicalKey = null,
            string? protoCanonicalKey = null,
            string? generatedRole = null)
        {
            TotalCount++;
            if (_items.Count >= MaximumFailures) return;
            var safeMessage = message.Length <= MaximumFailureMessageCharacters
                ? message
                : message[..MaximumFailureMessageCharacters];
            _items.Add(new GrpcLinkFailure(
                code,
                safeMessage,
                symbolCanonicalKey,
                protoCanonicalKey,
                generatedRole));
        }
    }
}
