using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>semantic_search</c> MCP tool. <see cref="Query"/> echoes
/// the user-supplied probe verbatim. Each hit carries a cosine similarity score in [-1, 1] and
/// the symbol descriptor.
/// </summary>
public sealed record SemanticSearchResult(
    string Query,
    string Mode,
    [property: JsonPropertyName("strategy_used")] string StrategyUsed,
    [property: JsonPropertyName("candidate_source")] string CandidateSource,
    [property: JsonPropertyName("embedded_symbols")] long EmbeddedSymbols,
    [property: JsonPropertyName("eligible_symbols")] long EligibleSymbols,
    [property: JsonPropertyName("embedding_coverage")] double EmbeddingCoverage,
    string Model,
    [property: JsonPropertyName("model_hash")] string? ModelHash,
    IReadOnlyList<SemanticSearchHit> Hits);

/// <summary>
/// One semantic-search hit: cosine similarity score paired with the symbol descriptor.
/// </summary>
public sealed record SemanticSearchHit(
    double Score,
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("xml_summary")] string? XmlSummary,
    [property: JsonPropertyName("vector_score")] double? VectorScore = null,
    string Group = "production");
