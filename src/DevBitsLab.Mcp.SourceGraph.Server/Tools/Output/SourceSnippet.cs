using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// A bounded source excerpt attached to structured evidence. Line numbers are one-based and
/// inclusive. The text contains only the requested window and never an entire file implicitly.
/// </summary>
public sealed record SourceSnippet(
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    string Text);
