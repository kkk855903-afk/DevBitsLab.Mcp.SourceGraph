using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>list_symbols_in_file</c> MCP tool. Pairs the resolved
/// file path with the list of symbols declared in it.
/// </summary>
public sealed record ListSymbolsInFileResult(
    [property: JsonPropertyName("file_path")] string FilePath,
    IReadOnlyList<ListSymbolsInFileRow> Symbols);

/// <summary>
/// One symbol declared in the file. The full file:line:col location is on the row so consumers
/// don't need to combine the per-result <see cref="ListSymbolsInFileResult.FilePath"/> with the
/// row's line — though they're guaranteed to point at the same file.
/// </summary>
public sealed record ListSymbolsInFileRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Name,
    string Fqn,
    string Kind,
    int Line,
    int Column,
    string? Signature,
    [property: JsonPropertyName("xml_summary")] string? XmlSummary);
