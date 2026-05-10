using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>list_tests_for</c> MCP tool. Pairs the resolved
/// production-symbol target with the list of tests exercising it (walked through inbound
/// <c>tests</c> edges in storage).
/// </summary>
public sealed record ListTestsForResult(
    [property: JsonPropertyName("target_symbol_id")] long TargetSymbolId,
    [property: JsonPropertyName("target_fqn")] string TargetFqn,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    IReadOnlyList<ListTestsForRow> Tests);

/// <summary>
/// One test-symbol row. <see cref="Framework"/> is the canonical lower-case framework token
/// stored alongside the test symbol (<c>"xunit"</c>, <c>"nunit"</c>, <c>"mstest"</c>, etc.) or
/// the literal <c>"unknown"</c> when storage didn't capture it.
/// </summary>
public sealed record ListTestsForRow(
    [property: JsonPropertyName("test_symbol_id")] long TestSymbolId,
    string Framework,
    [property: JsonPropertyName("test_fqn")] string TestFqn,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column);
