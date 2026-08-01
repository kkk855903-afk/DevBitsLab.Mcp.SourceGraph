using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// How strongly an analyzer established an edge occurrence. Values are ordered from weakest to
/// strongest so consumers can apply a minimum-confidence filter without language-specific rules.
/// </summary>
public enum EvidenceConfidence
{
    Inferred = 0,
    Semantic = 1,
    Exact = 2,
}

/// <summary>
/// One 1-based, half-open source range supporting an edge occurrence. The end position is
/// exclusive. <see cref="FilePath"/> is the absolute path supplied to the indexer or analyzer
/// for the current document.
/// </summary>
public sealed record SourceLocation(
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>
/// Occurrence-level proof for an emitted edge. The host owns producer-file identity and attaches
/// it when mapping this SDK value into storage, so plugins cannot attribute evidence to another
/// indexed file.
/// </summary>
/// <param name="Metadata">
/// Optional producer-defined details for this one source occurrence. They must not be treated as
/// graph-wide edge identity because multiple occurrences can support the same logical edge.
/// </param>
public sealed record EdgeEvidence(
    SourceLocation Location,
    EvidenceConfidence Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata = null);
