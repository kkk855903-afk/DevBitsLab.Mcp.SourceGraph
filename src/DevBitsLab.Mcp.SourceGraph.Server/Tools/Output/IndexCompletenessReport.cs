using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>Orthogonal completeness dimensions for safe negative-result interpretation.</summary>
public sealed record IndexCompletenessReport(
    [property: JsonPropertyName("source_coverage_complete")] bool SourceCoverageComplete,
    [property: JsonPropertyName("language_projection_complete")] bool LanguageProjectionComplete,
    [property: JsonPropertyName("relation_projection_complete")] bool RelationProjectionComplete,
    [property: JsonPropertyName("query_traversal_complete")] bool QueryTraversalComplete,
    [property: JsonPropertyName("indexed_files")] int IndexedFiles,
    [property: JsonPropertyName("eligible_files")] int EligibleFiles,
    [property: JsonPropertyName("missing_files")] IReadOnlyList<string> MissingFiles,
    [property: JsonPropertyName("missing_file_count")] int MissingFileCount,
    [property: JsonPropertyName("missing_files_truncated")] bool MissingFilesTruncated,
    [property: JsonPropertyName("loaded_indexers")] IReadOnlyList<string> LoadedIndexers,
    [property: JsonPropertyName("index_generation")] long IndexGeneration,
    [property: JsonPropertyName("indexed_at")] string? IndexedAt)
{
    [JsonPropertyName("absence_authoritative")]
    public bool AbsenceAuthoritative =>
        SourceCoverageComplete
        && LanguageProjectionComplete
        && RelationProjectionComplete
        && QueryTraversalComplete;
}
