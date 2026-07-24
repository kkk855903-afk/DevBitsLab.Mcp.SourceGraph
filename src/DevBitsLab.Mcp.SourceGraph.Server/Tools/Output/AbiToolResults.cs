using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>Typed, multi-scope object-root output for <c>compare_struct</c>.</summary>
public sealed record CompareStructResult(
    string Managed,
    string Native,
    string Status,
    string Compatibility,
    [property: JsonPropertyName("nested_mapping_count")] int NestedMappingCount,
    IReadOnlyList<AbiScopeComparisonResult> Scopes,
    [property: JsonPropertyName("total_scope_count")] int TotalScopeCount,
    [property: JsonPropertyName("total_check_count")] int TotalCheckCount,
    [property: JsonPropertyName("total_finding_count")] int TotalFindingCount,
    bool Partial,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("omitted_check_count")] int OmittedCheckCount,
    [property: JsonPropertyName("omitted_reason_count")] int OmittedReasonCount,
    [property: JsonPropertyName("omitted_evidence_count")] int OmittedEvidenceCount,
    [property: JsonPropertyName("omitted_metadata_count")] int OmittedMetadataCount,
    [property: JsonPropertyName("omitted_character_count")] int OmittedCharacterCount);
