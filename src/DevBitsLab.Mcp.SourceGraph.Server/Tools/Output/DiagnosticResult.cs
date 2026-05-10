using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Single source of truth for the short-circuit diagnostic shape every converted MCP tool
/// returns when it can't produce a structured result. Wraps the message in a
/// <see cref="CallToolResult"/> whose only content item is a single <see cref="TextContentBlock"/>,
/// so the leaf chokepoint (which brand-marks the first user-visible text block) still attaches
/// the 🌿 prefix and the agent reads the prose.
///
/// Two shapes are exposed because the wire-level <see cref="CallToolResult.IsError"/> flag drives
/// downstream telemetry classification (<see cref="Observability.ToolMetrics"/> records
/// <c>ok=false</c> only when <see cref="CallToolResult.IsError"/> is true):
///
/// <list type="bullet">
///   <item><see cref="Build"/> — IsError unset. Use for "no matches" / "no rows" cases where the
///     query ran cleanly but the symbol-resolution stage produced zero hits, and the tool can't
///     construct a meaningful structured shape (e.g. there's no resolved target descriptor). The
///     prose carries the "No matches for 'X'." message and telemetry counts the call as
///     ok=true — symmetric with the historical <c>Task&lt;string&gt;</c> behavior.</item>
///   <item><see cref="Error"/> — IsError = true. Use for input validation failures (unknown
///     severity / accessibility / edge kind), disabled subsystems (<c>--no-history</c>,
///     <c>--no-embeddings</c>), scope-routing failures (no scopes registered, scope degraded,
///     exception thrown inside the scope query), and any other condition where the request
///     itself failed to run. Telemetry counts these as ok=false; clients see <c>isError</c> on
///     the wire.</item>
/// </list>
///
/// Neither shape sets <see cref="CallToolResult.StructuredContent"/> — diagnostic short-circuits
/// don't produce a typed result (per spec scenario "Diagnostic responses may omit
/// structuredContent"). Successful zero-row results still ship through their tool-specific
/// <c>Build…Result</c> helper with the empty collection populated (e.g. <c>{"hits": []}</c>) so
/// consumers branch on collection length rather than parsing prose.
/// </summary>
public static class DiagnosticResult
{
    /// <summary>
    /// Build a single-text <see cref="CallToolResult"/> carrying <paramref name="message"/> with
    /// <see cref="CallToolResult.IsError"/> unset. Use for "no matches" / "no rows" cases that
    /// the tool body can't represent in structured form (typically because no target descriptor
    /// could be constructed). Telemetry counts the call as ok=true — preserves the
    /// <c>Task&lt;string&gt;</c>-era convention that "No matches for 'X'." is a successful
    /// response, not an error.
    /// </summary>
    public static CallToolResult Build(string message) =>
        new()
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
        };

    /// <summary>
    /// Build a single-text <see cref="CallToolResult"/> carrying <paramref name="message"/> with
    /// <see cref="CallToolResult.IsError"/> set to <c>true</c>. Use for input validation
    /// failures, disabled subsystems, scope-routing failures, and exceptions caught inside the
    /// tool body. Telemetry counts the call as ok=false; strict-validating clients see
    /// <c>isError</c> on the wire and can route the response into an error path.
    /// </summary>
    public static CallToolResult Error(string message) =>
        new()
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
            IsError = true,
        };
}
