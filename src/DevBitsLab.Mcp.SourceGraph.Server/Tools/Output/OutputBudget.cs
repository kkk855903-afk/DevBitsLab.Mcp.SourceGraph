namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Soft character budget for built-in tool responses. Claude Code (and similar MCP clients) clamp
/// per-tool-call results around ~16K tokens / ~64K characters; emitting more triggers a host
/// truncation that the agent reads as a cryptic error. All constants and the <see cref="ChooseKeep"/>
/// arithmetic are denominated in serialized characters of the resulting JSON wire payload — not
/// bytes, not tokens — matching how the host measures the limit.
///
/// Each list-shaped tool's result triplicates per-row data — prose row, <c>ResourceLinkBlock</c>
/// JSON, structured-content array entry — so a <c>limit=200</c> query can cross 80K characters
/// even when each row is compact. <see cref="ChooseKeep"/> picks a smaller "kept" count proactively
/// so the trio stays under <see cref="DefaultBudgetChars"/>; the dropped tail is reported via the
/// audience-restricted metadata block (<c>omitted_size=N</c>) so the model can detect truncation
/// and re-query with a tighter filter.
/// </summary>
public static class OutputBudget
{
    /// <summary>Target serialized-result size, sized comfortably under Claude Code's ~64K cap.</summary>
    public const int DefaultBudgetChars = 50_000;

    /// <summary>Reserved budget for header prose, audience metadata, and structured-content wrapper.</summary>
    public const int BaseOverheadChars = 2_000;

    /// <summary>
    /// Per-row cost for compact rows: kind label + file path + line/col. Covers
    /// <c>find_references</c>, <c>list_callers</c>, <c>list_callees</c>, <c>find_implementations</c>.
    /// </summary>
    public const int CompactRowChars = 500;

    /// <summary>
    /// Per-row cost for rich rows that also carry a signature and XML summary line. Covers
    /// <c>list_members</c> and <c>list_symbols_in_file</c>.
    /// </summary>
    public const int RichRowChars = 1_000;

    /// <summary>
    /// Per-row cost for rows that include a code excerpt or longer summary. Covers
    /// <c>semantic_search</c>.
    /// </summary>
    public const int SnippetRowChars = 1_500;

    /// <summary>
    /// Decide how many of <paramref name="totalItems"/> can fit under <paramref name="budget"/>
    /// characters, given each item costs roughly <paramref name="perItemChars"/> characters when
    /// serialized across prose + resource link + structured content.
    /// </summary>
    public static (int Kept, int OmittedDueToSize) ChooseKeep(
        int totalItems,
        int perItemChars,
        int baseChars = BaseOverheadChars,
        int budget = DefaultBudgetChars)
    {
        if (totalItems <= 0) return (0, 0);
        var available = System.Math.Max(0, budget - baseChars);
        var max = available / System.Math.Max(perItemChars, 1);
        if (max >= totalItems) return (totalItems, 0);
        max = System.Math.Max(0, max);
        return (max, totalItems - max);
    }
}
