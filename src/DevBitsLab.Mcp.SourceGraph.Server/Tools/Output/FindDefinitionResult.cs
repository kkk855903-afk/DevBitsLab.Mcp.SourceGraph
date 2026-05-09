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
    string Fqn,
    string Kind,
    string FilePath,
    int Line,
    int Column,
    string? Signature,
    string? XmlSummary);
