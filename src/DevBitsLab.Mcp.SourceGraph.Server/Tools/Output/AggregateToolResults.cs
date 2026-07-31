using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

public sealed record AggregateSymbol(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    string? Signature);

public sealed record AggregateReference(
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    [property: JsonPropertyName("is_generated")] bool IsGenerated);

public sealed record AggregateRelation(
    AggregateSymbol Symbol,
    string Relation,
    string Confidence,
    IReadOnlyList<TraceCallPathEvidence> Evidence,
    [property: JsonPropertyName("evidence_truncated")] bool EvidenceTruncated);

public sealed record ResolveAndReferencesResult(
    string Query,
    string Status,
    IReadOnlyList<AggregateSymbol> Candidates,
    AggregateSymbol? Definition,
    bool Truncated,
    IReadOnlyList<AggregateReference> References);

public sealed record SymbolOverviewResult(
    string Query,
    string Status,
    IReadOnlyList<AggregateSymbol> Candidates,
    AggregateSymbol? Definition,
    bool Truncated,
    IReadOnlyList<AggregateSymbol> Members,
    IReadOnlyList<AggregateRelation> Callers,
    IReadOnlyList<AggregateSymbol> Implementations);

public sealed record BatchQueryRequest(
    string Operation,
    string Symbol,
    int Limit = 20,
    [property: JsonPropertyName("include_generated")] bool IncludeGenerated = false,
    [property: JsonPropertyName("file_hint")] string? FileHint = null);

public sealed record BatchQueryItemResult(
    string Operation,
    string Symbol,
    string Status,
    [property: JsonPropertyName("resolve_and_references")]
    ResolveAndReferencesResult? ResolveAndReferences,
    [property: JsonPropertyName("symbol_overview")]
    SymbolOverviewResult? SymbolOverview);

public sealed record BatchQueryResult(IReadOnlyList<BatchQueryItemResult> Results);
