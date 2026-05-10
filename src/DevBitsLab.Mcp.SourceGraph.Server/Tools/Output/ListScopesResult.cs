using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>list_scopes</c> MCP tool. Pairs the (optional) configured
/// default scope with one row per registered scope. No
/// <c>ModelContextProtocol.Protocol.ResourceLinkBlock</c>s — scopes don't have their own
/// <c>graph://</c> URI scheme yet (every other tool's <c>scope</c> arg already routes by
/// <see cref="ListScopesRow.Id"/>).
/// </summary>
public sealed record ListScopesResult(
    [property: JsonPropertyName("default_scope")] string? DefaultScope,
    IReadOnlyList<ListScopesRow> Scopes);

/// <summary>
/// One scope row mirroring <see cref="DevBitsLab.Mcp.SourceGraph.Server.Scoping.ScopeHost"/>'s
/// public surface. <see cref="LastIndexedAt"/> is the ISO-8601 string when present, null when
/// the scope hasn't completed indexing yet (matches the prose <c>_never_</c> render).
/// <see cref="Status"/> is the kebab-case state token (<c>ok|degraded|indexing</c>);
/// <see cref="StatusMessage"/> carries the human-readable detail when status is degraded.
/// </summary>
public sealed record ListScopesRow(
    string Id,
    string Name,
    string Root,
    string Status,
    [property: JsonPropertyName("status_message")] string? StatusMessage,
    bool Isolated,
    [property: JsonPropertyName("last_indexed_at")] string? LastIndexedAt,
    [property: JsonPropertyName("project_count")] int ProjectCount);
