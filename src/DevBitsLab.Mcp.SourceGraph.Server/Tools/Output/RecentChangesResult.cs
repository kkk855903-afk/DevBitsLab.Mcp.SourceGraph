using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>recent_changes</c> MCP tool. Echoes the user-supplied
/// window and author filter back so an agent that fanned out across scopes can correlate which
/// inputs produced this list.
/// </summary>
public sealed record RecentChangesResult(
    int Days,
    string? Author,
    IReadOnlyList<RecentChangesRow> Changes);

/// <summary>
/// One recent-change row. <see cref="AuthoredAt"/> is the ISO-8601 string when present; the
/// <c>SymbolHistory.LastAuthoredAt</c> source nullable is preserved so consumers can branch on
/// "we have history but no commit data" cases.
/// </summary>
public sealed record RecentChangesRow(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("commit_sha")] string? CommitSha,
    string? Author,
    [property: JsonPropertyName("authored_at")] string? AuthoredAt);
