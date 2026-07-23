using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>find_implementations</c> MCP tool. Pairs the resolved
/// interface-member target with the list of concrete implementations satisfying it.
/// </summary>
public sealed record FindImplementationsResult(
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    IReadOnlyList<FindImplementationsRow> Implementations);

/// <summary>
/// One concrete implementation of the resolved interface member.
/// </summary>
public sealed record FindImplementationsRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    string? Signature);
