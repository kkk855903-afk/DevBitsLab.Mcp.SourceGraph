using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>who_authored</c> MCP tool. Singleton record (the tool
/// returns at most one row of git-blame metadata) wrapped in an object as required by the MCP
/// SDK's <c>"type":"object"</c> root constraint on every <c>outputSchema</c>.
///
/// Every git-blame field is nullable to accommodate the "no history yet" path: when the symbol
/// resolved but storage has no <c>symbol_history</c> row (uncommitted file, pipeline still
/// catching up), the tool emits the prose explanation and ships a structured payload with
/// <see cref="Author"/> / <see cref="Sha"/> / <see cref="AuthoredAt"/> / <see cref="BlamedLines"/>
/// set to <c>null</c> so agents can branch on the absence rather than parsing the prose or
/// disambiguating a real <c>0</c> line count from a missing one.
/// </summary>
public sealed record WhoAuthoredResult(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    string Fqn,
    string? Author,
    string? Sha,
    [property: JsonPropertyName("authored_at")] string? AuthoredAt,
    [property: JsonPropertyName("blamed_lines")] int? BlamedLines);
