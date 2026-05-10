using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>repair_scope</c> MCP tool. The wire shape names every
/// piece of context the agent needs to decide whether the repair actually fixed anything: the
/// before/after status, the elapsed time, the mode (<c>"minimal" | "rebuild"</c>), and a
/// free-form message explaining what happened (orphan-prune count, archive path, retry result).
/// </summary>
public sealed record RepairScopeResult(
    string Scope,
    string Mode,
    [property: JsonPropertyName("before_status")] string BeforeStatus,
    [property: JsonPropertyName("after_status")] string AfterStatus,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    string Message);
