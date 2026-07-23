using System.Text;
using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Builds the trailing audience-restricted <see cref="TextContentBlock"/> that every converted
/// MCP tool emits as the last item in <see cref="CallToolResult.Content"/>. The block carries
/// per-call diagnostics — resolved scope id, query latency, edge-kind defaults, row counts,
/// "X of N rows omitted" notices, etc. — that are useful to the model but noise to the human
/// reading the chat.
///
/// Centralising the construction here gives every tool the same shape:
/// <code>
/// _meta: scope=`default`, latency_ms=12, hits=10_
/// </code>
/// with <see cref="ContentBlock.Annotations"/> set to
/// <c>{ Audience = [Role.Assistant], Priority = 0.2 }</c> — the
/// "agent-only, deprioritised" signal the spec scenarios assert.
///
/// The brand-mark chokepoint (<see cref="LeafFormatter"/>) skips audience-restricted blocks, so
/// the leaf never lands on the metadata; clients that respect the <c>audience</c> annotation
/// don't render it to the human user.
/// </summary>
public static class AudienceMetadata
{
    /// <summary>
    /// Documented "informational, deprioritise" priority used by every converted tool's metadata
    /// block. Spec scenario "Tool ships scope and latency metadata to the model" pins this below
    /// 0.5; we land at 0.2 so dashboards / scoring layers can still differentiate metadata from
    /// substantive content if they care.
    /// </summary>
    public const float DefaultPriority = 0.2f;

    /// <summary>
    /// Build the trailing metadata block. <paramref name="scopeId"/> is the resolved scope id
    /// (typically <c>host.Scope.Id</c>); pass <c>null</c> for tools where the scope isn't a
    /// meaningful concept (e.g. <c>list_scopes</c> itself). <paramref name="latencyMs"/> is the
    /// elapsed wall-clock time inside the tool body — measured by the caller so the helper stays
    /// pure. <paramref name="extras"/> are appended verbatim as <c>key=value</c> pairs in the
    /// declared order; values that contain code-like identifiers (file paths, namespaces) should
    /// be wrapped in backticks by the caller for visual consistency with the prose.
    /// </summary>
    public static TextContentBlock Build(
        string? scopeId,
        long latencyMs,
        params (string Key, string Value)[] extras)
    {
        var sb = new StringBuilder("_meta: ");
        if (scopeId is not null)
        {
            sb.Append("scope=`").Append(scopeId).Append("`, ");
        }
        sb.Append("latency_ms=").Append(latencyMs);
        foreach (var (key, value) in extras)
        {
            sb.Append(", ").Append(key).Append('=').Append(value);
        }
        sb.Append('_');
        return new TextContentBlock
        {
            Text = sb.ToString(),
            Annotations = new Annotations
            {
                Audience = new[] { Role.Assistant },
                Priority = DefaultPriority,
            },
        };
    }
}
