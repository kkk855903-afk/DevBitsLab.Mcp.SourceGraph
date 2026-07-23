using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>find_by_annotation</c> MCP tool. Each hit carries the
/// matched symbol plus its annotations (the substantive data point the tool surfaces in prose).
/// </summary>
public sealed record FindByAnnotationResult(IReadOnlyList<FindByAnnotationHit> Hits);

/// <summary>
/// One annotated-symbol match. <see cref="Annotations"/> lists every annotation attached to the
/// symbol that's relevant to the rendering (the tool's annotation filter is by short name; the
/// list here is the full per-symbol annotation set so a downstream agent can see what's adjacent
/// to the matched annotation, not just the matched one).
/// </summary>
public sealed record FindByAnnotationHit(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    IReadOnlyList<FindByAnnotationEntry> Annotations);

/// <summary>
/// One annotation row — mirrors <see cref="DevBitsLab.Mcp.SourceGraph.Core.AnnotationRecord"/>'s
/// user-relevant fields. <see cref="Flavor"/> stays for polyglot scopes where two flavors might
/// share a short name; consumers can branch on it. <see cref="ArgsJson"/> is the raw arg payload
/// (already a JSON document) so an agent can pick out a specific value without us imposing a
/// schema on the args shape.
/// </summary>
public sealed record FindByAnnotationEntry(
    string Name,
    [property: JsonPropertyName("full_name")] string FullName,
    string Flavor,
    [property: JsonPropertyName("args_json")] string? ArgsJson);
