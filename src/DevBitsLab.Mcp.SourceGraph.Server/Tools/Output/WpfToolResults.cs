using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Canonical identity and declaration location for one graph symbol returned by a WPF tool.
/// </summary>
public sealed record WpfSymbolIdentity(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Name,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("scope_id")] string ScopeId)
{
    [JsonPropertyName("canonical_id")]
    public string? CanonicalId => CanonicalKey;
}

/// <summary>
/// One candidate retained by an ambiguous XAML resolver outcome.
/// </summary>
public sealed record WpfResolutionCandidate(
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string? Display,
    [property: JsonPropertyName("file_path")] string? FilePath,
    int? Line,
    int? Column);

/// <summary>
/// Exact occurrence-level proof for a WPF relationship or resolver outcome.
/// </summary>
public sealed record WpfOccurrenceEvidence(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("start_column")] int StartColumn,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// One resolved edge or unresolved XAML binding/command outcome.
/// </summary>
public sealed record WpfTraceMatch(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    string Relation,
    string Path,
    string Status,
    string Reason,
    WpfSymbolIdentity Source,
    WpfSymbolIdentity? Target,
    string Confidence,
    IReadOnlyList<WpfOccurrenceEvidence> Evidence,
    [property: JsonPropertyName("evidence_truncated")] bool EvidenceTruncated,
    IReadOnlyList<WpfResolutionCandidate> Candidates)
{
    [JsonPropertyName("command_executions")]
    public IReadOnlyList<WpfCommandExecution> CommandExecutions { get; init; } =
        Array.Empty<WpfCommandExecution>();
}

/// <summary>
/// One evidence-backed command handler reached from an ICommand property.
/// </summary>
public sealed record WpfCommandExecution(
    string Relation,
    WpfSymbolIdentity Target,
    string Confidence,
    IReadOnlyList<WpfOccurrenceEvidence> Evidence,
    [property: JsonPropertyName("evidence_truncated")] bool EvidenceTruncated);

/// <summary>
/// Per-scope provenance and completeness state retained by single- and multi-scope WPF results.
/// </summary>
public sealed record WpfScopeSummary(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string Status,
    bool Partial,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note);

/// <summary>
/// Structured output for <c>trace_binding</c>.
/// </summary>
public sealed record TraceBindingResult(
    string Status,
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string? Note,
    [property: JsonPropertyName("element_query")] string? ElementQuery,
    [property: JsonPropertyName("binding_query")] string? BindingQuery,
    [property: JsonPropertyName("element_status")] string ElementStatus,
    IReadOnlyList<WpfSymbolIdentity> Candidates,
    bool Partial,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    IReadOnlyList<WpfTraceMatch> Matches,
    IReadOnlyList<WpfScopeSummary> Scopes)
{
    public string Result => Matches.Count > 0
        ? "found"
        : Partial || Truncated ? "unknown" : "absent";
    public string Completeness => Partial || Truncated ? "partial" : "complete";
    [JsonPropertyName("absence_authoritative")]
    public bool AbsenceAuthoritative =>
        Matches.Count == 0 && !Partial && !Truncated;
    public string Reason => Truncated
        ? "scan-cap"
        : Partial ? "partial-scope" : Matches.Count == 0 ? "not-found" : "matched";
}

/// <summary>
/// Structured output for <c>trace_command</c>.
/// </summary>
public sealed record TraceCommandResult(
    string Status,
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string? Note,
    [property: JsonPropertyName("element_query")] string? ElementQuery,
    [property: JsonPropertyName("command_query")] string? CommandQuery,
    [property: JsonPropertyName("element_status")] string ElementStatus,
    IReadOnlyList<WpfSymbolIdentity> Candidates,
    bool Partial,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    IReadOnlyList<WpfTraceMatch> Matches,
    IReadOnlyList<WpfScopeSummary> Scopes)
{
    public string Result => Matches.Count > 0
        ? "found"
        : Partial || Truncated ? "unknown" : "absent";
    public string Completeness => Partial || Truncated ? "partial" : "complete";
    [JsonPropertyName("absence_authoritative")]
    public bool AbsenceAuthoritative =>
        Matches.Count == 0 && !Partial && !Truncated;
    public string Reason => Truncated
        ? "scan-cap"
        : Partial ? "partial-scope" : Matches.Count == 0 ? "not-found" : "matched";
}

/// <summary>
/// One resolved <c>uses-resource</c>/<c>applies-style</c> edge or unresolved XAML resource
/// outcome.
/// </summary>
public sealed record WpfResourceCheck(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    string Key,
    string Relation,
    string Status,
    string Reason,
    [property: JsonPropertyName("resource_lookup")] string? ResourceLookup,
    WpfSymbolIdentity Source,
    WpfSymbolIdentity? Target,
    string Confidence,
    IReadOnlyList<WpfOccurrenceEvidence> Evidence,
    [property: JsonPropertyName("evidence_truncated")] bool EvidenceTruncated,
    IReadOnlyList<WpfResolutionCandidate> Candidates);

/// <summary>
/// Structured output for <c>check_resources</c>.
/// </summary>
public sealed record CheckResourcesResult(
    string Status,
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("scope_status")] string ScopeStatus,
    string? Note,
    [property: JsonPropertyName("file_query")] string? FileQuery,
    [property: JsonPropertyName("key_query")] string? KeyQuery,
    bool Partial,
    bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    IReadOnlyList<WpfResourceCheck> Resources,
    IReadOnlyList<WpfScopeSummary> Scopes)
{
    public string Result => Resources.Count > 0
        ? "found"
        : Partial || Truncated ? "unknown" : "absent";
    public string Completeness => Partial || Truncated ? "partial" : "complete";
    [JsonPropertyName("absence_authoritative")]
    public bool AbsenceAuthoritative =>
        Resources.Count == 0 && !Partial && !Truncated;
    public string Reason => Truncated
        ? "scan-cap"
        : Partial ? "partial-scope" : Resources.Count == 0 ? "not-found" : "matched";
}
