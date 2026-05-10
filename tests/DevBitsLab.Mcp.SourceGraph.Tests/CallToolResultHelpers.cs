using System.Linq;
using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Shared test helpers for inspecting the multi-content <see cref="CallToolResult"/> shape
/// introduced by <c>tool-output-content-blocks</c>. The "user-visible" predicate and the prose
/// extractor are duplicated across several test classes that exercise different facets of the
/// same wire shape (tabular rendering, structured-content invariants, leaf-chokepoint
/// scenarios, the find_definition vertical slice). Centralising the predicate here keeps the
/// "user-visible" rule consistent — if the audience-annotation semantics change (additional
/// roles, different priority handling, etc.), we update one place instead of four.
/// </summary>
internal static class CallToolResultHelpers
{
    /// <summary>
    /// True when <paramref name="block"/> is user-visible — its annotations don't restrict the
    /// audience to <see cref="Role.Assistant"/> only. Mirrors the predicate
    /// <see cref="DevBitsLab.Mcp.SourceGraph.Server.Tools.LeafFormatter"/> uses to find the
    /// brand-mark target. The block is user-visible when:
    /// <list type="bullet">
    ///   <item>It has no <see cref="Annotations"/>;</item>
    ///   <item>Annotations exist but <see cref="Annotations.Audience"/> is null or empty;</item>
    ///   <item>The audience array contains <see cref="Role.User"/>.</item>
    /// </list>
    /// </summary>
    public static bool IsUserVisible(TextContentBlock block)
    {
        var aud = block.Annotations?.Audience;
        return aud is null || aud.Count == 0 || aud.Contains(Role.User);
    }

    /// <summary>
    /// Pull the user-visible prose text out of a <see cref="CallToolResult"/>: the first
    /// <see cref="TextContentBlock"/> that <see cref="IsUserVisible"/> accepts. The trailing
    /// audience-restricted metadata block (<c>_meta: scope=`…`, latency_ms=…</c>) is skipped so
    /// substring assertions can pin the substantive prose without competing with the per-call
    /// metadata line. Returns <see cref="string.Empty"/> when no qualifying block exists (rare —
    /// would mean the tool emitted only resource_links or only audience-restricted text, which
    /// every test scenario in the repo treats as a failure mode).
    /// </summary>
    public static string ProseText(CallToolResult result) =>
        result.Content?
            .OfType<TextContentBlock>()
            .FirstOrDefault(IsUserVisible)?.Text
        ?? string.Empty;
}
