using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>find_diagnostics</c> MCP tool. Mirrors the user-supplied
/// filters so an agent reading <see cref="ModelContextProtocol.Protocol.CallToolResult.StructuredContent"/>
/// can correlate the result list with the inputs that produced it. No
/// <c>ModelContextProtocol.Protocol.ResourceLinkBlock</c> per row — diagnostics aren't
/// first-class graph entities (no <c>graph://diagnostic/&lt;id&gt;</c> URI scheme).
/// </summary>
public sealed record FindDiagnosticsResult(
    [property: JsonPropertyName("severity_floor")] string SeverityFloor,
    string? Code,
    string? Symbol,
    [property: JsonPropertyName("symbol_id")] long? SymbolId,
    [property: JsonPropertyName("root_cause")] string? RootCause,
    IReadOnlyList<FindDiagnosticsRow> Diagnostics);

/// <summary>
/// One Roslyn diagnostic row. <see cref="Severity"/> is the kebab-style label that mirrors the
/// prose render (<c>hidden|info|warning|error</c>); the underlying numeric severity level (0..3)
/// is dropped so the wire shape doesn't expose Roslyn's enum ordering.
/// </summary>
public sealed record FindDiagnosticsRow(
    string Severity,
    string Code,
    string Message,
    [property: JsonPropertyName("symbol_id")] long? SymbolId,
    string? Symbol,
    [property: JsonPropertyName("symbol_canonical_key")]
        string? SymbolCanonicalKey,
    string Relation,
    string Confidence,
    string Producer,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column);
