using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Prepends the source-graph brand mark (🌿 + space) to the first user-visible text payload of
/// every built-in MCP tool's response, before the result is shipped to the MCP client. The mark
/// is the agent's (and reading human's) at-a-glance signal that "this answer came from the live
/// code graph."
///
/// Three input shapes are supported:
///   - <c>string</c>: prepended verbatim. The legacy single-string code path used by tools that
///     still return <c>Task&lt;string&gt;</c>.
///   - <see cref="IReadOnlyList{T}"/> of <see cref="ContentBlock"/>: the first
///     <see cref="TextContentBlock"/> whose annotations don't restrict it to
///     <see cref="Role.Assistant"/> only is replaced with a leaf-prefixed copy. Other items are
///     unchanged. Audience-restricted text blocks are skipped, so per-call diagnostics that ride
///     in <see cref="Annotations.Audience"/> = [Assistant] never receive the brand mark.
///   - <see cref="CallToolResult"/>: behaves identically to the content-list overload, mutating
///     <see cref="CallToolResult.Content"/> in place.
///
/// Suppressed by the <c>--no-leaf</c> CLI flag or <c>SOURCEGRAPH_NO_LEAF=1</c> env var; when
/// <see cref="Suppressed"/> is true, every <c>Brand</c>/<c>BrandFirstText</c> overload is a
/// zero-overhead pass-through.
/// </summary>
public static class LeafFormatter
{
    public const string Mark = "🌿 ";
    public const string EnvVarName = "SOURCEGRAPH_NO_LEAF";

    /// <summary>
    /// Set once at process start by <c>Program.cs</c> from <c>--no-leaf</c> /
    /// <c>SOURCEGRAPH_NO_LEAF</c>. Not intended to flip mid-session.
    /// </summary>
    public static bool Suppressed { get; set; }

    public static string Brand(string toolResult)
    {
        if (Suppressed) return toolResult;
        if (string.IsNullOrEmpty(toolResult)) return toolResult;
        // Idempotency: a tool whose body emits its own leaf must not end up double-stamped.
        if (toolResult.StartsWith(Mark, StringComparison.Ordinal)) return toolResult;
        return Mark + toolResult;
    }

    /// <summary>
    /// Returns a new content list whose first user-visible <see cref="TextContentBlock"/> has its
    /// <see cref="TextContentBlock.Text"/> prefixed by the brand mark. Audience-restricted
    /// blocks ([assistant]) are skipped. Other items in the list are passed through unchanged.
    /// When suppression is active or the input has no qualifying text block, the input is
    /// returned unchanged.
    /// </summary>
    public static IReadOnlyList<ContentBlock> BrandFirstText(IReadOnlyList<ContentBlock> content)
    {
        if (Suppressed || content.Count == 0) return content;

        var firstUserTextIdx = -1;
        for (var i = 0; i < content.Count; i++)
        {
            if (content[i] is TextContentBlock t && IsUserVisible(t))
            {
                firstUserTextIdx = i;
                break;
            }
        }
        if (firstUserTextIdx < 0) return content;

        var src = (TextContentBlock)content[firstUserTextIdx];
        if (string.IsNullOrEmpty(src.Text)) return content;
        if (src.Text.StartsWith(Mark, StringComparison.Ordinal)) return content;

        // Build a copy of the block with branded text. Other annotation/meta fields ride through.
        var branded = new TextContentBlock
        {
            Text = Mark + src.Text,
            Annotations = src.Annotations,
            Meta = src.Meta,
        };

        var copy = new ContentBlock[content.Count];
        for (var i = 0; i < content.Count; i++)
        {
            copy[i] = i == firstUserTextIdx ? branded : content[i];
        }
        return copy;
    }

    /// <summary>
    /// Brand the first user-visible <see cref="TextContentBlock"/> inside
    /// <see cref="CallToolResult.Content"/> in place, returning the same <paramref name="result"/>
    /// instance for fluent chaining. Suppression-aware.
    /// </summary>
    public static CallToolResult BrandFirstText(CallToolResult result)
    {
        if (Suppressed) return result;
        if (result.Content is null || result.Content.Count == 0) return result;
        // CallToolResult.Content is IList<ContentBlock>; the list overload takes IReadOnlyList<>
        // (same data, more constrained interface). Adapt with a snapshot — the read-only overload
        // never mutates the input, so the snapshot is throwaway.
        var snapshot = result.Content as IReadOnlyList<ContentBlock> ?? result.Content.ToArray();
        var branded = BrandFirstText(snapshot);
        if (!ReferenceEquals(branded, snapshot))
        {
            result.Content = branded.ToList();
        }
        return result;
    }

    private static bool IsUserVisible(ContentBlock block)
    {
        // A block is user-visible when its annotations don't carve it out for assistants only.
        // Either no annotations, or no audience, or audience includes Role.User.
        var ann = block.Annotations;
        if (ann is null) return true;
        var audience = ann.Audience;
        if (audience is null || audience.Count == 0) return true;
        return audience.Contains(Role.User);
    }
}
