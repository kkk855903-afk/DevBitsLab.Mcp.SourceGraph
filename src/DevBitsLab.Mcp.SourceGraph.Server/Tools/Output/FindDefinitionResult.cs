using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>find_definition</c> MCP tool. The wire shape ships in
/// <see cref="ModelContextProtocol.Protocol.CallToolResult.StructuredContent"/> so agents can
/// consume the result programmatically without re-parsing prose.
///
/// The top-level type is a wrapping record (not a bare collection) because the MCP SDK enforces
/// <c>"type":"object"</c> at the root of any <c>outputSchema</c> declared via
/// <c>OutputSchemaType</c>. See <c>openspec/changes/tool-output-content-blocks/design.md</c>
/// Decision 3 for the rationale and the Risks section for the documented constraint.
///
/// Multi-word fields carry an explicit <see cref="JsonPropertyNameAttribute"/> so both the
/// source-gen serializer (which already honours the snake_case naming policy from
/// <see cref="ToolOutputJsonContext"/>) AND the SDK's <c>outputSchema</c> exporter publish the
/// same wire names — the schema exporter doesn't pick up the JsonContext's naming policy, so
/// without these attributes the schema declared on <c>tools/list</c> uses C#-default casing
/// (camelCase) while the actual structured payload uses snake_case. The attributes make both
/// surfaces converge on snake_case.
/// </summary>
public sealed record FindDefinitionResult(IReadOnlyList<FindDefinitionHit> Hits);

/// <summary>
/// One symbol match returned by <c>find_definition</c>. Mirrors the per-hit fields rendered in
/// the prose so the structured payload stays in lockstep with what the user reads. Fields that
/// the underlying <c>SymbolHit</c> may legitimately omit (signature on namespaces; XML summary
/// on symbols without doc comments) are nullable here so consumers can probe the JSON instead of
/// branching on sentinel strings.
/// </summary>
public sealed record FindDefinitionHit(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string Relation,
    string Confidence,
    string? Signature,
    [property: JsonPropertyName("xml_summary")] string? XmlSummary,
    SourceSnippet? Snippet = null);
