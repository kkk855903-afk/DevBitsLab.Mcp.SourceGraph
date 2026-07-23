using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>verify_scope</c> MCP tool. The wire shape carries one row
/// per resolved scope; each row is read-only health metadata (status + row counts + integrity
/// check + drift sample). When called against a registry with no non-isolated scopes the tool
/// returns the structured <c>no_scopes</c> diagnostic shape established by other scope-aware
/// tools (handled by the tool body, not this DTO).
/// </summary>
public sealed record VerifyScopeResult(
    IReadOnlyList<VerifyScopeRow> Scopes);

/// <summary>
/// One scope's health snapshot. Fields surface raw numbers and the integrity-check string so the
/// agent can reason about anomalies in context — no graded "health score" is computed (a score
/// would invent meaning that doesn't map to any concrete user action). When <see cref="Status"/>
/// is <c>"degraded"</c> the snapshot omits the heavy fields (<see cref="RowCounts"/>,
/// <see cref="IntegrityCheck"/>, <see cref="DriftSample"/>) since they require a healthy DB
/// connection — the agent uses <see cref="StatusMessage"/> + <c>repair_scope</c> instead.
/// </summary>
public sealed record VerifyScopeRow(
    string Scope,
    string Status,
    [property: JsonPropertyName("status_message")] string? StatusMessage,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("views_schema_version")] int ViewsSchemaVersion,
    [property: JsonPropertyName("last_indexed_at")] string? LastIndexedAt,
    [property: JsonPropertyName("row_counts")] VerifyScopeCounts? RowCounts,
    [property: JsonPropertyName("integrity_check")] string? IntegrityCheck,
    [property: JsonPropertyName("drift_sample")] VerifyScopeDriftSample? DriftSample);

/// <summary>Per-table counts. Mirrors <see cref="DevBitsLab.Mcp.SourceGraph.Storage.RowCountsRow"/>.</summary>
public sealed record VerifyScopeCounts(
    long Symbols,
    long Refs,
    long Edges,
    long Files,
    long Annotations,
    long Diagnostics);

/// <summary>
/// Drift sample over up to N=20 random files. <see cref="Sampled"/> is the actual sample size
/// (capped by <see cref="TotalFiles"/>); <see cref="Changed"/> counts files whose on-disk SHA-256
/// differs from the DB's stored <c>content_sha256</c>. <see cref="ChangedPaths"/> is capped at the
/// first 5 changed paths so the response stays bounded.
/// </summary>
public sealed record VerifyScopeDriftSample(
    int Sampled,
    [property: JsonPropertyName("total_files")] int TotalFiles,
    int Changed,
    [property: JsonPropertyName("changed_paths")] IReadOnlyList<string> ChangedPaths);
