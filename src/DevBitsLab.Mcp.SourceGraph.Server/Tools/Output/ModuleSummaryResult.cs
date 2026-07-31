using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>module_summary</c> MCP tool. <see cref="Module"/> echoes
/// the user-supplied namespace-or-path probe verbatim (no normalisation) so consumers can
/// correlate with their own input.
/// </summary>
public sealed record ModuleSummaryResult(
    string Module,
    IReadOnlyList<ModuleSummaryRow> Top);

/// <summary>
/// One top-K row: a symbol with its inbound-call count. Sorted by <see cref="InDegree"/>
/// descending, then by FQN, mirroring the prose ordering.
/// </summary>
public sealed record ModuleSummaryRow(
    [property: JsonPropertyName("in_degree")] int InDegree,
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column);
