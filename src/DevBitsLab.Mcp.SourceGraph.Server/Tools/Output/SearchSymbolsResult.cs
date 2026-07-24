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
/// One persisted declaration matched by <c>search_symbols</c>. The query may be fuzzy, but the
/// returned graph identity, declaration range, <see cref="Relation"/>, and
/// <see cref="Confidence"/> describe the exact stored definition rather than a guessed code
/// relationship. <see cref="CanonicalKey"/> is null only for legacy/plugin symbols that did not
/// publish one.
/// </summary>
public sealed record SearchSymbolHit(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    string Relation,
    string Confidence,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("start_column")] int StartColumn,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string? Signature,
    [property: JsonPropertyName("xml_summary")] string? XmlSummary);
