using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>reconcile_drift</c> MCP tool. Exposes the symmetric
/// difference the tool computed (changed + added + removed) plus the counts that didn't change
/// and a <see cref="Partial"/> flag indicating whether the walk hit <c>max_files</c>.
/// </summary>
public sealed record ReconcileDriftResult(
    string Scope,
    [property: JsonPropertyName("scanned_count")] int ScannedCount,
    [property: JsonPropertyName("reindexed_count")] int ReindexedCount,
    [property: JsonPropertyName("added_count")] int AddedCount,
    [property: JsonPropertyName("removed_count")] int RemovedCount,
    [property: JsonPropertyName("unchanged_count")] int UnchangedCount,
    [property: JsonPropertyName("source_candidate_count")]
        int SourceCandidateCount,
    [property: JsonPropertyName("native_not_configured_count")]
        int NativeNotConfiguredCount,
    [property: JsonPropertyName("unsupported_or_asset_count")]
        int UnsupportedOrAssetCount,
    [property: JsonPropertyName("source_candidate_examples")]
        IReadOnlyList<string> SourceCandidateExamples,
    [property: JsonPropertyName("native_not_configured_examples")]
        IReadOnlyList<string> NativeNotConfiguredExamples,
    [property: JsonPropertyName("unsupported_or_asset_examples")]
        IReadOnlyList<string> UnsupportedOrAssetExamples,
    bool Partial,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs);
