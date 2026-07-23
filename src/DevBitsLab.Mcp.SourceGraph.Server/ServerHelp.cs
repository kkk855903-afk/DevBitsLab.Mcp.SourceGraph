namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Body of the on-demand help text that <see cref="ServerInstructions"/> points to via the
/// <c>graph://help</c> resource. The MCP <c>initialize</c> response carries only the short
/// preamble in <see cref="ServerInstructions.Template"/>; the verbose tool-selection guide and
/// the <c>describe_schema</c> / <c>query_graph</c> reference live here so agents that don't
/// need them never pay the upfront token cost. Served by
/// <see cref="Resources.GraphResources.GetHelp"/>.
/// </summary>
internal static class ServerHelp
{
    public const string Template =
        """
        # Tool selection

        By default each built-in tool's description in `tools/list` carries a `Use when:`
        line documenting the question shape it answers; pick the closest match. (The
        operator can suppress these lines with `--no-tool-triggers` /
        `SOURCEGRAPH_NO_TOOL_TRIGGERS=1` — if you don't see them, fall back to matching
        on the tool name and description prose.) Fall back to Grep / Read only when the
        graph genuinely doesn't cover the question (config files, plain text, comments,
        PR descriptions, etc.) or when a tool returns nothing relevant.

        # Ad-hoc queries (describe_schema + query_graph)

        For ad-hoc questions that don't fit a curated tool — aggregations, joins, "how
        many public types use X", "which classes implement IDisposable but lack
        Dispose", etc. — call `describe_schema` to learn the view layer, then
        `query_graph` to run read-only SQL against it. The view layer (v_symbols,
        v_edges, v_files, v_references, v_scopes, v_annotations, v_diagnostics,
        v_history) is a stable contract; the underlying tables remain implementation
        details.

        # Verifying graph use

        If `usage_stats` counts didn't move during a turn, you fell back to Grep + Read
        on a question a graph tool would have answered faster.
        """;
}
