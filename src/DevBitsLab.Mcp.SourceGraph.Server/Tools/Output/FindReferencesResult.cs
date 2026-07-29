using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>find_references</c> MCP tool. Wraps the
/// resolved-target descriptor + the list of reference rows so agents that consume
/// <see cref="ModelContextProtocol.Protocol.CallToolResult.StructuredContent"/> have everything
/// they need to chain calls (e.g. follow up with <c>list_callers</c> against
/// <see cref="TargetSymbolId"/>) without re-parsing prose. The wrapping object satisfies the
/// <c>"type":"object"</c> root constraint imposed on every <c>outputSchema</c> by the MCP SDK.
/// </summary>
public sealed record FindReferencesResult(
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    [property: JsonPropertyName("occurrence_count")] int OccurrenceCount,
    [property: JsonPropertyName("relation_count")] int RelationCount,
    IReadOnlyList<FindReferenceHit> References);

/// <summary>
/// One reference site returned by <c>find_references</c>. <see cref="Kind"/> mirrors the
/// short label rendered in the prose ("call", "ref", "read", "write", …); the underlying
/// <see cref="DevBitsLab.Mcp.SourceGraph.Core.ReferenceKind"/> enum is mapped to a stable
/// kebab-token string here so the wire shape doesn't leak the storage enum's ordinals.
/// </summary>
public sealed record FindReferenceHit(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    string Kind,
    string Relation,
    string Confidence,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("is_generated")] bool IsGenerated);
