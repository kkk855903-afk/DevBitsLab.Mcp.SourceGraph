using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>search_symbols</c> MCP tool. Same hit shape as
/// <c>find_definition</c> — both surface a list of <see cref="SearchSymbolHit"/> rows whose
/// fields mirror the SymbolHit columns in storage. The wrapping object satisfies the
/// <c>"type":"object"</c> root constraint on every <c>outputSchema</c>.
/// </summary>
public sealed record SearchSymbolsResult(IReadOnlyList<SearchSymbolHit> Hits);

/// <summary>
/// One symbol match returned by <c>search_symbols</c>. Mirrors the per-row prose:
/// <see cref="Fqn"/>, <see cref="Kind"/>, and the file:line:col triple. <see cref="Signature"/>
/// is null on namespaces and other symbols without a signature; <see cref="XmlSummary"/> is null
/// on symbols without doc comments.
/// </summary>
public sealed record SearchSymbolHit(
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    string? Signature,
    [property: JsonPropertyName("xml_summary")] string? XmlSummary);
