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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    [property: JsonPropertyName("source_coverage_complete"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SourceCoverageComplete,
    [property: JsonPropertyName("language_projection_complete"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LanguageProjectionComplete,
    [property: JsonPropertyName("relation_projection_complete"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RelationProjectionComplete,
    [property: JsonPropertyName("query_traversal_complete"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool QueryTraversalComplete,
    [property: JsonPropertyName("indexed_files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int IndexedFiles,
    [property: JsonPropertyName("eligible_files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int EligibleFiles,
    [property: JsonPropertyName("missing_files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MissingFiles,
    [property: JsonPropertyName("missing_file_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int MissingFileCount,
    [property: JsonPropertyName("missing_files_truncated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool MissingFilesTruncated,
    [property: JsonPropertyName("loaded_indexers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? LoadedIndexers,
    [property: JsonPropertyName("index_generation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long IndexGeneration,
    [property: JsonPropertyName("indexed_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IndexedAt)
{
    [JsonPropertyName("absence_authoritative"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AbsenceAuthoritative =>
        SourceCoverageComplete
        && LanguageProjectionComplete
        && RelationProjectionComplete
        && QueryTraversalComplete;
}

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
    [JsonPropertyName("source_coverage_complete")]
    public bool SourceCoverageComplete => Scopes.Count > 0 && Scopes.All(row => row.SourceCoverageComplete);
    [JsonPropertyName("language_projection_complete")]
    public bool LanguageProjectionComplete => Scopes.Count > 0 && Scopes.All(row => row.LanguageProjectionComplete);
    [JsonPropertyName("relation_projection_complete")]
    public bool RelationProjectionComplete => Scopes.Count > 0 && Scopes.All(row => row.RelationProjectionComplete);
    [JsonPropertyName("query_traversal_complete")]
    public bool QueryTraversalComplete => Scopes.Count > 0 && Scopes.All(row => row.QueryTraversalComplete);
    [JsonPropertyName("absence_authoritative")]
    public bool AbsenceAuthoritative =>
        Matches.Count == 0 && !Partial && !Truncated
        && SourceCoverageComplete && LanguageProjectionComplete
        && RelationProjectionComplete && QueryTraversalComplete;
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
    [JsonPropertyName("source_coverage_complete")]
    public bool SourceCoverageComplete => Scopes.Count > 0 && Scopes.All(row => row.SourceCoverageComplete);
    [JsonPropertyName("language_projection_complete")]
    public bool LanguageProjectionComplete => Scopes.Count > 0 && Scopes.All(row => row.LanguageProjectionComplete);
    [JsonPropertyName("relation_projection_complete")]
    public bool RelationProjectionComplete => Scopes.Count > 0 && Scopes.All(row => row.RelationProjectionComplete);
    [JsonPropertyName("query_traversal_complete")]
    public bool QueryTraversalComplete => Scopes.Count > 0 && Scopes.All(row => row.QueryTraversalComplete);
    [JsonPropertyName("absence_authoritative")]
    public bool AbsenceAuthoritative =>
        Matches.Count == 0 && !Partial && !Truncated
        && SourceCoverageComplete && LanguageProjectionComplete
        && RelationProjectionComplete && QueryTraversalComplete;
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
    [JsonPropertyName("source_coverage_complete")]
    public bool SourceCoverageComplete => Scopes.Count > 0 && Scopes.All(row => row.SourceCoverageComplete);
    [JsonPropertyName("language_projection_complete")]
    public bool LanguageProjectionComplete => Scopes.Count > 0 && Scopes.All(row => row.LanguageProjectionComplete);
    [JsonPropertyName("relation_projection_complete")]
    public bool RelationProjectionComplete => Scopes.Count > 0 && Scopes.All(row => row.RelationProjectionComplete);
    [JsonPropertyName("query_traversal_complete")]
    public bool QueryTraversalComplete => Scopes.Count > 0 && Scopes.All(row => row.QueryTraversalComplete);
    [JsonPropertyName("absence_authoritative")]
    public bool AbsenceAuthoritative =>
        Resources.Count == 0 && !Partial && !Truncated
        && SourceCoverageComplete && LanguageProjectionComplete
        && RelationProjectionComplete && QueryTraversalComplete;
    public string Reason => Truncated
        ? "scan-cap"
        : Partial ? "partial-scope" : Resources.Count == 0 ? "not-found" : "matched";
}
