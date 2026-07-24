using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>Typed object-root output for <c>trace_rpc</c>.</summary>
public sealed record TraceRpcResult(
    string Query,
    string Status,
    IReadOnlyList<GrpcTraceScopeResult> Scopes,
    [property: JsonPropertyName("total_scope_count")] int TotalScopeCount,
    [property: JsonPropertyName("total_rpc_count")] int TotalRpcCount,
    [property: JsonPropertyName("total_client_count")] int TotalClientCount,
    [property: JsonPropertyName("total_server_count")] int TotalServerCount,
    [property: JsonPropertyName("total_failure_count")] int TotalFailureCount,
    bool Partial,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_evidence_count")]
        int OmittedEvidenceCount);

public sealed record GrpcTraceScopeResult(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string Status,
    bool Partial,
    [property: JsonPropertyName("retained_last_good")]
        bool RetainedLastGood,
    [property: JsonPropertyName("selection_status")]
        string SelectionStatus,
    [property: JsonPropertyName("selection_canonical_key")]
        string? SelectionCanonicalKey,
    IReadOnlyList<GrpcRpcTraceRow> Rpcs,
    [property: JsonPropertyName("total_rpc_count")] int TotalRpcCount,
    [property: JsonPropertyName("total_client_count")] int TotalClientCount,
    [property: JsonPropertyName("total_server_count")] int TotalServerCount,
    IReadOnlyList<GrpcToolFailureRow> Failures,
    [property: JsonPropertyName("total_failure_count")] int TotalFailureCount,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_evidence_count")]
        int OmittedEvidenceCount);

public sealed record GrpcRpcTraceRow(
    [property: JsonPropertyName("canonical_key")] string CanonicalKey,
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("service_full_name")]
        string ServiceFullName,
    [property: JsonPropertyName("input_type")] string InputType,
    [property: JsonPropertyName("output_type")] string OutputType,
    [property: JsonPropertyName("client_streaming")] bool ClientStreaming,
    [property: JsonPropertyName("server_streaming")] bool ServerStreaming,
    [property: JsonPropertyName("stored_orientation")]
        string StoredOrientation,
    IReadOnlyList<GrpcToolEvidenceRow> Evidence,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount,
    IReadOnlyList<GrpcManagedRpcRelationRow> Clients,
    [property: JsonPropertyName("total_client_count")] int TotalClientCount,
    IReadOnlyList<GrpcManagedRpcRelationRow> Servers,
    [property: JsonPropertyName("total_server_count")] int TotalServerCount,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount);

public sealed record GrpcManagedRpcRelationRow(
    string Relation,
    [property: JsonPropertyName("managed_symbol")]
        string ManagedSymbol,
    [property: JsonPropertyName("managed_name")] string ManagedName,
    [property: JsonPropertyName("managed_kind")] string ManagedKind,
    [property: JsonPropertyName("stored_source")]
        string StoredSource,
    [property: JsonPropertyName("stored_target")]
        string StoredTarget,
    [property: JsonPropertyName("stored_orientation")]
        string StoredOrientation,
    [property: JsonPropertyName("traversal_direction")]
        string TraversalDirection,
    IReadOnlyList<GrpcToolEvidenceRow> Evidence,
    [property: JsonPropertyName("evidence_count_lower_bound")]
        int EvidenceCountLowerBound,
    [property: JsonPropertyName("evidence_truncated")]
        bool EvidenceTruncated,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount);

public sealed record GrpcToolEvidenceRow(
    [property: JsonPropertyName("producing_file_id")]
        long? ProducingFileId,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("start_column")] int StartColumn,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("metadata_omitted_count")]
        int MetadataOmittedCount,
    [property: JsonPropertyName("observed_at_unix_ms")]
        long? ObservedAtUnixMs);

public sealed record GrpcToolFailureRow(
    string Phase,
    string Code,
    string Message,
    [property: JsonPropertyName("symbol_canonical_key")]
        string? SymbolCanonicalKey);

/// <summary>Typed object-root output for <c>check_proto_contract</c>.</summary>
public sealed record CheckProtoContractResult(
    string? Symbol,
    string Status,
    IReadOnlyList<GrpcContractCheckScopeResult> Scopes,
    [property: JsonPropertyName("total_scope_count")] int TotalScopeCount,
    [property: JsonPropertyName("total_contract_count")]
        int TotalContractCount,
    [property: JsonPropertyName("total_finding_count")]
        int TotalFindingCount,
    [property: JsonPropertyName("total_failure_count")]
        int TotalFailureCount,
    bool Partial,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_evidence_count")]
        int OmittedEvidenceCount);

public sealed record GrpcContractCheckScopeResult(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string Status,
    bool Partial,
    [property: JsonPropertyName("retained_last_good")]
        bool RetainedLastGood,
    [property: JsonPropertyName("baseline_policy")]
        string BaselinePolicy,
    [property: JsonPropertyName("total_contract_count")]
        int TotalContractCount,
    IReadOnlyList<GrpcContractFindingRow> Findings,
    [property: JsonPropertyName("total_finding_count")]
        int TotalFindingCount,
    IReadOnlyList<GrpcToolFailureRow> Failures,
    [property: JsonPropertyName("total_failure_count")]
        int TotalFailureCount,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_evidence_count")]
        int OmittedEvidenceCount);

public sealed record GrpcContractFindingRow(
    [property: JsonPropertyName("rule_id")] string RuleId,
    string Category,
    string Severity,
    string Confidence,
    string Message,
    [property: JsonPropertyName("proto_symbol")] string ProtoSymbol,
    [property: JsonPropertyName("managed_symbol")] string? ManagedSymbol,
    [property: JsonPropertyName("generated_role")] string? GeneratedRole,
    [property: JsonPropertyName("baseline_provenance")]
        string? BaselineProvenance,
    IReadOnlyDictionary<string, string>? Details,
    [property: JsonPropertyName("current_evidence")]
        IReadOnlyList<GrpcToolEvidenceRow> CurrentEvidence,
    [property: JsonPropertyName("baseline_evidence")]
        IReadOnlyList<GrpcToolEvidenceRow> BaselineEvidence,
    [property: JsonPropertyName("evidence_omitted_count")]
        int EvidenceOmittedCount);
