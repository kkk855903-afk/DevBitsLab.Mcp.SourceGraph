using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

public sealed record SearchTextResult(
    string Query,
    string Mode,
    [property: JsonPropertyName("case_sensitive")] bool CaseSensitive,
    [property: JsonPropertyName("file_glob")] string? FileGlob,
    [property: JsonPropertyName("context_lines")] int ContextLines,
    [property: JsonPropertyName("max_results")] int MaxResults,
    [property: JsonPropertyName("total_matches")] long TotalMatches,
    [property: JsonPropertyName("total_matching_lines")] long TotalMatchingLines,
    [property: JsonPropertyName("returned_lines")] int ReturnedLines,
    [property: JsonPropertyName("omitted_lines")] long OmittedLines,
    [property: JsonPropertyName("candidate_documents")] int CandidateDocuments,
    bool Truncated,
    [property: JsonPropertyName("truncation_reasons")] IReadOnlyList<string> TruncationReasons,
    [property: JsonPropertyName("previewed_lines")] int PreviewedLines,
    [property: JsonPropertyName("prose_preview_truncated")] bool ProsePreviewTruncated,
    [property: JsonPropertyName("excluded_directories")] IReadOnlyList<string> ExcludedDirectories,
    [property: JsonPropertyName("index_generation")] long IndexGeneration,
    IReadOnlyList<SearchTextHit> Hits);

public sealed record SearchTextHit(
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("end_column")] int EndColumn,
    [property: JsonPropertyName("match_count")] int MatchCount,
    [property: JsonPropertyName("line_text")] string LineText,
    [property: JsonPropertyName("before_context")] IReadOnlyList<string> BeforeContext,
    [property: JsonPropertyName("after_context")] IReadOnlyList<string> AfterContext);
