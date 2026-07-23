using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>list_generated_files</c> MCP tool. One row per
/// source-generated file tracked by the index, with the count of symbols emitted from that file.
/// </summary>
public sealed record ListGeneratedFilesResult(IReadOnlyList<ListGeneratedFilesRow> Files);

/// <summary>
/// One source-generated file row.
/// </summary>
public sealed record ListGeneratedFilesRow(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("symbol_count")] int SymbolCount);
