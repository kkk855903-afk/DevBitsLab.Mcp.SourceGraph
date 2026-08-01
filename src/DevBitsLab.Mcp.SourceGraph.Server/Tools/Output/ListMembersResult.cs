using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>list_members</c> MCP tool. Pairs the resolved container
/// (class, struct, namespace, etc.) with the list of its direct members.
/// </summary>
public sealed record ListMembersResult(
    [property: JsonPropertyName("container_symbol_id")] long ContainerSymbolId,
    [property: JsonPropertyName("container_canonical_key")] string? ContainerCanonicalKey,
    [property: JsonPropertyName("container_fqn")] string ContainerFqn,
    [property: JsonPropertyName("container_kind")] string ContainerKind,
    IReadOnlyList<ListMembersRow> Members);

/// <summary>
/// One member of the container. <see cref="Signature"/> is the member's printable signature
/// (or null when the symbol's storage row carries no signature — e.g. nested types). The
/// renderer falls back to the FQN when null, so consumers should read <see cref="Fqn"/> as a
/// guaranteed identifier and treat the signature as "preferred display label when present".
/// </summary>
public sealed record ListMembersRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Name,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    string? Signature,
    [property: JsonPropertyName("xml_summary")] string? XmlSummary);
