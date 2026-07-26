using DevBitsLab.Mcp.SourceGraph.Server.Tools;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Cross-cutting guidance the MCP server publishes via the <c>initialize</c> response.
/// Per-tool trigger phrases are intentionally NOT enumerated here — they travel with their
/// tool's description in the catalog. This string carries only cross-cutting guidance: prefer
/// source-graph tools over Grep+Read, use the execution profile for UI-to-native flow, check its
/// absence-authoritative disclosure, fetch the detailed help resource, and call
/// <c>usage_stats</c> at end-of-turn. The leading <c>🌿 </c> teaches the connecting client the
/// leaf-glyph-to-<c>sourcegraph</c> association from the initialize handshake (suppressed at
/// publish time when <see cref="LeafFormatter.Suppressed"/> is true).
/// </summary>
internal static class ServerInstructions
{
    public const string EnvVarName = "SOURCEGRAPH_NO_INSTRUCTIONS";

    /// <summary>
    /// Returns true when either the CLI flag or the env var asks the server to suppress its
    /// <c>ServerInstructions</c>. Truthy env values: <c>1</c> or <c>true</c> (case-insensitive
    /// for <c>true</c>; <c>1</c> matches exactly).
    /// </summary>
    public static bool ShouldSuppress(bool noInstructionsFlag, string? envValue)
    {
        if (noInstructionsFlag) return true;
        if (string.IsNullOrEmpty(envValue)) return false;
        return envValue.Equals("1", System.StringComparison.Ordinal)
            || envValue.Equals("true", System.StringComparison.OrdinalIgnoreCase);
    }

    public const string Template =
        """
        🌿 This MCP server exposes a live evidence-backed graph for the connected .NET
        solution, including C#, WPF/XAML, protobuf/gRPC, zero-build C/C++ syntax,
        and configured C/C++ interop.
        For symbol-level questions ("where is X defined?", "who calls X?", "what would
        change if I edit X?", and similar) prefer these tools over Grep + Read — the
        graph answers in one structured call instead of dozens of file reads.

        For UI-to-native flow, use `trace_call` or `trace_call_path` with
        `profile="execution"` and trust an empty result only when
        `execution_state.absence_authoritative` is true.

        Fetch `graph://help` for the full tool-selection guide and the `query_graph`
        ad-hoc-query reference (MedInteropLens tools, completeness, view layer, examples).

        Call `usage_stats` at end-of-turn to verify the graph was actually queried.
        """;

    /// <summary>
    /// Returns the value to publish into <c>McpServerOptions.ServerInstructions</c>. Honours
    /// <see cref="LeafFormatter.Suppressed"/>: when set, strips the leading <c>🌿 </c> prefix
    /// from <see cref="Template"/> before returning. The two suppression knobs
    /// (<c>--no-instructions</c> and <c>--no-leaf</c>) compose independently — this helper
    /// only handles leaf-suppression. The instructions-suppression check (skip publishing
    /// entirely) lives at the call site in <c>Program.cs</c>.
    /// </summary>
    public static string ResolvePublished() =>
        LeafFormatter.Suppressed && Template.StartsWith(LeafFormatter.Mark, System.StringComparison.Ordinal)
            ? Template[LeafFormatter.Mark.Length..]
            : Template;
}
