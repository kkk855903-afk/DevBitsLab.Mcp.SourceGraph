using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>find_data_bindings</c> MCP tool. <see cref="Note"/>
/// carries the soft-empty / "no filter supplied" diagnostic prose when present (null on the
/// happy path). <see cref="Bindings"/> is empty for soft-empty responses; consumers branch on
/// collection length rather than parsing the prose.
/// </summary>
public sealed record FindDataBindingsResult(
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    string? Note,
    IReadOnlyList<FindDataBindingsRow> Bindings);

/// <summary>
/// One <c>binds-path</c> edge row: source XAML element on the left, resolved viewmodel symbol
/// on the right, payload JSON projected verbatim from <c>edges.payload</c>. Source-side
/// canonical key is preserved so consumers can chain back to the originating element via
/// <c>find_definition</c> or <c>list_callees</c>; same for the target side.
/// </summary>
public sealed record FindDataBindingsRow(
    [property: JsonPropertyName("source_symbol_id")] long SourceSymbolId,
    [property: JsonPropertyName("source_fqn")] string SourceFqn,
    [property: JsonPropertyName("source_canonical_key")] string? SourceCanonicalKey,
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_canonical_key")] string? TargetCanonicalKey,
    [property: JsonPropertyName("target_is_unresolved")] bool TargetIsUnresolved,
    [property: JsonPropertyName("payload_json")] string? PayloadJson);
