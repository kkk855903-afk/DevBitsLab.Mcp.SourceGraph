using DevBitsLab.Mcp.SourceGraph.Server.Tools;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Cross-cutting guidance the MCP server publishes via the <c>initialize</c> response.
/// Per-tool trigger phrases are intentionally NOT enumerated here — they travel with their
/// tool's description in the catalog. This string carries only what doesn't fit there:
/// (1) the rule to prefer source-graph tools over Grep+Read for symbol-level questions, and
/// (2) the closing directive to call <c>usage_stats</c> at end-of-turn to verify the graph
/// was actually queried. The leading <c>🌿 </c> teaches the connecting client the leaf-glyph-
/// to-<c>sourcegraph</c> association from the initialize handshake (suppressed at publish time
/// when <see cref="LeafFormatter.Suppressed"/> is true).
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
        🌿 This MCP server exposes a live code source graph for the connected .NET solution.
        For symbol-level questions ("where is X defined?", "who calls X?", "what's in this
        file?", "what would change if I edit X?", and similar) prefer these tools before
        reaching for Grep + Read — the graph answers in one structured call instead of
        dozens of file reads.

        Each tool's description includes a "Use when:" line documenting the question shape
        it answers; pick the closest match. Fall back to Grep / Read only when the graph
        genuinely doesn't cover the question (config files, plain text, comments, PR
        descriptions, etc.) or when a tool returns nothing relevant.

        For ad-hoc questions that don't fit a curated tool — aggregations, joins, "how many
        public types use X", "which classes implement IDisposable but lack Dispose", etc. —
        call `describe_schema` to learn the view layer, then `query_graph` to run read-only
        SQL against it. The view layer (v_symbols, v_edges, v_files, v_references, v_scopes)
        is a stable contract; the underlying tables remain implementation details.

        Call usage_stats at the end of a turn to verify the graph was actually queried.
        If the counts didn't move, you fell back to Grep + Read on a question a graph tool
        would have answered faster.
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
